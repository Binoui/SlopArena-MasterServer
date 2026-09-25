using System.Net;
using System.Security.Cryptography;
using System.Globalization;
using System.Threading.RateLimiting;
using MasterServer.Chat;
using MasterServer.Configuration;
using Microsoft.EntityFrameworkCore;
using MasterServer.Data;
using MasterServer.DTOs;
using MasterServer.Hubs;
using MasterServer.Lobbies;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(sp => MasterDeploymentOptions.Load(sp.GetRequiredService<IConfiguration>()));
builder.Services.Configure<HostOptions>(options =>
    options.ShutdownTimeout = TimeSpan.FromSeconds(15));
builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<MasterDeploymentOptions>((options, deployment) =>
{
    options.ForwardLimit = 1;
    options.KnownProxies.Clear();
    options.KnownNetworks.Clear();
    if (deployment.TrustedProxyAddress is { } proxyAddress)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownProxies.Add(proxyAddress);
    }
    else
    {
        options.ForwardedHeaders = ForwardedHeaders.None;
    }
});

// Service registration expands during subsequent tasks
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSignalR(options => options.AddFilter<ChatControlRateFilter>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ChatService>();
// JWT authentication — guest/dev auth (issue #30)
var jwtSecret = builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret))
    throw new InvalidOperationException("Jwt:Secret is not configured. Set it in appsettings, .env, or environment variable.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "SlopArena.Master";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "SlopArena.Client";
var jwtExpiryHours = builder.Configuration.GetValue<int>("Jwt:ExpiryHours", 24);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // SignalR/WebSocket connections cannot set Authorization headers from
        // browsers, so the client passes the JWT as a "access_token" query
        // string parameter. Extract and validate it here.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    path.StartsWithSegments("/lobby"))
                {
                    context.Token = accessToken!;
                }
                return Task.CompletedTask;
            }
        };

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.Zero
        };
    });

// ── Lobby services (issue #32) ──
// Lobby capacity (issue #6): max players per lobby from configuration,
// defaulting to 4. Registered once so the manager and the launcher share it.
builder.Services.AddSingleton(_ =>
{
    var max = builder.Configuration.GetValue("Lobby:MaxPlayersPerLobby", LobbyLimits.MaxPlayers);
    // The persisted Match row holds exactly Player1–Player4, so the config can
    // lower the default but never raise it — else the roster/row divergence
    // this issue exists to prevent would come back (issue #6).
    if (max is < LobbyLimits.MinPlayers or > LobbyLimits.MaxPlayers)
        throw new InvalidOperationException(
            $"Lobby:MaxPlayersPerLobby must be between {LobbyLimits.MinPlayers} and " +
            $"{LobbyLimits.MaxPlayers} (the persisted Match capacity).");
    return new LobbyOptions(max);
});
builder.Services.AddSingleton<LobbyManager>();
// Scoped: HttpMatchLauncher consumes the scoped AppDbContext to look up the
// game server's IP + port before POSTing the match-start command (issue #35).
// AddHttpClient gives the launcher a managed, pooled HttpClient (avoids socket
// exhaustion from per-scope `new HttpClient()` — issue #35 review).
builder.Services.AddHttpClient<IMatchLauncher, HttpMatchLauncher>();
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    var rateLimitMax = builder.Configuration.GetValue("RateLimit:MaxRequestsPerWindow", 10);
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var path = context.Request.Path;
        // Long-poll POSTs carry both chat and lobby invocations. Enforce their
        // separate per-identity budgets at the hub, not on the transport POST.
        var isHubTransport = path == "/lobby" || path == "/lobby/";
        var isProtectedWrite = HttpMethods.IsPost(context.Request.Method) ||
            (HttpMethods.IsPut(context.Request.Method) && path.StartsWithSegments("/auth/name"));
        if (isHubTransport || !isProtectedWrite)
            return RateLimitPartition.GetNoLimiter("unlimited");

        // A party signing in together must not spend the heartbeat/admission
        // budget on guest creation, name setup, or connection negotiation.
        var budget = path.StartsWithSegments("/auth/guest") ? "guest"
            : path.StartsWithSegments("/auth/name") ? "name"
            : path.StartsWithSegments("/auth/refresh") ? "refresh"
            : path.StartsWithSegments("/lobby/negotiate") ? "negotiate"
            : "control";
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{budget}:{ip}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimitMax,
                Window = TimeSpan.FromSeconds(10),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

