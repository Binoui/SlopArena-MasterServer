using MasterServer.Chat;
using MasterServer.Configuration;
using MasterServer.Data;
using MasterServer.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace MasterServer.Rooms;

public sealed class RoomCleanupService(
    RoomManager rooms,
    ChatService chat,
    IHubContext<LobbyHub> hub,
    RoomDirectoryNotifier directory,
    MatchCompletionCoordinator completion,
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    MasterDeploymentOptions deployment,
    TimeProvider clock,
    ILogger<RoomCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan HostStaleAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await chat.MembershipGate.WaitAsync(stoppingToken);
            try
            {
                RoomMutationResult[] changes;
                lock (chat.MembershipSync)
                {
                    changes = rooms.SweepExpired();
                    foreach (var change in changes)
                        if (change.Deleted)
                            chat.ForgetRoom(change.RoomId);
                }

                foreach (var change in changes)
                {
                    try
                    {
                        if (change.ConnectionIds.Count > 0)
                        {
                            await hub.Clients.Clients(change.ConnectionIds)
                                .SendAsync("ChatServerChanged", new ServerChatState(null, []), stoppingToken);
                            await hub.Clients.Clients(change.ConnectionIds)
                                .SendAsync("RoomMembershipRevoked", change.RoomId, stoppingToken);
                            foreach (var connectionId in change.ConnectionIds)
                                await hub.Groups.RemoveFromGroupAsync(connectionId, GroupName(change.RoomId), stoppingToken);
                        }

                        if (change.Deleted)
                            await hub.Clients.Group(GroupName(change.RoomId))
                                .SendAsync("RoomDeleted", change.RoomId, stoppingToken);
                        else if (change.Snapshot is not null)
                            await hub.Clients.Group(GroupName(change.RoomId))
                                .SendAsync("RoomUpdated", change.Snapshot, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Room {RoomId} expired but its notification failed", change.RoomId);
                    }
                }

                if (changes.Length > 0)
                {
                    try
                    {
                        await directory.NotifyChangedAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Room directory expiry notification failed");
                    }
                }
            }
            finally
            {
                chat.MembershipGate.Release();
            }

            await ReconcileUnavailableHostsAsync(stoppingToken);
        }
    }

    public async Task ReconcileUnavailableHostsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var staleBefore = clock.GetUtcNow().UtcDateTime - HostStaleAfter;
        var hosts = await db.GameServers.AsNoTracking()
            .Where(server => server.LastHeartbeat <= staleBefore &&
                (!deployment.IsVps || server.Id == deployment.ApprovedHostId) &&
                db.Matches.Any(match => match.ServerId == server.Id &&
                    match.EndedAt == null && match.CanceledAt == null))
            .Select(server => new HostCandidate(
                server.Id, server.InstanceId, server.LastHeartbeat, server.IpAddress, server.Port))
            .ToArrayAsync(cancellationToken);
        if (hosts.Length == 0)
            return;

        using var client = httpClientFactory.CreateClient("GameHostHealth");
        foreach (var host in hosts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsHealthyAsync(client, host, cancellationToken))
                continue;

            var canceled = await completion.CancelUnavailableHostMatchesAsync(
                db, host.ServerId, host.InstanceId, host.LastHeartbeat, staleBefore, cancellationToken);
            if (canceled > 0)
                logger.LogWarning("Unavailable GameHost {ServerId}: canceled {MatchCount} open Matches",
                    host.ServerId, canceled);
        }
    }

    private async Task<bool> IsHealthyAsync(HttpClient client, HostCandidate host,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HealthTimeout);
        var healthUri = deployment.IsVps
            ? new Uri(deployment.ControlUrl!, "/health")
            : new UriBuilder(Uri.UriSchemeHttp, host.IpAddress, host.Port) { Path = "/health" }.Uri;
        try
        {
            using var response = await client.GetAsync(
                healthUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "GameHost {ServerId} health probe failed", host.ServerId);
            return false;
        }
    }

    private sealed record HostCandidate(
        Guid ServerId, Guid? InstanceId, DateTime LastHeartbeat, string IpAddress, int Port);

    private static string GroupName(Guid roomId) => $"room:{roomId}";
}
