using System.Net;
using System.Net.Http.Json;
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
/// Integration tests for the game-server lifecycle endpoints (issue #49):
/// register (with upsert on ip:port), heartbeat, and the token-authenticated
/// deregister endpoint. Runs against an in-memory DB via WebApplicationFactory.
/// </summary>
public class ServerRegistrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ServerRegistrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            // TestServer connections all share the "unknown" IP in the rate
            // limiter; raise the POST budget so the suite doesn't trip 429s.
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimit:MaxRequestsPerWindow"] = "100000"
                }));

            builder.ConfigureServices(services =>
            {
                // Replace the real Postgres DbContext with an in-memory store so the
                // test does not need a database.
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor is not null)
                    services.Remove(descriptor);

                // Unique DB name per test instance: the in-memory store is shared
                // process-wide by name, and each test gets a fresh factory, so this
                // isolates row-count assertions from other tests in the class.
                var dbName = $"server-registration-test-{Guid.NewGuid():N}";
                services.AddDbContext<AppDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName));
            });
        });
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    private static readonly ServerRegistrationRequest DefaultRequest = new(
        Name: "Test Server",
        IpAddress: "203.0.113.10",
        Port: 9876,
        Region: "EU",
        IsOfficial: false,
        MaxConcurrentMatches: 15,
        CustomRulesJson: null);

    // Each test registers on a unique port so tests stay hermetic regardless of
    // whether register inserts or upserts.
    private static int _nextPort = 9000;

    private static ServerRegistrationRequest UniqueRequest() =>
        DefaultRequest with { Port = Interlocked.Increment(ref _nextPort) };

    private async Task<RegisterResponse> RegisterAsync(HttpClient client, ServerRegistrationRequest? request = null)
    {
        var response = await client.PostAsJsonAsync("/servers/register", request ?? UniqueRequest());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
    }

    private async Task<HttpResponseMessage> HeartbeatAsync(HttpClient client, Guid serverId, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/servers/{serverId}/heartbeat")
        {
            Content = JsonContent.Create(new { currentMatches = 0 })
        };
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

    public record RegisterResponse(Guid ServerId, string ApiToken);
}