var app = builder.Build();
// Validate the selected deployment profile before any listener accepts requests.
_ = app.Services.GetRequiredService<MasterDeploymentOptions>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();
app.UseForwardedHeaders();


app.MapGet("/health", () => new { status = "ok", version = "0.1.0" });
app.MapGet("/ready", async (AppDbContext db, CancellationToken requestAborted) =>
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
    timeout.CancelAfter(TimeSpan.FromSeconds(3));
    try
    {
        if (!await db.Database.CanConnectAsync(timeout.Token))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if ((await db.Database.GetPendingMigrationsAsync(timeout.Token)).Any())
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

// Includes guest creation and hub negotiation. Native limiter partitions expire
// when idle; chat/control quotas live with the authenticated guest instead.
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ── Helper: extract and validate Bearer token ──
static string? ExtractBearerToken(HttpContext httpContext, ILogger logger)
{
    var authHeader = httpContext.Request.Headers["Authorization"].ToString();
    if (string.IsNullOrWhiteSpace(authHeader))
    {
        logger.LogWarning("Missing Authorization header");
        return null;
    }


    // Case-insensitive "Bearer " prefix check with trim
    const string prefix = "Bearer ";
    if (authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        var token = authHeader[prefix.Length..].Trim();
        if (string.IsNullOrEmpty(token))
        {
            logger.LogWarning("Empty token after Bearer prefix");
            return null;
        }
        return token;
    }

    logger.LogWarning("Authorization header missing Bearer prefix");
    return null;
}

// ── Helper: timing-safe string comparison ──
static bool TimingSafeEquals(string a, string b)
{
    if (a.Length != b.Length) return false;
    return CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(a),
        System.Text.Encoding.UTF8.GetBytes(b));
}

static string GenerateServerApiToken() =>
    Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

// ── Helper: validate server address (IP literal or DNS hostname) ──
static bool IsValidIpAddress(string ip)
{
    // IPv4 literal, or a DNS hostname (domain allowed — ADR-0009: official
    // servers behind NAT register with a public domain like
    // sloparena.barakaslurp.fr; clients and the match launcher both resolve it).
    // IPv6 not yet supported.
    return Uri.CheckHostName(ip) is UriHostNameType.IPv4 or UriHostNameType.Dns;
}

// ── Helper: validate port range ──
static bool IsValidPort(int port) => port > 0 && port <= 65535;

// ── Guest auth endpoint (issue #30) ──
app.MapPost("/auth/guest", async (AppDbContext db) =>
{
    // Eight base-36 digits give guests short tags without truncating identity.
    // The database primary key remains the final uniqueness constraint.
    long steamId;
    do
    {
        steamId = Random.Shared.NextInt64(1, ChatText.GuestIdLimit);
    } while (await db.Users.AnyAsync(user => user.SteamId == steamId));

    var user = new MasterServer.Data.Models.User
    {
        SteamId = steamId,
        Username = $"Guest-{Random.Shared.Next(10000, 99999)}",
        Mmr = 1000,
        CreatedAt = DateTime.UtcNow,
        LastLogin = DateTime.UtcNow
    };

    db.Users.Add(user);
    await db.SaveChangesAsync();

    logger.LogInformation("Guest auth: created user {SteamId} ({Username})", steamId, user.Username);

    return Results.Ok(GenerateGuestAuth(steamId));
});

// ── Authed endpoint: get current user info (issue #30) ──
app.MapGet("/auth/me", async (HttpContext httpContext, AppDbContext db) =>
{
    var steamIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (steamIdClaim == null || !long.TryParse(steamIdClaim, out var steamId))
        return Results.Unauthorized();

    var user = await db.Users.FindAsync(steamId);
    if (user == null)
        return Results.NotFound(new { error = "User not found" });

    return Results.Ok(new GuestUserInfo(user.SteamId, user.Username, user.Mmr,
        ChatText.Player(user.SteamId, user.Username).SessionTag));
}).RequireAuthorization();

// The client applies its chosen name before connecting to the hub. This same
// operation handles later renames; it never creates another guest identity.
app.MapPut("/auth/name", async (
    SetDisplayNameRequest request,
    HttpContext context,
    AppDbContext db,
    ChatService chat,
    IHubContext<LobbyHub> hub) =>
{
    if (!long.TryParse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out var playerId))
        return Results.Unauthorized();

    string name;
    try
    {
        name = ChatText.DisplayName(request.DisplayName);
    }
    catch (HubException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    ChatPlayer profile;
    bool notify;
    await chat.MembershipGate.WaitAsync(context.RequestAborted);
    try
    {
        if (chat.HasLobbyMembership(playerId))
            return Results.Conflict(new { error = "joined_server" });

        var user = await db.Users.FindAsync(playerId);
        if (user is null)
            return Results.NotFound(new { error = "User not found" });

        var changed = user.Username != name;
        if (changed)
        {
            user.Username = name;
            await db.SaveChangesAsync(context.RequestAborted);
        }
        profile = chat.UpdateDisplayName(playerId, name);
        notify = changed && chat.IsOnline(playerId);
    }
    finally
    {
        chat.MembershipGate.Release();
    }

    if (notify)
        await hub.Clients.All.SendAsync("ChatPresenceChanged", new ChatPresence(profile, true),
            context.RequestAborted);
    return Results.Ok(profile);
}).RequireAuthorization();

app.MapPost("/auth/refresh", async (HttpContext context, AppDbContext db) =>
{
    if (!long.TryParse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out var playerId))
        return Results.Unauthorized();
    if (await db.Users.FindAsync(playerId) is null)
        return Results.NotFound(new { error = "User not found" });
    return Results.Ok(GenerateGuestAuth(playerId));
}).RequireAuthorization();

