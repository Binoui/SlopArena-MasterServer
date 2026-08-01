// MasterServer/Lobbies/LobbyManager.cs
using System.Collections.Concurrent;

namespace MasterServer.Lobbies;

/// <summary>
/// In-memory lobby state authority (ADR-0004). One lobby per game server,
/// keyed by <c>serverId</c>. Lobbies are ephemeral: they live only while the
/// master server process is up, which is acceptable for the demo.
/// </summary>
public sealed class LobbyManager
{
    private readonly ConcurrentDictionary<Guid, Lobby> _lobbiesByServer = new();
    private readonly ConcurrentDictionary<string, Lobby> _lobbyByConnection = new();

    /// <summary>
    /// Adds a player to the lobby for <paramref name="serverId"/>. The first
    /// player to join an empty lobby becomes the host (issue #32).
    /// </summary>
    public JoinLobbyResult JoinLobby(Guid serverId, string connectionId, long steamId, string name)
    {
        var lobby = _lobbiesByServer.GetOrAdd(serverId, _ => new Lobby(serverId));

        // A connection can only be in one lobby at a time. If it was previously
        // in a *different* lobby, remove it there and surface the departure so
        // the hub can broadcast PlayerLeft to the old lobby's survivors and drop
        // the old SignalR group membership. If it was already in THIS lobby
        // (same server, e.g. a duplicate join), return the current snapshot
        // without removing/re-adding — avoids orphaning the lobby.
        LeaveLobbyResult? departure = null;
        if (_lobbyByConnection.TryGetValue(connectionId, out var previous))
        {
            if (previous == lobby)
            {
                // Already in this lobby (duplicate join) — return current state
                // without removing/re-adding to avoid a duplicate member.
                return new JoinLobbyResult(lobby.GetPlayer(connectionId)!, lobby.Snapshot(), null);
            }

            // Different lobby — remove from the old one and surface the departure.
            var player = previous.RemovePlayer(connectionId);
            if (previous.IsEmpty)
                _lobbiesByServer.TryRemove(previous.ServerId, out _);
            departure = new LeaveLobbyResult(previous.ServerId, player, previous.Snapshot());
        }

        _lobbyByConnection[connectionId] = lobby;

        var joined = lobby.AddPlayer(connectionId, steamId, name);
        return new JoinLobbyResult(joined, lobby.Snapshot(), departure);
    }

    /// <summary>
    /// Removes a player from whatever lobby they were in (used on
    /// <c>OnDisconnectedAsync</c> and on explicit <c>LeaveLobby</c>).
    /// </summary>
    public LeaveLobbyResult LeaveLobby(string connectionId)
    {
        if (!_lobbyByConnection.TryRemove(connectionId, out var lobby))
            return new LeaveLobbyResult(null, null, null);

        var player = lobby.RemovePlayer(connectionId);
        // Reap empty lobbies so an idle server does not accumulate state, and
        // signal "nothing left to broadcast" via a null snapshot to the caller.
        if (lobby.IsEmpty)
        {
            _lobbiesByServer.TryRemove(lobby.ServerId, out _);
            return new LeaveLobbyResult(lobby.ServerId, player, null);
        }

        return new LeaveLobbyResult(lobby.ServerId, player, lobby.Snapshot());
    }

    /// <summary>
    /// Returns the lobby a connection currently belongs to, or null.
    /// </summary>
    public LobbySnapshot? GetSnapshot(string connectionId)
        => _lobbyByConnection.TryGetValue(connectionId, out var lobby) ? lobby.Snapshot() : null;

    /// <summary>
    /// Attempts a host start for the connection. Only the lobby host may start
    /// (issue #32). On success returns the roster for the match-start broadcast.
    /// </summary>
    public HostStartResult TryHostStart(string connectionId)
    {
        if (!_lobbyByConnection.TryGetValue(connectionId, out var lobby))
            return new HostStartResult(false, "You are not in a lobby.", null);

        if (!lobby.IsHostByConnection(connectionId, out var host))
            return new HostStartResult(false, "Only the host can start the match.", null);

        return new HostStartResult(true, null, new MatchStartingConfig(lobby.ServerId, lobby.Snapshot().Players));
    }

    /// <summary>
    /// A single lobby: an ordered, locked player list. The first member is the
    /// host; on host departure the next-joined member is promoted.
    /// </summary>
    private sealed class Lobby(Guid serverId)
    {
        private readonly object _gate = new();
        private readonly List<Member> _members = new();

        public Guid ServerId => serverId;

        public LobbyPlayer AddPlayer(string connectionId, long steamId, string name)
        {
            lock (_gate)
            {
                var isHost = _members.Count == 0;
                var member = new Member(connectionId, steamId, name, isHost);
                _members.Add(member);
                return member.ToPlayer();
            }
        }

        public LobbyPlayer? GetPlayer(string connectionId)
        {
            lock (_gate)
            {
                var idx = _members.FindIndex(m => m.ConnectionId == connectionId);
                return idx >= 0 ? _members[idx].ToPlayer() : null;
            }
        }

        public LobbyPlayer? RemovePlayer(string connectionId)
        {
            lock (_gate)
            {
                var idx = _members.FindIndex(m => m.ConnectionId == connectionId);
                if (idx < 0) return null;

                var removed = _members[idx];
                _members.RemoveAt(idx);

                // Promote the now-first member to host when the host left.
                if (removed.IsHost && _members.Count > 0)
                    _members[0] = _members[0] with { IsHost = true };

                return removed.ToPlayer();
            }
        }

        public bool IsEmpty
        {
            get
            {
                lock (_gate) { return _members.Count == 0; }
            }
        }

        public bool IsHostByConnection(string connectionId, out LobbyPlayer? host)
        {
            lock (_gate)
            {
                var idx = _members.FindIndex(m => m.ConnectionId == connectionId);
                if (idx < 0 || !_members[idx].IsHost)
                {
                    host = null;
                    return false;
                }
                host = _members[idx].ToPlayer();
                return true;
            }
        }

        public LobbySnapshot Snapshot()
        {
            lock (_gate)
            {
                return new LobbySnapshot(serverId, _members.Select(m => m.ToPlayer()).ToList());
            }
        }

        private readonly record struct Member(string ConnectionId, long SteamId, string Name, bool IsHost)
        {
            public LobbyPlayer ToPlayer() => new(SteamId, Name, null, IsHost);
        }
    }
}
