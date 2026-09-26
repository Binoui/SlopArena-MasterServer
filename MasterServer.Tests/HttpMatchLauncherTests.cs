// MasterServer.Tests/HttpMatchLauncherTests.cs
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using MasterServer.Data;
using MasterServer.Data.Models;
using MasterServer.Configuration;
using MasterServer.Lobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MasterServer.Tests;

/// <summary>
/// Tests for <see cref="HttpMatchLauncher"/> (issue #35): the master server's
/// HTTP bridge to the game server's <c>POST /match/start</c> endpoint. Proves the
/// roster + character classes + entity IDs leave the master server with the
/// right wire shape, and the returned match port flows back to the hub.
/// </summary>
public class HttpMatchLauncherTests
{
    private static readonly Guid ServerId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// A stub HttpMessageHandler that records the request and replies with a
    /// JSON body the launcher must parse (<c>{ "port": 9877, "content": {} }</c>).
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = "";
        public Uri? RequestUri { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }
        public bool SteamResponse { get; set; }
        public string ResponseSteamId { get; set; } = "90293421017699331";
        public int AbortCalls { get; private set; }
        public string ResponseBody { get; set; } = """{"port":9877,"content":{"schemaVersion":1,"entries":[]}}""";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Authorization = request.Headers.Authorization;
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? ""
                : request.Content.ReadAsStringAsync(ct).Result;
            if (request.RequestUri?.AbsolutePath == "/match/abort")
                AbortCalls++;
            var response = ResponseBody;
            if (SteamResponse && request.RequestUri?.AbsolutePath == "/match/start")
            {
                using var doc = JsonDocument.Parse(RequestBody);
                response = JsonSerializer.Serialize(new
                {
                    matchId = doc.RootElement.GetProperty("matchId").GetString(),
                    serverSteamId = ResponseSteamId,
                    virtualPort = 0,
                    protocolVersion = 2,
                    contentHash = new string('a', 64),
                    content = new { schemaVersion = 1, entries = Array.Empty<object>() }
                });
            }
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static AppDbContext SeedServer(string ip, int port, string? steamId = null)
    {
        var db = CreateInMemoryDb();
        db.GameServers.Add(new GameServer
        {
            Id = ServerId,
            Name = "Test Server",
            IpAddress = ip,
            Port = port,
            Region = "EU",
            ApiToken = "tok",
            LastHeartbeat = DateTime.UtcNow,
            SteamId = steamId,
            ProtocolVersion = steamId is null ? 0 : 2,
            InstanceId = steamId is null ? null : Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            CatalogHash = steamId is null ? null : new string('a', 64),
        });
        if (steamId is not null)
            db.Users.AddRange(
                new User { SteamId = 101, AuthProvider = "steam", Username = "Alice" },
                new User { SteamId = 202, AuthProvider = "steam", Username = "Bob" });
        db.SaveChanges();
        return db;
    }

    private static HttpMatchLauncher CreateLauncher(
        AppDbContext db, HttpClient http, MasterDeploymentOptions deployment) =>
        new(db, NullLogger<HttpMatchLauncher>.Instance, http, deployment);

    private static List<LobbyPlayer> TwoPlayerRoster() => new()
    {
        new LobbyPlayer(101, "Alice", "Manki", true, true, 1),
        new LobbyPlayer(202, "Bob", "FightGuy", true, false, 2),
    };

    /// <summary>A roster of <paramref name="count"/> locked-in players with 1-based entity IDs.</summary>
    private static List<LobbyPlayer> Roster(int count) => Enumerable.Range(1, count)
        .Select(i => new LobbyPlayer(100L + i, $"P{i}", "Manki", true, i == 1, i))
        .ToList();