// Token renewal preserves the player identity. Expired tokens cannot silently
// create another guest; the client must renew while its credential is valid.
GuestAuthResponse GenerateGuestAuth(long steamId)
{
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, steamId.ToString(CultureInfo.InvariantCulture)),
        new Claim("steam_id", steamId.ToString(CultureInfo.InvariantCulture)),
        new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
    };

    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
    var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    var expiresAt = DateTimeOffset.FromUnixTimeSeconds(
        DateTimeOffset.UtcNow.AddHours(jwtExpiryHours).ToUnixTimeSeconds());

    var token = new JwtSecurityToken(
        issuer: jwtIssuer,
        audience: jwtAudience,
        claims: claims,
        expires: expiresAt.UtcDateTime,
        signingCredentials: credentials);

    return new GuestAuthResponse(new JwtSecurityTokenHandler().WriteToken(token), steamId, expiresAt);
}

// ── Game server registration endpoint ──
app.MapPost("/servers/register", async (
    ServerRegistrationRequest request,
    HttpContext httpContext,
    AppDbContext db,
    MasterDeploymentOptions deployment) =>
{
    if (deployment.IsVps)
    {
        var registrationToken = ExtractBearerToken(httpContext, logger);
        if (registrationToken is null
            || !TimingSafeEquals(deployment.RegistrationKey!, registrationToken)
            || request.HostId != deployment.ApprovedHostId)
            return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "Name is required" });

    if (string.IsNullOrWhiteSpace(request.Region))
        return Results.BadRequest(new { error = "Region is required" });

    var ipAddress = deployment.IsVps ? deployment.PublicHost! : request.IpAddress;
    var port = deployment.IsVps ? deployment.PublicPort!.Value : request.Port;
    var isOfficial = deployment.IsVps || request.IsOfficial;
    if (!deployment.IsVps && !IsValidIpAddress(ipAddress))
        return Results.BadRequest(new { error = $"Invalid IP address: {ipAddress}" });

    if (!IsValidPort(port))
        return Results.BadRequest(new { error = $"Invalid port: {port} (must be 1-65535)" });

    if (request.MaxConcurrentMatches <= 0)
        return Results.BadRequest(new { error = "MaxConcurrentMatches must be positive" });

    if (request.MaxConcurrentMatches > 100)
        return Results.BadRequest(new { error = "MaxConcurrentMatches must be <= 100" });
    if (port + request.MaxConcurrentMatches - 1 > 65535)
        return Results.BadRequest(new { error = "Match port range exceeds 65535" });

    var serverId = deployment.IsVps ? deployment.ApprovedHostId!.Value : Guid.NewGuid();

    async Task<IResult> Refresh(MasterServer.Data.Models.GameServer target)
    {
        target.Name = request.Name;
        target.IpAddress = ipAddress;
        target.Port = port;
        target.Region = request.Region;
        target.IsOfficial = isOfficial;
        target.MaxConcurrentMatches = request.MaxConcurrentMatches;
        target.CustomRulesJson = request.CustomRulesJson;
        if (!deployment.IsVps)
        {
            target.CurrentMatches = 0;
            target.ApiToken = GenerateServerApiToken();
        }
        target.LastHeartbeat = DateTime.UtcNow;

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Game server re-registered: {Name} (ID: {Id}, Region: {Region})",
            target.Name, target.Id, target.Region);

        return Results.Ok(new { serverId = target.Id, apiToken = target.ApiToken });
    }

    // VPS identity is the provisioned primary key, never a request address.
    // Development keeps its explicit legacy ip:port upsert.
    var existing = deployment.IsVps
        ? await db.GameServers.FindAsync(serverId)
        : await db.GameServers.FirstOrDefaultAsync(s => s.IpAddress == ipAddress && s.Port == port);
    if (existing is not null)
        return await Refresh(existing);

    var gameServer = new MasterServer.Data.Models.GameServer
    {
        Id = serverId,
        Name = request.Name,
        IpAddress = ipAddress,
        Port = port,
        Region = request.Region,
        IsOfficial = isOfficial,
        MaxConcurrentMatches = request.MaxConcurrentMatches,
        CurrentMatches = 0,
        CustomRulesJson = request.CustomRulesJson,
        ApiToken = GenerateServerApiToken(),
        LastHeartbeat = DateTime.UtcNow
    };

    db.GameServers.Add(gameServer);

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        // The primary key arbitrates concurrent VPS inserts; ip:port does so in
        // development. Re-read the winner and return its stable API token.
        db.Entry(gameServer).State = EntityState.Detached;
        var winner = deployment.IsVps
            ? await db.GameServers.FindAsync(serverId)
            : await db.GameServers.FirstOrDefaultAsync(s => s.IpAddress == ipAddress && s.Port == port);
        if (winner is null)
            throw;
        return await Refresh(winner);
    }

    logger.LogInformation(
        "Game server registered: {Name} (ID: {Id}, Region: {Region})",
        gameServer.Name, gameServer.Id, gameServer.Region);

    return Results.Ok(new { serverId = gameServer.Id, apiToken = gameServer.ApiToken });
});

