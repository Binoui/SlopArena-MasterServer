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
using MasterServer.Steam;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
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
// Guest issuance is an explicit development mode, never inferred from the VPS profile.
builder.Services.AddSingleton(sp => SteamAuthOptions.Load(
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<MasterDeploymentOptions>()));
const string SteamProvider = "steam";
const string GuestProvider = "guest";
// Valve's GET API places the publisher key and ticket in the URI; never log outbound URLs.
builder.Services.AddHttpClient<SteamTicketVerifier>().RemoveAllLoggers();
builder.Services.AddScoped<SteamTicketReplayStore>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwtSecret = builder.Configuration["Jwt:Secret"];
        if (string.IsNullOrWhiteSpace(jwtSecret))
            throw new InvalidOperationException("Jwt:Secret is not configured.");
        var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "SlopArena.Master";
        var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "SlopArena.Client";
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
            },
            OnTokenValidated = context =>
            {
                var provider = context.Principal?.FindFirst("provider")?.Value;
                var auth = context.HttpContext.RequestServices.GetRequiredService<SteamAuthOptions>();
                if ((provider != SteamProvider || !auth.UsesSteam) &&
                    !(auth.UsesDevelopmentGuests && provider == GuestProvider))
                    context.Fail("Authentication provider is not permitted.");
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
            : path.StartsWithSegments("/auth/steam") ? "steam"
            : path.StartsWithSegments("/auth/name") ? "name"
            : path.StartsWithSegments("/auth/refresh") ? "refresh"
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
var authOptions = app.Services.GetRequiredService<SteamAuthOptions>();
var allowGuest = authOptions.UsesDevelopmentGuests;
var jwtSecret = app.Configuration["Jwt:Secret"]!;
var jwtIssuer = app.Configuration["Jwt:Issuer"] ?? "SlopArena.Master";
var jwtAudience = app.Configuration["Jwt:Audience"] ?? "SlopArena.Client";
var logger = app.Services.GetRequiredService<ILogger<Program>>();
app.UseForwardedHeaders();
// Reject oversized credential bodies before minimal-API JSON binding (including TestServer).
app.Use(async (context, next) =>
{
    if (context.Request.ContentLength is > 16_384 &&
        (context.Request.Path == "/auth/steam" || context.Request.Path == "/auth/refresh"))
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return;
    }
    await next();
});


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
static bool TrySteamIdentity(string? value, out string identity)
{
    identity = string.Empty;
    if (value is null || value.Length is < 1 or > 20 ||
        !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId) ||
        steamId == 0 || steamId.ToString(CultureInfo.InvariantCulture) != value)
        return false;
    identity = value;
    return true;
}
static bool IsCatalogHash(string? hash) =>
    hash is { Length: 64 } &&
    hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

// ── Guest auth endpoint (issue #30) ──
app.MapPost("/auth/guest", async (AppDbContext db) =>
{
    if (!allowGuest)
        return Results.NotFound();
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
    return Results.Ok(GenerateAuth(steamId, GuestProvider));
});

