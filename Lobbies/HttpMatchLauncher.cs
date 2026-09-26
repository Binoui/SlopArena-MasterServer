// MasterServer/Lobbies/HttpMatchLauncher.cs
using System.Net.Http.Json;
using System.Net.Http.Headers;
using MasterServer.Configuration;
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
/// The listener exposes <c>POST /match/start</c>. Development mode uses the
/// registered address; VPS mode uses the separately provisioned private control
/// URL and bearer key. The roster is sent and the authoritative content map is
/// forwarded unchanged (ADR-0008).
/// </summary>
public sealed class HttpMatchLauncher : IMatchLauncher
{
    private readonly AppDbContext _db;
    private readonly HttpClient _http;
    private readonly ILogger<HttpMatchLauncher> _logger;
    private readonly MasterDeploymentOptions _deployment;
    private readonly int _maxPlayersPerLobby;

    /// <param name="http">Managed <see cref="HttpClient"/> from <c>AddHttpClient</c>; reused across scopes to avoid socket exhaustion.</param>
    /// <param name="options">Lobby capacity options (issue #6); defaults to 4 per lobby.</param>
    public HttpMatchLauncher(
        AppDbContext db,
        ILogger<HttpMatchLauncher> logger,
        HttpClient http,
        MasterDeploymentOptions deployment,
        LobbyOptions? options = null)
    {
        _db = db;
        _logger = logger;
        _http = http;
        _deployment = deployment;
        _maxPlayersPerLobby = LobbyOptions.ResolveMax(options);
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task<MatchLaunchResult> LaunchAsync(MatchStartedConfig config)
    {
        if (_deployment.IsVps && config.ServerId != _deployment.ApprovedHostId)
            throw new InvalidOperationException("Only the provisioned GameServer can launch VPS matches.");
        var server = await _db.GameServers.FindAsync(config.ServerId);
        if (server is null)
            throw new InvalidOperationException(
                $"Game server {config.ServerId} is not registered — cannot start match.");
        if (_deployment.IsVps)
        {
            if (server.ProtocolVersion != 2 || string.IsNullOrEmpty(server.SteamId) ||
                (server.InstanceId is null || server.InstanceId == Guid.Empty) ||
                server.CatalogHash is not { Length: 64 } ||
                server.LastHeartbeat < DateTime.UtcNow.AddSeconds(-15))
                throw new InvalidOperationException("Steam GameHost is not currently registered and ready.");
        }
        var instanceAtStart = server.InstanceId;
        var steamIdAtStart = server.SteamId;
        var catalogAtStart = server.CatalogHash;

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
        if (_deployment.IsVps)
        {
            var identities = players.Select(p => p.SteamId).ToArray();
            if (identities.Any(id => id <= 0) || identities.Distinct().Count() != identities.Length ||
                players.Select(p => p.EntityId).Distinct().Count() != players.Count ||
                players.Any(p => p.EntityId <= 0 || string.IsNullOrEmpty(p.Character)))
                throw new InvalidOperationException("Match roster is not a unique assigned Steam roster.");
            int verified = await _db.Users.CountAsync(user => identities.Contains(user.SteamId) &&
                user.AuthProvider == "steam");
            if (verified != identities.Length)
                throw new InvalidOperationException("Every roster member must have a verified Steam identity.");
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
            ServerId = server.Id,
            ServerRegion = server.Region,
            StartedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var admissionDeadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var body = new Dictionary<string, object?>
        {
            ["matchId"] = matchId,
            ["arenaName"] = arena,
            ["players"] = config.Players.Select(p => new
                {
                    steamId = p.SteamId,
                    // Wire key stays `characterClass` — the game server's
                    // MatchStartRequestCodec reads it verbatim (issue #7).
                    characterClass = p.Character,
                    entityId = p.EntityId,
                })
                .ToArray(),
        };
        if (_deployment.IsVps)
        {
            body["protocolVersion"] = 2;
            body["virtualPort"] = 0;
            body["maxStocks"] = 3;
            body["catalogHash"] = catalogAtStart;
            body["admissionExpiresAtUtc"] = admissionDeadline;
        }

        var url = _deployment.IsVps
            ? _deployment.ControlUrl!
            : new Uri($"http://{server.IpAddress}:{server.Port}/match/start");
        _logger.LogInformation(
            "Launching match {MatchId} on server {ServerId} with {Count} players",
            matchId, config.ServerId, config.Players.Count);

        bool launchSent = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body)
            };
            if (_deployment.IsVps)
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _deployment.MatchControlKey!);
            launchSent = true;
            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<MatchStartResponse>();
            if (result is null || result.Content.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("GameHost did not return match content.");
            if (_deployment.IsVps)
            {
                await _db.Entry(server).ReloadAsync();
                if (result.MatchId != matchGuid || result.ServerSteamId != steamIdAtStart ||
                    server.SteamId != steamIdAtStart || server.InstanceId != instanceAtStart ||
                    server.CatalogHash != catalogAtStart || result.ContentHash != catalogAtStart ||
                    server.ProtocolVersion != 2 || server.LastHeartbeat < DateTime.UtcNow.AddSeconds(-15) ||
                    result.ProtocolVersion != 2 || result.VirtualPort != 0)
                    throw new InvalidOperationException("GameHost returned an incompatible or stale Steam match route.");
                var descriptor = new SteamMatchDescriptor("steam-p2p", result.ServerSteamId!,
                    matchGuid, 0, 2, result.ContentHash!, admissionDeadline);
                _logger.LogInformation("Steam match {MatchId} launched on host {ServerId}", matchId, server.Id);
                return new MatchLaunchResult(0, result.Content.Clone(), descriptor);
            }
            if (result.Port <= 0)
                throw new InvalidOperationException("Development GameServer did not return a match port.");
            return new MatchLaunchResult(result.Port, result.Content.Clone());
        }
        catch
        {
            if (_deployment.IsVps && launchSent)
            {
                try
                {
                    using var abort = new HttpRequestMessage(HttpMethod.Post,
                        new Uri(_deployment.ControlUrl!, "/match/abort"))
                    {
                        Content = JsonContent.Create(new { matchId })
                    };
                    abort.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", _deployment.MatchControlKey!);
                    using var aborted = await _http.SendAsync(abort);
                    if (!aborted.IsSuccessStatusCode)
                        _logger.LogWarning("GameHost did not acknowledge abort for match {MatchId}.", matchId);
                }
                catch (Exception)
                {
                    _logger.LogWarning("GameHost abort unavailable for match {MatchId}.", matchId);
                }
            }
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
        public Guid MatchId { get; set; }
        public string? ServerSteamId { get; set; }
        public int VirtualPort { get; set; }
        public int ProtocolVersion { get; set; }
        public string? ContentHash { get; set; }
        public JsonElement Content { get; set; }
    }
}
