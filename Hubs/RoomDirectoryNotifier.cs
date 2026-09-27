using Microsoft.AspNetCore.SignalR;

namespace MasterServer.Hubs;

public sealed class RoomDirectoryNotifier(IHubContext<LobbyHub> hub)
{
    public Task NotifyChangedAsync(CancellationToken cancellationToken = default)
        => hub.Clients.All.SendAsync("RoomDirectoryChanged", cancellationToken: cancellationToken);
}
