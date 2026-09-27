using System.Globalization;
using MasterServer.Data;
using MasterServer.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MasterServer.Rooms;

public sealed class MatchCompletionCoordinator(
    RoomManager rooms,
    RoomDirectoryNotifier directory,
    IHubContext<LobbyHub> hub,
    ILogger<MatchCompletionCoordinator> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            _gate.Release();
        }
    }


    public Task<int> CancelUnavailableHostMatchesAsync(AppDbContext db, Guid serverId,
        Guid? expectedInstanceId, DateTime expectedHeartbeat, DateTime staleBefore,
        CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async () =>
        {
            var current = await db.GameServers.AsNoTracking()
                .SingleOrDefaultAsync(server => server.Id == serverId, cancellationToken);
            if (current is null || current.InstanceId != expectedInstanceId ||
                current.LastHeartbeat != expectedHeartbeat || current.LastHeartbeat > staleBefore)
                return 0;

            return await CancelOpenMatchesUnderGateAsync(db, serverId, "host_unavailable");
        }, cancellationToken);

    public async Task<int> CancelOpenMatchesUnderGateAsync(AppDbContext db, Guid serverId,
        string reason, Guid? matchId = null)
    {
        var open = await db.Matches.Where(match => match.ServerId == serverId &&
            match.EndedAt == null && match.CanceledAt == null &&
            (matchId == null || match.Id == matchId)).ToListAsync();
        if (open.Count == 0)
            return 0;

        foreach (var match in open)
        {
            match.CanceledAt = DateTime.UtcNow;
            match.CancelReason = reason;
            match.WinnerSteamId = null;
        }
        var server = await db.GameServers.FindAsync(serverId);
        if (server is not null)
            server.CurrentMatches = Math.Max(0, server.CurrentMatches - open.Count);
        await db.SaveChangesAsync();

        foreach (var match in open)
        {
            await PublishRoomTerminalUpdateAsync(match.RoomId, match.Id);
            var roster = new[] { match.Player1SteamId, match.Player2SteamId }
                .Concat(new[] { match.Player3SteamId, match.Player4SteamId }
                    .Where(id => id.HasValue).Select(id => id!.Value))
                .Distinct()
                .Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray();
            try
            {
                await hub.Clients.Users(roster).SendAsync("MatchAborted",
                    new { matchId = match.Id, reason });
            }
            catch (Exception)
            {
                logger.LogWarning("Could not notify match {MatchId} cancellation.", match.Id);
            }
        }
        return open.Count;
    }

    public async Task PublishRoomTerminalUpdateAsync(Guid? roomId, Guid matchId)
    {
        if (roomId is not { } id)
            return;

        await rooms.MatchLifecycleGate.WaitAsync();
        try
        {
            var snapshot = rooms.CompleteMatch(id, matchId);
            if (snapshot is null)
                return;

            try
            {
                await hub.Clients.Group(LobbyHub.RoomGroupName(id)).SendAsync("RoomUpdated", snapshot);
                await directory.NotifyChangedAsync();
            }
            catch (Exception)
            {
                logger.LogWarning("Could not notify Room {RoomId} terminal Match {MatchId}.", id, matchId);
            }
        }
        finally
        {
            rooms.MatchLifecycleGate.Release();
        }
    }
}
