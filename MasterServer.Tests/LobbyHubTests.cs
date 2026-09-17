// MasterServer.Tests/LobbyHubTests.cs
using System.Net.Http.Json;
using System.Text.Json;
using MasterServer.Data;
using MasterServer.DTOs;
using MasterServer.Lobbies;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MasterServer.Tests;

/// <summary>
/// Small real-wire lobby regression suite: an actual SignalR long-polling
/// client against a WebApplicationFactory-hosted hub — no mocked
/// Hub/Clients/GroupManager/SendCore forwarding. The exhaustive join/leave/
/// select/start state-machine rules already live in LobbyManagerTests (pure,
/// no SignalR) and HttpMatchLauncherTests; this file only defends the
/// end-to-end behaviors a hub-wiring regression could actually break: host
/// promotion, host-only start, full-lobby rejection, the pinned wire keys the
/// client parses, and match-port delivery through a fake launcher (no real
/// GameServer process needed).
/// </summary>
public class LobbyHubTests : IDisposable
{
    private readonly List<WebApplicationFactory<Program>> _factories = new();
    private readonly List<HubConnection> _connections = new();

    private sealed class FakeMatchLauncher(int port, string arena) : IMatchLauncher
    {
        public string DefaultArena { get; } = arena;
        public Task<int> LaunchAsync(MatchStartedConfig config) => Task.FromResult(port);
    }

    private sealed record ServerRegisterResponse(Guid ServerId, string ApiToken);