// ── Server heartbeat endpoint ──
app.MapPost("/servers/{serverId}/heartbeat", async (
    Guid serverId,
    HeartbeatRequest request,
    HttpContext httpContext,
    AppDbContext db,
    MasterDeploymentOptions deployment) =>
{
    var token = ExtractBearerToken(httpContext, logger);
    if (token == null)
        return Results.Unauthorized();

    var server = await db.GameServers.FindAsync(serverId);
    if (server == null)
    {
        logger.LogWarning("Heartbeat from unknown server: {ServerId}", serverId);
        return Results.NotFound(new { error = "Server not found" });
    }

    if ((deployment.IsVps && server.Id != deployment.ApprovedHostId) ||
        !TimingSafeEquals(server.ApiToken, token))
    {
        logger.LogWarning("Heartbeat auth failed for server {ServerId}", serverId);
        return Results.Unauthorized();
    }

    server.CurrentMatches = request.CurrentMatches;
    server.LastHeartbeat = DateTime.UtcNow;
    await db.SaveChangesAsync();

    return Results.Ok(new { status = "ok" });
});

// ── Server deregister endpoint (issue #49) ──
// Removes the calling server's row so it disappears from GET /servers
// immediately instead of lingering for the heartbeat TTL window. Authenticated
// by the server's apiToken (same bearer scheme as the heartbeat).
app.MapDelete("/servers/{serverId}", async (
    Guid serverId,
    HttpContext httpContext,
    AppDbContext db,
    MasterDeploymentOptions deployment) =>
{
    var token = ExtractBearerToken(httpContext, logger);
    if (token == null)
        return Results.Unauthorized();

    var server = await db.GameServers.FindAsync(serverId);
    if (server == null)
    {
        logger.LogWarning("Deregister from unknown server: {ServerId}", serverId);
        return Results.NotFound(new { error = "Server not found" });
    }

    if ((deployment.IsVps && server.Id != deployment.ApprovedHostId) ||
        !TimingSafeEquals(server.ApiToken, token))
    {
        logger.LogWarning("Deregister auth failed for server {ServerId}", serverId);
        return Results.Unauthorized();
    }

    db.GameServers.Remove(server);
    await db.SaveChangesAsync();

    logger.LogInformation("Game server deregistered: {Name} (ID: {ServerId})", server.Name, server.Id);
    return Results.Ok(new { status = "deregistered" });
});

