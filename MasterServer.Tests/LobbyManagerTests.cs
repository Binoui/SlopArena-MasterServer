// MasterServer.Tests/LobbyManagerTests.cs
using MasterServer.Lobbies;
using Xunit;

namespace MasterServer.Tests;

/// <summary>
/// Pure state-machine tests for <see cref="LobbyManager"/> — no SignalR, no DB.
/// These cover the join/leave/host-promotion/host-check contracts from issue #32.
/// </summary>
public class LobbyManagerTests
{
    private static readonly Guid ServerA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ServerB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void JoinLobby_FirstPlayer_IsHost()
    {
        var mgr = new LobbyManager();

        var result = mgr.JoinLobby(ServerA, "c1", 101, "Alice");

        Assert.True(result.Player!.IsHost);
        Assert.Equal("Alice", result.Player!.Username);
        Assert.Equal(101, result.Player!.SteamId);
        Assert.Single(result.Snapshot!.Players);
        Assert.Equal(ServerA, result.Snapshot!.ServerId);
    }

    [Fact]
    public void JoinLobby_SecondPlayer_NotHost()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");

        var result = mgr.JoinLobby(ServerA, "c2", 202, "Bob");

        Assert.False(result.Player!.IsHost);
        Assert.Equal(2, result.Snapshot!.Players.Count);
    }

    [Fact]
    public void LobbyUpdated_Pushed_On_Join()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");

        var result = mgr.JoinLobby(ServerA, "c2", 202, "Bob");

        Assert.Equal(2, result.Snapshot!.Players.Count);
        Assert.Contains(result.Snapshot!.Players, p => p.SteamId == 101);
        Assert.Contains(result.Snapshot!.Players, p => p.SteamId == 202);
    }

    [Fact]
    public void LeaveLobby_RemovesPlayer()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.JoinLobby(ServerA, "c2", 202, "Bob");

        var result = mgr.LeaveLobby("c2", 202);

        Assert.Equal(ServerA, result.ServerId);
        Assert.NotNull(result.Player);
        Assert.Equal(202, result.Player!.SteamId);
        Assert.Single(result.Snapshot!.Players);
    }

    [Fact]
    public void LeaveLobby_EmptyLobby_ReapsIt()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");

        mgr.LeaveLobby("c1", 101);

        // Re-joining after the lobby was reaped starts fresh → first player is host again.
        var rejoin = mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        Assert.True(rejoin.Player!.IsHost);
    }

    [Fact]
    public void LeaveLobby_HostDeparts_PromotesNextPlayer()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.JoinLobby(ServerA, "c2", 202, "Bob");

        // Alice (host) leaves — Bob should be promoted.
        mgr.LeaveLobby("c1", 101);

        var snapshot = mgr.GetSnapshot("c2");
        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Players);
        Assert.True(snapshot.Players[0].IsHost);
        Assert.Equal(202, snapshot.Players[0].SteamId);
    }

    [Fact]
    public void LeaveLobby_NonMember_ReturnsNull()
    {
        var mgr = new LobbyManager();

        var result = mgr.LeaveLobby("nobody", 999);

        Assert.Null(result.ServerId);
        Assert.Null(result.Player);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void LeaveLobby_LastPlayer_SnapshotIsNull()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");

        var result = mgr.LeaveLobby("c1", 101);

        Assert.Equal(ServerA, result.ServerId);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void JoinLobby_RejoinSameServer_DropsPriorMembership()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");

        // Same connection switches servers.
        var result = mgr.JoinLobby(ServerB, "c1", 101, "Alice");

        Assert.Equal(ServerB, result.Snapshot!.ServerId);
        Assert.True(result.Player!.IsHost);

        // ServerA's lobby should be empty/reaped.
        var fresh = mgr.JoinLobby(ServerA, "c3", 303, "Carol");
        Assert.True(fresh.Player!.IsHost);
    }

    [Fact]
    public void TryHostStart_Host_Succeeds()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.JoinLobby(ServerA, "c2", 202, "Bob");

        var result = mgr.TryHostStart("c1");

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.NotNull(result.Config);
        Assert.Equal(ServerA, result.Config!.ServerId);
        Assert.Equal(2, result.Config.Players.Count);
    }

    [Fact]
    public void TryHostStart_NonHost_Rejected()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.JoinLobby(ServerA, "c2", 202, "Bob");

        var result = mgr.TryHostStart("c2");

        Assert.False(result.Success);
        Assert.Null(result.Config);
        Assert.Contains("host", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryHostStart_NoLobby_Rejected()
    {
        var mgr = new LobbyManager();

        var result = mgr.TryHostStart("nobody");

        Assert.False(result.Success);
        Assert.Contains("not in a lobby", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryHostStart_PromotedHost_CanStart()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.JoinLobby(ServerA, "c2", 202, "Bob");
        mgr.LeaveLobby("c1", 101); // Alice leaves → Bob promoted.

        var result = mgr.TryHostStart("c2");

        Assert.True(result.Success);
    }

    [Fact]
    public void GetSnapshot_NonMember_ReturnsNull()
    {
        var mgr = new LobbyManager();

        Assert.Null(mgr.GetSnapshot("nobody"));
    }

    [Fact]
    public void JoinLobby_DistinctServers_DistinctLobbies()
    {
        var mgr = new LobbyManager();

        var a = mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        var b = mgr.JoinLobby(ServerB, "c2", 202, "Bob");

        Assert.Equal(ServerA, a.Snapshot!.ServerId);
        Assert.Equal(ServerB, b.Snapshot!.ServerId);
        Assert.True(a.Player!.IsHost);
        Assert.True(b.Player!.IsHost);
    }

    [Fact]
    public void JoinLobby_SwitchingServers_SurfacesDeparture_FromOldLobby()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.JoinLobby(ServerA, "c2", 202, "Bob");

        // c2 leaves ServerA for ServerB — departure must be surfaced.
        var result = mgr.JoinLobby(ServerB, "c2", 202, "Bob");

        Assert.NotNull(result.Departure);
        Assert.Equal(ServerA, result.Departure!.ServerId);
        Assert.Equal(202, result.Departure!.Player!.SteamId);
        // Old lobby has one survivor (Alice).
        Assert.Single(result.Departure!.Snapshot!.Players);
        // New lobby has just Bob.
        Assert.Equal(ServerB, result.Snapshot!.ServerId);
        Assert.True(result.Player!.IsHost);
    }

    [Fact]
    public void JoinLobby_SameServerRejoin_DoesNotOrphanLobby()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");

        // Duplicate join to the same server — should be a no-op, not remove+readd.
        var result = mgr.JoinLobby(ServerA, "c1", 101, "Alice");

        Assert.Null(result.Departure);
        Assert.True(result.Player!.IsHost);
        Assert.Single(result.Snapshot!.Players);

        // A second player joining the same server must land in the SAME lobby.
        var b = mgr.JoinLobby(ServerA, "c2", 202, "Bob");
        Assert.Equal(2, b.Snapshot!.Players.Count);
        Assert.False(b.Player!.IsHost);
    }

    // ── SelectCharacter (issue #34) ──

    [Fact]
    public void SelectCharacter_LocksIn_And_SetsSelection()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");

        var result = mgr.SelectCharacter("c1", "Manki");

        Assert.True(result.Success);
        Assert.NotNull(result.Player);
        Assert.Equal("Manki", result.Player!.Character);
        Assert.True(result.Player!.LockedIn);
    }

    [Fact]
    public void SelectCharacter_Broadcasts_Updated_Snapshot()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.JoinLobby(ServerA, "c2", 202, "Bob");

        var result = mgr.SelectCharacter("c1", "Manki");

        Assert.NotNull(result.Snapshot);
        Assert.Equal(2, result.Snapshot!.Players.Count);
        Assert.True(result.Snapshot.Players[0].LockedIn);
        Assert.False(result.Snapshot.Players[1].LockedIn);
    }

    [Fact]
    public void SelectCharacter_CanChangePick()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");

        mgr.SelectCharacter("c1", "Manki");
        var result = mgr.SelectCharacter("c1", "FightGuy");

        Assert.True(result.Success);
        Assert.Equal("FightGuy", result.Player!.Character);
        Assert.True(result.Player!.LockedIn);
    }

    [Fact]
    public void SelectCharacter_NonMember_Fails()
    {
        var mgr = new LobbyManager();

        var result = mgr.SelectCharacter("nobody", "Manki");

        Assert.False(result.Success);
        Assert.Null(result.Player);
        Assert.Null(result.Snapshot);
    }

    // ── TryStartMatch (issue #34) ──

    [Fact]
    public void TryStartMatch_AllLockedIn_Succeeds()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.JoinLobby(ServerA, "c2", 202, "Bob");
        mgr.SelectCharacter("c1", "Manki");
        mgr.SelectCharacter("c2", "FightGuy");

        var result = mgr.TryStartMatch("c1", "training");

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.NotNull(result.Config);
        Assert.Equal(ServerA, result.Config!.ServerId);
        Assert.Equal(2, result.Config.Players.Count);
        Assert.True(result.Config.Players.All(p => p.LockedIn));
        Assert.Equal("training", result.Config.ArenaName);
    }

    [Fact]
    public void TryStartMatch_NotAllLockedIn_Fails()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.JoinLobby(ServerA, "c2", 202, "Bob");
        mgr.SelectCharacter("c1", "Manki");
        // Bob hasn't locked in.

        var result = mgr.TryStartMatch("c1", "training");

        Assert.False(result.Success);
        Assert.Null(result.Config);
        Assert.Contains("lock", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryStartMatch_SinglePlayer_Fails()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.SelectCharacter("c1", "Manki");

        var result = mgr.TryStartMatch("c1", "training");

        Assert.False(result.Success);
        Assert.Contains("at least 2", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryStartMatch_NonHost_Fails()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.JoinLobby(ServerA, "c2", 202, "Bob");
        mgr.SelectCharacter("c1", "Manki");
        mgr.SelectCharacter("c2", "FightGuy");

        var result = mgr.TryStartMatch("c2", "training");

        Assert.False(result.Success);
        Assert.Contains("host", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryStartMatch_NoLobby_Fails()
    {
        var mgr = new LobbyManager();

        var result = mgr.TryStartMatch("nobody", "training");

        Assert.False(result.Success);
        Assert.Contains("not in a lobby", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryStartMatch_EmptyArena_Fails()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.JoinLobby(ServerA, "c2", 202, "Bob");
        mgr.SelectCharacter("c1", "Manki");
        mgr.SelectCharacter("c2", "FightGuy");

        var result = mgr.TryStartMatch("c1", "");

        Assert.False(result.Success);
        Assert.Contains("arena", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── IsAllLockedIn (stage select gate) ──

    [Fact]
    public void IsAllLockedIn_AllLocked_True()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.JoinLobby(ServerA, "c2", 202, "Bob");
        mgr.SelectCharacter("c1", "Manki");
        mgr.SelectCharacter("c2", "FightGuy");

        Assert.True(mgr.IsAllLockedIn("c1", out var error));
        Assert.Null(error);
    }

    [Fact]
    public void IsAllLockedIn_UnlockedPlayer_False()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.JoinLobby(ServerA, "c2", 202, "Bob");
        mgr.SelectCharacter("c1", "Manki");

        Assert.False(mgr.IsAllLockedIn("c1", out var error));
        Assert.Contains("lock", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsAllLockedIn_NoLobby_False()
    {
        var mgr = new LobbyManager();

        Assert.False(mgr.IsAllLockedIn("nobody", out var error));
        Assert.Contains("not in a lobby", error, StringComparison.OrdinalIgnoreCase);
    }

    // ── Player-count contract (issue #6): max 4 per lobby, min 2 to start ──

    [Fact]
    public void JoinLobby_AtCapacity_Rejected()
    {
        var mgr = new LobbyManager();
        for (int i = 0; i < 4; i++)
            mgr.JoinLobby(ServerA, $"c{i}", 100 + i, $"P{i}");

        var result = mgr.JoinLobby(ServerA, "c5", 505, "Eve");

        Assert.False(result.Success);
        Assert.Contains("full", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Player);
        Assert.Null(result.Snapshot);
        Assert.Null(result.Departure);
        // The rejected connection is not in the lobby.
        Assert.Null(mgr.GetSnapshot("c5"));
        // Existing members unaffected.
        Assert.Equal(4, mgr.GetSnapshot("c0")!.Players.Count);
    }

    [Fact]
    public void JoinLobby_AtCapacity_ExistingMemberRejoin_Succeeds()
    {
        var mgr = new LobbyManager();
        for (int i = 0; i < 4; i++)
            mgr.JoinLobby(ServerA, $"c{i}", 100 + i, $"P{i}");

        var result = mgr.JoinLobby(ServerA, "c0", 100, "P0");

        Assert.True(result.Success);
        Assert.True(result.Player!.IsHost);
        Assert.Equal(4, result.Snapshot!.Players.Count);
        Assert.Null(result.Departure);
    }

    [Fact]
    public void JoinLobby_FullTarget_AdmitsServerChatAndSwitchesMembership()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        for (int i = 0; i < 4; i++)
            mgr.JoinLobby(ServerB, $"b{i}", 200 + i, $"B{i}");

        // Waiting capacity is separate from authoritative Server Chat
        // membership. A full target reports lobby_full but admits c1 to B.
        var result = mgr.JoinLobby(ServerB, "c1", 101, "Alice");

        Assert.False(result.Success);
        Assert.True(result.ServerAdmitted);
        Assert.Contains("full", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ServerB, mgr.GetServerId("c1"));
        Assert.Equal(ServerA, result.Departure!.ServerId);
    }

    [Fact]
    public void DisconnectThenResumeServer_RestoresChatOnlyWithoutWaitingRoster()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.SelectCharacter("c1", "Manki");
        mgr.DisconnectConnection("c1");

        Assert.True(mgr.ResumeServer(ServerA, "c2", 101, "Alice", out var error));
        Assert.Null(error);
        Assert.Equal(ServerA, mgr.GetServerId("c2"));
        Assert.Null(mgr.GetSnapshot("c2"));
    }

    [Fact]
    public void LeaveLobby_ClearsResumeAdmission()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.LeaveLobby("c1", 101);

        Assert.False(mgr.ResumeServer(ServerA, "c2", 101, "Alice", out var error));
        Assert.Equal("not_admitted", error);
    }

    [Fact]
    public void DisconnectThenFreshConnectionLeave_ClearsResumeAdmission()
    {
        var mgr = new LobbyManager();
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.DisconnectConnection("c1");

        var departure = mgr.LeaveLobby("c2", 101);

        Assert.Null(departure.ServerId);
        Assert.False(mgr.ResumeServer(ServerA, "c3", 101, "Alice", out var error));
        Assert.Equal("not_admitted", error);
    }

    [Fact]
    public void JoinLobby_CustomMax_Enforced()
    {
        var mgr = new LobbyManager(new LobbyOptions(2));
        mgr.JoinLobby(ServerA, "c1", 101, "Alice");
        mgr.JoinLobby(ServerA, "c2", 202, "Bob");

        var result = mgr.JoinLobby(ServerA, "c3", 303, "Carol");

        Assert.False(result.Success);
        Assert.Contains("full", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, mgr.GetSnapshot("c1")!.Players.Count);
    }

    [Fact]
    public void TryStartMatch_MaxPlayers_AllLockedIn_Succeeds()
    {
        var mgr = new LobbyManager();
        for (int i = 0; i < 4; i++)
            mgr.JoinLobby(ServerA, $"c{i}", 100 + i, $"P{i}");
        for (int i = 0; i < 4; i++)
            mgr.SelectCharacter($"c{i}", "Manki");

        var result = mgr.TryStartMatch("c0", "training");

        Assert.True(result.Success);
        Assert.Equal(4, result.Config!.Players.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, result.Config.Players.Select(p => p.EntityId));
    }
}
