// MasterServer/Hubs/LobbyHub.cs
using System.Security.Claims;
using MasterServer.Data;
using MasterServer.Lobbies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MasterServer.Hubs;

/// <summary>
/// SignalR hub managing per-game-server lobby state (ADR-0004, see docs/adr/; issue #32).
/// One lobby per game server, identified by <c>serverId</c>. The hub is a thin
/// adapter: <see cref="LobbyManager"/> owns the state, the hub performs the
/// SignalR group joins and broadcasts.
///
/// Server → client pushes: <c>PlayerJoined</c>, <c>PlayerLeft</c>,
/// <c>LobbyUpdated</c>, <c>CharacterSelected</c>, <c>MatchStarting</c>,
/// <c>MatchStarted</c>.
/// Client → server methods: <see cref="JoinLobby"/>, <see cref="LeaveLobby"/>,
/// <see cref="HostStart"/>, <see cref="SelectCharacter"/>, <see cref="StartMatch"/>.
/// </summary>
[Authorize]
public sealed class LobbyHub : Hub
{
    private readonly LobbyManager _lobbies;
    private readonly AppDbContext _db;
    private readonly IMatchLauncher _launcher;
    private readonly ILogger<LobbyHub> _logger;

    public LobbyHub(LobbyManager lobbies, AppDbContext db, IMatchLauncher launcher, ILogger<LobbyHub> logger)
    {
        _lobbies = lobbies;
        _db = db;
        _launcher = launcher;
        _logger = logger;
    }

    /// <summary>Join the lobby for a specific game server. Requires JWT auth.</summary>
    public async Task JoinLobby(Guid serverId)
    {
        try
        {
            if (!TryGetSteamId(out var steamId))
                throw new HubException("Authenticated identity missing.");

            var user = await _db.Users.FindAsync(steamId);
            if (user is null)
                throw new HubException("Authenticated user not found.");

            var result = _lobbies.JoinLobby(serverId, Context.ConnectionId, steamId, user.Username);

            // Rejected joins (e.g. lobby at capacity, issue #6) surface as a
            // HubException before any group membership or broadcast happens.
            if (!result.Success)
                throw new HubException(result.Error ?? "Join rejected.");

            // If the connection was previously in a different lobby, announce the
            // departure to the old lobby's survivors and drop the old group membership.
            if (result.Departure is { } dep)
                await AnnounceDeparture(dep.ServerId, dep.Player, dep.Snapshot);

            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(serverId));

            _logger.LogInformation("Lobby {ServerId}: {Username} ({SteamId}) joined", serverId, user.Username, steamId);

            await Clients.Group(GroupName(serverId)).SendAsync("PlayerJoined", result.Player!);
            await Clients.Group(GroupName(serverId)).SendAsync("LobbyUpdated", result.Snapshot!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JoinLobby FAILED: server {ServerId} connection {ConnectionId}", serverId, Context.ConnectionId);
            throw;
        }
    }

    /// <summary>Leave the current lobby.</summary>
    public async Task LeaveLobby()
    {
        var (serverId, player, snapshot) = _lobbies.LeaveLobby(Context.ConnectionId);
        await AnnounceDeparture(serverId, player, snapshot);
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
        var (serverId, player, snapshot) = _lobbies.LeaveLobby(Context.ConnectionId);
        await AnnounceDeparture(serverId, player, snapshot);
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
