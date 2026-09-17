// MasterServer/Lobbies/HttpMatchLauncher.cs
using System.Net.Http.Json;
using System.Text.Json;
using MasterServer.Data;
using Microsoft.EntityFrameworkCore;
namespace MasterServer.Lobbies;

/// <summary>
/// Launches a selected PvP arena on the registered GameServer.
/// </summary>
public interface IMatchLauncher
{
    /// <summary>
    /// Starts a match and returns both its assigned UDP port and the opaque
    /// authoritative content map emitted by the GameServer.
    /// </summary>
    Task<MatchLaunchResult> LaunchAsync(MatchStartedConfig config);
}

/// <summary>
/// Real <see cref="IMatchLauncher"/> (issue #35): POSTs the match-start command
/// to the game server over HTTP and returns its assigned UDP port plus the
/// opaque authoritative content map.
///
/// The game server runs a tiny HTTP control listener (System.Net.HttpListener on
/// the registered base port) exposing <c>POST /match/start</c>. The master server
/// looks up the game server's IP + port from the registration record, sends the
/// roster (steamId + locked-in character + assigned entityId), and forwards the
/// response's <c>content</c> JSON unchanged. This keeps the game server stateless
/// between matches (ADR-0008), and matches the existing game→master result report.
/// </summary>
public sealed class HttpMatchLauncher : IMatchLauncher
{
    private readonly AppDbContext _db;
    private readonly HttpClient _http;
    private readonly ILogger<HttpMatchLauncher> _logger;
    private readonly int _maxPlayersPerLobby;

    /// <param name="http">Managed <see cref="HttpClient"/> from <c>AddHttpClient</c>; reused across scopes to avoid socket exhaustion.</param>
    /// <param name="options">Lobby capacity options (issue #6); defaults to 4 per lobby.</param>
    public HttpMatchLauncher(AppDbContext db, ILogger<HttpMatchLauncher> logger, HttpClient http, LobbyOptions? options = null)
    {
        _db = db;
        _logger = logger;
        _http = http;
        _maxPlayersPerLobby = LobbyOptions.ResolveMax(options);
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task<MatchLaunchResult> LaunchAsync(MatchStartedConfig config)
    {
        var server = await _db.GameServers.FindAsync(config.ServerId);
        if (server is null)
            throw new InvalidOperationException(
                $"Game server {config.ServerId} is not registered — cannot start match.");

        var arena = config.ArenaName;
        var matchGuid = Guid.NewGuid();
        var matchId = matchGuid.ToString();
        var players = config.Players;

        // The persisted Match row fits exactly 2–4 players — reject anything
        // else before creating the row or POSTing, so the roster and the row
        // can never diverge (issue #6).
        if (players.Count < LobbyLimits.MinPlayers || players.Count > _maxPlayersPerLobby)
        {
            throw new InvalidOperationException(
                $"Cannot launch match with {players.Count} players " +
                $"(expected {LobbyLimits.MinPlayers}–{_maxPlayersPerLobby}).");
        }

        // Create the Match row up front so the game server's later
        // POST /match/result finds it (issue #40). Rolled back on launch failure.
        _db.Matches.Add(new MasterServer.Data.Models.Match
        {
            Id = matchGuid,
            Player1SteamId = players[0].SteamId,
            Player2SteamId = players[1].SteamId,
            Player3SteamId = players.Count > 2 ? players[2].SteamId : null,
            Player4SteamId = players.Count > 3 ? players[3].SteamId : null,
            ServerRegion = server.Region,
            StartedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var body = new
        {
            matchId,
            arenaName = arena,
            players = config.Players
                .Select(p => new
                {
                    steamId = p.SteamId,
                    // Wire key stays `characterClass` — the game server's
                    // MatchStartRequestCodec reads it verbatim (issue #7).
                    characterClass = p.Character,
                    entityId = p.EntityId,
                })
                .ToArray(),
        };

        var url = $"http://{server.IpAddress}:{server.Port}/match/start";
        _logger.LogInformation(
            "Launching match {MatchId} on server {ServerId} ({Url}) with {Count} players",
            matchId, config.ServerId, url, config.Players.Count);

        try
        {
            using var response = await _http.PostAsJsonAsync(url, body);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<MatchStartResponse>();
            if (result is null || result.Port <= 0
                || result.Content.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                throw new InvalidOperationException(
                    $"Game server {config.ServerId} did not return a valid match port/content payload.");

            _logger.LogInformation(
                "Match {MatchId} launched on port {Port}", matchId, result.Port);
            return new MatchLaunchResult(result.Port, result.Content.Clone());
        }
        catch
        {
            // Roll the pre-created row back so a failed launch leaves no orphan.
            var row = await _db.Matches.FindAsync(matchGuid);
            if (row != null)
            {
                _db.Matches.Remove(row);
                await _db.SaveChangesAsync();
            }
            throw;
        }
    }

    private sealed class MatchStartResponse
    {
        public int Port { get; set; }
        public JsonElement Content { get; set; }
    }
}
