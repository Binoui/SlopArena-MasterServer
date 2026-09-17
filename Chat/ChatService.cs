// MasterServer/Chat/ChatService.cs
using System.Globalization;
using MasterServer.Lobbies;
using Microsoft.AspNetCore.SignalR;

namespace MasterServer.Chat;

/// <summary>
/// In-memory chat state authority: presence, profiles, public history, and the
/// shared per-identity send/control quotas. One lock guards every mutation
/// (presence, profile, rate windows, history, recipient snapshots) so a caller
/// gets an atomic accept-or-reject with no partial registration; nothing async
/// or network/DB-bound ever runs while the lock is held. <see cref="MembershipGate"/>
/// is a *separate* semaphore that callers hold around DB-backed rename/join
/// transitions to linearize them against each other — sends never take it.
/// </summary>
public sealed class ChatService
{
    // Online identities / connections per identity (contract: 256 identities,
    // 4 connections each — bounds presence memory and the recipient scans below).
    private const int MaxOnlineIdentities = 256;
    private const int MaxConnectionsPerIdentity = 4;

    // Public history depth per channel, and how many distinct GameServer
    // backlogs are retained at once. Active GameServers can never exceed the
    // online identity count (one joined connection per identity), so 256 is
    // never tighter than reality.
    private const int MaxHistoryPerChannel = 50;
    private const int MaxServerBacklogs = 256;

    // Shared send quota (all channels, all connections of one identity) vs. the
    // separate, smaller hub-control quota. Both windows are enforced against
    // bounded dictionaries so a churn of throwaway guest identities cannot grow
    // rate state without limit.
    private const int MessageRateLimit = 5;
    private const int ControlRateLimit = 20;
    private const int MaxRateEntries = 1024;
    private static readonly TimeSpan MessageRateWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ControlRateWindow = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private readonly LobbyManager _lobbies;
    private readonly TimeProvider _clock;

    private readonly Dictionary<long, IdentityState> _identities = new();
    private readonly Dictionary<string, long> _connections = new();
    private readonly Queue<ChatMessage> _globalHistory = new(MaxHistoryPerChannel);
    private readonly Dictionary<Guid, ServerBacklog> _serverBacklogs = new();

    // Keyed by identity, retained across disconnects (contract: quota survives
    // a reconnect within its window). Separate tables so a message rejection
    // never touches control allowance and vice versa.
    private readonly Dictionary<long, Queue<long>> _messageAttempts = new();
    private readonly Dictionary<long, Queue<long>> _controlAttempts = new();

    private long _sequence;
    private long _historyAccess;

    /// <summary>Held by callers around DB-backed rename/join transitions; sends never take it.</summary>
    internal SemaphoreSlim MembershipGate { get; } = new(1, 1);

    // Lock order: MembershipGate, then MembershipSync. Never await under this
    // lock: lobby membership and chat recipient/backlog snapshots must agree.
    internal object MembershipSync => _lock;

    public ChatService(LobbyManager lobbies, TimeProvider clock)
    {
        _lobbies = lobbies;
        _clock = clock;
    }

    internal ChatPresence? Connect(long playerId, string displayName, string connectionId)
    {
        var name = ChatText.DisplayName(displayName);
        lock (_lock)
        {
            if (_connections.TryGetValue(connectionId, out var connectedId))
            {
                if (connectedId != playerId)
                    throw new HubException("not_connected");
                return null;
            }

            if (_identities.TryGetValue(playerId, out var identity))
            {
                if (identity.ConnectionIds.Count >= MaxConnectionsPerIdentity)
                    throw new HubException("chat_capacity");
                identity.ConnectionIds.Add(connectionId);
                _connections.Add(connectionId, playerId);
                return null;
            }

            if (_identities.Count >= MaxOnlineIdentities)
                throw new HubException("chat_capacity");

            identity = new IdentityState(ChatText.Player(playerId, name));
            identity.ConnectionIds.Add(connectionId);
            _identities.Add(playerId, identity);
            _connections.Add(connectionId, playerId);
            return new ChatPresence(identity.Profile, true);
        }
    }

    internal ChatPresence? Disconnect(string connectionId)
    {
        lock (_lock)
        {
            if (!_connections.Remove(connectionId, out var playerId))
                return null;

            if (!_identities.TryGetValue(playerId, out var identity))
                return null;

            identity.ConnectionIds.Remove(connectionId);
            if (identity.ConnectionIds.Count > 0)
                return null; // other connections still online: no presence change

            _identities.Remove(playerId);
            // Rate tables are keyed by identity and are deliberately left untouched:
            // quota must survive this disconnect (contract).
            return new ChatPresence(identity.Profile, false);
        }
    }

    internal bool IsOnline(long playerId)
    {
        lock (_lock)
            return _identities.ContainsKey(playerId);
    }

    internal bool HasLobbyMembership(long playerId)
    {
        lock (_lock)
        {
            if (!_identities.TryGetValue(playerId, out var identity))
                return false;

            foreach (var connectionId in identity.ConnectionIds)
                if (_lobbies.GetServerId(connectionId) is not null)
                    return true;

            return false;
        }
    }

