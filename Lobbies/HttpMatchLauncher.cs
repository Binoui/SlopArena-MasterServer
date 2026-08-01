// MasterServer/Lobbies/HttpMatchLauncher.cs
using System.Net.Http.Json;
using MasterServer.Data;
using Microsoft.EntityFrameworkCore;

namespace MasterServer.Lobbies;

/// <summary>
/// Default arena for PvP matches until host arena-select is wired. Lives on
/// the interface (not the concrete launcher) so the hub does not depend on
/// <see cref="HttpMatchLauncher"/> just to read the default (issue #35 review).
/// </summary>
public interface IMatchLauncher
{
    /// <summary>Default arena used when the host has not picked one.</summary>
    string DefaultArena { get; }

    /// <summary>
    /// Tell the game server for <paramref name="config"/> to start a match with
    /// the locked-in roster + entity IDs. Returns the UDP port the game server
    /// assigned to the match, or throws if the game server rejects/unreachable.
    /// </summary>
    Task<int> LaunchAsync(MatchStartedConfig config);
}

/// <summary>
/// Real <see cref="IMatchLauncher"/> (issue #35): POSTs the match-start command
/// to the game server over HTTP and returns the UDP port it assigns.
///
/// The game server runs a tiny HTTP control listener (System.Net.HttpListener on
/// the registered base port) exposing <c>POST /match/start</c>. The master server
/// looks up the game server's IP + port from the registration record, sends the
/// roster (steamId + locked-in characterClass + assigned entityId), and reads back
/// the match port. This keeps the game server stateless between matches (ADR-0008)
/// and matches the existing game→master result report (also HTTP).
/// </summary>
public sealed class HttpMatchLauncher : IMatchLauncher
{
    public const string DefaultArenaName = "split";
    public string DefaultArena => DefaultArenaName;

    private readonly AppDbContext _db;
    private readonly HttpClient _http;
    private readonly ILogger<HttpMatchLauncher> _logger;

    /// <param name="http">Managed <see cref="HttpClient"/> from <c>AddHttpClient</c>; reused across scopes to avoid socket exhaustion.</param>
    public HttpMatchLauncher(AppDbContext db, ILogger<HttpMatchLauncher> logger, HttpClient http)
    {
        _db = db;
        _logger = logger;
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task<int> LaunchAsync(MatchStartedConfig config)
    {
        var server = await _db.GameServers.FindAsync(config.ServerId);
        if (server is null)
            throw new InvalidOperationException(
                $"Game server {config.ServerId} is not registered — cannot start match.");

        var arena = string.IsNullOrEmpty(config.ArenaName) ? DefaultArena : config.ArenaName;
        var matchId = Guid.NewGuid().ToString();
        var body = new
        {
            matchId,
            arenaName = arena,
            players = config.Players
                .Select(p => new
                {
                    steamId = p.SteamId,
                    characterClass = p.CharacterSelection,
                    entityId = p.EntityId,
                })
                .ToArray(),
        };

        var url = $"http://{server.IpAddress}:{server.Port}/match/start";
        _logger.LogInformation(
            "Launching match {MatchId} on server {ServerId} ({Url}) with {Count} players",
            matchId, config.ServerId, url, config.Players.Count);

        using var response = await _http.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<MatchStartResponse>();
        if (result is null || result.Port <= 0)
            throw new InvalidOperationException(
                $"Game server {config.ServerId} did not return a valid match port.");

        _logger.LogInformation(
            "Match {MatchId} launched on port {Port}", matchId, result.Port);
        return result.Port;
    }

    private sealed class MatchStartResponse
    {
        public int Port { get; set; }
    }
}
