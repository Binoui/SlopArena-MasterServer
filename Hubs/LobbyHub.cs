// MasterServer/Hubs/LobbyHub.cs
using System.Security.Claims;
using MasterServer.Chat;
using MasterServer.Data;
using MasterServer.Lobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MasterServer.Hubs;

/// <summary>
/// Authenticated game-wide chat and per-GameServer lobby control.
/// LobbyManager owns admission; ChatService owns presence, message budgets,
/// immutable history, and recipient snapshots. Network sends happen outside
/// the state locks. Chat never participates in gameplay simulation.
/// </summary>
[Authorize]
public sealed class LobbyHub : Hub
{
    private readonly LobbyManager _lobbies;
    private readonly AppDbContext _db;
    private readonly IMatchLauncher _launcher;
    private readonly ILogger<LobbyHub> _logger;
    private readonly ChatService _chat;

    public LobbyHub(LobbyManager lobbies, AppDbContext db, IMatchLauncher launcher,
        ILogger<LobbyHub> logger, ChatService chat)
    {
        _lobbies = lobbies;
        _db = db;
        _launcher = launcher;
        _logger = logger;
        _chat = chat;
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
            // SignalR does not promise OnDisconnected after a failed connect.
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
        // Acceptance is not a recipient delivery/read receipt.
        return delivery.Message;
    }

    /// <summary>Join the lobby for a specific game server. Requires JWT auth.</summary>
    public async Task JoinLobby(Guid serverId)
    {
        if (!TryGetSteamId(out var playerId))
            throw new HubException("Authenticated identity missing.");

        JoinLobbyResult result;
        ServerChatState serverChat;
        await _chat.MembershipGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-15);
            if (!await _db.GameServers.AnyAsync(
                    server => server.Id == serverId && server.LastHeartbeat >= cutoff,
                    Context.ConnectionAborted))
                throw new HubException("server_unavailable");
            if (_chat.HasOtherLobbyConnection(playerId, Context.ConnectionId))
                throw new HubException("already_joined");

            var user = await _db.Users.FindAsync(playerId);
            if (user is null)
                throw new HubException("Authenticated user not found.");

            lock (_chat.MembershipSync)
            {
                result = _lobbies.JoinLobby(serverId, Context.ConnectionId, playerId, user.Username);
                if (!result.Success)
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

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(serverId));
        _logger.LogInformation("Lobby {ServerId}: player {PlayerId} joined", serverId, playerId);
        await Clients.Group(GroupName(serverId)).SendAsync("PlayerJoined", result.Player!);
        await Clients.Group(GroupName(serverId)).SendAsync("LobbyUpdated", result.Snapshot!);
        await Clients.Caller.SendAsync("ChatServerChanged", serverChat);
    }

    /// <summary>Leave the current lobby.</summary>
    public async Task LeaveLobby()
    {
        LeaveLobbyResult departure;
        await _chat.MembershipGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            lock (_chat.MembershipSync)
                departure = _lobbies.LeaveLobby(Context.ConnectionId);
        }
        finally
        {
            _chat.MembershipGate.Release();
        }
        await AnnounceDeparture(departure.ServerId, departure.Player, departure.Snapshot);
        await Clients.Caller.SendAsync("ChatServerChanged", new ServerChatState(null, []));
    }

    /// <summary>
    /// Host-only: transitions the lobby to character select (ADR-0008, see
    /// docs/adr/; issue #34). Broadcasts <c>MatchStarting</c> so all clients switch to the char
    /// select screen. Does NOT launch the game server — that happens in
    /// <see cref="StartMatch"/> once all players lock in.
    /// </summary>
    public async Task HostStart()
    {
        var result = _lobbies.TryHostStart(Context.ConnectionId);
        if (!result.Success)
        {
            // HubException surfaces to the caller; the other lobby members are unaffected.
            throw new HubException(result.Error ?? "Host start rejected.");
        }

        var config = result.Config!;
        _logger.LogInformation("Lobby {ServerId}: host started char select", config.ServerId);

        await Clients.Group(GroupName(config.ServerId)).SendAsync("MatchStarting", config);
    }

    /// <summary>
    /// Lock in a character selection (issue #34). Broadcasts
    /// <c>CharacterSelected</c> (the updated player) and <c>LobbyUpdated</c>
    /// (full snapshot) to all lobby members. A player may call this again to
    /// change their pick before the match starts.
    /// </summary>
    public async Task SelectCharacter(string character)
    {
        var result = _lobbies.SelectCharacter(Context.ConnectionId, character);
        if (!result.Success)
            throw new HubException(result.Error ?? "Character selection rejected.");

        _logger.LogInformation("Lobby: {Username} locked in {Character}", result.Player!.Username, character);

        await Clients.Group(GroupName(result.Snapshot!.ServerId)).SendAsync("CharacterSelected", result.Player);
        await Clients.Group(GroupName(result.Snapshot.ServerId)).SendAsync("LobbyUpdated", result.Snapshot);
    }

    /// <summary>
    /// Host-only: starts the actual match from char select (issue #34/#35).
    /// Requires all players locked in (minimum 2). Launches the game server
    /// (HTTP match-start with the roster + entity IDs + characters),
    /// then broadcasts <c>MatchStarted</c> carrying the assigned UDP port +
    /// arena so every client can connect and load the right scene.
    /// </summary>
    public async Task StartMatch()
    {
        var result = _lobbies.TryStartMatch(Context.ConnectionId);
        if (!result.Success)
            throw new HubException(result.Error ?? "Start match rejected.");

        var config = result.Config! with { ArenaName = _launcher.DefaultArena };
        _logger.LogInformation("Lobby {ServerId}: host started the match ({Count} players)", config.ServerId, config.Players.Count);

        // Launch first: the game server assigns the UDP match port, which the
        // broadcast must carry so clients know where to connect (issue #35).
        var matchPort = await _launcher.LaunchAsync(config);
        config = config with { MatchPort = matchPort };

        await Clients.Group(GroupName(config.ServerId)).SendAsync("MatchStarted", config);
    }

    /// <summary>Cleanup on disconnect: drop the player and announce to survivors.</summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        LeaveLobbyResult departure;
        ChatPresence? presence;
        // ConnectionAborted is already cancelled here; cleanup must still run.
        await _chat.MembershipGate.WaitAsync();
        try
        {
            lock (_chat.MembershipSync)
            {
                presence = _chat.Disconnect(Context.ConnectionId);
                departure = _lobbies.LeaveLobby(Context.ConnectionId);
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

    private async Task AnnounceDeparture(Guid? serverId, LobbyPlayer? player, LobbySnapshot? snapshot)
    {
        if (serverId is null || player is null)
            return; // was not in a lobby

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(serverId.Value));
        _logger.LogInformation("Lobby {ServerId}: {Username} ({SteamId}) left", serverId, player.Username, player.SteamId);

        // No snapshot means the lobby is now empty — nothing to broadcast.
        if (snapshot is null)
            return;

        await Clients.Group(GroupName(serverId.Value)).SendAsync("PlayerLeft", player.SteamId);
        await Clients.Group(GroupName(serverId.Value)).SendAsync("LobbyUpdated", snapshot);
    }

    private bool TryGetSteamId(out long steamId)
    {
        steamId = 0;
        var claim = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return claim is not null && long.TryParse(claim, out steamId);
    }

    private static string GroupName(Guid serverId) => $"lobby:{serverId}";
}