// Verify the Playtest ticket and current ownership before creating any application identity.
app.MapPost("/auth/steam", async (SteamAuthRequest request, SteamTicketVerifier verifier,
    SteamTicketReplayStore replay, AppDbContext db, HttpContext context) =>
{
    if (!authOptions.UsesSteam)
        return Results.NotFound();
    if (!context.Request.IsHttps)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!SteamTicketVerifier.IsValidTicket(request.Ticket))
        return Results.BadRequest(new { error = "invalid_ticket" });

    try
    {
        var steamId = await verifier.VerifyAsync(request.Ticket!, context.RequestAborted);
        if (steamId is null)
            return Results.Unauthorized();
        if (!await replay.TryConsumeAsync(request.Ticket!, context.RequestAborted))
            return Results.Unauthorized();

        var user = await db.Users.FindAsync(steamId.Value);
        if (user is null)
        {
            user = new MasterServer.Data.Models.User
            {
                SteamId = steamId.Value,
                AuthProvider = SteamProvider,
                Username = $"Steam-{steamId.Value}",
                Mmr = 1000,
                CreatedAt = DateTime.UtcNow
            };
            db.Users.Add(user);
        }
        else if (user.AuthProvider != SteamProvider)
            return Results.Unauthorized();

        user.LastLogin = DateTime.UtcNow;
        await db.SaveChangesAsync(context.RequestAborted);
        return Results.Ok(GenerateAuth(steamId.Value, SteamProvider));
    }
    catch (SteamApiUnavailableException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (SteamReplayStoreUnavailableException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}).WithMetadata(new RequestSizeLimitAttribute(16_384));

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

app.MapPost("/auth/refresh", async (HttpContext context, AppDbContext db,
    SteamTicketVerifier verifier, SteamTicketReplayStore replay) =>
{
    if (!long.TryParse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out var playerId))
        return Results.Unauthorized();
    var provider = context.User.FindFirst("provider")?.Value;
    var user = await db.Users.FindAsync(playerId);
    if (user is null || user.AuthProvider != provider)
        return Results.Unauthorized();
    if (provider == GuestProvider && allowGuest)
        return Results.Ok(GenerateAuth(playerId, GuestProvider));
    if (provider != SteamProvider || !authOptions.UsesSteam)
        return Results.Unauthorized();
    if (!context.Request.IsHttps)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!context.Request.HasJsonContentType())
        return Results.BadRequest(new { error = "invalid_ticket" });
    SteamAuthRequest? request;
    try
    {
        request = await context.Request.ReadFromJsonAsync<SteamAuthRequest>(
            cancellationToken: context.RequestAborted);
    }
    catch (System.Text.Json.JsonException)
    {
        return Results.BadRequest(new { error = "invalid_ticket" });
    }
    if (!SteamTicketVerifier.IsValidTicket(request?.Ticket))
        return Results.BadRequest(new { error = "invalid_ticket" });
    try
    {
        if (await verifier.VerifyAsync(request!.Ticket!, context.RequestAborted) != playerId ||
            !await replay.TryConsumeAsync(request.Ticket!, context.RequestAborted))
            return Results.Unauthorized();
        return Results.Ok(GenerateAuth(playerId, SteamProvider));
    }
    catch (SteamApiUnavailableException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (SteamReplayStoreUnavailableException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}).RequireAuthorization().WithMetadata(new RequestSizeLimitAttribute(16_384));

GuestAuthResponse GenerateAuth(long steamId, string provider)
{
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, steamId.ToString(CultureInfo.InvariantCulture)),
        new Claim("steam_id", steamId.ToString(CultureInfo.InvariantCulture)),
        new Claim("provider", provider),
        new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
    };

    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
    var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    var expiresAt = DateTimeOffset.FromUnixTimeSeconds(
        DateTimeOffset.UtcNow.AddHours(provider == SteamProvider ? 1 : 24).ToUnixTimeSeconds());

    var token = new JwtSecurityToken(
        issuer: jwtIssuer,
        audience: jwtAudience,
        claims: claims,
        expires: expiresAt.UtcDateTime,
        signingCredentials: credentials);

    return new GuestAuthResponse(new JwtSecurityTokenHandler().WriteToken(token), steamId, expiresAt);
}

// ponytail: one Master process serializes result/cancel races; use DB conditional updates if Master is replicated.
var matchCompletionGate = new SemaphoreSlim(1, 1);

