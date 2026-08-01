// MasterServer.Tests/LobbyHubTests.cs
using System.Security.Claims;
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

    private sealed class HubHarness(AppDbContext db)
    {
        public LobbyManager Lobbies { get; } = new();
        public Mock<IMatchLauncher> Launcher { get; } = new();
        public Mock<IHubCallerClients> Clients { get; } = new();
        public Mock<IGroupManager> Groups { get; } = new();
        public Mock<IClientProxy> GroupProxy { get; } = new();

        public LobbyHub CreateHub(string connectionId, long steamId, string username)
        {
            // Seed the user so the hub's DB lookup succeeds.
            db.Users.Add(new MasterServer.Data.Models.User
            {
                SteamId = steamId,
                Username = username,
                Mmr = 1000,
                CreatedAt = DateTime.UtcNow,
                LastLogin = DateTime.UtcNow
            });
            db.SaveChanges();

            // Each hub gets its own context mock — connectionId must not bleed
            // between hub instances (two players = two connections).
            var ctx = new Mock<HubCallerContext>();
            ctx.SetupGet(c => c.ConnectionId).Returns(connectionId);
            ctx.SetupGet(c => c.User!)
                .Returns(new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, steamId.ToString())
                }, "TestAuth")));

            Clients.Setup(c => c.Group(It.IsAny<string>())).Returns(GroupProxy.Object);
            Clients.SetupGet(c => c.Caller).Returns(Mock.Of<ISingleClientProxy>());

            var hub = new LobbyHub(
                Lobbies,
                db,
                Launcher.Object,
                Mock.Of<ILogger<LobbyHub>>())
            {
                Context = ctx.Object,
                Clients = Clients.Object,
                Groups = Groups.Object
            };
            return hub;
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
        harness.Launcher.Verify(l => l.LaunchAsync(It.IsAny<MatchStartingConfig>()), Times.Never);
    }

    [Fact]
    public async Task HostStart_Host_Broadcasts_MatchStarting_And_Launches()
    {
        var db = CreateInMemoryDb();
        var harness = new HubHarness(db);
        var hub1 = harness.CreateHub("c1", 101, "Alice");
        var hub2 = harness.CreateHub("c2", 202, "Bob");

        await hub1.JoinLobby(ServerId);
        await hub2.JoinLobby(ServerId);

        await hub1.HostStart();

        harness.GroupProxy.Verify(
            p => p.SendCoreAsync("MatchStarting", It.IsAny<object[]>(), default),
            Times.Once);
        harness.Launcher.Verify(l => l.LaunchAsync(It.Is<MatchStartingConfig>(
            c => c.ServerId == ServerId && c.Players.Count == 2)), Times.Once);
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
}