    [Fact]
    public async Task LaunchAsync_PostsRosterWithClassesAndEntityIds()
    {
        var handler = new StubHandler();
        var db = SeedServer("127.0.0.1", 9876);
        var launcher = CreateLauncher(db, new HttpClient(handler), MasterDeploymentOptions.Development);

        var config = new MatchStartedConfig(ServerId, TwoPlayerRoster(), 0, "slop_court");
        var launch = await launcher.LaunchAsync(config);

        Assert.Equal(9877, launch.MatchPort);
        Assert.Equal(JsonValueKind.Object, launch.Content.ValueKind);
        Assert.Equal(1, launch.Content.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("http://127.0.0.1:9876/match/start", handler.RequestUri!.ToString());
        Assert.Null(handler.Authorization);

        var body = System.Text.Json.JsonDocument.Parse(handler.RequestBody);
        var players = body.RootElement.GetProperty("players");
        Assert.Equal(2, players.GetArrayLength());
        Assert.Equal("Manki", players[0].GetProperty("characterClass").GetString());
        Assert.Equal(1, players[0].GetProperty("entityId").GetInt32());
        Assert.Equal("FightGuy", players[1].GetProperty("characterClass").GetString());
        Assert.Equal(2, players[1].GetProperty("entityId").GetInt32());
        Assert.NotEmpty(body.RootElement.GetProperty("matchId").GetString()!);
        Assert.Equal("slop_court", body.RootElement.GetProperty("arenaName").GetString());

        // Issue #40: the Match row is created up front with the same Guid posted
        // to the game server, winner still NULL, 2-player roster → no P3/P4.
        var match = Assert.Single(db.Matches);
        Assert.Equal(Guid.Parse(body.RootElement.GetProperty("matchId").GetString()!), match.Id);
        Assert.Equal(101, match.Player1SteamId);
        Assert.Equal(202, match.Player2SteamId);
        Assert.Null(match.Player3SteamId);
        Assert.Null(match.Player4SteamId);
        Assert.Null(match.WinnerSteamId);
        Assert.Equal("EU", match.ServerRegion);
        Assert.Null(match.EndedAt);
    }

    [Fact]
    public async Task LaunchAsync_VpsUsesPrivateControlUrlAndSeparateBearerKey()
    {
        var handler = new StubHandler { SteamResponse = true };
        var deployment = new MasterDeploymentOptions(
            "vps",
            ServerId,
            "registration-key",
            "public.example",
            9876,
            new Uri("http://gameserver.internal:9876/match/start"),
            "match-control-key");
        var db = SeedServer("attacker.example", 4321, "90293421017699331");
        var launcher = CreateLauncher(db, new HttpClient(handler), deployment);

        var result = await launcher.LaunchAsync(new MatchStartedConfig(ServerId, TwoPlayerRoster(), 0, "slop_court"));

        Assert.Equal("http://gameserver.internal:9876/match/start", handler.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Authorization!.Scheme);
        Assert.Equal("match-control-key", handler.Authorization.Parameter);
        Assert.NotNull(result.Descriptor);
        Assert.Equal("90293421017699331", result.Descriptor!.ServerSteamId);
        Assert.Equal(result.Descriptor.MatchId, Assert.Single(db.Matches).Id);
        Assert.Equal(2, result.Descriptor.ProtocolVersion);
        Assert.Equal(0, result.Descriptor.VirtualPort);
        Assert.Equal(new string('a', 64), result.Descriptor.ContentHash);
        using var posted = JsonDocument.Parse(handler.RequestBody);
        Assert.Equal(result.Descriptor.ContentHash, posted.RootElement.GetProperty("catalogHash").GetString());
    }

    [Fact]
    public async Task VpsLauncher_RejectsUnexpectedHostAndAbortsAllocatedMatch()
    {
        var handler = new StubHandler { SteamResponse = true, ResponseSteamId = "90293421017699399" };
        var db = SeedServer("attacker.example", 4321, "90293421017699331");
        var deployment = new MasterDeploymentOptions(
            "vps", ServerId, "registration-key", "public.example", 9876,
            new Uri("http://gameserver.internal:9876/match/start"), "match-control-key");
        var launcher = CreateLauncher(db, new HttpClient(handler), deployment);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.LaunchAsync(new MatchStartedConfig(ServerId, TwoPlayerRoster(), 0, "slop_court")));

        Assert.Empty(db.Matches);
        Assert.Equal(1, handler.AbortCalls);
        Assert.Equal("http://gameserver.internal:9876/match/abort", handler.RequestUri!.ToString());
    }

    [Fact]
    public async Task LaunchAsync_VpsRejectsUnapprovedPersistedServerBeforePosting()
    {
        var handler = new StubHandler();
        var db = SeedServer("attacker.example", 4321);
        var deployment = new MasterDeploymentOptions(
            "vps", Guid.NewGuid(), "registration-secret-0123456789abcdef",
            "game.example.com", 9876, new Uri("http://gameserver.internal:9876/match/start"),
            "control-secret-0123456789abcdef01234567");
        var launcher = CreateLauncher(db, new HttpClient(handler), deployment);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.LaunchAsync(new MatchStartedConfig(ServerId, TwoPlayerRoster(), 0, "slop_court")));
        Assert.Empty(db.Matches);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task LaunchAsync_RespectsConfigArenaName()
    {
        var handler = new StubHandler();
        var db = SeedServer("10.0.0.5", 7000);
        var launcher = CreateLauncher(db, new HttpClient(handler), MasterDeploymentOptions.Development);

        var config = new MatchStartedConfig(ServerId, TwoPlayerRoster(), 0, "stadium");
        await launcher.LaunchAsync(config);

        var body = System.Text.Json.JsonDocument.Parse(handler.RequestBody);
        Assert.Equal("stadium", body.RootElement.GetProperty("arenaName").GetString());
    }

