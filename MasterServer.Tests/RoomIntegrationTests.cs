using System.Net;
using System.Text.Json;
using MasterServer.Lobbies;
using MasterServer.Data.Models;

using System.Net.Http.Json;
using MasterServer.Chat;
using MasterServer.DTOs;
using MasterServer.Data;
using MasterServer.Rooms;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MasterServer.Tests;

public sealed class RoomIntegrationTests : IDisposable
{
    private readonly List<WebApplicationFactory<Program>> _factories = new();
    private readonly List<HubConnection> _connections = new();

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _utcTicks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;
        private long _timestamp;
        private readonly List<ManualTimer> _timers = new();

        public override DateTimeOffset GetUtcNow()
            => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_timers) _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            Interlocked.Add(ref _utcTicks, delta.Ticks);
            Interlocked.Add(ref _timestamp, delta.Ticks);
            lock (_timers)
                foreach (var timer in _timers.ToArray())
                    timer.FireDue(_timestamp);
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _clock;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private long _due;
            private long _period;
            private bool _disposed;

            public ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state,
                TimeSpan dueTime, TimeSpan period)
            {
                _clock = clock;
                _callback = callback;
                _state = state;
                Change(dueTime, period);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : _clock.GetTimestamp() + dueTime.Ticks;
                _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                return true;
            }

            public void FireDue(long now)
            {
                if (_disposed || now < _due) return;
                _due = _period > 0 ? now + _period : long.MaxValue;
                _callback(_state);
            }

            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
    private int _nextPort = 24000;

    private (WebApplicationFactory<Program> Factory, HttpClient Client) CreateFactory(
        TimeProvider? clock = null, HttpMessageHandler? gameHost = null)
    {
        var databaseName = $"room-test-{Guid.NewGuid():N}";
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Deployment:Profile"] = "development",
                    ["Auth:Mode"] = "development-guest",
                    ["Room:CatalogHash"] = new string('a', 64),
                    ["RateLimit:MaxRequestsPerWindow"] = "1000"
                }));
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(
                    service => service.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor is not null)
                    services.Remove(descriptor);
                services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase(databaseName));

                if (clock is not null)
                {
                    var timeDescriptor = services.SingleOrDefault(
                        service => service.ServiceType == typeof(TimeProvider));
                    if (timeDescriptor is not null)
                        services.Remove(timeDescriptor);
                    services.AddSingleton<TimeProvider>(clock);
                }
                if (gameHost is not null)
                {
                    services.AddHttpClient<IMatchLauncher, HttpMatchLauncher>()
                        .ConfigurePrimaryHttpMessageHandler(() => gameHost);
                    services.AddHttpClient("GameHostHealth")
                        .ConfigurePrimaryHttpMessageHandler(() => gameHost);
                }
            });
        });
        _factories.Add(factory);
        return (factory, factory.CreateClient());
    }

    private static async Task<string> RegisterPlayerAsync(HttpClient client, string name)
    {
        using var guestResponse = await client.PostAsJsonAsync("/auth/guest", new { });
        guestResponse.EnsureSuccessStatusCode();
        var guest = (await guestResponse.Content.ReadFromJsonAsync<GuestAuthResponse>())!;

        using var nameRequest = new HttpRequestMessage(HttpMethod.Put, "/auth/name")
        {
            Content = JsonContent.Create(new SetDisplayNameRequest(name))
        };
        nameRequest.Headers.Authorization = new("Bearer", guest.Token);
        using var nameResponse = await client.SendAsync(nameRequest);
        nameResponse.EnsureSuccessStatusCode();
        return guest.Token;
    }

    private async Task<HubConnection> ConnectAsync(
        WebApplicationFactory<Program> factory, string token, Action<HubConnection>? beforeStart = null)
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
        beforeStart?.Invoke(connection);
        await connection.StartAsync();
        return connection;
    }

    private static Task<T> WaitForPushAsync<T>(HubConnection connection, string method, Func<T, bool> predicate)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = connection.On<T>(method, value =>
        {
            if (predicate(value))
                completion.TrySetResult(value);
        });
        return AwaitPushAsync(completion, subscription);
    }

    private static async Task<T> AwaitPushAsync<T>(TaskCompletionSource<T> completion, IDisposable subscription)
    {
        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            subscription.Dispose();
        }
    }

    private async Task<Guid> RegisterServerAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/servers/register", new ServerRegistrationRequest(
            "Test host", "203.0.113.50", _nextPort++, "EU", false, 8, null));
        response.EnsureSuccessStatusCode();
        var registered = (await response.Content.ReadFromJsonAsync<ServerRegistrationResponse>())!;
        return registered.ServerId;
    }

    private sealed class GameHostHandler : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public bool HoldHealthResponse { get; set; }
        public HttpStatusCode HealthStatus { get; set; } = HttpStatusCode.OK;
        public TaskCompletionSource<bool> HealthProbeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> HealthProbeRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ResponseCatalogHash { get; set; } = new('a', 64);
        public List<JsonElement> Starts { get; } = new();
        public List<Uri> StartUris { get; } = new();
        public List<Uri> HealthUris { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/health")
            {
                HealthUris.Add(request.RequestUri);
                HealthProbeStarted.TrySetResult(true);
                if (HoldHealthResponse)
                    await HealthProbeRelease.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HealthStatus);
            }
            if (request.RequestUri.AbsolutePath == "/match/abort")
                return new HttpResponseMessage(HttpStatusCode.OK);
            StartUris.Add(request.RequestUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Starts.Add(body.RootElement.Clone());
            return new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    port = 25000 + Starts.Count,
                    contentHash = ResponseCatalogHash,
                    content = new { schemaVersion = 1, entries = Array.Empty<object>() }
                })
            };
        }
    }

    private async Task<ServerRegistrationResponse> RegisterCompatibleHostAsync(
        HttpClient client, int capacity = 2, string? catalogHash = null, int? port = null)
    {
        var serverPort = port ?? _nextPort++;
        using var response = await client.PostAsJsonAsync("/servers/register", new ServerRegistrationRequest(
            "Compatible host", "203.0.113.60", serverPort, "EU", false, capacity, null,
            CatalogHash: catalogHash ?? new string('a', 64)));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ServerRegistrationResponse>())!;
    }

    private static RoomCleanupService RoomCleanup(WebApplicationFactory<Program> factory)
        => factory.Services.GetServices<IHostedService>().OfType<RoomCleanupService>().Single();

    private static (Task Signal, IDisposable Subscription) SubscribeSignal(
        HubConnection connection, string method)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = connection.On(method, () => { completion.TrySetResult(true); });
        return (completion.Task, subscription);
    }

    private static async Task<HttpResponseMessage> SendHeartbeatAsync(
        HttpClient client, ServerRegistrationResponse server)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/servers/{server.ServerId}/heartbeat")
        {
            Content = JsonContent.Create(new HeartbeatRequest(0, CatalogHash: new string('a', 64)))
        };
        request.Headers.Authorization = new("Bearer", server.ApiToken);
        return await client.SendAsync(request);
    }

    private static async Task PrepareMatchAsync(HubConnection leader, HubConnection member, string name)
    {
        var room = await leader.InvokeAsync<RoomSnapshot>("CreateRoom", name);
        await member.InvokeAsync<RoomSnapshot>("JoinRoom", room.Id);
        await leader.InvokeAsync<RoomSnapshot>("RoomStartCharacterSelect");
        await leader.InvokeAsync<RoomSnapshot>("RoomSelectCharacter", "Manki");
        await member.InvokeAsync<RoomSnapshot>("RoomSelectCharacter", "FightGuy");
        await leader.InvokeAsync<RoomSnapshot>("RoomStartStageSelect");
        await leader.InvokeAsync<RoomSnapshot>("RoomChooseArena", "slop_pit");
    }

    private static async Task PrepareExistingRoomAsync(HubConnection leader, HubConnection member)
    {
        await leader.InvokeAsync<RoomSnapshot>("RoomStartCharacterSelect");
        await leader.InvokeAsync<RoomSnapshot>("RoomSelectCharacter", "Manki");
        await member.InvokeAsync<RoomSnapshot>("RoomSelectCharacter", "FightGuy");
        await leader.InvokeAsync<RoomSnapshot>("RoomStartStageSelect");
        await leader.InvokeAsync<RoomSnapshot>("RoomChooseArena", "slop_pit");
    }

    private static async Task<HttpResponseMessage> ReportMatchResultAsync(
        HttpClient client, Guid matchId, long winnerSteamId, string? serverToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/match/result")
        {
            Content = JsonContent.Create(new MatchResultRequest(matchId, winnerSteamId))
        };
        if (serverToken is not null)
            request.Headers.Authorization = new("Bearer", serverToken);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> CancelMatchAsync(
        HttpClient client, Guid matchId, string reason, string? serverToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/match/cancel")
        {
            Content = JsonContent.Create(new MatchCancelRequest(matchId, reason))
        };
        if (serverToken is not null)
            request.Headers.Authorization = new("Bearer", serverToken);
        return await client.SendAsync(request);
    }

    public void Dispose()
    {
        foreach (var connection in _connections)
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var factory in _factories)
            factory.Dispose();
    }

    [Fact]
    public async Task AuthenticatedPlayers_CreateBrowseJoinAndLeavePublicRoom_WithRosterPushes()
    {
        var (factory, client) = CreateFactory();
        var alice = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Alice"));
        var bob = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Bob"));

        var aliceCreatePush = WaitForPushAsync<RoomSnapshot>(alice, "RoomUpdated", _ => true);
        var created = await alice.InvokeAsync<RoomSnapshot>("CreateRoom", "Same name");
        var pushedCreate = await aliceCreatePush;
        Assert.Equal(created.Id, pushedCreate.Id);
        Assert.Equal(created.MemberCount, pushedCreate.MemberCount);
        Assert.Equal(created.Members[0].SteamId, pushedCreate.Members[0].SteamId);
        Assert.Equal("Lobby", created.Phase);
        Assert.Equal(1, created.MemberCount);
        Assert.Equal(4, created.Capacity);
        Assert.True(created.Joinable);
        Assert.Equal(created.LeaderSteamId, created.Members[0].SteamId);
        Assert.True(created.Members[0].IsLeader);
        Assert.Equal("Alice", created.Members[0].Name);

        var listed = await bob.InvokeAsync<RoomSummary[]>("GetRooms");
        var summary = Assert.Single(listed);
        Assert.Equal(created.Id, summary.Id);
        Assert.Equal(created.Name, summary.Name);
        Assert.Equal("Lobby", summary.Phase);
        Assert.Equal(1, summary.MemberCount);
        Assert.Equal(4, summary.Capacity);
        Assert.True(summary.Joinable);

        var aliceJoinPush = WaitForPushAsync<RoomSnapshot>(alice, "RoomUpdated", snapshot => snapshot.MemberCount == 2);
        var bobJoinPush = WaitForPushAsync<RoomSnapshot>(bob, "RoomUpdated", snapshot => snapshot.MemberCount == 2);
        var joined = await bob.InvokeAsync<RoomSnapshot>("JoinRoom", created.Id);
        Assert.Equal(2, joined.MemberCount);
        Assert.Equal(2, (await aliceJoinPush).MemberCount);
        Assert.Equal(2, (await bobJoinPush).MemberCount);
        Assert.Equal("Bob", joined.Members[1].Name);
        Assert.True(joined.Members[0].IsLeader);
        Assert.False(joined.Members[1].IsLeader);

        var charlie = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Charlie"));
        var duplicateName = await charlie.InvokeAsync<RoomSnapshot>("CreateRoom", "Same name");
        Assert.NotEqual(created.Id, duplicateName.Id);
        Assert.Equal("Same name", duplicateName.Name);
        Assert.Equal(2, (await bob.InvokeAsync<RoomSummary[]>("GetRooms")).Length);

        var bobAfterAliceLeaves = WaitForPushAsync<RoomSnapshot>(
            bob, "RoomUpdated", snapshot => snapshot.Id == created.Id && snapshot.MemberCount == 1);
        await alice.InvokeAsync("LeaveRoom");
        var promoted = await bobAfterAliceLeaves;
        Assert.Null(await alice.InvokeAsync<RoomSnapshot?>("GetMyRoom"));
        Assert.Equal(promoted.LeaderSteamId, promoted.Members[0].SteamId);
        Assert.Equal(joined.Members[1].SteamId, promoted.LeaderSteamId);
        Assert.True(promoted.Members[0].IsLeader);

        await bob.InvokeAsync("LeaveRoom");
        Assert.Null(await bob.InvokeAsync<RoomSnapshot?>("GetMyRoom"));
        var remainingRooms = await bob.InvokeAsync<RoomSummary[]>("GetRooms");
        Assert.Equal(2, remainingRooms.Length);
        var emptyRoom = Assert.Single(remainingRooms, summary => summary.Id == created.Id);
        Assert.Equal(0, emptyRoom.MemberCount);
        Assert.Equal(0, emptyRoom.LeaderSteamId);
        Assert.True(emptyRoom.Joinable);
    }

    [Fact]
    public async Task RoomLimitsAndMembershipFailuresAreAtomicAndActionable()
    {
        var (factory, client) = CreateFactory();
        var owners = new List<(HubConnection Connection, Guid RoomId)>();
        for (var i = 0; i < 5; i++)
        {
            var token = await RegisterPlayerAsync(client, $"Owner {i}");
            var connection = await ConnectAsync(factory, token);
            var room = await connection.InvokeAsync<RoomSnapshot>("CreateRoom", "Duplicate");
            owners.Add((connection, room.Id));
        }

        var sixth = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Sixth"));
        var invalid = await Assert.ThrowsAsync<HubException>(
            () => sixth.InvokeAsync<RoomSnapshot>("CreateRoom", "bad\nname"));
        Assert.Contains("invalid_room_name", invalid.Message);
        var limit = await Assert.ThrowsAsync<HubException>(
            () => sixth.InvokeAsync<RoomSnapshot>("CreateRoom", "No space"));
        Assert.Contains("room_limit", limit.Message);
        Assert.Equal(5, (await sixth.InvokeAsync<RoomSummary[]>("GetRooms")).Length);

        var duplicateMember = await Assert.ThrowsAsync<HubException>(
            () => owners[0].Connection.InvokeAsync<RoomSnapshot>("JoinRoom", owners[1].RoomId));
        Assert.Contains("already_in_room", duplicateMember.Message);
        var sameRoomAgain = await owners[0].Connection.InvokeAsync<RoomSnapshot>("JoinRoom", owners[0].RoomId);
        Assert.Equal(1, sameRoomAgain.MemberCount);
        var duplicateCreate = await Assert.ThrowsAsync<HubException>(
            () => owners[0].Connection.InvokeAsync<RoomSnapshot>("CreateRoom", "Another"));
        Assert.Contains("already_in_room", duplicateCreate.Message);
        Assert.Equal(5, (await sixth.InvokeAsync<RoomSummary[]>("GetRooms")).Length);

        for (var i = 0; i < 3; i++)
        {
            var player = await ConnectAsync(factory, await RegisterPlayerAsync(client, $"Member {i}"));
            await player.InvokeAsync<RoomSnapshot>("JoinRoom", owners[0].RoomId);
        }
        var full = await Assert.ThrowsAsync<HubException>(() =>
            sixth.InvokeAsync<RoomSnapshot>("JoinRoom", owners[0].RoomId));
        Assert.Contains("room_full", full.Message);
        var rooms = await sixth.InvokeAsync<RoomSummary[]>("GetRooms");
        var fullRoom = Assert.Single(rooms, room => room.Id == owners[0].RoomId);
        Assert.Equal(4, fullRoom.MemberCount);
        Assert.False(fullRoom.Joinable);

        var notInRoom = await Assert.ThrowsAsync<HubException>(() => sixth.InvokeAsync("LeaveRoom"));
        Assert.Contains("not_in_room", notInRoom.Message);
    }

    [Fact]
    public async Task RoomMembershipSurvivesReconnectAndRemainsExclusiveWithPhysicalLobby()
    {
        var (factory, client) = CreateFactory();
        var token = await RegisterPlayerAsync(client, "Alice");
        var serverId = await RegisterServerAsync(client);
        var firstConnection = await ConnectAsync(factory, token);

        await firstConnection.InvokeAsync("JoinLobby", serverId, 0);
        var physicalFirst = await Assert.ThrowsAsync<HubException>(() =>
            firstConnection.InvokeAsync<RoomSnapshot>("CreateRoom", "Denied"));
        Assert.Contains("already_in_room", physicalFirst.Message);
        await firstConnection.InvokeAsync("LeaveLobby");

        var room = await firstConnection.InvokeAsync<RoomSnapshot>("CreateRoom", "Remembered");
        var secondConnection = await ConnectAsync(factory, token);
        var remembered = await secondConnection.InvokeAsync<RoomSnapshot?>("GetMyRoom");
        Assert.NotNull(remembered);
        Assert.Equal(room.Id, remembered.Id);
        Assert.Equal(1, remembered.MemberCount);
        var idempotentJoin = await secondConnection.InvokeAsync<RoomSnapshot>("JoinRoom", room.Id);

        var bob = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Bob"));
        var firstPush = WaitForPushAsync<RoomSnapshot>(firstConnection, "RoomUpdated",
            snapshot => snapshot.Id == room.Id && snapshot.MemberCount == 2);
        var reconnectPush = WaitForPushAsync<RoomSnapshot>(secondConnection, "RoomUpdated",
            snapshot => snapshot.Id == room.Id && snapshot.MemberCount == 2);
        await bob.InvokeAsync<RoomSnapshot>("JoinRoom", room.Id);
        await firstPush;
        await reconnectPush;
        var physicalSecond = await Assert.ThrowsAsync<HubException>(() =>
            secondConnection.InvokeAsync("JoinLobby", serverId, 0));
        Assert.Contains("already_in_room", physicalSecond.Message);

        await firstConnection.StopAsync();
        var afterDisconnect = await secondConnection.InvokeAsync<RoomSnapshot?>("GetMyRoom");
        Assert.NotNull(afterDisconnect);
        Assert.Equal(2, afterDisconnect.MemberCount);
        Assert.Equal(room.Id, afterDisconnect.Id);
        Assert.Equal(1, idempotentJoin.MemberCount);

    }


    [Fact]
    public async Task ReconnectRestoresItsRoomChatHistoryAndRejectsGuessedRoomIds()
    {
        var (factory, client) = CreateFactory();
        var aliceToken = await RegisterPlayerAsync(client, "Alice");
        var alice = await ConnectAsync(factory, aliceToken);
        var room = await alice.InvokeAsync<RoomSnapshot>("CreateRoom", "Chat history");
        var sent = await alice.InvokeAsync<ChatMessage>("SendServer", room.Id, "remembered Room message");
        await alice.StopAsync();

        var restoredState = new TaskCompletionSource<ServerChatState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnected = await ConnectAsync(factory, aliceToken, beforeStart: connection =>
            connection.On<ServerChatState>("ChatServerChanged", state => restoredState.TrySetResult(state)));
        var pushed = await restoredState.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(room.Id, pushed.RoomId);
        Assert.Contains(pushed.Messages, message => message.MessageId == sent.MessageId);

        var snapshot = await reconnected.InvokeAsync<ChatSnapshot>("GetChatState");
        Assert.Equal(room.Id, snapshot.Server.RoomId);
        Assert.Contains(snapshot.Server.Messages, message => message.MessageId == sent.MessageId);

        var stranger = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Stranger"));
        var guessed = await Assert.ThrowsAsync<HubException>(
            () => stranger.InvokeAsync<ChatMessage>("SendServer", room.Id, "guessed access"));
        Assert.Contains("not_in_room", guessed.Message);
        Assert.Null((await stranger.InvokeAsync<ChatSnapshot>("GetChatState")).Server.RoomId);
    }
    [Fact]
    public async Task RoomPreparationIsLeaderControlled_RoomScoped_AndRequiresAdmittedLockedRosterAndArena()
    {
        var (factory, client) = CreateFactory();
        var alice = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Alice"));
        var bob = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Bob"));
        var carol = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Carol"));
        var outsider = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Outsider"));
        var newcomer = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Newcomer"));
        var room = await alice.InvokeAsync<RoomSnapshot>("CreateRoom", "First");
        var other = await outsider.InvokeAsync<RoomSnapshot>("CreateRoom", "Other");
        var tooFew = await Assert.ThrowsAsync<HubException>(() =>
            alice.InvokeAsync<RoomSnapshot>("RoomStartCharacterSelect"));
        Assert.Contains("two", tooFew.Message, StringComparison.OrdinalIgnoreCase);
        await bob.InvokeAsync<RoomSnapshot>("JoinRoom", room.Id);
        await carol.InvokeAsync<RoomSnapshot>("JoinRoom", room.Id);

        var denied = await Assert.ThrowsAsync<HubException>(() =>
            bob.InvokeAsync<RoomSnapshot>("RoomStartCharacterSelect"));
        Assert.Contains("leader", denied.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Lobby", (await alice.InvokeAsync<RoomSnapshot>("GetMyRoom")).Phase);
        var characterPush = WaitForPushAsync<RoomSnapshot>(bob, "RoomUpdated",
            snapshot => snapshot.Phase == "Character Select");
        await alice.InvokeAsync<RoomSnapshot>("RoomStartCharacterSelect");
        Assert.Equal(room.Id, (await characterPush).Id);
        Assert.Equal("Lobby", (await outsider.InvokeAsync<RoomSnapshot>("GetMyRoom")).Phase);
        Assert.Empty((await outsider.InvokeAsync<RoomSnapshot>("GetMyRoom")).Members
            .Where(member => member.Name == "Alice"));
        var deniedJoin = await Assert.ThrowsAsync<HubException>(() =>
            newcomer.InvokeAsync<RoomSnapshot>("JoinRoom", room.Id));
        Assert.Contains("room_selecting", deniedJoin.Message);
        Assert.Equal(other.Id, (await outsider.InvokeAsync<ChatSnapshot>("GetChatState")).Server.RoomId);
        Assert.Null((await newcomer.InvokeAsync<ChatSnapshot>("GetChatState")).Server.RoomId);

        var invalid = await Assert.ThrowsAsync<HubException>(() =>
            alice.InvokeAsync<RoomSnapshot>("RoomSelectCharacter", "unpublished"));
        Assert.Contains("character", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All((await alice.InvokeAsync<RoomSnapshot>("GetMyRoom")).Members,
            member => Assert.False(member.LockedIn));
        await alice.InvokeAsync<RoomSnapshot>("RoomSelectCharacter", "Manki");
        await bob.InvokeAsync<RoomSnapshot>("RoomSelectCharacter", "FightGuy");
        var unlocked = await Assert.ThrowsAsync<HubException>(() =>
            alice.InvokeAsync<RoomSnapshot>("RoomStartStageSelect"));
        Assert.Contains("lock", unlocked.Message, StringComparison.OrdinalIgnoreCase);
        await carol.InvokeAsync<RoomSnapshot>("RoomSelectCharacter", "Wibou");
        var stagePush = WaitForPushAsync<RoomSnapshot>(bob, "RoomUpdated",
            snapshot => snapshot.Phase == "Stage Select");
        await alice.InvokeAsync<RoomSnapshot>("RoomStartStageSelect");
        var stage = await stagePush;
        Assert.Equal(new[] { "Manki", "FightGuy", "Wibou" },
            stage.Members.Select(member => member.CharacterSelection));
        Assert.All(stage.Members, member => Assert.True(member.LockedIn));
        Assert.Null(stage.ArenaName);
        Assert.Contains("Manki", stage.AdmittedCharacters);
        Assert.Contains("slop_pit", stage.AdmittedArenas);

        var deniedArena = await Assert.ThrowsAsync<HubException>(() =>
            bob.InvokeAsync<RoomSnapshot>("RoomChooseArena", "slop_pit"));
        Assert.Contains("leader", deniedArena.Message, StringComparison.OrdinalIgnoreCase);
        var invalidArena = await Assert.ThrowsAsync<HubException>(() =>
            alice.InvokeAsync<RoomSnapshot>("RoomChooseArena", "unpublished"));
        Assert.Contains("arena", invalidArena.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null((await alice.InvokeAsync<RoomSnapshot>("GetMyRoom")).ArenaName);
        var arenaPush = WaitForPushAsync<RoomSnapshot>(bob, "RoomUpdated",
            snapshot => snapshot.ArenaName == "slop_pit");
        await alice.InvokeAsync<RoomSnapshot>("RoomChooseArena", "slop_pit");
        Assert.Equal("slop_pit", (await arenaPush).ArenaName);
        Assert.Equal("Lobby", (await outsider.InvokeAsync<RoomSnapshot>("GetMyRoom")).Phase);
        await carol.InvokeAsync("LeaveRoom");
        Assert.Null((await alice.InvokeAsync<RoomSnapshot>("GetMyRoom")).ArenaName);
        await bob.InvokeAsync("LeaveRoom");
        var reset = await alice.InvokeAsync<RoomSnapshot>("GetMyRoom");
        Assert.Equal("Lobby", reset.Phase);
        Assert.Null(reset.Members[0].CharacterSelection);
        Assert.False(reset.Members[0].LockedIn);
        Assert.True(reset.Joinable);
    }

    [Fact]
    public async Task DisconnectedMemberExpiresAfterGrace_AndEmptyRoomExpiresAfterRetention()
    {
        var clock = new ManualTimeProvider();
        var (factory, client) = CreateFactory(clock);
        var aliceToken = await RegisterPlayerAsync(client, "Alice");
        var alice = await ConnectAsync(factory, aliceToken);
        var bob = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Bob"));
        var room = await alice.InvokeAsync<RoomSnapshot>("CreateRoom", "Timed");
        var joined = await bob.InvokeAsync<RoomSnapshot>("JoinRoom", room.Id);
        var bobSteamId = joined.Members.Single(member => member.Name == "Bob").SteamId;

        var promotions = 0;
        using var promotionSubscription = bob.On<RoomSnapshot>("RoomUpdated", snapshot =>
        {
            if (snapshot.Id == room.Id && snapshot.LeaderSteamId == bobSteamId)
                Interlocked.Increment(ref promotions);
        });
        await alice.StopAsync();
        var duringGrace = await bob.InvokeAsync<RoomSnapshot?>("GetMyRoom");
        Assert.NotNull(duringGrace);
        Assert.Equal(room.LeaderSteamId, duringGrace.LeaderSteamId);
        Assert.Equal(2, duringGrace.MemberCount);
        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(room.LeaderSteamId, (await bob.InvokeAsync<RoomSnapshot>("GetMyRoom")).LeaderSteamId);
        Assert.Equal(0, Volatile.Read(ref promotions));

        var expiredMemberPush = WaitForPushAsync<RoomSnapshot>(bob, "RoomUpdated",
            snapshot => snapshot.Id == room.Id && snapshot.MemberCount == 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        var afterGrace = await expiredMemberPush;
        Assert.Equal(bobSteamId, afterGrace.LeaderSteamId);
        Assert.Equal(bobSteamId, afterGrace.Members[0].SteamId);
        Assert.Equal(1, Volatile.Read(ref promotions));

        var expiryChat = new TaskCompletionSource<ServerChatState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var aliceAfterExpiry = await ConnectAsync(factory, aliceToken, beforeStart: connection =>
            connection.On<ServerChatState>("ChatServerChanged", state => expiryChat.TrySetResult(state)));
        var revokedState = await expiryChat.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(revokedState.RoomId);
        Assert.Null(await aliceAfterExpiry.InvokeAsync<RoomSnapshot?>("GetMyRoom"));
        Assert.Null((await aliceAfterExpiry.InvokeAsync<ChatSnapshot>("GetChatState")).Server.RoomId);
        var expiredSend = await Assert.ThrowsAsync<HubException>(
            () => aliceAfterExpiry.InvokeAsync<ChatMessage>("SendServer", room.Id, "expired Room draft"));
        Assert.Contains("not_in_room", expiredSend.Message);

        await bob.InvokeAsync("LeaveRoom");
        var empty = Assert.Single(await bob.InvokeAsync<RoomSummary[]>("GetRooms"));
        Assert.Equal(0, empty.MemberCount);
        Assert.Equal(0, empty.LeaderSteamId);
        Assert.True(empty.Joinable);

        var charlie = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Charlie"));
        var joinedEmptyRoom = await charlie.InvokeAsync<RoomSnapshot>("JoinRoom", room.Id);
        Assert.Single(joinedEmptyRoom.Members);
        Assert.True(joinedEmptyRoom.Members[0].IsLeader);
        await charlie.InvokeAsync("LeaveRoom");
        Assert.Equal(0, Assert.Single(await bob.InvokeAsync<RoomSummary[]>("GetRooms")).MemberCount);

        clock.Advance(TimeSpan.FromSeconds(60));
        RoomSummary[] afterRetention = [];
        for (var attempt = 0; attempt < 10000; attempt++)
        {
            afterRetention = await bob.InvokeAsync<RoomSummary[]>("GetRooms");
            if (afterRetention.All(summary => summary.Id != room.Id)) break;
            await Task.Yield();
        }
        Assert.DoesNotContain(afterRetention, summary => summary.Id == room.Id);
    }

    [Fact]
    public async Task ReconnectBeforeGraceDeadlineCancelsMemberExpiry()
    {
        var clock = new ManualTimeProvider();
        var (factory, client) = CreateFactory(clock);
        var token = await RegisterPlayerAsync(client, "Alice");
        var firstConnection = await ConnectAsync(factory, token);
        var room = await firstConnection.InvokeAsync<RoomSnapshot>("CreateRoom", "Reconnect");
        await firstConnection.StopAsync();

        clock.Advance(TimeSpan.FromSeconds(14));
        var reconnected = await ConnectAsync(factory, token);
        var remembered = await reconnected.InvokeAsync<RoomSnapshot?>("GetMyRoom");
        Assert.NotNull(remembered);
        Assert.Equal(room.Id, remembered.Id);
        Assert.Single(remembered.Members);

        clock.Advance(TimeSpan.FromSeconds(2));
        var afterGrace = await reconnected.InvokeAsync<RoomSnapshot?>("GetMyRoom");
        Assert.NotNull(afterGrace);
        Assert.Equal(room.Id, afterGrace.Id);
        Assert.Single(afterGrace.Members);
    }

    [Fact]
    public async Task LeaderReconnectBeforeDeadlineKeepsOrder_AndLateReturnDoesNotRetakeLeadership()
    {
        var clock = new ManualTimeProvider();
        var (factory, client) = CreateFactory(clock);
        var aliceToken = await RegisterPlayerAsync(client, "Alice");
        var alice = await ConnectAsync(factory, aliceToken);
        var bob = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Bob"));
        var room = await alice.InvokeAsync<RoomSnapshot>("CreateRoom", "Ordered");
        await bob.InvokeAsync<RoomSnapshot>("JoinRoom", room.Id);
        var alicePlayerId = (await alice.InvokeAsync<ChatSnapshot>("GetChatState")).Self.PlayerId;
        var firstDisconnect = WaitForPushAsync<ChatPresence>(bob, "ChatPresenceChanged",
            presence => !presence.Online && presence.Player.PlayerId == alicePlayerId);
        await alice.StopAsync();
        await firstDisconnect;

        clock.Advance(TimeSpan.FromSeconds(14));
        var carol = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Carol"));
        var three = await carol.InvokeAsync<RoomSnapshot>("JoinRoom", room.Id);
        Assert.Equal(room.LeaderSteamId, three.LeaderSteamId);
        Assert.Equal(new[] { "Alice", "Bob", "Carol" }, three.Members.Select(member => member.Name));

        var aliceBack = await ConnectAsync(factory, aliceToken);
        var restored = await aliceBack.InvokeAsync<RoomSnapshot>("JoinRoom", room.Id);
        Assert.Equal(room.LeaderSteamId, restored.LeaderSteamId);
        Assert.Equal(3, restored.MemberCount);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(room.LeaderSteamId, (await bob.InvokeAsync<RoomSnapshot>("GetMyRoom")).LeaderSteamId);

        var secondDisconnect = WaitForPushAsync<ChatPresence>(bob, "ChatPresenceChanged",
            presence => !presence.Online && presence.Player.PlayerId == alicePlayerId);
        await aliceBack.StopAsync();
        await secondDisconnect;
        var promotion = WaitForPushAsync<RoomSnapshot>(bob, "RoomUpdated",
            snapshot => snapshot.Id == room.Id && snapshot.LeaderSteamId == three.Members[1].SteamId);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(three.Members[1].SteamId, (await promotion).LeaderSteamId);
        var late = await ConnectAsync(factory, aliceToken);
        Assert.Null(await late.InvokeAsync<RoomSnapshot?>("GetMyRoom"));
        var rejoined = await late.InvokeAsync<RoomSnapshot>("JoinRoom", room.Id);
        Assert.Equal(three.Members[1].SteamId, rejoined.LeaderSteamId);
        Assert.Equal(new[] { "Bob", "Carol", "Alice" }, rejoined.Members.Select(member => member.Name));
    }

    [Fact]
    public async Task TwoRoomsLaunchOnOneGameHost_KeepDistinctRoutesAndCapacity()
    {
        var host = new GameHostHandler();
        var (factory, client) = CreateFactory(gameHost: host);
        var registered = await RegisterCompatibleHostAsync(client, capacity: 2);
        var alice = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Alice"));
        var bob = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Bob"));
        var carol = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Carol"));
        var dan = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Dan"));
        var eve = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Eve"));
        var frank = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Frank"));
        await PrepareMatchAsync(alice, bob, "Room A");
        await PrepareMatchAsync(carol, dan, "Room B");
        await PrepareMatchAsync(eve, frank, "Room C");
        var aRoom = (await alice.InvokeAsync<RoomSnapshot>("GetMyRoom")).Id;
        var bRoom = (await carol.InvokeAsync<RoomSnapshot>("GetMyRoom")).Id;
        Assert.Empty(host.Starts);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Empty(await db.Matches.ToListAsync());
            Assert.Equal(0, (await db.GameServers.FindAsync(registered.ServerId))!.CurrentMatches);
        }

        var aPush = WaitForPushAsync<MatchStartedConfig>(bob, "MatchStarted", match => match.RoomId == aRoom);
        var bPush = WaitForPushAsync<MatchStartedConfig>(dan, "MatchStarted", match => match.RoomId == bRoom);
        var first = await alice.InvokeAsync<MatchStartedConfig>("RoomStartMatch");
        var second = await carol.InvokeAsync<MatchStartedConfig>("RoomStartMatch");
        Assert.Equal(first.MatchId, (await aPush).MatchId);
        Assert.Equal(second.MatchId, (await bPush).MatchId);
        Assert.NotEqual(first.MatchId, second.MatchId);
        Assert.NotEqual(first.MatchPort, second.MatchPort);
        Assert.Equal(registered.ServerId, first.ServerId);
        Assert.Equal(registered.ServerId, second.ServerId);
        Assert.Equal("203.0.113.60", first.ServerAddress);
        Assert.Equal(first.ServerAddress, second.ServerAddress);
        Assert.Equal(new[] { "Alice", "Bob" }, first.Players.Select(player => player.Username));
        Assert.Equal(new[] { "Carol", "Dan" }, second.Players.Select(player => player.Username));
        Assert.Equal("In Match", (await bob.InvokeAsync<RoomSnapshot>("GetMyRoom")).Phase);
        var duplicate = await Assert.ThrowsAsync<HubException>(
            () => alice.InvokeAsync<MatchStartedConfig>("RoomStartMatch"));
        Assert.Contains("invalid_phase", duplicate.Message);
        var noCapacity = await Assert.ThrowsAsync<HubException>(
            () => eve.InvokeAsync<MatchStartedConfig>("RoomStartMatch"));
        Assert.Contains("gamehost_unavailable", noCapacity.Message);
        var retry = await eve.InvokeAsync<RoomSnapshot>("GetMyRoom");
        Assert.Equal("Stage Select", retry.Phase);
        Assert.Equal("slop_pit", retry.ArenaName);
        Assert.All(retry.Members, member => Assert.True(member.LockedIn));
        Assert.Equal(2, host.Starts.Count);

        using (var scope = factory.Services.CreateScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Matches.ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(registered.ServerId, rows.Single(row => row.RoomId == aRoom).ServerId);
            Assert.Equal(registered.ServerId, rows.Single(row => row.RoomId == bRoom).ServerId);
            Assert.Equal(first.MatchId, rows.Single(row => row.RoomId == aRoom).Id);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "/match/result")
        {
            Content = JsonContent.Create(new MatchResultRequest(first.MatchId, first.Players[0].SteamId))
        };
        request.Headers.Authorization = new("Bearer", registered.ApiToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var third = await eve.InvokeAsync<MatchStartedConfig>("RoomStartMatch");
        Assert.NotEqual(first.MatchId, third.MatchId);
        Assert.Equal(registered.ServerId, third.ServerId);
    }

    [Fact]
    public async Task ResultAndCancellationAreMatchScopedIdempotentAndPreserveRoomChatAndRematch()
    {
        var host = new GameHostHandler();
        var (factory, client) = CreateFactory(gameHost: host);
        var registered = await RegisterCompatibleHostAsync(client, capacity: 2);
        var alice = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Alice"));
        var bob = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Bob"));
        var carol = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Carol"));
        var dan = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Dan"));
        await PrepareMatchAsync(alice, bob, "Result Room");
        await PrepareMatchAsync(carol, dan, "Cancellation Room");
        var resultRoom = (await alice.InvokeAsync<RoomSnapshot>("GetMyRoom")).Id;
        var cancellationRoom = (await carol.InvokeAsync<RoomSnapshot>("GetMyRoom")).Id;
        var chat = await alice.InvokeAsync<ChatMessage>("SendServer", resultRoom, "Room chat survives result");
        var canceledChat = await carol.InvokeAsync<ChatMessage>("SendServer", cancellationRoom, "Room chat survives abort");
        var resultMatch = await alice.InvokeAsync<MatchStartedConfig>("RoomStartMatch");
        var canceledMatch = await carol.InvokeAsync<MatchStartedConfig>("RoomStartMatch");

        var resultTerminalUpdates = 0;
        using var resultTerminalSubscription = bob.On<RoomSnapshot>("RoomUpdated", snapshot =>
        {
            if (snapshot.Id == resultRoom && snapshot.Phase == "Lobby" && snapshot.ActiveMatchId is null)
                Interlocked.Increment(ref resultTerminalUpdates);
        });
        var resultReset = WaitForPushAsync<RoomSnapshot>(bob, "RoomUpdated",
            snapshot => snapshot.Id == resultRoom && snapshot.Phase == "Lobby" && snapshot.ActiveMatchId is null);
        using (var unauthorized = await ReportMatchResultAsync(client, resultMatch.MatchId,
                   resultMatch.Players[0].SteamId, null))
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using (var response = await ReportMatchResultAsync(client, resultMatch.MatchId,
                   resultMatch.Players[0].SteamId, registered.ApiToken))
            response.EnsureSuccessStatusCode();

        var returned = await resultReset;
        Assert.Equal(new[] { "Alice", "Bob" }, returned.Members.Select(member => member.Name));
        Assert.Null(returned.ArenaName);
        Assert.All(returned.Members, member =>
        {
            Assert.Null(member.CharacterSelection);
            Assert.False(member.LockedIn);
        });
        var unaffected = await dan.InvokeAsync<RoomSnapshot>("GetMyRoom");
        Assert.Equal(canceledMatch.MatchId, unaffected.ActiveMatchId);
        Assert.Equal("In Match", unaffected.Phase);
        var chatState = await alice.InvokeAsync<ChatSnapshot>("GetChatState");
        Assert.Equal(resultRoom, chatState.Server.RoomId);
        Assert.Contains(chatState.Server.Messages, message => message.MessageId == chat.MessageId);

        await PrepareExistingRoomAsync(alice, bob);
        var rematch = await alice.InvokeAsync<MatchStartedConfig>("RoomStartMatch");
        Assert.Equal(resultRoom, rematch.RoomId);
        Assert.NotEqual(resultMatch.MatchId, rematch.MatchId);
        using (var stale = await ReportMatchResultAsync(client, resultMatch.MatchId,
                   resultMatch.Players[0].SteamId, registered.ApiToken))
            stale.EnsureSuccessStatusCode();
        var stillRematching = await bob.InvokeAsync<RoomSnapshot>("GetMyRoom");
        Assert.Equal(rematch.MatchId, stillRematching.ActiveMatchId);
        Assert.Equal("In Match", stillRematching.Phase);

        var cancellationTerminalUpdates = 0;
        using var cancellationTerminalSubscription = dan.On<RoomSnapshot>("RoomUpdated", snapshot =>
        {
            if (snapshot.Id == cancellationRoom && snapshot.Phase == "Lobby" && snapshot.ActiveMatchId is null)
                Interlocked.Increment(ref cancellationTerminalUpdates);
        });
        var abortNotice = WaitForPushAsync<MatchAbortedNotice>(dan, "MatchAborted",
            notice => notice.MatchId == canceledMatch.MatchId);
        var canceledReset = WaitForPushAsync<RoomSnapshot>(dan, "RoomUpdated",
            snapshot => snapshot.Id == cancellationRoom && snapshot.Phase == "Lobby" && snapshot.ActiveMatchId is null);
        using (var canceled = await CancelMatchAsync(client, canceledMatch.MatchId, "absent", registered.ApiToken))
            canceled.EnsureSuccessStatusCode();
        Assert.Equal("absent", (await abortNotice).Reason);
        var cancellationReturn = await canceledReset;
        Assert.All(cancellationReturn.Members, member =>
        {
            Assert.Null(member.CharacterSelection);
            Assert.False(member.LockedIn);
        });
        var canceledChatState = await carol.InvokeAsync<ChatSnapshot>("GetChatState");
        Assert.Equal(cancellationRoom, canceledChatState.Server.RoomId);
        Assert.Contains(canceledChatState.Server.Messages, message => message.MessageId == canceledChat.MessageId);

        await PrepareExistingRoomAsync(carol, dan);
        var cancellationRematch = await carol.InvokeAsync<MatchStartedConfig>("RoomStartMatch");
        Assert.NotEqual(canceledMatch.MatchId, cancellationRematch.MatchId);
        using (var duplicate = await CancelMatchAsync(client, canceledMatch.MatchId, "absent", registered.ApiToken))
            duplicate.EnsureSuccessStatusCode();
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref resultTerminalUpdates));
        Assert.Equal(1, Volatile.Read(ref cancellationTerminalUpdates));
        Assert.Equal(rematch.MatchId, (await alice.InvokeAsync<RoomSnapshot>("GetMyRoom")).ActiveMatchId);
        Assert.Equal(cancellationRematch.MatchId,
            (await carol.InvokeAsync<RoomSnapshot>("GetMyRoom")).ActiveMatchId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var canceledRow = await db.Matches.FindAsync(canceledMatch.MatchId);
        Assert.NotNull(canceledRow!.CanceledAt);
        Assert.Null(canceledRow.EndedAt);
        Assert.Null(canceledRow.WinnerSteamId);
        Assert.Equal("absent", canceledRow.CancelReason);
    }

    [Fact]
    public async Task HostRestartAndShutdownReturnOnlyTheirOpenRoomMatches()
    {
        var host = new GameHostHandler();
        var (factory, client) = CreateFactory(gameHost: host);
        var hostPort = _nextPort++;
        var registered = await RegisterCompatibleHostAsync(client, capacity: 2, port: hostPort);
        var alice = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Alice"));
        var bob = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Bob"));
        var carol = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Carol"));
        var dan = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Dan"));
        await PrepareMatchAsync(alice, bob, "Restart Room");
        await PrepareMatchAsync(carol, dan, "Other Room");
        var restartRoom = (await alice.InvokeAsync<RoomSnapshot>("GetMyRoom")).Id;
        var otherRoom = (await carol.InvokeAsync<RoomSnapshot>("GetMyRoom")).Id;
        var restartMatch = await alice.InvokeAsync<MatchStartedConfig>("RoomStartMatch");
        var otherMatch = await carol.InvokeAsync<MatchStartedConfig>("RoomStartMatch");

        var restartReset = WaitForPushAsync<RoomSnapshot>(bob, "RoomUpdated",
            snapshot => snapshot.Id == restartRoom && snapshot.Phase == "Lobby");
        var otherReset = WaitForPushAsync<RoomSnapshot>(dan, "RoomUpdated",
            snapshot => snapshot.Id == otherRoom && snapshot.Phase == "Lobby");
        var restartAbort = WaitForPushAsync<MatchAbortedNotice>(bob, "MatchAborted",
            notice => notice.MatchId == restartMatch.MatchId);
        var refreshed = await RegisterCompatibleHostAsync(client, capacity: 2, port: hostPort);
        Assert.Equal(registered.ServerId, refreshed.ServerId);
        Assert.NotEqual(registered.ApiToken, refreshed.ApiToken);
        Assert.Equal("host_restart", (await restartAbort).Reason);
        Assert.Equal(restartRoom, (await restartReset).Id);
        Assert.Equal(otherRoom, (await otherReset).Id);
        Assert.Equal("Lobby", (await carol.InvokeAsync<RoomSnapshot>("GetMyRoom")).Phase);

        await PrepareExistingRoomAsync(alice, bob);
        var shutdownMatch = await alice.InvokeAsync<MatchStartedConfig>("RoomStartMatch");
        var shutdownReset = WaitForPushAsync<RoomSnapshot>(bob, "RoomUpdated",
            snapshot => snapshot.Id == restartRoom && snapshot.Phase == "Lobby");
        var shutdownAbort = WaitForPushAsync<MatchAbortedNotice>(bob, "MatchAborted",
            notice => notice.MatchId == shutdownMatch.MatchId);
        using (var request = new HttpRequestMessage(HttpMethod.Delete, $"/servers/{refreshed.ServerId}"))
        {
            request.Headers.Authorization = new("Bearer", refreshed.ApiToken);
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        Assert.Equal("host_shutdown", (await shutdownAbort).Reason);
        Assert.Equal(restartRoom, (await shutdownReset).Id);
        Assert.Equal("Lobby", (await bob.InvokeAsync<RoomSnapshot>("GetMyRoom")).Phase);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var matchId in new[] { restartMatch.MatchId, otherMatch.MatchId, shutdownMatch.MatchId })
        {
            var row = await db.Matches.FindAsync(matchId);
            Assert.NotNull(row!.CanceledAt);
            Assert.Null(row.WinnerSteamId);
            Assert.Equal(matchId == shutdownMatch.MatchId ? "host_shutdown" : "host_restart", row.CancelReason);
        }
    }

    [Fact]
    public async Task RoomAllocatorFiltersContentAndFreshness_ThenUsesLoadRatioAndStableId()
    {
        var host = new GameHostHandler();
        var (factory, client) = CreateFactory(gameHost: host);
        var slow = await RegisterCompatibleHostAsync(client, capacity: 2);
        var lessLoaded = await RegisterCompatibleHostAsync(client, capacity: 4);
        var tie = await RegisterCompatibleHostAsync(client, capacity: 4);
        await RegisterCompatibleHostAsync(client, capacity: 10, catalogHash: new string('b', 64));
        var stale = await RegisterCompatibleHostAsync(client, capacity: 10);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var id in new[] { slow.ServerId, lessLoaded.ServerId, tie.ServerId })
                (await db.GameServers.FindAsync(id))!.CurrentMatches = 1;
            (await db.GameServers.FindAsync(stale.ServerId))!.LastHeartbeat = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var leader = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Leader"));
        var member = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Member"));
        await PrepareMatchAsync(leader, member, "Ranked");
        var started = await leader.InvokeAsync<MatchStartedConfig>("RoomStartMatch");
        Assert.Equal(new[] { lessLoaded.ServerId, tie.ServerId }.Min(), started.ServerId);
        Assert.Single(host.StartUris);
    }

    [Fact]
    public async Task SimultaneousSameIdentityStartsAllocateExactlyOneMatch()
    {
        var host = new GameHostHandler();
        var (factory, client) = CreateFactory(gameHost: host);
        await RegisterCompatibleHostAsync(client);
        var token = await RegisterPlayerAsync(client, "Leader");
        var firstConnection = await ConnectAsync(factory, token);
        var secondConnection = await ConnectAsync(factory, token);
        var member = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Member"));
        await PrepareMatchAsync(firstConnection, member, "Race");

        async Task<bool> Start(HubConnection connection)
        {
            try { await connection.InvokeAsync<MatchStartedConfig>("RoomStartMatch"); return true; }
            catch (HubException) { return false; }
        }
        var outcomes = await Task.WhenAll(Start(firstConnection), Start(secondConnection));
        Assert.Single(outcomes.Where(success => success));
        Assert.Single(host.Starts);
        using var scope = factory.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Matches.ToListAsync());
    }

    [Fact]
    public async Task RoomMatchStartRejectsNonleaderAndMissingArenaWithoutChangingPreparation()
    {
        var (factory, client) = CreateFactory();
        var leader = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Leader"));
        var member = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Member"));
        var room = await leader.InvokeAsync<RoomSnapshot>("CreateRoom", "Incomplete");
        await member.InvokeAsync<RoomSnapshot>("JoinRoom", room.Id);
        await leader.InvokeAsync<RoomSnapshot>("RoomStartCharacterSelect");
        await leader.InvokeAsync<RoomSnapshot>("RoomSelectCharacter", "Manki");
        await member.InvokeAsync<RoomSnapshot>("RoomSelectCharacter", "FightGuy");
        await leader.InvokeAsync<RoomSnapshot>("RoomStartStageSelect");

        var unauthorized = await Assert.ThrowsAsync<HubException>(
            () => member.InvokeAsync<MatchStartedConfig>("RoomStartMatch"));
        Assert.Contains("not_leader", unauthorized.Message);
        var incomplete = await Assert.ThrowsAsync<HubException>(
            () => leader.InvokeAsync<MatchStartedConfig>("RoomStartMatch"));
        Assert.Contains("room_not_ready", incomplete.Message);
        var unchanged = await member.InvokeAsync<RoomSnapshot>("GetMyRoom");
        Assert.Equal("Stage Select", unchanged.Phase);
        Assert.Null(unchanged.ArenaName);
        Assert.All(unchanged.Members, player => Assert.True(player.LockedIn));
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Matches.ToListAsync());
    }

    [Fact]
    public async Task FailedGameHostStartRestoresPreparedRoomWithoutMatchRow()
    {
        var host = new GameHostHandler { Fail = true };
        var (factory, client) = CreateFactory(gameHost: host);
        await RegisterCompatibleHostAsync(client, capacity: 1);
        var leader = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Leader"));
        var member = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Member"));
        await PrepareMatchAsync(leader, member, "Retry");
        var failed = await Assert.ThrowsAsync<HubException>(
            () => leader.InvokeAsync<MatchStartedConfig>("RoomStartMatch"));
        Assert.Contains("gamehost_start_failed", failed.Message);
        var restored = await member.InvokeAsync<RoomSnapshot>("GetMyRoom");
        Assert.Equal("Stage Select", restored.Phase);
        Assert.Equal("slop_pit", restored.ArenaName);
        Assert.Null(restored.ActiveMatchId);
        Assert.All(restored.Members, player => Assert.True(player.LockedIn));
        using (var scope = factory.Services.CreateScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Matches.ToListAsync());
        host.Fail = false;
        host.ResponseCatalogHash = new string('b', 64);
        var incompatible = await Assert.ThrowsAsync<HubException>(
            () => leader.InvokeAsync<MatchStartedConfig>("RoomStartMatch"));
        Assert.Contains("gamehost_start_failed", incompatible.Message);
        Assert.Equal("Stage Select", (await member.InvokeAsync<RoomSnapshot>("GetMyRoom")).Phase);
        using (var scope = factory.Services.CreateScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Matches.ToListAsync());
        host.ResponseCatalogHash = new string('a', 64);
        var started = await leader.InvokeAsync<MatchStartedConfig>("RoomStartMatch");
        Assert.Equal(restored.Id, started.RoomId);
        Assert.Equal("In Match", (await member.InvokeAsync<RoomSnapshot>("GetMyRoom")).Phase);
    }

    [Fact]
    public async Task ActiveMatchKeepsRoomRosterAcrossLongSignalRAbsence()
    {
        var clock = new ManualTimeProvider();
        var host = new GameHostHandler();
        var (factory, client) = CreateFactory(clock, host);
        await RegisterCompatibleHostAsync(client);
        var leaderToken = await RegisterPlayerAsync(client, "Leader");
        var leader = await ConnectAsync(factory, leaderToken);
        var member = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Member"));
        await PrepareMatchAsync(leader, member, "Fighting");
        var started = await leader.InvokeAsync<MatchStartedConfig>("RoomStartMatch");
        await leader.StopAsync();
        await member.StopAsync();
        clock.Advance(TimeSpan.FromSeconds(76));

        var reconnected = await ConnectAsync(factory, leaderToken);
        var restored = await reconnected.InvokeAsync<RoomSnapshot?>("GetMyRoom");
        Assert.NotNull(restored);
        Assert.Equal(started.RoomId, restored.Id);
        Assert.Equal("In Match", restored.Phase);
        Assert.Equal(started.MatchId, restored.ActiveMatchId);
        Assert.Equal(2, restored.MemberCount);
    }

    [Fact]
    public async Task LeavingRoomRevokesEveryActiveConnectionForTheIdentity()
    {
        var (factory, client) = CreateFactory();
        var aliceToken = await RegisterPlayerAsync(client, "Alice");
        var alice = await ConnectAsync(factory, aliceToken);
        var room = await alice.InvokeAsync<RoomSnapshot>("CreateRoom", "Revocation");
        var aliceSecondary = await ConnectAsync(factory, aliceToken);
        var bob = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Bob"));
        await bob.InvokeAsync<RoomSnapshot>("JoinRoom", room.Id);

        var revoked = WaitForPushAsync<Guid>(aliceSecondary, "RoomMembershipRevoked",
            roomId => roomId == room.Id);
        var chatCleared = WaitForPushAsync<ServerChatState>(aliceSecondary, "ChatServerChanged",
            state => state.RoomId is null);
        var bobUpdated = WaitForPushAsync<RoomSnapshot>(bob, "RoomUpdated",
            snapshot => snapshot.Id == room.Id && snapshot.MemberCount == 1);
        await alice.InvokeAsync("LeaveRoom");

        Assert.Equal(room.Id, await revoked);
        Assert.Null((await chatCleared).RoomId);
        var remaining = await bobUpdated;
        Assert.Equal("Bob", remaining.Members[0].Name);
        Assert.Null(await aliceSecondary.InvokeAsync<RoomSnapshot?>("GetMyRoom"));
    }


    [Fact]
    public async Task LegacyDevelopmentMatchStartedCarriesTheMatchIdSentToGameHost()
    {
        var host = new GameHostHandler();
        var (factory, client) = CreateFactory(gameHost: host);
        var serverId = await RegisterServerAsync(client);
        var alice = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Alice"));
        var bob = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Bob"));
        var startedPush = WaitForPushAsync<MatchStartedConfig>(alice, "MatchStarted", _ => true);

        await alice.InvokeAsync("JoinLobby", serverId, 0);
        await bob.InvokeAsync("JoinLobby", serverId, 0);
        await alice.InvokeAsync("SelectCharacter", "Manki");
        await bob.InvokeAsync("SelectCharacter", "FightGuy");
        await alice.InvokeAsync("StartMatch", "training");

        var started = await startedPush;
        var requestedMatchId = Guid.Parse(Assert.Single(host.Starts)
            .GetProperty("matchId").GetString()!);
        Assert.NotEqual(Guid.Empty, started.MatchId);
        Assert.Equal(requestedMatchId, started.MatchId);
    }

    [Fact]
    public async Task RoomDirectoryChangedLetsOutsidersRefetchOnlyAfterSummaryChanges()
    {
        var (factory, client) = CreateFactory();
        var alice = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Alice"));
        var bob = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Bob"));
        var outsider = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Outsider"));
        var changeCount = 0;
        using var subscription = outsider.On("RoomDirectoryChanged",
            () => { Interlocked.Increment(ref changeCount); });

        var (createdSignal, createdSubscription) = SubscribeSignal(outsider, "RoomDirectoryChanged");
        Guid roomId;
        using (createdSubscription)
        {
            var room = await alice.InvokeAsync<RoomSnapshot>("CreateRoom", "Browser visible");
            roomId = room.Id;
            await createdSignal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var createdSummary = Assert.Single(await outsider.InvokeAsync<RoomSummary[]>("GetRooms"));
        Assert.Equal(roomId, createdSummary.Id);
        Assert.Equal(1, Volatile.Read(ref changeCount));

        var (joinedSignal, joinedSubscription) = SubscribeSignal(outsider, "RoomDirectoryChanged");
        using (joinedSubscription)
        {
            await bob.InvokeAsync<RoomSnapshot>("JoinRoom", roomId);
            await joinedSignal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(2, Volatile.Read(ref changeCount));
        Assert.Equal(2, Assert.Single(await outsider.InvokeAsync<RoomSummary[]>("GetRooms")).MemberCount);

        var (duplicateSignal, duplicateSubscription) = SubscribeSignal(outsider, "RoomDirectoryChanged");
        using (duplicateSubscription)
        {
            await bob.InvokeAsync<RoomSnapshot>("JoinRoom", roomId);
            await Task.Delay(100);
            Assert.False(duplicateSignal.IsCompleted);
        }
        Assert.Equal(2, Volatile.Read(ref changeCount));

        var (leftSignal, leftSubscription) = SubscribeSignal(outsider, "RoomDirectoryChanged");
        using (leftSubscription)
        {
            await bob.InvokeAsync("LeaveRoom");
            await leftSignal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(3, Volatile.Read(ref changeCount));
        Assert.Equal(1, Assert.Single(await outsider.InvokeAsync<RoomSummary[]>("GetRooms")).MemberCount);
        var (rejoinedSignal, rejoinedSubscription) = SubscribeSignal(outsider, "RoomDirectoryChanged");
        using (rejoinedSubscription)
        {
            await bob.InvokeAsync<RoomSnapshot>("JoinRoom", roomId);
            await rejoinedSignal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(4, Volatile.Read(ref changeCount));

        var (phaseSignal, phaseSubscription) = SubscribeSignal(outsider, "RoomDirectoryChanged");
        using (phaseSubscription)
        {
            await alice.InvokeAsync<RoomSnapshot>("RoomStartCharacterSelect");
            await phaseSignal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var phaseSummary = Assert.Single(await outsider.InvokeAsync<RoomSummary[]>("GetRooms"));
        Assert.Equal("Character Select", phaseSummary.Phase);
        Assert.Equal(5, Volatile.Read(ref changeCount));

        var (selectionSignal, selectionSubscription) = SubscribeSignal(outsider, "RoomDirectoryChanged");
        using (selectionSubscription)
        {
            await alice.InvokeAsync<RoomSnapshot>("RoomSelectCharacter", "Manki");
            await Task.Delay(100);
            Assert.False(selectionSignal.IsCompleted);
        }
        Assert.Equal(5, Volatile.Read(ref changeCount));
    }

    [Fact]
    public async Task StaleHostIsAbortedOnlyAfterFailedHealthProbe_AndOnlyItsRoomIsReset()
    {
        var clock = new ManualTimeProvider();
        var host = new GameHostHandler();
        var (factory, client) = CreateFactory(clock, host);
        var port = _nextPort++;
        var registered = await RegisterCompatibleHostAsync(client, port: port);
        var leader = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Leader"));
        var member = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Member"));
        var otherLeader = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Other Leader"));
        var otherMember = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Other Member"));
        var outsider = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Browser"));
        await PrepareMatchAsync(leader, member, "Lost host");
        var started = await leader.InvokeAsync<MatchStartedConfig>("RoomStartMatch");
        var lostRoomId = started.RoomId!.Value;
        var otherRoom = await otherLeader.InvokeAsync<RoomSnapshot>("CreateRoom", "Unrelated");
        await otherMember.InvokeAsync<RoomSnapshot>("JoinRoom", otherRoom.Id);
        var unrelatedChat = await otherLeader.InvokeAsync<ChatMessage>(
            "SendServer", otherRoom.Id, "Room chat survives host loss");
        var cleanup = RoomCleanup(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.GameServers.FindAsync(registered.ServerId);
            row!.LastHeartbeat = clock.GetUtcNow().UtcDateTime.AddSeconds(-59);
            await db.SaveChangesAsync();
        }
        await cleanup.ReconcileUnavailableHostsAsync();
        Assert.Empty(host.HealthUris);
        using (var scope = factory.Services.CreateScope())
            Assert.Null((await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Matches.FindAsync(started.MatchId))!.CanceledAt);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.GameServers.FindAsync(registered.ServerId);
            row!.LastHeartbeat = clock.GetUtcNow().UtcDateTime.AddSeconds(-60);
            await db.SaveChangesAsync();
        }
        await cleanup.ReconcileUnavailableHostsAsync();
        Assert.Equal(new Uri($"http://203.0.113.60:{port}/health"), Assert.Single(host.HealthUris));
        using (var scope = factory.Services.CreateScope())
            Assert.Null((await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Matches.FindAsync(started.MatchId))!.CanceledAt);
        Assert.Equal("In Match", (await member.InvokeAsync<RoomSnapshot>("GetMyRoom")).Phase);

        host.HealthStatus = HttpStatusCode.ServiceUnavailable;
        var leaderAbortCount = 0;
        var memberAbortCount = 0;
        var outsiderAbortCount = 0;
        using var leaderCounter = leader.On<MatchAbortedNotice>("MatchAborted",
            _ => Interlocked.Increment(ref leaderAbortCount));
        using var memberCounter = member.On<MatchAbortedNotice>("MatchAborted",
            _ => Interlocked.Increment(ref memberAbortCount));
        using var outsiderCounter = outsider.On<MatchAbortedNotice>("MatchAborted",
            _ => Interlocked.Increment(ref outsiderAbortCount));
        var leaderAbort = WaitForPushAsync<MatchAbortedNotice>(leader, "MatchAborted",
            notice => notice.MatchId == started.MatchId);
        var memberAbort = WaitForPushAsync<MatchAbortedNotice>(member, "MatchAborted",
            notice => notice.MatchId == started.MatchId);
        var reset = WaitForPushAsync<RoomSnapshot>(member, "RoomUpdated",
            snapshot => snapshot.Id == lostRoomId && snapshot.Phase == "Lobby" && snapshot.ActiveMatchId is null);
        var (directorySignal, directorySubscription) = SubscribeSignal(outsider, "RoomDirectoryChanged");
        using (directorySubscription)
        {
            await cleanup.ReconcileUnavailableHostsAsync();
            await directorySignal.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal("host_unavailable", (await leaderAbort).Reason);
        Assert.Equal("host_unavailable", (await memberAbort).Reason);
        Assert.Equal(lostRoomId, (await reset).Id);
        Assert.Equal(1, Volatile.Read(ref leaderAbortCount));
        Assert.Equal(1, Volatile.Read(ref memberAbortCount));
        Assert.Equal(0, Volatile.Read(ref outsiderAbortCount));
        Assert.Equal("Lobby", (await leader.InvokeAsync<RoomSnapshot>("GetMyRoom")).Phase);
        var unaffectedRoom = await otherLeader.InvokeAsync<RoomSnapshot>("GetMyRoom");
        Assert.Equal(otherRoom.Id, unaffectedRoom.Id);
        Assert.Equal("Lobby", unaffectedRoom.Phase);
        Assert.Equal(2, unaffectedRoom.MemberCount);
        var summaries = await outsider.InvokeAsync<RoomSummary[]>("GetRooms");
        Assert.Equal("Lobby", Assert.Single(summaries.Where(room => room.Id == lostRoomId)).Phase);
        Assert.Equal(2, Assert.Single(summaries.Where(room => room.Id == otherRoom.Id)).MemberCount);
        var chat = await otherLeader.InvokeAsync<ChatSnapshot>("GetChatState");
        Assert.Equal(otherRoom.Id, chat.Server.RoomId);
        Assert.Contains(chat.Server.Messages, message => message.MessageId == unrelatedChat.MessageId);
        Assert.Equal(2, host.HealthUris.Count);

        using var resultScope = factory.Services.CreateScope();
        var resultDb = resultScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var canceled = await resultDb.Matches.FindAsync(started.MatchId);
        Assert.NotNull(canceled!.CanceledAt);
        Assert.Null(canceled.EndedAt);
        Assert.Null(canceled.WinnerSteamId);
        Assert.Equal("host_unavailable", canceled.CancelReason);
    }

    [Fact]
    public async Task HostHeartbeatRenewedDuringHealthProbePreventsStaleCancellation()
    {
        var clock = new ManualTimeProvider();
        var host = new GameHostHandler
        {
            HealthStatus = HttpStatusCode.ServiceUnavailable,
            HoldHealthResponse = true
        };
        var (factory, client) = CreateFactory(clock, host);
        var registered = await RegisterCompatibleHostAsync(client);
        var leader = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Leader"));
        var member = await ConnectAsync(factory, await RegisterPlayerAsync(client, "Member"));
        await PrepareMatchAsync(leader, member, "Returning host");
        var started = await leader.InvokeAsync<MatchStartedConfig>("RoomStartMatch");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.GameServers.FindAsync(registered.ServerId);
            row!.LastHeartbeat = clock.GetUtcNow().UtcDateTime.AddSeconds(-60);
            await db.SaveChangesAsync();
        }

        var aborts = 0;
        using var abortSubscription = leader.On<MatchAbortedNotice>("MatchAborted",
            _ => Interlocked.Increment(ref aborts));
        var reconciliation = RoomCleanup(factory).ReconcileUnavailableHostsAsync();
        await host.HealthProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (var heartbeat = await SendHeartbeatAsync(client, registered))
            heartbeat.EnsureSuccessStatusCode();
        host.HealthProbeRelease.TrySetResult(true);
        await reconciliation;

        using var resultScope = factory.Services.CreateScope();
        var dbResult = resultScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var match = await dbResult.Matches.FindAsync(started.MatchId);
        Assert.Null(match!.CanceledAt);
        Assert.Equal("In Match", (await member.InvokeAsync<RoomSnapshot>("GetMyRoom")).Phase);
        Assert.Equal(0, Volatile.Read(ref aborts));
    }
    private sealed record ServerRegistrationResponse(Guid ServerId, string ApiToken);
    private sealed record MatchAbortedNotice(Guid MatchId, string Reason);
}
