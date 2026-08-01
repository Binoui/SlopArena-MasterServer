// MasterServer/Hubs/LobbyHub.cs
using System.Security.Claims;
using MasterServer.Data;
using MasterServer.Lobbies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MasterServer.Hubs;

/// <summary>
/// SignalR hub managing per-game-server lobby state (ADR-0004, issue #32).
/// One lobby per game server, identified by <c>serverId</c>. The hub is a thin
/// adapter: <see cref="LobbyManager"/> owns the state, the hub performs the
/// SignalR group joins and broadcasts.
///
/// Server → client pushes: <c>PlayerJoined</c>, <c>PlayerLeft</c>,
/// <c>LobbyUpdated</c>, <c>MatchStarting</c>.
/// Client → server methods: <see cref="JoinLobby"/>, <see cref="LeaveLobby"/>,
/// <see cref="HostStart"/>.
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
        if (!TryGetSteamId(out var steamId))
            throw new HubException("Authenticated identity missing.");

        var user = await _db.Users.FindAsync(steamId);
        if (user is null)
            throw new HubException("Authenticated user not found.");

        var result = _lobbies.JoinLobby(serverId, Context.ConnectionId, steamId, user.Username);

        // If the connection was previously in a different lobby, announce the
        // departure to the old lobby's survivors and drop the old group membership.
        if (result.Departure is { } dep)
            await AnnounceDeparture(dep.ServerId, dep.Player, dep.Snapshot);

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(serverId));

        _logger.LogInformation("Lobby {ServerId}: {Name} ({SteamId}) joined", serverId, user.Username, steamId);

        await Clients.Group(GroupName(serverId)).SendAsync("PlayerJoined", result.Player);
        await Clients.Group(GroupName(serverId)).SendAsync("LobbyUpdated", result.Snapshot);
    }

    /// <summary>Leave the current lobby.</summary>
    public async Task LeaveLobby()
    {
        var (serverId, player, snapshot) = _lobbies.LeaveLobby(Context.ConnectionId);
        await AnnounceDeparture(serverId, player, snapshot);
    }

    /// <summary>Host-only: start the match for this lobby.</summary>
    public async Task HostStart()
    {
        var result = _lobbies.TryHostStart(Context.ConnectionId);
        if (!result.Success)
        {
            // HubException surfaces to the caller; the other lobby members are unaffected.
            throw new HubException(result.Error ?? "Host start rejected.");
        }

        var config = result.Config!;
        _logger.LogInformation("Lobby {ServerId}: host started the match", config.ServerId);

        await Clients.Group(GroupName(config.ServerId)).SendAsync("MatchStarting", config);
        await _launcher.LaunchAsync(config);
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
        _logger.LogInformation("Lobby {ServerId}: {Name} ({SteamId}) left", serverId, player.Name, player.SteamId);

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
