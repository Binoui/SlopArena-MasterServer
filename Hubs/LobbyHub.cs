// MasterServer/Hubs/LobbyHub.cs
using System.Globalization;
using System.Security.Claims;
using MasterServer.Chat;
using MasterServer.Configuration;
using MasterServer.Data;
using MasterServer.Lobbies;
using MasterServer.Rooms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MasterServer.Hubs;

/// <summary>
/// Authenticated game-wide chat, public Rooms, and per-GameServer lobby control.
/// LobbyManager owns waiting-roster and authoritative GameServer membership;
/// RoomManager owns independent public Room membership; ChatService owns
/// presence, message budgets, history, and recipient snapshots.
/// </summary>
[Authorize]
public sealed class LobbyHub : Hub
{
    private readonly LobbyManager _lobbies;
    private readonly RoomManager _rooms;
    private readonly AppDbContext _db;
    private readonly IMatchLauncher _launcher;
    private readonly ILogger<LobbyHub> _logger;
    private readonly ChatService _chat;
    private readonly MasterDeploymentOptions _deployment;
    private readonly RoomDirectoryNotifier _directory;
    // One Master process serializes host selection with launch; the persisted open rows
    // arbitrate capacity after each release. A replicated Master needs DB reservation.
    private static readonly SemaphoreSlim RoomLaunchGate = new(1, 1);

    public LobbyHub(LobbyManager lobbies, AppDbContext db, IMatchLauncher launcher,
        ILogger<LobbyHub> logger, ChatService chat, MasterDeploymentOptions deployment, RoomManager rooms,
        RoomDirectoryNotifier directory)
    {
        _lobbies = lobbies;
        _rooms = rooms;
        _db = db;
        _launcher = launcher;
        _logger = logger;
        _chat = chat;
        _deployment = deployment;
        _directory = directory;
    }