    internal bool HasOtherLobbyConnection(long playerId, string connectionId)
    {
        lock (_lock)
        {
            if (!_identities.TryGetValue(playerId, out var identity))
                return false;

            foreach (var candidate in identity.ConnectionIds)
            {
                if (candidate == connectionId)
                    continue;
                if (_lobbies.GetServerId(candidate) is not null)
                    return true;
            }

            return false;
        }
    }

    internal ChatPlayer UpdateDisplayName(long playerId, string displayName)
    {
        var validName = ChatText.DisplayName(displayName);
        lock (_lock)
        {
            if (_identities.TryGetValue(playerId, out var identity))
            {
                identity.Profile = identity.Profile with { DisplayName = validName };
                return identity.Profile;
            }
            return ChatText.Player(playerId, validName);
        }
    }

    internal ChatSnapshot GetSnapshot(string connectionId)
    {
        lock (_lock)
        {
            var playerId = RequireConnected(connectionId);
            var self = _identities[playerId].Profile;
            return new ChatSnapshot(self, _globalHistory.ToArray(), GetServerStateLocked(connectionId));
        }
    }

    internal ServerChatState GetServerState(string connectionId)
    {
        lock (_lock)
            return GetServerStateLocked(connectionId);
    }

    internal ChatPlayer[] GetOnlinePlayers()
    {
        lock (_lock)
            return _identities.Values.Select(identity => identity.Profile).ToArray();
    }

    internal void CheckControlRate(string connectionId)
    {
        lock (_lock)
        {
            var playerId = RequireConnected(connectionId);
            switch (TryConsume(_controlAttempts, playerId, ControlRateWindow, ControlRateLimit))
            {
                case RateResult.RateLimited:
                    throw new HubException("control_rate_limited");
                case RateResult.CapacityExhausted:
                    throw new HubException("chat_capacity");
            }
        }
    }

    internal ChatDelivery SendGlobal(string connectionId, string text)
    {
        lock (_lock)
        {
            var playerId = RequireConnected(connectionId);
            ConsumeMessageRate(playerId);
            var validText = ChatText.Message(text);

            var message = NewMessage("global", null, null, playerId, validText);
            Append(_globalHistory, message);

            var recipients = _connections.Keys.ToArray();
            return new ChatDelivery(message, recipients);
        }
    }

    internal ChatDelivery SendServer(string connectionId, Guid serverId, string text)
    {
        lock (_lock)
        {
            var playerId = RequireConnected(connectionId);
            ConsumeMessageRate(playerId);

            // Reject unauthorized targets before any routing/state mutation: the
            // caller's live membership (never the RPC argument alone) decides scope.
            if (_lobbies.GetServerId(connectionId) is not { } currentServerId || currentServerId != serverId)
                throw new HubException("not_in_server");

            var validText = ChatText.Message(text);

            var backlog = GetOrCreateBacklog(serverId);
            var message = NewMessage("server", serverId, null, playerId, validText);
            backlog.LastActivity = ++_historyAccess;
            Append(backlog.Messages, message);

            var recipients = CollectServerRecipients(serverId);
            return new ChatDelivery(message, recipients);
        }
    }

    internal ChatDelivery SendDirect(string connectionId, string playerId, string text)
    {
        lock (_lock)
        {
            var senderId = RequireConnected(connectionId);
            ConsumeMessageRate(senderId);
            var validText = ChatText.Message(text);

            // Recipient must be a known-online identity; malformed/unknown IDs are
            // indistinguishable from offline to the caller.
            if (!long.TryParse(playerId, NumberStyles.None, CultureInfo.InvariantCulture, out var targetId)
                || !_identities.TryGetValue(targetId, out var target))
                throw new HubException("recipient_offline");

            var canonicalTargetId = targetId.ToString(CultureInfo.InvariantCulture);
            var message = NewMessage("direct", null, canonicalTargetId, senderId, validText);

            // No history retained for Direct. Self-send must not duplicate connections.
            var sender = _identities[senderId];
            var recipients = new string[target.ConnectionIds.Count +
                (senderId == targetId ? 0 : sender.ConnectionIds.Count)];
            target.ConnectionIds.CopyTo(recipients);
            if (senderId != targetId)
                sender.ConnectionIds.CopyTo(recipients, target.ConnectionIds.Count);
            return new ChatDelivery(message, recipients);
        }
    }

    private ChatMessage NewMessage(string channel, Guid? serverId, string? recipientId, long senderId, string text)
    {
        var sender = _identities[senderId].Profile;
        return new ChatMessage(Guid.NewGuid(), ++_sequence, channel, serverId, recipientId, sender, text, _clock.GetUtcNow());
    }

    private ServerChatState GetServerStateLocked(string connectionId)
    {
        var serverId = _lobbies.GetServerId(connectionId);
        if (serverId is null)
            return new ServerChatState(null, Array.Empty<ChatMessage>());

        ChatMessage[] messages;
        if (_serverBacklogs.TryGetValue(serverId.Value, out var backlog))
        {
            backlog.LastActivity = ++_historyAccess;
            messages = backlog.Messages.ToArray();
        }
        else
            messages = Array.Empty<ChatMessage>();
        return new ServerChatState(serverId, messages);
    }