// ── Server browser list endpoint (issue #31) ──
app.MapGet("/servers", async (AppDbContext db, MasterDeploymentOptions deployment) =>
{
    var cutoff = DateTime.UtcNow.AddSeconds(-15);
    var eligible = db.GameServers
        .Where(s => s.LastHeartbeat > cutoff && s.CurrentMatches < s.MaxConcurrentMatches);
    if (deployment.IsVps)
    {
        var approvedId = deployment.ApprovedHostId!.Value;
        eligible = eligible.Where(s => s.Id == approvedId);
    }
    var servers = await eligible
        .OrderByDescending(s => s.IsOfficial)
        .ThenBy(s => s.Name)
        .Select(s => new
        {
            id = s.Id,
            name = s.Name,
            ipAddress = s.IpAddress,
            port = s.Port,
            region = s.Region,
            currentMatches = s.CurrentMatches,
            maxConcurrentMatches = s.MaxConcurrentMatches,
            isOfficial = s.IsOfficial
        })
        .ToListAsync();

    return Results.Ok(servers);
}).RequireAuthorization();

// ── Match result endpoint ──
app.MapPost("/match/result", async (
    MatchResultRequest request,
    HttpContext httpContext,
    AppDbContext db,
    MasterDeploymentOptions deployment) =>
{
    var token = ExtractBearerToken(httpContext, logger);
    if (token == null)
        return Results.Unauthorized();

    // Verify server token (find server with this token)
    var server = await db.GameServers.FirstOrDefaultAsync(s => s.ApiToken == token);
    if (server == null || (deployment.IsVps && server.Id != deployment.ApprovedHostId) ||
        !TimingSafeEquals(server.ApiToken, token))
    {
        logger.LogWarning("Match result auth failed");
        return Results.Unauthorized();
    }

    // Wrap in transaction for atomic MMR update
    await using var transaction = await db.Database.BeginTransactionAsync();

    try
    {
        var match = await db.Matches.FindAsync(request.MatchId);
        if (match == null)
        {
            logger.LogWarning("Match result for unknown match: {MatchId}", request.MatchId);
            await transaction.RollbackAsync();
            return Results.NotFound(new { error = "Match not found" });
        }

        match.WinnerSteamId = request.WinnerSteamId > 0 ? request.WinnerSteamId : null;
        match.EndedAt = DateTime.UtcNow;

        // MMR update disabled (issue #40) — the Match row + winner are still recorded.

        server.CurrentMatches = Math.Max(0, server.CurrentMatches - 1);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return Results.Ok(new { status = "recorded", mmrChange = 0 });
    }
    catch
    {
        await transaction.RollbackAsync();
        throw;
    }
});

// ── SignalR lobby hub (issue #32) ──
app.MapHub<LobbyHub>("/lobby");

app.Run();

// Exposed for the test host (WebApplicationFactory<Program>).
public partial class Program { }