    public override async Task OnConnectedAsync()
    {
        if (!TryGetSteamId(out var playerId))
            throw new HubException("Authenticated identity missing.");

        ChatPresence? presence = null;
        RoomConnectionResult? room = null;
        ServerChatState roomChat;
        await _chat.MembershipGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            var user = await _db.Users.FindAsync(playerId);
            if (user is null)
                throw new HubException("Authenticated user not found.");

            lock (_chat.MembershipSync)
            {
                presence = _chat.Connect(playerId, user.Username, Context.ConnectionId);
                room = _rooms.Connect(playerId, Context.ConnectionId);
                roomChat = _chat.GetServerState(Context.ConnectionId);
            }

            await base.OnConnectedAsync();
            if (room?.Snapshot is { } snapshot)
                await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroupName(snapshot.Id));
            if (room?.Change is { } change)
            {
                await RevokeRoomConnectionsAsync(change);
                await RemoveRoomConnectionsAsync(change);
                if (change.Snapshot is { MemberCount: > 0 })
                    await Clients.Group(RoomGroupName(change.RoomId))
                        .SendAsync("RoomUpdated", change.Snapshot);
                if (change.Changed)
                    await _directory.NotifyChangedAsync(Context.ConnectionAborted);
            }
            await Clients.Caller.SendAsync("ChatServerChanged", roomChat, Context.ConnectionAborted);
        }
        catch
        {
            lock (_chat.MembershipSync)
            {
                _rooms.Disconnect(Context.ConnectionId);
                _chat.Disconnect(Context.ConnectionId);
            }
            throw;
        }
        finally
        {
            _chat.MembershipGate.Release();
        }

        if (presence is not null)
            await Clients.All.SendAsync("ChatPresenceChanged", presence);
    }

    public ChatSnapshot GetChatState() => _chat.GetSnapshot(Context.ConnectionId);

    public ChatPlayer[] GetOnlinePlayers() => _chat.GetOnlinePlayers();

    public Task<ChatMessage> SendGlobal(string text)
        => Deliver(_chat.SendGlobal(Context.ConnectionId, text));

    public async Task<ChatMessage> SendServer(Guid roomId, string text)
    {
        await _chat.MembershipGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            var delivery = _chat.SendServer(Context.ConnectionId, roomId, text);
            await Deliver(delivery);
            return delivery.Message;
        }
        finally
        {
            _chat.MembershipGate.Release();
        }
    }

    public Task<ChatMessage> SendDirect(string playerId, string text)
        => Deliver(_chat.SendDirect(Context.ConnectionId, playerId, text));

    private async Task<ChatMessage> Deliver(ChatDelivery delivery)
    {
        await Clients.Clients(delivery.ConnectionIds)
            .SendAsync("ChatMessage", delivery.Message, Context.ConnectionAborted);
        return delivery.Message;
    }

    /// <summary>Development-only legacy GameServer waiting-roster admission.</summary>
    public async Task JoinLobby(Guid serverId, int protocolVersion)
    {
        if (!TryGetSteamId(out var playerId))
            throw new HubException("Authenticated identity missing.");
        if (_deployment.IsVps)
            throw new HubException("physical_admission_disabled: Join a Room instead.");

        JoinLobbyResult result;
        await _chat.MembershipGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            if (!await IsFreshServerAsync(serverId))
                throw new HubException("server_unavailable");
            if (_chat.HasOtherLobbyConnection(playerId, Context.ConnectionId))
                throw new HubException("already_joined");
            if (_rooms.GetMyRoom(playerId) is not null)
                throw new HubException("already_in_room: Leave your Room before joining a GameServer lobby.");

            var user = await _db.Users.FindAsync(playerId);
            if (user is null)
                throw new HubException("Authenticated user not found.");

            lock (_chat.MembershipSync)
            {
                result = _lobbies.JoinLobby(serverId, Context.ConnectionId, playerId, user.Username);
                if (!result.GameServerAdmitted)
                    throw new HubException(result.Error ?? "Join rejected.");
            }
        }
        finally
        {
            _chat.MembershipGate.Release();
        }

        if (result.Departure is { } departure)
            await AnnounceDeparture(departure.ServerId, departure.Player, departure.Snapshot);

        if (result.Success)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(serverId));
            _logger.LogInformation("Lobby {ServerId}: player {PlayerId} joined waiting roster", serverId, playerId);
            await Clients.Group(GroupName(serverId)).SendAsync("PlayerJoined", result.Player!);
            await Clients.Group(GroupName(serverId)).SendAsync("LobbyUpdated", result.Snapshot!);
        }
        else
        {
            _logger.LogInformation("Server {ServerId}: player {PlayerId} joined; waiting roster full", serverId, playerId);
        }

        if (!result.Success)
            throw new HubException("lobby_full");
    }

    /// <summary>Development-only reconnect for remembered GameServer admission.</summary>
    public async Task ResumeServer(Guid serverId, int protocolVersion)
    {
        if (!TryGetSteamId(out var playerId))
            throw new HubException("Authenticated identity missing.");
        if (_deployment.IsVps)
            throw new HubException("physical_admission_disabled: Join a Room instead.");

        await _chat.MembershipGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            if (!await IsFreshServerAsync(serverId))
                throw new HubException("server_unavailable");
            if (_chat.HasOtherLobbyConnection(playerId, Context.ConnectionId))
                throw new HubException("already_joined");
            if (_rooms.GetMyRoom(playerId) is not null)
                throw new HubException("already_in_room: Leave your Room before resuming a GameServer lobby.");

            var user = await _db.Users.FindAsync(playerId);
            if (user is null)
                throw new HubException("Authenticated user not found.");

            lock (_chat.MembershipSync)
            {
                if (!_lobbies.ResumeServer(serverId, Context.ConnectionId, playerId, user.Username, out var error))
                    throw new HubException(error ?? "not_admitted");
            }
        }
        finally
        {
            _chat.MembershipGate.Release();
        }

    }

    public RoomSummary[] GetRooms() => _rooms.GetRooms();

    public RoomSnapshot? GetMyRoom()
    {
        if (!TryGetSteamId(out var playerId))
            throw new HubException("Authenticated identity missing.");
        return _rooms.GetMyRoom(playerId);
    }
    public Task<RoomSnapshot> RoomStartCharacterSelect()
        => ApplyRoomPreparationAsync(_rooms.StartCharacterSelect, directoryChanged: true);

    public Task<RoomSnapshot> RoomSelectCharacter(string character)
        => ApplyRoomPreparationAsync(playerId => _rooms.SelectCharacter(playerId, character));

    public Task<RoomSnapshot> RoomStartStageSelect()
        => ApplyRoomPreparationAsync(_rooms.StartStageSelect, directoryChanged: true);

    public Task<RoomSnapshot> RoomChooseArena(string arena)
        => ApplyRoomPreparationAsync(playerId => _rooms.ChooseArena(playerId, arena));

    private async Task<RoomSnapshot> ApplyRoomPreparationAsync(
        Func<long, RoomMutationResult> mutate, bool directoryChanged = false)
    {
        if (!TryGetSteamId(out var playerId))
            throw new HubException("Authenticated identity missing.");
        await _chat.MembershipGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            RoomMutationResult result;
            lock (_chat.MembershipSync)
                result = mutate(playerId);
            if (result.Error is not null)
                throw new HubException(result.Error);
            await Clients.Group(RoomGroupName(result.RoomId))
                .SendAsync("RoomUpdated", result.Snapshot!, Context.ConnectionAborted);
            if (directoryChanged && result.Changed)
                await _directory.NotifyChangedAsync(Context.ConnectionAborted);
            return result.Snapshot!;
        }
        finally
        {
            _chat.MembershipGate.Release();
        }
    }


    public async Task<MatchStartedConfig> RoomStartMatch()
    {
        await _rooms.MatchLifecycleGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            return await StartRoomMatchCoreAsync();
        }
        finally
        {
            _rooms.MatchLifecycleGate.Release();
        }
    }

    private async Task<MatchStartedConfig> StartRoomMatchCoreAsync()
    {
        if (!TryGetSteamId(out var playerId))
            throw new HubException("Authenticated identity missing.");
        var preparation = _rooms.BeginMatch(playerId, out var error);
        if (preparation is null)
            throw new HubException(error ?? "room_not_ready: Finish Room preparation first.");

        var roomId = preparation.RoomId;
        var matchId = preparation.MatchId;
        MatchStartedConfig? started = null;
        try
        {
            await Clients.Group(RoomGroupName(roomId)).SendAsync("RoomUpdated", _rooms.GetMyRoom(playerId)!);
            await _directory.NotifyChangedAsync(Context.ConnectionAborted);
            await RoomLaunchGate.WaitAsync();
            try
            {
                var server = await ChooseRoomHostAsync();
                if (server is null)
                    throw new HubException("gamehost_unavailable: No fresh compatible GameHost has a free Match slot. Retry later.");
                var config = new MatchStartedConfig(server.Id, preparation.Players,
                    ArenaName: preparation.ArenaName, RoomId: roomId, MatchId: matchId,
                    CatalogHash: _rooms.CatalogHash,
                    ServerAddress: _deployment.IsVps ? null : server.IpAddress);
                var launch = await _launcher.LaunchAsync(config);
                if (launch.MatchId != matchId ||
                    launch.Descriptor is { } descriptor && descriptor.MatchId != matchId)
                    throw new InvalidOperationException("GameHost returned a different Match identity.");
                started = config with
                {
                    MatchPort = launch.MatchPort,
                    Content = launch.Content,
                    Descriptor = launch.Descriptor,
                    MatchId = launch.MatchId
                };
                if (_rooms.CompleteLaunch(roomId, matchId) is null)
                    throw new InvalidOperationException("Room launch identity changed during GameHost admission.");
            }
            finally
            {
                RoomLaunchGate.Release();
            }
        }
        catch (Exception exception)
        {
            var restored = _rooms.FailLaunch(roomId, matchId);
            if (restored is not null)
            {
                await Clients.Group(RoomGroupName(roomId)).SendAsync("RoomUpdated", restored);
                await _directory.NotifyChangedAsync(Context.ConnectionAborted);
            }
            if (exception is HubException) throw;
            _logger.LogWarning(exception, "Room {RoomId} could not launch Match {MatchId}", roomId, matchId);
            throw new HubException("gamehost_start_failed: GameHost could not start this Match. Room selections are intact; retry.");
        }

        var inMatch = _rooms.GetMyRoom(playerId);
        if (inMatch is not null)
        {
            await Clients.Group(RoomGroupName(roomId)).SendAsync("RoomUpdated", inMatch);
            await _directory.NotifyChangedAsync(Context.ConnectionAborted);
        }
        await Clients.Group(RoomGroupName(roomId)).SendAsync("MatchStarted", started!);
        return started!;
    }

    private async Task<MasterServer.Data.Models.GameServer?> ChooseRoomHostAsync()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-15);
        var approved = _deployment.ApprovedHostId;
        var expectedHash = _rooms.CatalogHash;
        if (expectedHash is null || expectedHash.Length != 64 ||
            expectedHash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new HubException("room_content_unconfigured: Master needs the deployed Match catalog hash.");
        var candidates = await _db.GameServers.Where(server =>
            server.LastHeartbeat > cutoff && server.MaxConcurrentMatches > 0 &&
            server.CurrentMatches < server.MaxConcurrentMatches &&
            server.CatalogHash == expectedHash &&
            (!_deployment.IsVps && server.ProtocolVersion == 0 ||
                _deployment.IsVps && server.Id == approved &&
                server.ProtocolVersion == SteamMatchDescriptor.Protocol && server.SteamId != null &&
                server.InstanceId != null && server.InstanceId != Guid.Empty))
            .ToListAsync();
        if (candidates.Count == 0)
            return null;
        var ids = candidates.Select(candidate => candidate.Id).ToArray();
        var open = await _db.Matches.Where(match => match.ServerId != null &&
            ids.Contains(match.ServerId.Value) &&
            match.EndedAt == null && match.CanceledAt == null).ToListAsync();
        return candidates.Select(server => new
            {
                Server = server,
                Load = Math.Max(server.CurrentMatches, open.Count(match => match.ServerId == server.Id))
            })
            .Where(candidate => candidate.Load < candidate.Server.MaxConcurrentMatches)
            .OrderBy(candidate => (double)candidate.Load / candidate.Server.MaxConcurrentMatches)
            .ThenBy(candidate => candidate.Server.Id)
            .Select(candidate => candidate.Server)
            .FirstOrDefault();
    }

    public async Task<RoomSnapshot> CreateRoom(string name)
    {
        await _chat.MembershipGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            var (playerId, playerName) = await GetRoomPlayerAsync();
            RoomMutationResult result;
            ServerChatState roomChat;
            lock (_chat.MembershipSync)
            {
                if (_chat.HasLobbyMembership(playerId))
                    throw new HubException("already_in_room: Leave your GameServer lobby before creating a Room.");
                result = _rooms.CreateRoom(playerId, name, playerName);
                if (result.Error is not null)
                    throw new HubException(result.Error);
                roomChat = _chat.GetServerState(Context.ConnectionId);
            }

            await AddRoomConnectionsAsync(result);
            await Clients.Group(RoomGroupName(result.RoomId)).SendAsync("RoomUpdated", result.Snapshot!);
            await _directory.NotifyChangedAsync(Context.ConnectionAborted);
            await Clients.Clients(result.ConnectionIds).SendAsync("ChatServerChanged", roomChat);
            return result.Snapshot!;
        }
        finally
        {
            _chat.MembershipGate.Release();
        }
    }

    public async Task<RoomSnapshot> JoinRoom(Guid roomId)
    {
        await _chat.MembershipGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            var (playerId, playerName) = await GetRoomPlayerAsync();
            RoomMutationResult result;
            ServerChatState roomChat;
            lock (_chat.MembershipSync)
            {
                if (_chat.HasLobbyMembership(playerId))
                    throw new HubException("already_in_room: Leave your GameServer lobby before joining a Room.");
                result = _rooms.JoinRoom(roomId, playerId, playerName);
                if (result.Error is not null)
                    throw new HubException(result.Error);
                roomChat = _chat.GetServerState(Context.ConnectionId);
            }

            await AddRoomConnectionsAsync(result);
            if (result.Changed)
            {
                await Clients.Group(RoomGroupName(result.RoomId)).SendAsync("RoomUpdated", result.Snapshot!);
                await _directory.NotifyChangedAsync(Context.ConnectionAborted);
            }
            await Clients.Clients(result.ConnectionIds).SendAsync("ChatServerChanged", roomChat);
            return result.Snapshot!;
        }
        finally
        {
            _chat.MembershipGate.Release();
        }
    }

    public async Task LeaveRoom()
    {
        if (!TryGetSteamId(out var playerId))
            throw new HubException("Authenticated identity missing.");

        await _chat.MembershipGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            RoomMutationResult result;
            lock (_chat.MembershipSync)
            {
                result = _rooms.LeaveRoom(playerId);
                if (result.Error is not null)
                    throw new HubException(result.Error);
            }

            await RevokeRoomConnectionsAsync(result);
            await RemoveRoomConnectionsAsync(result);
            if (result.Snapshot!.MemberCount > 0)
                await Clients.Group(RoomGroupName(result.RoomId)).SendAsync("RoomUpdated", result.Snapshot);
            await _directory.NotifyChangedAsync(Context.ConnectionAborted);
        }
        finally
        {
            _chat.MembershipGate.Release();
        }
    }

    private async Task<(long SteamId, string Name)> GetRoomPlayerAsync()
    {
        if (!TryGetSteamId(out var playerId))
            throw new HubException("Authenticated identity missing.");
        var user = await _db.Users.FindAsync(playerId);
        if (user is null)
            throw new HubException("Authenticated user not found.");
        return (playerId, user.Username);
    }

    private Task AddRoomConnectionsAsync(RoomMutationResult result)
        => Task.WhenAll(result.ConnectionIds.Select(connectionId =>
            Groups.AddToGroupAsync(connectionId, RoomGroupName(result.RoomId))));

    private async Task RevokeRoomConnectionsAsync(RoomMutationResult result)
    {
        if (result.ConnectionIds.Count == 0)
            return;

        await Clients.Clients(result.ConnectionIds)
            .SendAsync("ChatServerChanged", new ServerChatState(null, []));
        await Clients.Clients(result.ConnectionIds).SendAsync("RoomMembershipRevoked", result.RoomId);
    }
    private Task RemoveRoomConnectionsAsync(RoomMutationResult result)
        => Task.WhenAll(result.ConnectionIds.Select(connectionId =>
            Groups.RemoveFromGroupAsync(connectionId, RoomGroupName(result.RoomId))));


    /// <summary>Explicitly leaves the current GameServer lobby.</summary>
    public async Task LeaveLobby()
    {
        if (!TryGetSteamId(out var playerId))
            throw new HubException("Authenticated identity missing.");

        LeaveLobbyResult departure;
        await _chat.MembershipGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            lock (_chat.MembershipSync)
                departure = _lobbies.LeaveLobby(Context.ConnectionId, playerId);
        }
        finally
        {
            _chat.MembershipGate.Release();
        }
        await AnnounceDeparture(departure.ServerId, departure.Player, departure.Snapshot);
    }

    public async Task HostStart()
    {
        var result = _lobbies.TryHostStart(Context.ConnectionId);
        if (!result.Success)
            throw new HubException(result.Error ?? "Host start rejected.");
        await Clients.Group(GroupName(result.Config!.ServerId)).SendAsync("MatchStarting", result.Config);
    }

    public async Task SelectCharacter(string character)
    {
        var result = _lobbies.SelectCharacter(Context.ConnectionId, character);
        if (!result.Success)
            throw new HubException(result.Error ?? "Character selection rejected.");
        await Clients.Group(GroupName(result.Snapshot!.ServerId)).SendAsync("CharacterSelected", result.Player);
        await Clients.Group(GroupName(result.Snapshot.ServerId)).SendAsync("LobbyUpdated", result.Snapshot);
    }

    /// <summary>Host-only transition from character select to stage select.</summary>
    public async Task StartStageSelect()
    {
        var result = _lobbies.TryHostStart(Context.ConnectionId);
        if (!result.Success)
            throw new HubException(result.Error ?? "Stage select rejected.");
        if (!_lobbies.IsAllLockedIn(Context.ConnectionId, out var lockedInError))
            throw new HubException(lockedInError ?? "Not all players locked in.");
        await Clients.Group(GroupName(result.Config!.ServerId)).SendAsync("StageSelect", result.Config);
    }

    /// <summary>
    /// Launches the selected arena and forwards the GameServer's opaque
    /// authoritative content map in the MatchStarted payload.
    /// </summary>
    public async Task StartMatch(string arena)
    {
        MatchStartedConfig config;
        await _chat.MembershipGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            var result = _lobbies.TryStartMatch(Context.ConnectionId, arena);
            if (!result.Success)
                throw new HubException(result.Error ?? "Start match rejected.");

            config = result.Config!;
            var launch = await _launcher.LaunchAsync(config);
            if (launch.MatchId == Guid.Empty ||
                launch.Descriptor is { } descriptor && descriptor.MatchId != launch.MatchId)
                throw new InvalidOperationException("GameHost returned an invalid Match identity.");
            config = config with
            {
                MatchPort = launch.MatchPort,
                Content = launch.Content,
                Descriptor = launch.Descriptor,
                MatchId = launch.MatchId
            };

            // Broadcast while the launched roster is still in the lobby group,
            // then remove its waiting slots while retaining server membership.
            try
            {
                await Clients.Group(GroupName(config.ServerId)).SendAsync("MatchStarted", config);
            }
            finally
            {
                IReadOnlyList<string> releasedConnections;
                lock (_chat.MembershipSync)
                    releasedConnections = _lobbies.ReleaseMatchRoster(
                        config.ServerId, config.Players.Select(player => player.SteamId).ToArray());
                foreach (var connectionId in releasedConnections)
                    await Groups.RemoveFromGroupAsync(connectionId, GroupName(config.ServerId));
            }
        }
        finally
        {
            _chat.MembershipGate.Release();
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        LeaveLobbyResult departure;
        ChatPresence? presence;
        await _chat.MembershipGate.WaitAsync();
        try
        {
            RoomMutationResult? roomChange;
            lock (_chat.MembershipSync)
            {
                roomChange = _rooms.Disconnect(Context.ConnectionId);
                presence = _chat.Disconnect(Context.ConnectionId);
                departure = _lobbies.DisconnectConnection(Context.ConnectionId);
            }

            if (roomChange is { Snapshot: not null })
                await Clients.Group(RoomGroupName(roomChange.RoomId))
                    .SendAsync("RoomUpdated", roomChange.Snapshot);
            if (roomChange is { Changed: true })
                await _directory.NotifyChangedAsync();
        }
        finally
        {
            _chat.MembershipGate.Release();
        }
        await AnnounceDeparture(departure.ServerId, departure.Player, departure.Snapshot);
        if (presence is not null)
            await Clients.All.SendAsync("ChatPresenceChanged", presence);
        await base.OnDisconnectedAsync(exception);
    }

    private async Task<bool> IsFreshServerAsync(Guid serverId)
    {
        if (_deployment.IsVps && serverId != _deployment.ApprovedHostId)
            return false;
        var cutoff = DateTime.UtcNow.AddSeconds(-15);
        return await _db.GameServers.AnyAsync(
            server => server.Id == serverId && server.LastHeartbeat >= cutoff &&
                (!_deployment.IsVps || (server.SteamId != null &&
                    server.InstanceId != null && server.ProtocolVersion == SteamMatchDescriptor.Protocol)),
            Context.ConnectionAborted);
    }

    private async Task AnnounceDeparture(Guid? serverId, LobbyPlayer? player, LobbySnapshot? snapshot)
    {
        if (serverId is null || player is null)
            return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(serverId.Value));
        if (snapshot is null)
            return;
        await Clients.Group(GroupName(serverId.Value)).SendAsync("PlayerLeft", player.SteamId);
        await Clients.Group(GroupName(serverId.Value)).SendAsync("LobbyUpdated", snapshot);
    }

    private bool TryGetSteamId(out long steamId)
    {
        steamId = 0;
        var claim = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return claim is not null && long.TryParse(claim, NumberStyles.None, CultureInfo.InvariantCulture, out steamId);
    }

    internal static string RoomGroupName(Guid roomId) => $"room:{roomId}";
    private static string GroupName(Guid serverId) => $"lobby:{serverId}";
}
