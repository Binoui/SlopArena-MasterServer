// MasterServer.Tests/HttpMatchLauncherTests.cs
using System.Net;
using System.Net.Http.Json;
using MasterServer.Data;
using MasterServer.Data.Models;
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
    /// JSON body the launcher must parse (<c>{ "port": 9877 }</c>).
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = "";
        public Uri? RequestUri { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = """{"port":9877}""";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? ""
                : request.Content.ReadAsStringAsync(ct).Result;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(ResponseBody, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static AppDbContext SeedServer(string ip, int port)
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
        });
        db.SaveChanges();
        return db;
    }

    private static List<LobbyPlayer> TwoPlayerRoster() => new()
    {
        new LobbyPlayer(101, "Alice", "Manki", true, true, 1),
        new LobbyPlayer(202, "Bob", "FightGuy", true, false, 2),
    };

    [Fact]
    public async Task LaunchAsync_PostsRosterWithClassesAndEntityIds()
    {
        var handler = new StubHandler();
        var db = SeedServer("127.0.0.1", 9876);
        var launcher = new HttpMatchLauncher(db, NullLogger<HttpMatchLauncher>.Instance, new HttpClient(handler));

        var config = new MatchStartedConfig(ServerId, TwoPlayerRoster());
        int port = await launcher.LaunchAsync(config);

        Assert.Equal(9877, port);
        Assert.Equal("http://127.0.0.1:9876/match/start", handler.RequestUri!.ToString());

        var body = System.Text.Json.JsonDocument.Parse(handler.RequestBody);
        var players = body.RootElement.GetProperty("players");
        Assert.Equal(2, players.GetArrayLength());
        Assert.Equal("Manki", players[0].GetProperty("characterClass").GetString());
        Assert.Equal(1, players[0].GetProperty("entityId").GetInt32());
        Assert.Equal("FightGuy", players[1].GetProperty("characterClass").GetString());
        Assert.Equal(2, players[1].GetProperty("entityId").GetInt32());
        Assert.NotEmpty(body.RootElement.GetProperty("matchId").GetString()!);
        Assert.Equal("split", body.RootElement.GetProperty("arenaName").GetString());

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
    public async Task LaunchAsync_RespectsConfigArenaName()
    {
        var handler = new StubHandler();
        var db = SeedServer("10.0.0.5", 7000);
        var launcher = new HttpMatchLauncher(db, NullLogger<HttpMatchLauncher>.Instance, new HttpClient(handler));

        var config = new MatchStartedConfig(ServerId, TwoPlayerRoster(), 0, "stadium");
        await launcher.LaunchAsync(config);

        var body = System.Text.Json.JsonDocument.Parse(handler.RequestBody);
        Assert.Equal("stadium", body.RootElement.GetProperty("arenaName").GetString());
    }

    [Fact]
    public async Task LaunchAsync_UnknownServer_Throws()
    {
        var db = CreateInMemoryDb(); // no server registered
        var launcher = new HttpMatchLauncher(db, NullLogger<HttpMatchLauncher>.Instance, new HttpClient(new StubHandler()));

        var config = new MatchStartedConfig(ServerId, TwoPlayerRoster());

        await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.LaunchAsync(config));
    }

    [Fact]
    public async Task LaunchAsync_NonSuccess_Throws()
    {
        var handler = new StubHandler { Status = HttpStatusCode.BadRequest };
        var db = SeedServer("127.0.0.1", 9876);
        var launcher = new HttpMatchLauncher(db, NullLogger<HttpMatchLauncher>.Instance, new HttpClient(handler));

        var config = new MatchStartedConfig(ServerId, TwoPlayerRoster());

        await Assert.ThrowsAsync<HttpRequestException>(() => launcher.LaunchAsync(config));
    }

    [Fact]
    public async Task LaunchAsync_InvalidPortBody_Throws()
    {
        var handler = new StubHandler { ResponseBody = """{"port":0}""" };
        var db = SeedServer("127.0.0.1", 9876);
        var launcher = new HttpMatchLauncher(db, NullLogger<HttpMatchLauncher>.Instance, new HttpClient(handler));

        var config = new MatchStartedConfig(ServerId, TwoPlayerRoster());

        await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.LaunchAsync(config));
    }

    [Fact]
    public async Task LaunchAsync_Failure_RollsBackMatchRow()
    {
        // Issue #40: a failed launch must not leave an orphan Match row.
        var handler = new StubHandler { Status = HttpStatusCode.InternalServerError };
        var db = SeedServer("127.0.0.1", 9876);
        var launcher = new HttpMatchLauncher(db, NullLogger<HttpMatchLauncher>.Instance, new HttpClient(handler));

        var config = new MatchStartedConfig(ServerId, TwoPlayerRoster());

        await Assert.ThrowsAsync<HttpRequestException>(() => launcher.LaunchAsync(config));
        Assert.Empty(db.Matches);
    }
}
