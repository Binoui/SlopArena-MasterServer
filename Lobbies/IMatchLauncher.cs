// MasterServer/Lobbies/IMatchLauncher.cs
namespace MasterServer.Lobbies;

/// <summary>
/// Bridge from the char-select host's <c>StartMatch</c> to the game server's
/// match start (ADR-0008, issue #34). The real implementation (HTTP/SignalR
/// backchannel to the game server) lands with ticket #35 ("Match start with
/// character classes"). This stub lets the lobby flow ship and be exercised
/// end-to-end now.
/// </summary>
public interface IMatchLauncher
{
    Task LaunchAsync(MatchStartedConfig config);
}

internal sealed class StubMatchLauncher(ILogger<StubMatchLauncher> logger) : IMatchLauncher
{
    public Task LaunchAsync(MatchStartedConfig config)
    {
        logger.LogInformation(
            "STUB match start for server {ServerId} with {Count} players (game-server backchannel not yet wired — ticket #35)",
            config.ServerId, config.Players.Count);
        return Task.CompletedTask;
    }
}