    private long RequireConnected(string connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var playerId))
            throw new HubException("not_connected");
        return playerId;
    }

    private void ConsumeMessageRate(long playerId)
    {
        switch (TryConsume(_messageAttempts, playerId, MessageRateWindow, MessageRateLimit))
        {
            case RateResult.RateLimited:
                throw new HubException("rate_limited");
            case RateResult.CapacityExhausted:
                throw new HubException("chat_capacity");
        }
    }

    private RateResult TryConsume(Dictionary<long, Queue<long>> table, long playerId, TimeSpan window, int limit)
    {
        var now = _clock.GetTimestamp();

        if (table.TryGetValue(playerId, out var attempts))
        {
            Trim(attempts, now, window);
        }
        else
        {
            if (table.Count >= MaxRateEntries)
            {
                SweepExpired(table, now, window);
                if (table.Count >= MaxRateEntries)
                    return RateResult.CapacityExhausted;
            }

            attempts = new Queue<long>(limit);
            table[playerId] = attempts;
        }

        if (attempts.Count >= limit)
            return RateResult.RateLimited;

        attempts.Enqueue(now);
        return RateResult.Allowed;
    }

    private void Trim(Queue<long> attempts, long now, TimeSpan window)
    {
        while (attempts.Count > 0 && _clock.GetElapsedTime(attempts.Peek(), now) >= window)
            attempts.Dequeue();
    }

    // ponytail: O(entries) linear sweep, bounded by MaxRateEntries (1024); only
    // runs once the retained-rate table is already full and a never-seen identity
    // needs a slot. Upgrade to an expiry heap/LRU if this ever shows up hot.
    private void SweepExpired(Dictionary<long, Queue<long>> table, long now, TimeSpan window)
    {
        List<long>? emptyKeys = null;
        foreach (var (key, attempts) in table)
        {
            Trim(attempts, now, window);
            if (attempts.Count == 0)
                (emptyKeys ??= new List<long>()).Add(key);
        }

        if (emptyKeys is null)
            return;
        foreach (var key in emptyKeys)
            table.Remove(key);
    }

    private ServerBacklog GetOrCreateBacklog(Guid serverId)
    {
        if (_serverBacklogs.TryGetValue(serverId, out var existing))
            return existing;

        if (_serverBacklogs.Count >= MaxServerBacklogs)
            EvictIdleBacklog();

        var backlog = new ServerBacklog();
        _serverBacklogs[serverId] = backlog;
        return backlog;
    }

    private void EvictIdleBacklog()
    {
        // ponytail: bounded scan over every online identity's connections
        // (<=256*4=1024) to find which GameServers currently have a joined
        // member. LobbyManager.GetServerId is a direct dictionary lookup with
        // no allocation, so this stays cheap, and it only runs at the 256
        // backlog cap. Active GameServers <= online identities, so a fresh
        // ID never overflows the cap without at least one idle backlog to evict.
        var active = new HashSet<Guid>();
        foreach (var identity in _identities.Values)
            foreach (var connectionId in identity.ConnectionIds)
                if (_lobbies.GetServerId(connectionId) is { } activeServerId)
                    active.Add(activeServerId);

        Guid evictKey = default;
        long oldest = 0;
        var found = false;

        foreach (var (key, backlog) in _serverBacklogs)
        {
            if (active.Contains(key))
                continue;
            if (found && backlog.LastActivity >= oldest)
                continue;

            found = true;
            oldest = backlog.LastActivity;
            evictKey = key;
        }

        if (!found)
            throw new HubException("chat_capacity");

        _serverBacklogs.Remove(evictKey);
    }

    // ponytail: same bounded connection scan as EvictIdleBacklog, reused to build
    // the actual Server recipient list (connections currently joined to serverId).
    private string[] CollectServerRecipients(Guid serverId)
    {
        var recipients = new List<string>();
        foreach (var identity in _identities.Values)
            foreach (var connectionId in identity.ConnectionIds)
                if (_lobbies.GetServerId(connectionId) == serverId)
                    recipients.Add(connectionId);
        return recipients.ToArray();
    }

    private static void Append(Queue<ChatMessage> history, ChatMessage message)
    {
        if (history.Count == MaxHistoryPerChannel)
            history.Dequeue();
        history.Enqueue(message);
    }

    private enum RateResult
    {
        Allowed,
        RateLimited,
        CapacityExhausted,
    }

    private sealed class IdentityState(ChatPlayer profile)
    {
        public ChatPlayer Profile { get; set; } = profile;
        public List<string> ConnectionIds { get; } = new(MaxConnectionsPerIdentity);
    }

    private sealed class ServerBacklog
    {
        public Queue<ChatMessage> Messages { get; } = new(MaxHistoryPerChannel);
        public long LastActivity { get; set; }
    }
}
