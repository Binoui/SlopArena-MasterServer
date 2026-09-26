using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MasterServer.Data;
using MasterServer.Data.Models;
using MasterServer.DTOs;
using Xunit;

namespace MasterServer.Tests;

/// <summary>
/// HTTP lifecycle tests for development and approved VPS GameServers.
/// EF InMemory does not prove PostgreSQL uniqueness or insert races.
/// </summary>
public class ServerRegistrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string RegistrationKey = "registration-secret-0123456789abcdef";
    private const string MatchControlKey = "control-secret-0123456789abcdef01234567";
    private static readonly Guid ApprovedHostId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid HostInstance = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly WebApplicationFactory<Program> _vpsFactory;

    public ServerRegistrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Deployment:Profile"] = "development",
                    ["Auth:Mode"] = "development-guest",
                    ["RateLimit:MaxRequestsPerWindow"] = "100000"
                }));
            builder.ConfigureServices(services =>
                ReplaceDatabase(services, $"server-registration-test-{Guid.NewGuid():N}"));
        });

        _vpsFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(VpsConfiguration()));
            builder.ConfigureServices(services =>
                ReplaceDatabase(services, $"vps-registration-test-{Guid.NewGuid():N}"));
        });
    }

    private static Dictionary<string, string?> VpsConfiguration() => new()
    {
        ["Deployment:Profile"] = "vps",
        ["Auth:Mode"] = "steam",
        ["Steam:ApiKey"] = "test-publisher-key",
        ["Steam:AppId"] = "5325920",
        ["Steam:Identity"] = "sloparena-playtest",
        ["Proxy:TrustedAddress"] = "127.0.0.1",
        ["ApprovedHost:Id"] = ApprovedHostId.ToString(),
        ["ApprovedHost:RegistrationKey"] = RegistrationKey,
        ["ApprovedHost:PublicHost"] = "game.example.com",
        ["ApprovedHost:PublicPort"] = "28765",
        ["ApprovedHost:ControlUrl"] = "http://gameserver.internal:28765/match/start",
        ["MatchControl:Key"] = MatchControlKey,
        ["Jwt:Secret"] = "vps-test-jwt-secret-0123456789abcdefghijklmnopqrstuvwxyz",
        ["RateLimit:MaxRequestsPerWindow"] = "100000"
    };
    private static string SteamToken(long steamId)
    {
        var token = new JwtSecurityToken(
            issuer: "SlopArena.Master",
            audience: "SlopArena.Client",
            claims: new[]
            {
                new Claim(ClaimTypes.NameIdentifier, steamId.ToString()),
                new Claim("provider", "steam")
            },
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(VpsConfiguration()["Jwt:Secret"]!)),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }


    private static void ReplaceDatabase(IServiceCollection services, string name)
    {
        var descriptor = services.SingleOrDefault(
            d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
        if (descriptor is not null)
            services.Remove(descriptor);
        services.AddDbContext<AppDbContext>(opts => opts.UseInMemoryDatabase(name));
    }

    private HttpClient CreateClient() => _factory.CreateClient();
    private HttpClient CreateVpsClient() => _vpsFactory.CreateClient();

    private static readonly ServerRegistrationRequest DefaultRequest = new(
        Name: "Test Server",
        IpAddress: "203.0.113.10",
        Port: 9876,
        Region: "EU",
        IsOfficial: false,
        MaxConcurrentMatches: 15,
        CustomRulesJson: null);

    private static ServerRegistrationRequest VpsRequest(string name = "Approved Host") => new(
        Name: name,
        IpAddress: "198.51.100.99",
        Port: 12345,
        Region: "EU",
        IsOfficial: false,
        MaxConcurrentMatches: 15,
        CustomRulesJson: null,
        HostId: ApprovedHostId,
        SteamId: "90293421017699331",
        ProtocolVersion: 2,
        InstanceId: HostInstance,
        CatalogHash: new string('a', 64));

    // Each development registration uses a unique port so tests stay hermetic.
    private static int _nextPort = 9000;

    private static ServerRegistrationRequest UniqueRequest() =>
        DefaultRequest with { Port = Interlocked.Increment(ref _nextPort) };

    private async Task<RegisterResponse> RegisterAsync(HttpClient client, ServerRegistrationRequest? request = null)
    {
        var response = await client.PostAsJsonAsync("/servers/register", request ?? UniqueRequest());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
    }

    private static async Task<HttpResponseMessage> PostVpsRegistrationAsync(
        HttpClient client, ServerRegistrationRequest request, string? token)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/servers/register")
        {
            Content = JsonContent.Create(request)
        };
        if (token is not null)
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(message);
    }

    private static async Task<RegisterResponse> RegisterVpsAsync(
        HttpClient client, ServerRegistrationRequest? request = null)
    {
        using var response = await PostVpsRegistrationAsync(
            client, request ?? VpsRequest(), RegistrationKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
    }

    private async Task<HttpResponseMessage> HeartbeatAsync(
        HttpClient client, Guid serverId, string? token, int currentMatches = 0)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/servers/{serverId}/heartbeat")
        {
            Content = JsonContent.Create(new { currentMatches, steamId = "90293421017699331",
                instanceId = HostInstance, catalogHash = new string('a', 64) })
        };
        if (token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostMatchResultAsync(
        HttpClient client, Guid matchId, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/match/result")
        {
            Content = JsonContent.Create(new MatchResultRequest(matchId, 101))
        };
        if (token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> DeregisterAsync(HttpClient client, Guid serverId, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/servers/{serverId}");
        if (token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private async Task<int> CountGameServersAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.GameServers.CountAsync();
    }

    private async Task<GameServer?> FindGameServerAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.GameServers.FindAsync(id);
    }

    private async Task<int> CountVpsGameServersAsync()
    {
        using var scope = _vpsFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.GameServers.CountAsync();
    }

    private async Task<GameServer?> FindVpsGameServerAsync(Guid id)
    {
        using var scope = _vpsFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.GameServers.FindAsync(id);
    }

    [Fact]
    public async Task Register_CreatesRow_ReturnsServerIdAndToken()
    {
        var client = CreateClient();

        var result = await RegisterAsync(client);

        Assert.NotEqual(Guid.Empty, result.ServerId);
        Assert.False(string.IsNullOrWhiteSpace(result.ApiToken));
        Assert.Equal(1, await CountGameServersAsync());
    }

    [Fact]
    public async Task Register_WithDnsHostnameIp_IsAccepted()
    {
        var client = CreateClient();

        // ADR-0009: official servers behind NAT register with a public domain
        // (e.g. sloparena.barakaslurp.fr). The validator must accept DNS names,
        // not just IPv4 literals.
        var request = UniqueRequest() with { IpAddress = "sloparena.barakaslurp.fr" };

        var response = await client.PostAsJsonAsync("/servers/register", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
        var stored = await FindGameServerAsync(row.ServerId);
        Assert.NotNull(stored);
        Assert.Equal("sloparena.barakaslurp.fr", stored.IpAddress);
    }

    [Fact]
    public async Task Deregister_WithCorrectToken_RemovesRowImmediately()
    {
        var client = CreateClient();
        var registered = await RegisterAsync(client);

        // Row is provably fresh (heartbeated) right before deregister.
        var heartbeat = await HeartbeatAsync(client, registered.ServerId, registered.ApiToken);
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        Assert.Equal(1, await CountGameServersAsync());

        var response = await DeregisterAsync(client, registered.ServerId, registered.ApiToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await FindGameServerAsync(registered.ServerId));
    }

    [Fact]
    public async Task Deregister_WithWrongToken_Returns401_KeepsRow()
    {
        var client = CreateClient();
        var registered = await RegisterAsync(client);

        var response = await DeregisterAsync(client, registered.ServerId, "wrong-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotNull(await FindGameServerAsync(registered.ServerId));
    }

    [Fact]
    public async Task Deregister_WithoutToken_Returns401()
    {
        var client = CreateClient();
        var registered = await RegisterAsync(client);

        var response = await DeregisterAsync(client, registered.ServerId, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Deregister_UnknownServer_Returns404()
    {
        var client = CreateClient();

        var response = await DeregisterAsync(client, Guid.NewGuid(), "any-token");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Register_SameIpPort_ReclaimsExistingRow_NoDuplicate()
    {
        var client = CreateClient();
        var request = UniqueRequest();
        var first = await RegisterAsync(client, request);

        var second = await RegisterAsync(client, request with { Name = "Renamed Server", Region = "US" });

        // Same socket reclaimed — same serverId, no duplicate row.
        Assert.Equal(first.ServerId, second.ServerId);
        Assert.Equal(1, await CountGameServersAsync());

        // Fields refreshed and the API token rotated.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.GameServers.SingleAsync();
        Assert.Equal("Renamed Server", row.Name);
        Assert.Equal("US", row.Region);
        Assert.Equal(second.ApiToken, row.ApiToken);
        Assert.NotEqual(first.ApiToken, second.ApiToken);
    }

    [Fact]
    public async Task Register_SameIp_DifferentPort_KeepsSeparateRows()
    {
        var client = CreateClient();
        var first = await RegisterAsync(client, UniqueRequest() with { IpAddress = "203.0.113.11" });
        var second = await RegisterAsync(client, UniqueRequest() with { IpAddress = "203.0.113.11" });

        // Same IP, different ports — two legitimate servers, two rows.
        // Guards against a unique constraint on IpAddress alone.
        Assert.NotEqual(first.ServerId, second.ServerId);
        Assert.Equal(2, await CountGameServersAsync());
    }

    [Fact]
    public async Task Upsert_RotatesToken_OldTokenRejected()
    {
        var client = CreateClient();
        var request = UniqueRequest();
        var first = await RegisterAsync(client, request);
        var second = await RegisterAsync(client, request);

        var oldTokenHeartbeat = await HeartbeatAsync(client, first.ServerId, first.ApiToken);
        var newTokenHeartbeat = await HeartbeatAsync(client, second.ServerId, second.ApiToken);

        Assert.Equal(HttpStatusCode.Unauthorized, oldTokenHeartbeat.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newTokenHeartbeat.StatusCode);
    }


    [Fact]
    public async Task VpsRegistration_RejectsWrongMissingJwtAndUnapprovedHostWithoutMutation()
    {
        var client = CreateVpsClient();
        var registered = await RegisterVpsAsync(client);
        Assert.Equal(ApprovedHostId, registered.ServerId);
        var heartbeat = await HeartbeatAsync(client, registered.ServerId, registered.ApiToken, currentMatches: 4);
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        var before = await FindVpsGameServerAsync(registered.ServerId);
        Assert.NotNull(before);

        var steamToken = SteamToken(101);
        var matchId = Guid.NewGuid();
        using (var scope = _vpsFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            db.Users.AddRange(
                new User { SteamId = 101, Username = "One", Mmr = 1000, CreatedAt = now, LastLogin = now },
                new User { SteamId = 202, Username = "Two", Mmr = 1000, CreatedAt = now, LastLogin = now });
            db.Matches.Add(new Match
            {
                Id = matchId,
                Player1SteamId = 101,
                Player2SteamId = 202,
                ServerRegion = "EU"
            });
            await db.SaveChangesAsync();
        }

        using var wrong = await PostVpsRegistrationAsync(client, VpsRequest("Wrong key"), "wrong-key");
        using var missing = await PostVpsRegistrationAsync(client, VpsRequest("Missing key"), token: null);
        using var wrongHost = await PostVpsRegistrationAsync(
            client, VpsRequest("Wrong host") with { HostId = Guid.NewGuid() }, RegistrationKey);
        using var jwt = await PostVpsRegistrationAsync(client, VpsRequest("JWT credential"), steamToken);
        using var wrongHeartbeat = await HeartbeatAsync(client, registered.ServerId, "wrong-key", currentMatches: 0);
        using var missingHeartbeat = await HeartbeatAsync(client, registered.ServerId, token: null, currentMatches: 0);
        using var jwtHeartbeat = await HeartbeatAsync(client, registered.ServerId, steamToken, currentMatches: 0);
        using var wrongResult = await PostMatchResultAsync(client, matchId, "wrong-key");
        using var missingResult = await PostMatchResultAsync(client, matchId, token: null);
        using var jwtResult = await PostMatchResultAsync(client, matchId, steamToken);

        Assert.All(new[]
        {
            wrong, missing, wrongHost, jwt, wrongHeartbeat, missingHeartbeat,
            jwtHeartbeat, wrongResult, missingResult, jwtResult
        }, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(1, await CountVpsGameServersAsync());

        var after = await FindVpsGameServerAsync(registered.ServerId);
        Assert.NotNull(after);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.ApiToken, after.ApiToken);
        Assert.Equal(before.IpAddress, after.IpAddress);
        Assert.Equal(before.Port, after.Port);
        Assert.Equal(before.CurrentMatches, after.CurrentMatches);
        Assert.Equal(before.LastHeartbeat, after.LastHeartbeat);
        using var scopeAfter = _vpsFactory.Services.CreateScope();
        var match = await scopeAfter.ServiceProvider.GetRequiredService<AppDbContext>().Matches.FindAsync(matchId);
        Assert.NotNull(match);
        Assert.Null(match.EndedAt);
        Assert.Null(match.WinnerSteamId);
    }

    [Fact]
    public async Task VpsRegistration_UsesApprovedAddressAndPreservesTokenLoadAndBrowserShape()
    {
        var client = CreateVpsClient();
        var first = await RegisterVpsAsync(client);
        var heartbeat = await HeartbeatAsync(client, first.ServerId, first.ApiToken, currentMatches: 3);
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);

        var second = await RegisterVpsAsync(client, VpsRequest("Renamed") with
        {
            IpAddress = "attacker.example",
            Port = 1234,
            IsOfficial = false
        });

        Assert.Equal(ApprovedHostId, first.ServerId);
        Assert.Equal(first.ServerId, second.ServerId);
        Assert.Equal(first.ApiToken, second.ApiToken);
        Assert.Equal(1, await CountVpsGameServersAsync());
        var row = await FindVpsGameServerAsync(first.ServerId);
        Assert.NotNull(row);
        Assert.Equal("game.example.com", row.IpAddress);
        Assert.Equal(28765, row.Port);
        Assert.True(row.IsOfficial);
        Assert.Equal("90293421017699331", row.SteamId);
        Assert.Equal(2, row.ProtocolVersion);
        Assert.Equal(3, row.CurrentMatches);

        using var heartbeatAfterDuplicate = await HeartbeatAsync(
            client, second.ServerId, second.ApiToken, currentMatches: 3);
        Assert.Equal(HttpStatusCode.OK, heartbeatAfterDuplicate.StatusCode);

        var steamToken = SteamToken(101);
        var importedId = Guid.NewGuid();
        using (var scope = _vpsFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.GameServers.Add(new GameServer
            {
                Id = importedId, Name = "Old imported host", IpAddress = "old.example.net",
                Port = 28766, Region = "EU", IsOfficial = true,
                MaxConcurrentMatches = 5, ApiToken = "old-token", LastHeartbeat = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        using var browserRequest = new HttpRequestMessage(HttpMethod.Get, "/servers");
        browserRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", steamToken);
        using var browserResponse = await client.SendAsync(browserRequest);
        browserResponse.EnsureSuccessStatusCode();
        using var browser = System.Text.Json.JsonDocument.Parse(
            await browserResponse.Content.ReadAsStringAsync());
        var server = Assert.Single(browser.RootElement.EnumerateArray().ToArray());
        Assert.Equal("90293421017699331", server.GetProperty("serverSteamId").GetString());
        Assert.Equal(2, server.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("game.example.com", server.GetProperty("ipAddress").GetString());
        Assert.Equal(28765, server.GetProperty("port").GetInt32());
        Assert.True(server.GetProperty("isOfficial").GetBoolean());
        Assert.Equal(3, server.GetProperty("currentMatches").GetInt32());
        using var oldHeartbeat = await HeartbeatAsync(client, importedId, "old-token", currentMatches: 3);
        using var oldResult = await PostMatchResultAsync(client, Guid.NewGuid(), "old-token");
        using var oldDeregister = await DeregisterAsync(client, importedId, "old-token");
        Assert.Equal(HttpStatusCode.Unauthorized, oldHeartbeat.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, oldResult.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, oldDeregister.StatusCode);
        Assert.NotNull(await FindVpsGameServerAsync(importedId));
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("steam")]
    public async Task VpsHostRotation_CancelsOldMatchAndRevokesOldHeartbeat(string changedField)
    {
        using var client = CreateVpsClient();
        var first = await RegisterVpsAsync(client);
        var matchId = Guid.NewGuid();
        using (var scope = _vpsFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.AddRange(
                new User { SteamId = 101, AuthProvider = "steam", Username = "One" },
                new User { SteamId = 202, AuthProvider = "steam", Username = "Two" });
            db.Matches.Add(new Match
            {
                Id = matchId, ServerId = first.ServerId,
                Player1SteamId = 101, Player2SteamId = 202, ServerRegion = "EU"
            });
            await db.SaveChangesAsync();
        }
        var rotated = await RegisterVpsAsync(client,
            VpsRequest() with
            {
                SteamId = changedField == "steam" ? "90293421017699332" : "90293421017699331",
                InstanceId = changedField == "instance" ? Guid.NewGuid() : HostInstance
            });
        Assert.Equal(first.ServerId, rotated.ServerId);
        Assert.NotEqual(first.ApiToken, rotated.ApiToken);
        using var staleHeartbeat = await HeartbeatAsync(client, first.ServerId, first.ApiToken);
        Assert.Equal(HttpStatusCode.Unauthorized, staleHeartbeat.StatusCode);
        using var wrongIdentity = await HeartbeatAsync(client, first.ServerId, rotated.ApiToken);
        Assert.Equal(HttpStatusCode.Conflict, wrongIdentity.StatusCode);
        using var scopeAfter = _vpsFactory.Services.CreateScope();
        var canceled = await scopeAfter.ServiceProvider.GetRequiredService<AppDbContext>()
            .Matches.FindAsync(matchId);
        Assert.NotNull(canceled!.CanceledAt);
        Assert.Equal("host_restart", canceled.CancelReason);
        Assert.Null(canceled.EndedAt);
        Assert.Null(canceled.WinnerSteamId);
    }

    [Fact]
    public async Task VpsRegistration_RejectsMissingSteamIdentityBeforeAdvertisingHost()
    {
        using var client = CreateVpsClient();
        using var response = await PostVpsRegistrationAsync(
            client, VpsRequest() with { SteamId = null }, RegistrationKey);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var invalidHash = await PostVpsRegistrationAsync(
            client, VpsRequest() with { CatalogHash = "not-a-catalog" }, RegistrationKey);
        Assert.Equal(HttpStatusCode.BadRequest, invalidHash.StatusCode);
        Assert.Equal(0, await CountVpsGameServersAsync());
    }

    [Fact]
    public async Task VpsHeartbeat_ContentRotationStopsAdvertisingStaleIdentityUntilReregistered()
    {
        using var client = CreateVpsClient();
        var first = await RegisterVpsAsync(client);
        var activeMatch = Guid.NewGuid();
        using (var scope = _vpsFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.AddRange(
                new User { SteamId = 101, AuthProvider = "steam", Username = "One" },
                new User { SteamId = 202, AuthProvider = "steam", Username = "Two" });
            db.Matches.Add(new Match
            {
                Id = activeMatch, ServerId = first.ServerId,
                Player1SteamId = 101, Player2SteamId = 202, ServerRegion = "EU"
            });
            await db.SaveChangesAsync();
        }
        using var load = await HeartbeatAsync(client, first.ServerId, first.ApiToken, 1);
        Assert.Equal(HttpStatusCode.OK, load.StatusCode);
        using var changed = new HttpRequestMessage(HttpMethod.Post,
            $"/servers/{first.ServerId}/heartbeat")
        {
            Content = JsonContent.Create(new
            {
                currentMatches = 1, steamId = VpsRequest().SteamId,
                instanceId = HostInstance, catalogHash = new string('b', 64)
            })
        };
        changed.Headers.Authorization = new("Bearer", first.ApiToken);
        using var mismatch = await client.SendAsync(changed);
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);

        using var browser = new HttpRequestMessage(HttpMethod.Get, "/servers");
        browser.Headers.Authorization = new("Bearer", SteamToken(101));
        using var hidden = await client.SendAsync(browser);
        hidden.EnsureSuccessStatusCode();
        var list = (await hidden.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).EnumerateArray();
        Assert.Empty(list);

        var rotated = await RegisterVpsAsync(client,
            VpsRequest() with { CatalogHash = new string('b', 64) });
        Assert.Equal(first.ApiToken, rotated.ApiToken);
        using var browserAfter = new HttpRequestMessage(HttpMethod.Get, "/servers");
        browserAfter.Headers.Authorization = new("Bearer", SteamToken(101));
        using var visible = await client.SendAsync(browserAfter);
        visible.EnsureSuccessStatusCode();
        var restored = (await visible.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).EnumerateArray();
        Assert.Single(restored);
        using var stateScope = _vpsFactory.Services.CreateScope();
        var stateDb = stateScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null((await stateDb.Matches.FindAsync(activeMatch))!.CanceledAt);
        Assert.Equal(1, (await stateDb.GameServers.FindAsync(first.ServerId))!.CurrentMatches);
    }

    [Fact]
    public async Task MatchCancellation_DoesNotManufactureWinnerOrConsumeCapacityTwice()
    {
        using var client = CreateVpsClient();
        var registered = await RegisterVpsAsync(client);
        using var heartbeat = await HeartbeatAsync(client, registered.ServerId, registered.ApiToken, 2);
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        var canceledId = Guid.NewGuid();
        var finishedId = Guid.NewGuid();
        using (var scope = _vpsFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.AddRange(
                new User { SteamId = 101, AuthProvider = "steam", Username = "One" },
                new User { SteamId = 202, AuthProvider = "steam", Username = "Two" });
            db.Matches.AddRange(
                new Match { Id = canceledId, ServerId = registered.ServerId,
                    Player1SteamId = 101, Player2SteamId = 202, ServerRegion = "EU" },
                new Match { Id = finishedId, ServerId = registered.ServerId,
                    Player1SteamId = 101, Player2SteamId = 202, ServerRegion = "EU" });
            await db.SaveChangesAsync();
        }

        async Task<HttpResponseMessage> Cancel(Guid matchId)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/match/cancel")
            {
                Content = JsonContent.Create(new MatchCancelRequest(matchId, "absent"))
            };
            request.Headers.Authorization = new("Bearer", registered.ApiToken);
            return await client.SendAsync(request);
        }
        using var first = await Cancel(canceledId);
        using var duplicate = await Cancel(canceledId);
        using var tooLate = await PostMatchResultAsync(client, canceledId, registered.ApiToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);

        using var result = await PostMatchResultAsync(client, finishedId, registered.ApiToken);
        using var repeatedResult = await PostMatchResultAsync(client, finishedId, registered.ApiToken);
        using var cancelFinished = await Cancel(finishedId);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeatedResult.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, cancelFinished.StatusCode);

        using var scopeAfter = _vpsFactory.Services.CreateScope();
        var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<AppDbContext>();
        var canceled = (await dbAfter.Matches.FindAsync(canceledId))!;
        var finished = (await dbAfter.Matches.FindAsync(finishedId))!;
        Assert.Equal("absent", canceled.CancelReason);
        Assert.NotNull(canceled.CanceledAt);
        Assert.Null(canceled.EndedAt);
        Assert.Null(canceled.WinnerSteamId);
        Assert.NotNull(finished.EndedAt);
        Assert.Null(finished.CanceledAt);
        Assert.Equal(101, finished.WinnerSteamId);
        Assert.Equal(0, (await dbAfter.GameServers.FindAsync(registered.ServerId))!.CurrentMatches);
    }

    [Fact]
    public async Task CancelMatch_NotifiesOnlyItsRosterAndKeepsServerChatMembership()
    {
        using var client = CreateVpsClient();
        var registered = await RegisterVpsAsync(client);
        var matchId = Guid.NewGuid();
        using (var scope = _vpsFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.AddRange(
                new User { SteamId = 101, AuthProvider = "steam", Username = "One" },
                new User { SteamId = 202, AuthProvider = "steam", Username = "Two" },
                new User { SteamId = 303, AuthProvider = "steam", Username = "Outsider" });
            db.Matches.Add(new Match
            {
                Id = matchId, ServerId = registered.ServerId, Player1SteamId = 101,
                Player2SteamId = 202, ServerRegion = "EU"
            });
            await db.SaveChangesAsync();
        }

        await using var rostered = new HubConnectionBuilder()
            .WithUrl(new Uri(_vpsFactory.Server.BaseAddress, "lobby"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _vpsFactory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(SteamToken(101));
            }).Build();
        await using var outsider = new HubConnectionBuilder()
            .WithUrl(new Uri(_vpsFactory.Server.BaseAddress, "lobby"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _vpsFactory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(SteamToken(303));
            }).Build();
        var notice = new TaskCompletionSource<System.Text.Json.JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var leaked = 0;
        rostered.On<System.Text.Json.JsonElement>("MatchAborted", payload => notice.TrySetResult(payload));
        outsider.On<System.Text.Json.JsonElement>("MatchAborted", _ => Interlocked.Increment(ref leaked));
        await rostered.StartAsync();
        await outsider.StartAsync();
        await rostered.InvokeAsync("JoinLobby", registered.ServerId, 2);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/match/cancel")
        {
            Content = JsonContent.Create(new MatchCancelRequest(matchId, "unfilled"))
        };
        request.Headers.Authorization = new("Bearer", registered.ApiToken);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = await notice.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(matchId, received.GetProperty("matchId").GetGuid());
        Assert.Equal("unfilled", received.GetProperty("reason").GetString());
        Assert.Equal(0, leaked);
        var chat = await rostered.InvokeAsync<MasterServer.Chat.ChatSnapshot>("GetChatState");
        Assert.Equal(registered.ServerId, chat.Server.ServerId);
    }

    [Fact]
    public async Task VpsLobby_RejectsFreshImportedServerEvenWithKnownId()
    {
        using var client = CreateVpsClient();
        var importedId = Guid.NewGuid();
        using (var scope = _vpsFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.GameServers.Add(new GameServer
            {
                Id = importedId, Name = "Old imported host", IpAddress = "old.example.net",
                Port = 28766, Region = "EU", IsOfficial = true, MaxConcurrentMatches = 5,
                ApiToken = "old-token", LastHeartbeat = DateTime.UtcNow
            });
            db.Users.Add(new User
            {
                SteamId = 101, AuthProvider = "steam", Username = "Test Steam User",
                Mmr = 1000, CreatedAt = DateTime.UtcNow, LastLogin = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var steamToken = SteamToken(101);
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_vpsFactory.Server.BaseAddress, "lobby"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _vpsFactory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(steamToken);
            }).Build();
        await connection.StartAsync();

        var error = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync("JoinLobby", importedId, 2));
        Assert.Contains("server_unavailable", error.Message);
    }

    [Fact]
    public async Task VpsLobby_RejectsLegacyProtocolBeforeAllocatingRosterSlot()
    {
        using var client = CreateVpsClient();
        var registered = await RegisterVpsAsync(client);
        using (var scope = _vpsFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User { SteamId = 101, AuthProvider = "steam", Username = "Player" });
            await db.SaveChangesAsync();
        }
        var token = SteamToken(101);
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_vpsFactory.Server.BaseAddress, "lobby"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _vpsFactory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            }).Build();
        await connection.StartAsync();
        var legacy = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync("JoinLobby", registered.ServerId, 0));
        Assert.Contains("incompatible_protocol", legacy.Message);
        await connection.InvokeAsync("JoinLobby", registered.ServerId, 2);
        var snapshot = _vpsFactory.Services.GetRequiredService<MasterServer.Lobbies.LobbyManager>()
            .GetSnapshot(connection.ConnectionId!);
        Assert.Single(Assert.IsType<MasterServer.Lobbies.LobbySnapshot>(snapshot).Players);
    }

    [Fact]
    public async Task VpsRegistration_ConcurrentRefreshKeepsOneRowStableTokenAndLoad()
    {
        var client = CreateVpsClient();
        var registered = await RegisterVpsAsync(client);
        var heartbeat = await HeartbeatAsync(client, registered.ServerId, registered.ApiToken, currentMatches: 5);

        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var response = await PostVpsRegistrationAsync(client, VpsRequest(), RegistrationKey);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
        }));

        Assert.All(results, result =>
        {
            Assert.Equal(registered.ServerId, result.ServerId);
            Assert.Equal(registered.ApiToken, result.ApiToken);
        });
        Assert.Equal(1, await CountVpsGameServersAsync());
        var row = await FindVpsGameServerAsync(registered.ServerId);
        Assert.NotNull(row);
        Assert.Equal(5, row.CurrentMatches);
        Assert.Equal(registered.ApiToken, row.ApiToken);
    }

    [Fact]
    public void VpsStartup_MissingApprovedHostConfigurationFailsClosed()
    {
        var configuration = VpsConfiguration();
        configuration.Remove("ApprovedHost:Id");
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration)));

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("ApprovedHost:Id", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void VpsStartup_RejectsMissingDatabaseConnection()
    {
        var configuration = VpsConfiguration();
        configuration["ConnectionStrings:DefaultConnection"] = "";
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration)));

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("ConnectionStrings:DefaultConnection", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void VpsStartup_RejectsPublicControlEndpoint()
    {
        var configuration = VpsConfiguration();
        configuration["ApprovedHost:ControlUrl"] = "http://game.example.com:28765/match/start";
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration)));

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("ApprovedHost:ControlUrl", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void VpsStartup_RejectsPublicIpv6UntilTheGameServerCanAdvertiseIt()
    {
        var configuration = VpsConfiguration();
        configuration["ApprovedHost:PublicHost"] = "2001:db8::1";
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration)));

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("ApprovedHost:PublicHost", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void VpsStartup_RejectsMalformedRegistrationBearerKey()
    {
        var configuration = VpsConfiguration();
        configuration["ApprovedHost:RegistrationKey"] = "invalid space secret-0123456789abcdef";
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration)));

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("bearer tokens", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void VpsStartup_RejectsReusedRegistrationAndControlSecrets()
    {
        var configuration = VpsConfiguration();
        configuration["MatchControl:Key"] = RegistrationKey;
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration)));

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("must be distinct", exception.ToString(), StringComparison.Ordinal);
    }

    public record RegisterResponse(Guid ServerId, string ApiToken);
}