async Task<int> CancelOpenMatchesAsync(AppDbContext db, IHubContext<LobbyHub> hub,
    Guid serverId, string reason, Guid? matchId = null)
{
    await matchCompletionGate.WaitAsync();
    try
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
        await db.SaveChangesAsync();
        foreach (var match in open)
        {
            var roster = new[] { match.Player1SteamId, match.Player2SteamId }
                .Concat(new[] { match.Player3SteamId, match.Player4SteamId }
                    .Where(id => id.HasValue).Select(id => id!.Value))
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
    finally { matchCompletionGate.Release(); }
}

// ── Game server registration endpoint ──
app.MapPost("/servers/register", async (
    ServerRegistrationRequest request,
    HttpContext httpContext,
    AppDbContext db,
    MasterDeploymentOptions deployment,
    IHubContext<LobbyHub> hub) =>
{
    if (deployment.IsVps)
    {
        var registrationToken = ExtractBearerToken(httpContext, logger);
        if (registrationToken is null
            || !TimingSafeEquals(deployment.RegistrationKey!, registrationToken)
            || request.HostId != deployment.ApprovedHostId)
            return Results.Unauthorized();
    }
    if (deployment.IsVps &&
        (request.ProtocolVersion != 2 || !TrySteamIdentity(request.SteamId, out _) ||
         request.InstanceId is null || request.InstanceId == Guid.Empty ||
         !IsCatalogHash(request.CatalogHash)))
        return Results.BadRequest(new { error = "Current Steam identity, process instance, catalog hash and protocol 2 are required." });

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
        if (deployment.IsVps && (target.SteamId != request.SteamId ||
            target.InstanceId != request.InstanceId))
        {
            await CancelOpenMatchesAsync(db, hub, target.Id, "host_restart");
            target.ApiToken = GenerateServerApiToken();
            target.CurrentMatches = 0;
        }
        target.SteamId = deployment.IsVps ? request.SteamId : null;
        target.ProtocolVersion = deployment.IsVps ? request.ProtocolVersion : 0;
        target.InstanceId = deployment.IsVps ? request.InstanceId : null;
        target.CatalogHash = deployment.IsVps ? request.CatalogHash : null;
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
        SteamId = deployment.IsVps ? request.SteamId : null,
        ProtocolVersion = deployment.IsVps ? request.ProtocolVersion : 0,
        InstanceId = deployment.IsVps ? request.InstanceId : null,
        CatalogHash = deployment.IsVps ? request.CatalogHash : null,
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
    if (deployment.IsVps && (server.SteamId != request.SteamId ||
        server.InstanceId != request.InstanceId || server.ProtocolVersion != 2 ||
        server.CatalogHash != request.CatalogHash))
    {
        // Stop advertising the superseded identity immediately; an authenticated
        // re-registration must pin the new identity/catalog before another launch.
        server.LastHeartbeat = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        return Results.Conflict(new { error = "host_identity_changed" });
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
    MasterDeploymentOptions deployment,
    IHubContext<LobbyHub> hub) =>
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

    if (deployment.IsVps)
        await CancelOpenMatchesAsync(db, hub, server.Id, "host_shutdown");
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
        eligible = eligible.Where(s => s.Id == approvedId &&
            s.SteamId != null && s.InstanceId != null && s.ProtocolVersion == 2);
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
            serverSteamId = s.SteamId,
            protocolVersion = s.ProtocolVersion,
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

    await matchCompletionGate.WaitAsync(httpContext.RequestAborted);
    try
    {
        var match = await db.Matches.FindAsync(request.MatchId);
        if (match is null)
            return Results.NotFound(new { error = "Match not found" });
        if (deployment.IsVps && match.ServerId != server.Id)
            return Results.Unauthorized();
        if (match.CanceledAt is not null)
            return Results.Conflict(new { error = "match_canceled" });
        if (match.EndedAt is not null)
            return Results.Ok(new { status = "recorded", mmrChange = 0 });
        if (request.WinnerSteamId != 0 &&
            request.WinnerSteamId != match.Player1SteamId &&
            request.WinnerSteamId != match.Player2SteamId &&
            request.WinnerSteamId != match.Player3SteamId &&
            request.WinnerSteamId != match.Player4SteamId)
            return Results.BadRequest(new { error = "winner_not_rostered" });

        match.WinnerSteamId = request.WinnerSteamId > 0 ? request.WinnerSteamId : null;
        match.EndedAt = DateTime.UtcNow;
        server.CurrentMatches = Math.Max(0, server.CurrentMatches - 1);
        await db.SaveChangesAsync(httpContext.RequestAborted);
        return Results.Ok(new { status = "recorded", mmrChange = 0 });
    }
    finally { matchCompletionGate.Release(); }
});

app.MapPost("/match/cancel", async (MatchCancelRequest request, HttpContext context,
    AppDbContext db, IHubContext<LobbyHub> hub, MasterDeploymentOptions deployment) =>
{
    var token = ExtractBearerToken(context, logger);
    if (token is null) return Results.Unauthorized();
    var server = await db.GameServers.FirstOrDefaultAsync(s => s.ApiToken == token);
    if (server is null || (deployment.IsVps && server.Id != deployment.ApprovedHostId) ||
        !TimingSafeEquals(server.ApiToken, token))
        return Results.Unauthorized();
    if (request.MatchId == Guid.Empty || request.Reason is not
        ("unfilled" or "absent" or "host_restart" or "host_shutdown" or "content_unavailable"))
        return Results.BadRequest(new { error = "invalid_cancellation" });
    var match = await db.Matches.FindAsync(request.MatchId);
    if (match is null)
        return Results.NotFound();
    if (match.ServerId != server.Id)
        return Results.Unauthorized();
    if (match.EndedAt is not null)
        return Results.Conflict(new { error = "match_completed" });
    var count = await CancelOpenMatchesAsync(db, hub, server.Id, request.Reason, request.MatchId);
    if (count > 0)
    {
        server.CurrentMatches = Math.Max(0, server.CurrentMatches - 1);
        await db.SaveChangesAsync();
    }
    else
    {
        await db.Entry(match).ReloadAsync();
        if (match.EndedAt is not null)
            return Results.Conflict(new { error = "match_completed" });
    }
    return Results.Ok(new { status = "canceled" });
});

// ── SignalR lobby hub (issue #32) ──
app.MapHub<LobbyHub>("/lobby", options => options.CloseOnAuthenticationExpiration = true);

app.Run();

// Exposed for the test host (WebApplicationFactory<Program>).
public partial class Program { }