    private (WebApplicationFactory<Program> Factory, HttpClient Client) CreateFactory(int maxPlayersPerLobby = 4)
    {
        var databaseName = $"lobby-hub-test-{Guid.NewGuid():N}";
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // TestServer connections all share the "unknown" IP in the rate
            // limiter; raise the POST budget so long-polling + auth calls in
            // this suite never trip it (that exclusion itself is covered by
            // ChatIntegrationTests).
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:MaxRequestsPerWindow"] = "100000",
                ["Lobby:MaxPlayersPerLobby"] = maxPlayersPerLobby.ToString(),
            }));

            builder.ConfigureServices(services =>
            {
                Replace<DbContextOptions<AppDbContext>>(services);
                services.AddDbContext<AppDbContext>(opts => opts.UseInMemoryDatabase(databaseName));

                // A tiny fake for the external match-launch seam only — no
                // real GameServer/Unity process needed to prove port delivery.
                Replace<IMatchLauncher>(services);
                services.AddSingleton<IMatchLauncher>(new FakeMatchLauncher(9877, "TestArena"));
            });
        });
        _factories.Add(factory);
        return (factory, factory.CreateClient());
    }

    private static void Replace<TService>(IServiceCollection services)
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(TService));
        if (descriptor is not null)
            services.Remove(descriptor);
    }

    private static async Task<string> AuthGuestAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/auth/guest", new { });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<GuestAuthResponse>())!;
        return auth.Token;
    }

    private static async Task<Guid> RegisterServerAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/servers/register", new ServerRegistrationRequest(
            Name: "TestServer",
            IpAddress: "203.0.113.30",
            Port: 20000,
            Region: "EU",
            IsOfficial: false,
            MaxConcurrentMatches: 8,
            CustomRulesJson: null));
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<ServerRegisterResponse>())!;
        return body.ServerId;
    }

    private async Task<HubConnection> ConnectAsync(WebApplicationFactory<Program> factory, string token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "lobby"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        _connections.Add(connection);
        await connection.StartAsync();
        return connection;
    }

    /// <summary>Registers a handler before the caller triggers the action, avoiding a race on the push.</summary>
    private static Task<T> WaitForPushAsync<T>(HubConnection connection, string method, Func<T, bool> predicate, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = connection.On<T>(method, value =>
        {
            if (predicate(value))
                tcs.TrySetResult(value);
        });
        return AwaitWithTimeoutAsync(tcs, registration, timeout);
    }

    private static async Task<T> AwaitWithTimeoutAsync<T>(TaskCompletionSource<T> tcs, IDisposable registration, TimeSpan? timeout)
    {
        try
        {
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));
        }
        finally
        {
            registration.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var connection in _connections)
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var factory in _factories)
            factory.Dispose();
    }

    [Fact]
    public async Task JoinLobby_HostPromotion_PromotedHostCanStart()
    {
        var (factory, client) = CreateFactory();
        var aliceToken = await AuthGuestAsync(client);
        var bobToken = await AuthGuestAsync(client);
        var serverId = await RegisterServerAsync(client);

        var alice = await ConnectAsync(factory, aliceToken);
        var bob = await ConnectAsync(factory, bobToken);

        await alice.InvokeAsync("JoinLobby", serverId);
        await bob.InvokeAsync("JoinLobby", serverId);

        // Host (Alice, first joiner) leaves — Bob is promoted and can start.
        await alice.InvokeAsync("LeaveLobby");

        var starting = WaitForPushAsync<MatchStartingConfig>(bob, "MatchStarting", c => c.ServerId == serverId);
        await bob.InvokeAsync("HostStart");
        await starting;
    }

    [Fact]
    public async Task HostStart_NonHost_ThrowsHubException()
    {
        var (factory, client) = CreateFactory();
        var aliceToken = await AuthGuestAsync(client);
        var bobToken = await AuthGuestAsync(client);
        var serverId = await RegisterServerAsync(client);

        var alice = await ConnectAsync(factory, aliceToken);
        var bob = await ConnectAsync(factory, bobToken);

        await alice.InvokeAsync("JoinLobby", serverId); // Alice is host (first joiner)
        await bob.InvokeAsync("JoinLobby", serverId);

        await Assert.ThrowsAsync<HubException>(() => bob.InvokeAsync("HostStart"));
    }

    [Fact]
    public async Task JoinLobby_FullLobby_RejectsThirdPlayer()
    {
        // Capacity 2: only three real connections are needed to exercise the
        // rejection; the capacity state-machine rule itself is covered
        // exhaustively (0/1/exact/over) in LobbyManagerTests.
        var (factory, client) = CreateFactory(maxPlayersPerLobby: 2);
        var aliceToken = await AuthGuestAsync(client);
        var bobToken = await AuthGuestAsync(client);
        var carolToken = await AuthGuestAsync(client);
        var serverId = await RegisterServerAsync(client);

        var alice = await ConnectAsync(factory, aliceToken);
        var bob = await ConnectAsync(factory, bobToken);
        var carol = await ConnectAsync(factory, carolToken);

        await alice.InvokeAsync("JoinLobby", serverId);
        await bob.InvokeAsync("JoinLobby", serverId);

        await Assert.ThrowsAsync<HubException>(() => carol.InvokeAsync("JoinLobby", serverId));
    }

    [Fact]
    public async Task StartMatch_AllLockedIn_DeliversAssignedMatchPortAndArena_ViaFakeLauncher()
    {
        var (factory, client) = CreateFactory(maxPlayersPerLobby: 2);
        var aliceToken = await AuthGuestAsync(client);
        var bobToken = await AuthGuestAsync(client);
        var serverId = await RegisterServerAsync(client);

        var alice = await ConnectAsync(factory, aliceToken);
        var bob = await ConnectAsync(factory, bobToken);

        await alice.InvokeAsync("JoinLobby", serverId);
        await bob.InvokeAsync("JoinLobby", serverId);

        // Character selection: real wire push carries the consumer wire keys
        // (LobbyPlayer_WireKeys_Pinned_ForClientCodec pins the JSON shape).
        var characterSelected = WaitForPushAsync<LobbyPlayer>(bob, "CharacterSelected", p => p.Character == "Manki");
        await alice.InvokeAsync("SelectCharacter", "Manki");
        var selected = await characterSelected;
        Assert.Equal("Manki", selected.Character);

        await bob.InvokeAsync("SelectCharacter", "FightGuy");
        await alice.InvokeAsync("HostStart");

        // No real GameServer process exists — the fake launcher stands in for
        // the external launch seam and returns a known port/arena, proving
        // the broadcast carries whatever the launcher assigned.
        var started = WaitForPushAsync<MatchStartedConfig>(bob, "MatchStarted", c => c.ServerId == serverId);
        await alice.InvokeAsync("StartMatch");
        var config = await started;

        Assert.Equal(9877, config.MatchPort);
        Assert.Equal("TestArena", config.ArenaName);
        Assert.Equal(2, config.Players.Count);
    }

    [Fact]
    public void LobbyPlayer_WireKeys_Pinned_ForClientCodec()
    {
        // Issue #7: the C# properties are Username/Character, but the wire
        // keys must stay `name`/`characterSelection` — the client (SlopArena
        // repo) parses those exact keys in LobbyPayloadCodec, so the wire
        // cannot change until both repos ship in lockstep.
        var player = new LobbyPlayer(101, "Alice", "Manki", true, true, 1);

        var json = JsonSerializer.Serialize(player, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Alice", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("Manki", doc.RootElement.GetProperty("characterSelection").GetString());
        Assert.Equal(101, doc.RootElement.GetProperty("steamId").GetInt64());
        Assert.True(doc.RootElement.GetProperty("lockedIn").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("isHost").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("entityId").GetInt32());
    }
}