    [Fact]
    public async Task LaunchAsync_UnknownServer_Throws()
    {
        var db = CreateInMemoryDb(); // no server registered
        var launcher = CreateLauncher(db, new HttpClient(new StubHandler()), MasterDeploymentOptions.Development);

        var config = new MatchStartedConfig(ServerId, TwoPlayerRoster(), 0, "slop_court");

        await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.LaunchAsync(config));
    }

    [Fact]
    public async Task LaunchAsync_NonSuccess_Throws()
    {
        var handler = new StubHandler { Status = HttpStatusCode.BadRequest };
        var db = SeedServer("127.0.0.1", 9876);
        var launcher = CreateLauncher(db, new HttpClient(handler), MasterDeploymentOptions.Development);

        var config = new MatchStartedConfig(ServerId, TwoPlayerRoster(), 0, "slop_court");

        await Assert.ThrowsAsync<HttpRequestException>(() => launcher.LaunchAsync(config));
    }

    [Fact]
    public async Task LaunchAsync_InvalidPortBody_Throws()
    {
        var handler = new StubHandler { ResponseBody = """{"port":0}""" };
        var db = SeedServer("127.0.0.1", 9876);
        var launcher = CreateLauncher(db, new HttpClient(handler), MasterDeploymentOptions.Development);

        var config = new MatchStartedConfig(ServerId, TwoPlayerRoster(), 0, "slop_court");

        await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.LaunchAsync(config));
    }

    [Fact]
    public async Task LaunchAsync_Failure_RollsBackMatchRow()
    {
        // Issue #40: a failed launch must not leave an orphan Match row.
        var handler = new StubHandler { Status = HttpStatusCode.InternalServerError };
        var db = SeedServer("127.0.0.1", 9876);
        var launcher = CreateLauncher(db, new HttpClient(handler), MasterDeploymentOptions.Development);

        var config = new MatchStartedConfig(ServerId, TwoPlayerRoster(), 0, "slop_court");

        await Assert.ThrowsAsync<HttpRequestException>(() => launcher.LaunchAsync(config));
        Assert.Empty(db.Matches);
    }

    // ── Player-count contract (issue #6): the roster must fit the persisted Match ──

    [Fact]
    public async Task LaunchAsync_TooManyPlayers_Throws_BeforePersistOrPost()
    {
        var handler = new StubHandler();
        var db = SeedServer("127.0.0.1", 9876);
        var launcher = CreateLauncher(db, new HttpClient(handler), MasterDeploymentOptions.Development);

        var config = new MatchStartedConfig(ServerId, Roster(5), 0, "slop_court");

        await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.LaunchAsync(config));

        // No Match row and no HTTP call to the game server.
        Assert.Empty(db.Matches);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task LaunchAsync_SinglePlayer_Throws_BeforePersistOrPost()
    {
        var handler = new StubHandler();
        var db = SeedServer("127.0.0.1", 9876);
        var launcher = CreateLauncher(db, new HttpClient(handler), MasterDeploymentOptions.Development);

        var config = new MatchStartedConfig(ServerId, Roster(1), 0, "slop_court");

        await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.LaunchAsync(config));

        Assert.Empty(db.Matches);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task LaunchAsync_FourPlayers_PersistsP3P4_AndPosts()
    {
        var handler = new StubHandler();
        var db = SeedServer("127.0.0.1", 9876);
        var launcher = CreateLauncher(db, new HttpClient(handler), MasterDeploymentOptions.Development);

        var config = new MatchStartedConfig(ServerId, Roster(4), 0, "slop_court");
        var launch = await launcher.LaunchAsync(config);

        Assert.Equal(9877, launch.MatchPort);
        var match = Assert.Single(db.Matches);
        Assert.NotNull(match.Player3SteamId);
        Assert.NotNull(match.Player4SteamId);
        var body = System.Text.Json.JsonDocument.Parse(handler.RequestBody);
        Assert.Equal(4, body.RootElement.GetProperty("players").GetArrayLength());
    }
}
