// MasterServer.Tests/LobbyHubTests.cs
using System.Text.Json;
using System.Security.Claims;
using MasterServer.Chat;
using MasterServer.Data;
using MasterServer.Hubs;
using MasterServer.Lobbies;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace MasterServer.Tests;

/// <summary>
/// Unit tests for <see cref="LobbyHub"/> using a mocked SignalR context.
/// Verifies the broadcast contracts from issue #32:
/// <c>PlayerJoined</c>/<c>LobbyUpdated</c> on join, <c>PlayerLeft</c>/<c>LobbyUpdated</c>
/// on leave, <c>HostStart</c> rejected for non-hosts, <c>MatchStarting</c> broadcast + launcher
/// invoked for hosts.
/// </summary>
public class LobbyHubTests
{
    private static readonly Guid ServerId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        return db;
    }

    private sealed class HubHarness
    {
        private readonly AppDbContext _db;
        public LobbyManager Lobbies { get; }
        public ChatService Chat { get; }
        public Mock<IMatchLauncher> Launcher { get; } = new();
        public Mock<IHubCallerClients> Clients { get; } = new();
        public Mock<IGroupManager> Groups { get; } = new();
        public Mock<IClientProxy> GroupProxy { get; } = new();

        public HubHarness(AppDbContext db)
        {
            _db = db;
            Lobbies = new LobbyManager();
            Chat = new ChatService(Lobbies, TimeProvider.System);
            db.GameServers.Add(new MasterServer.Data.Models.GameServer
            {
                Id = ServerId,
                Name = "Test",
                IpAddress = "127.0.0.1",
                Port = 9876,
                Region = "EU",
                MaxConcurrentMatches = 8,
                LastHeartbeat = DateTime.UtcNow
            });
            db.SaveChanges();
            Launcher.Setup(l => l.LaunchAsync(It.IsAny<MatchStartedConfig>()))
                .ReturnsAsync(new MatchLaunchResult(
                    9877,
                    JsonDocument.Parse("""{"schemaVersion":1,"entries":[]}""").RootElement.Clone()));
        }

        public LobbyHub CreateHub(string connectionId, long steamId, string username)
        {
            _db.Users.Add(new MasterServer.Data.Models.User
            {
                SteamId = steamId,
                Username = username,
                Mmr = 1000,
                CreatedAt = DateTime.UtcNow,
                LastLogin = DateTime.UtcNow
            });
            _db.SaveChanges();

            var ctx = new Mock<HubCallerContext>();
            ctx.SetupGet(c => c.ConnectionId).Returns(connectionId);
            ctx.SetupGet(c => c.User!)
                .Returns(new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, steamId.ToString())
                }, "TestAuth")));

            Clients.Setup(c => c.Group(It.IsAny<string>())).Returns(GroupProxy.Object);
            Clients.SetupGet(c => c.Caller).Returns(Mock.Of<ISingleClientProxy>());

            return new LobbyHub(
                Lobbies,
                _db,
                Launcher.Object,
                Mock.Of<ILogger<LobbyHub>>(),
                Chat)
            {
                Context = ctx.Object,
                Clients = Clients.Object,
                Groups = Groups.Object
            };
        }
    }

    private static string GroupName(Guid id) => $"lobby:{id}";

    [Fact]
    public async Task JoinLobby_FirstPlayer_Broadcasts_PlayerJoined_And_LobbyUpdated()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub = harness.CreateHub("c1", 101, "Alice");

        await hub.JoinLobby(ServerId);

        // PlayerJoined + LobbyUpdated are both broadcast to the lobby group.
        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("PlayerJoined", It.IsAny<object[]>(), default),
            Times.Once);
        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("LobbyUpdated", It.IsAny<object[]>(), default),
            Times.Once);
        // Added to the SignalR group for the server.
        harness.Groups.Verify(
            g => g.AddToGroupAsync("c1", GroupName(ServerId), default),
            Times.Once);
    }

    [Fact]
    public async Task JoinLobby_TwoPlayers_BothReceive_Broadcasts()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");
        var hub2 = harness.CreateHub("c2", 202, "Bob");

        await hub1.JoinLobby(ServerId);
        await hub2.JoinLobby(ServerId);

        // Two joins → two PlayerJoined, two LobbyUpdated.
        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("PlayerJoined", It.IsAny<object[]>(), default),
            Times.Exactly(2));
        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("LobbyUpdated", It.IsAny<object[]>(), default),
            Times.Exactly(2));
    }

    [Fact]
    public async Task LeaveLobby_Broadcasts_PlayerLeft_And_LobbyUpdated_To_Remaining()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");
        var hub2 = harness.CreateHub("c2", 202, "Bob");

        await hub1.JoinLobby(ServerId);
        await hub2.JoinLobby(ServerId);
        // Clear invocations from the join phase.
        harness.GroupProxy.Reset();

        await hub2.LeaveLobby();

        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("PlayerLeft", It.IsAny<object[]>(), default),
            Times.Once);
        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("LobbyUpdated", It.IsAny<object[]>(), default),
            Times.Once);
        harness.Groups.Verify(
            g => g.RemoveFromGroupAsync("c2", GroupName(ServerId), default),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task HostStart_NonHost_Throws_HubException()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");
        var hub2 = harness.CreateHub("c2", 202, "Bob");

        await hub1.JoinLobby(ServerId);
        await hub2.JoinLobby(ServerId);

        await Assert.ThrowsAsync<HubException>(() => hub2.HostStart());

        // Non-host start must NOT broadcast MatchStarting nor launch the match.
        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("MatchStarting", It.IsAny<object[]>(), default),
            Times.Never);
        harness.Launcher.Verify(l => l.LaunchAsync(It.IsAny<MatchStartedConfig>()), Times.Never);
    }

    [Fact]
    public async Task HostStart_Host_Broadcasts_MatchStarting_But_DoesNot_Launch()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");
        var hub2 = harness.CreateHub("c2", 202, "Bob");

        await hub1.JoinLobby(ServerId);
        await hub2.JoinLobby(ServerId);

        await hub1.HostStart();

        // HostStart transitions to char select: broadcasts MatchStarting but
        // does NOT launch the game server (that's StartMatch, issue #34).
        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("MatchStarting", It.IsAny<object[]>(), default),
            Times.Once);
        harness.Launcher.Verify(l => l.LaunchAsync(It.IsAny<MatchStartedConfig>()), Times.Never);
    }

    [Fact]
    public async Task OnDisconnectedAsync_RemovesPlayer_Broadcasts_To_Survivors()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");
        var hub2 = harness.CreateHub("c2", 202, "Bob");

        await hub1.JoinLobby(ServerId);
        await hub2.JoinLobby(ServerId);
        harness.GroupProxy.Reset();

        await hub2.OnDisconnectedAsync(null);

        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("PlayerLeft", It.IsAny<object[]>(), default),
            Times.Once);
        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("LobbyUpdated", It.IsAny<object[]>(), default),
            Times.Once);
    }

    [Fact]
    public async Task OnDisconnectedAsync_LastPlayer_NoBroadcast()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");

        await hub1.JoinLobby(ServerId);

        // Clear invocations from the join phase — we only care about disconnect broadcasts.
        harness.GroupProxy.Reset();

        // Last player leaving → empty lobby → nothing to broadcast to.
        await hub1.OnDisconnectedAsync(null);

        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("PlayerLeft", It.IsAny<object[]>(), default),
            Times.Never);
        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("LobbyUpdated", It.IsAny<object[]>(), default),
            Times.Never);
    }

    [Fact]
    public async Task HostStart_PromotedHost_CanStart()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");
        var hub2 = harness.CreateHub("c2", 202, "Bob");

        await hub1.JoinLobby(ServerId);
        await hub2.JoinLobby(ServerId);
        await hub1.LeaveLobby(); // host leaves → Bob promoted

        // Bob (now host) can start.
        await hub2.HostStart();

        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("MatchStarting", It.IsAny<object[]>(), default),
            Times.Once);
    }
    [Fact]
    public async Task SelectCharacter_LocksIn_And_Broadcasts_CharacterSelected_And_LobbyUpdated()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");

        await hub1.JoinLobby(ServerId);
        harness.GroupProxy.Reset();

        await hub1.SelectCharacter("Manki");

        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("CharacterSelected", It.IsAny<object[]>(), default),
            Times.Once);
        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("LobbyUpdated", It.IsAny<object[]>(), default),
            Times.Once);
    }

    [Fact]
    public async Task SelectCharacter_NonMember_Throws_HubException()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");

        // Never joined a lobby.
        await Assert.ThrowsAsync<HubException>(() => hub1.SelectCharacter("Manki"));
    }

    [Fact]
    public async Task SelectCharacter_CanChangePick_Before_LockIn()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");

        await hub1.JoinLobby(ServerId);

        await hub1.SelectCharacter("Manki");
        await hub1.SelectCharacter("FightGuy");

        // Two CharacterSelected broadcasts (one per call).
        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("CharacterSelected", It.IsAny<object[]>(), default),
            Times.Exactly(2));
    }

    [Fact]
    public async Task StartMatch_NonHost_Throws_HubException()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");
        var hub2 = harness.CreateHub("c2", 202, "Bob");

        await hub1.JoinLobby(ServerId);
        await hub2.JoinLobby(ServerId);
        await hub1.SelectCharacter("Manki");
        await hub2.SelectCharacter("FightGuy");

        await Assert.ThrowsAsync<HubException>(() => hub2.StartMatch("training"));

        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("MatchStarted", It.IsAny<object[]>(), default),
            Times.Never);
        harness.Launcher.Verify(l => l.LaunchAsync(It.IsAny<MatchStartedConfig>()), Times.Never);
    }

    [Fact]
    public async Task StartMatch_NotAllLockedIn_Throws_HubException()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");
        var hub2 = harness.CreateHub("c2", 202, "Bob");

        await hub1.JoinLobby(ServerId);
        await hub2.JoinLobby(ServerId);

        // Only Alice locks in; Bob hasn't.
        await hub1.SelectCharacter("Manki");
        harness.GroupProxy.Reset();

        await Assert.ThrowsAsync<HubException>(() => hub1.StartMatch("training"));

        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("MatchStarted", It.IsAny<object[]>(), default),
            Times.Never);
        harness.Launcher.Verify(l => l.LaunchAsync(It.IsAny<MatchStartedConfig>()), Times.Never);
    }

    [Fact]
    public async Task StartMatch_AllLockedIn_Broadcasts_MatchStarted_And_Launches()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");
        var hub2 = harness.CreateHub("c2", 202, "Bob");

        await hub1.JoinLobby(ServerId);
        await hub2.JoinLobby(ServerId);
        await hub1.SelectCharacter("Manki");
        await hub2.SelectCharacter("FightGuy");
        harness.GroupProxy.Reset();

        await hub1.StartMatch("training");

        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("MatchStarted", It.IsAny<object[]>(), default),
            Times.Once);
        // The launcher is invoked with the roster + entity IDs + the chosen arena (issue #35).
        harness.Launcher.Verify(l => l.LaunchAsync(It.Is<MatchStartedConfig>(
            c => c.ServerId == ServerId
              && c.Players.Count == 2
              && c.Players[0].EntityId == 1
              && c.Players[1].EntityId == 2
              && c.Players[0].Character == "Manki"
              && c.Players[1].Character == "FightGuy"
              && c.ArenaName == "training")), Times.Once);
    }

    [Fact]
    public async Task StartMatch_AllLockedIn_BroadcastCarriesMatchPortAndArena()
    {
        // Issue #35: the MatchStarted broadcast must carry the game server's
        // assigned UDP port + arena so clients connect to the right place.
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");
        var hub2 = harness.CreateHub("c2", 202, "Bob");

        await hub1.JoinLobby(ServerId);
        await hub2.JoinLobby(ServerId);
        await hub1.SelectCharacter("Manki");
        await hub2.SelectCharacter("FightGuy");
        harness.GroupProxy.Reset();

        await hub1.StartMatch("slop_court");

        harness.Launcher.Verify(l => l.LaunchAsync(It.Is<MatchStartedConfig>(
            c => c.ArenaName == "slop_court")), Times.Once);
        // The broadcast config carries the port the launcher returned (9877).
        // Cast (not `is` pattern) keeps this inside a Moq expression tree.
        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("MatchStarted",
                It.Is<object[]>(args => args.Length > 0
                    && ((MatchStartedConfig)args[0]).MatchPort == 9877
                    && ((MatchStartedConfig)args[0]).ArenaName == "slop_court"),
                default),
            Times.Once);
    }

    [Fact]
    public async Task StartMatch_MatchStartedFailure_ReleasesRosterButRetainsServerMembership()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");
        var hub2 = harness.CreateHub("c2", 202, "Bob");

        await hub1.JoinLobby(ServerId);
        await hub2.JoinLobby(ServerId);
        await hub1.SelectCharacter("Manki");
        await hub2.SelectCharacter("FightGuy");
        harness.GroupProxy.Reset();
        harness.GroupProxy
            .Setup(p => p.SendCoreAsync("MatchStarted", It.IsAny<object[]>(), default))
            .ThrowsAsync(new InvalidOperationException("broadcast failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => hub1.StartMatch("training"));

        Assert.Null(harness.Lobbies.GetSnapshot("c1"));
        Assert.Null(harness.Lobbies.GetSnapshot("c2"));
        Assert.Equal(ServerId, harness.Lobbies.GetServerId("c1"));
        Assert.Equal(ServerId, harness.Lobbies.GetServerId("c2"));
        harness.Groups.Verify(
            g => g.RemoveFromGroupAsync("c1", GroupName(ServerId), default),
            Times.Once);
        harness.Groups.Verify(
            g => g.RemoveFromGroupAsync("c2", GroupName(ServerId), default),
            Times.Once);
    }

    [Fact]
    public async Task StartMatch_SinglePlayer_Throws_HubException()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");

        await hub1.JoinLobby(ServerId);
        await hub1.SelectCharacter("Manki");

        await Assert.ThrowsAsync<HubException>(() => hub1.StartMatch("training"));
    }

    // ── StartStageSelect (stage select transition, before StartMatch) ──

    [Fact]
    public async Task StartStageSelect_AllLockedIn_Broadcasts_StageSelect_DoesNotLaunch()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");
        var hub2 = harness.CreateHub("c2", 202, "Bob");

        await hub1.JoinLobby(ServerId);
        await hub2.JoinLobby(ServerId);
        await hub1.SelectCharacter("Manki");
        await hub2.SelectCharacter("FightGuy");
        harness.GroupProxy.Reset();

        await hub1.StartStageSelect();

        // Broadcasts the stage-select transition, but must NOT launch the game
        // server — that happens on StartMatch once the host picks an arena.
        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("StageSelect", It.IsAny<object[]>(), default),
            Times.Once);
        harness.Launcher.Verify(l => l.LaunchAsync(It.IsAny<MatchStartedConfig>()), Times.Never);
    }

    [Fact]
    public async Task StartStageSelect_NonHost_Throws_HubException()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");
        var hub2 = harness.CreateHub("c2", 202, "Bob");

        await hub1.JoinLobby(ServerId);
        await hub2.JoinLobby(ServerId);
        await hub1.SelectCharacter("Manki");
        await hub2.SelectCharacter("FightGuy");

        await Assert.ThrowsAsync<HubException>(() => hub2.StartStageSelect());

        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("StageSelect", It.IsAny<object[]>(), default),
            Times.Never);
    }

    [Fact]
    public async Task StartStageSelect_NotAllLockedIn_Throws_HubException()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");
        var hub2 = harness.CreateHub("c2", 202, "Bob");

        await hub1.JoinLobby(ServerId);
        await hub2.JoinLobby(ServerId);
        // Only Alice locked in.
        await hub1.SelectCharacter("Manki");

        await Assert.ThrowsAsync<HubException>(() => hub1.StartStageSelect());

        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("StageSelect", It.IsAny<object[]>(), default),
            Times.Never);
    }

    [Fact]
    public async Task JoinLobby_FullLobby_Throws_HubException()
    {
        // Issue #6: a fifth player joining a full (4-player) lobby is rejected
        // with a HubException and nothing is broadcast or group-added for them.
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        for (int i = 0; i < 4; i++)
        {
            var h = harness.CreateHub($"c{i}", 100 + i, $"P{i}");
            await h.JoinLobby(ServerId);
        }
        var fifth = harness.CreateHub("c4", 505, "Eve");

        await Assert.ThrowsAsync<HubException>(() => fifth.JoinLobby(ServerId));

        // Exactly 4 PlayerJoined broadcasts — the rejected join adds none.
        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("PlayerJoined", It.IsAny<object[]>(), default),
            Times.Exactly(4));
        harness.Groups.Verify(
            g => g.AddToGroupAsync("c4", GroupName(ServerId), default),
            Times.Never);
    }

    [Fact]
    public void LobbyPlayer_WireKeys_Pinned_ForClientCodec()
    {
        // Issue #7: the C# properties are Username/Character, but the wire keys
        // must stay `name`/`characterSelection` — the client (SlopArena repo)
        // parses those exact keys in LobbyPayloadCodec. Serialize with the same
        // camelCase policy SignalR's JSON hub protocol uses.
        var player = new LobbyPlayer(101, "Alice", "Manki", true, true, 1);

        var json = System.Text.Json.JsonSerializer.Serialize(
            player,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("Alice", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("Manki", doc.RootElement.GetProperty("characterSelection").GetString());
        Assert.Equal(101, doc.RootElement.GetProperty("steamId").GetInt64());
        Assert.True(doc.RootElement.GetProperty("lockedIn").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("isHost").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("entityId").GetInt32());
    }
}
