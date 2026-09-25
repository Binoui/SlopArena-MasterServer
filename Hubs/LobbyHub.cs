// MasterServer/Hubs/LobbyHub.cs
using System.Globalization;
using System.Security.Claims;
using MasterServer.Chat;
using MasterServer.Configuration;
using MasterServer.Data;
using MasterServer.Lobbies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MasterServer.Hubs;

/// <summary>
/// Authenticated game-wide chat and per-GameServer lobby control. LobbyManager
/// owns waiting-roster and authoritative GameServer membership; ChatService owns
/// presence, message budgets, history, and recipient snapshots.
/// </summary>
[Authorize]
public sealed class LobbyHub : Hub
{
    private readonly LobbyManager _lobbies;
    private readonly AppDbContext _db;
    private readonly IMatchLauncher _launcher;
    private readonly ILogger<LobbyHub> _logger;
    private readonly ChatService _chat;
    private readonly MasterDeploymentOptions _deployment;

    public LobbyHub(LobbyManager lobbies, AppDbContext db, IMatchLauncher launcher,
        ILogger<LobbyHub> logger, ChatService chat, MasterDeploymentOptions deployment)
    {
        _lobbies = lobbies;
        _db = db;
        _launcher = launcher;
        _logger = logger;
        _chat = chat;
        _deployment = deployment;
    }

    public override async Task OnConnectedAsync()
    {
        if (!TryGetSteamId(out var playerId))
            throw new HubException("Authenticated identity missing.");

        ChatPresence? presence;
        await _chat.MembershipGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            var user = await _db.Users.FindAsync(playerId);
            if (user is null)
                throw new HubException("Authenticated user not found.");
            presence = _chat.Connect(playerId, user.Username, Context.ConnectionId);
        }
        finally
        {
            _chat.MembershipGate.Release();
        }

        try
        {
            await base.OnConnectedAsync();
            if (presence is not null)
                await Clients.All.SendAsync("ChatPresenceChanged", presence);
        }
        catch
        {
            _chat.Disconnect(Context.ConnectionId);
            throw;
        }
    }

    public ChatSnapshot GetChatState() => _chat.GetSnapshot(Context.ConnectionId);

    public ChatPlayer[] GetOnlinePlayers() => _chat.GetOnlinePlayers();

    public Task<ChatMessage> SendGlobal(string text)
        => Deliver(_chat.SendGlobal(Context.ConnectionId, text));

    public Task<ChatMessage> SendServer(Guid serverId, string text)
        => Deliver(_chat.SendServer(Context.ConnectionId, serverId, text));

    public Task<ChatMessage> SendDirect(string playerId, string text)
        => Deliver(_chat.SendDirect(Context.ConnectionId, playerId, text));

    private async Task<ChatMessage> Deliver(ChatDelivery delivery)
    {
        await Clients.Clients(delivery.ConnectionIds)
            .SendAsync("ChatMessage", delivery.Message, Context.ConnectionAborted);
        return delivery.Message;
    }

    /// <summary>
    /// Explicitly enters a GameServer's waiting room. A full waiting roster
    /// still admits authoritative Server Chat membership; the invocation then
    /// fails with <c>lobby_full</c> after the caller receives ChatServerChanged.
    /// </summary>
    public async Task JoinLobby(Guid serverId)
    {
        if (!TryGetSteamId(out var playerId))
            throw new HubException("Authenticated identity missing.");

        JoinLobbyResult result;
        ServerChatState serverChat;
        await _chat.MembershipGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            if (!await IsFreshServerAsync(serverId))
                throw new HubException("server_unavailable");
            if (_chat.HasOtherLobbyConnection(playerId, Context.ConnectionId))
                throw new HubException("already_joined");

            var user = await _db.Users.FindAsync(playerId);
            if (user is null)
                throw new HubException("Authenticated user not found.");

            lock (_chat.MembershipSync)
            {
                result = _lobbies.JoinLobby(serverId, Context.ConnectionId, playerId, user.Username);
                if (!result.ServerAdmitted)
                    throw new HubException(result.Error ?? "Join rejected.");
                serverChat = _chat.GetServerState(Context.ConnectionId);
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
            _logger.LogInformation("Server {ServerId}: player {PlayerId} admitted to chat; waiting roster full", serverId, playerId);
        }

        await Clients.Caller.SendAsync("ChatServerChanged", serverChat);
        if (!result.Success)
            throw new HubException("lobby_full");
    }

    /// <summary>
    /// Revalidates and restores a remembered active-match GameServer
    /// membership after reconnect. This never enters the waiting roster.
    /// </summary>
    public async Task ResumeServer(Guid serverId)
    {
        if (!TryGetSteamId(out var playerId))
            throw new HubException("Authenticated identity missing.");

        ServerChatState serverChat;
        await _chat.MembershipGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            if (!await IsFreshServerAsync(serverId))
                throw new HubException("server_unavailable");
            if (_chat.HasOtherLobbyConnection(playerId, Context.ConnectionId))
                throw new HubException("already_joined");

            var user = await _db.Users.FindAsync(playerId);
            if (user is null)
                throw new HubException("Authenticated user not found.");

            lock (_chat.MembershipSync)
            {
                if (!_lobbies.ResumeServer(serverId, Context.ConnectionId, playerId, user.Username, out var error))
                    throw new HubException(error ?? "not_admitted");
                serverChat = _chat.GetServerState(Context.ConnectionId);
            }
        }
        finally
        {
            _chat.MembershipGate.Release();
        }

        await Clients.Caller.SendAsync("ChatServerChanged", serverChat);
    }

    /// <summary>Explicitly leaves the current GameServer and its chat channel.</summary>
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
        await Clients.Caller.SendAsync("ChatServerChanged", new ServerChatState(null, []));
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
            config = config with { MatchPort = launch.MatchPort, Content = launch.Content };

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
            lock (_chat.MembershipSync)
            {
                presence = _chat.Disconnect(Context.ConnectionId);
                departure = _lobbies.DisconnectConnection(Context.ConnectionId);
            }
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
            server => server.Id == serverId && server.LastHeartbeat >= cutoff,
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

    private static string GroupName(Guid serverId) => $"lobby:{serverId}";
}
