// MasterServer.Tests/ChatIntegrationTests.cs
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MasterServer.Chat;
using MasterServer.Lobbies;
using MasterServer.Data;
using MasterServer.DTOs;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace MasterServer.Tests;

/// <summary>Real long-polling clients, isolated databases, and controlled quota time.</summary>
public class ChatIntegrationTests : IDisposable
{
    private readonly List<WebApplicationFactory<Program>> _factories = new();
    private readonly List<HubConnection> _connections = new();
    private int _nextServerPort = 20000;

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void ShiftWallClock(TimeSpan delta) => _now += delta;
        public void Advance(TimeSpan delta)
        {
            Interlocked.Add(ref _timestamp, delta.Ticks);
            ShiftWallClock(delta);
        }
    }

    private sealed record ServerRegisterResponse(Guid ServerId, string ApiToken);
    private sealed class FakeMatchLauncher : IMatchLauncher
    {
        public Task<MatchLaunchResult> LaunchAsync(MatchStartedConfig config)
            => Task.FromResult(new MatchLaunchResult(
                9877,
                JsonDocument.Parse("""{"schemaVersion":1,"entries":[]}""").RootElement.Clone()));
    }

    private (WebApplicationFactory<Program> Factory, HttpClient Client, ManualTimeProvider Clock) CreateFactory(int httpRateLimit = 100_000)
    {
        var clock = new ManualTimeProvider();
        var databaseName = $"chat-test-{Guid.NewGuid():N}";
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deployment:Profile"] = "development",
                ["Auth:Mode"] = "development-guest",
                ["RateLimit:MaxRequestsPerWindow"] = httpRateLimit.ToString(),
            }));
            builder.ConfigureServices(services =>
            {
                Replace<DbContextOptions<AppDbContext>>(services);
                services.AddDbContext<AppDbContext>(opts => opts.UseInMemoryDatabase(databaseName));

                // Replace only the external GameServer launch boundary. The
                // SignalR path and lobby/membership state remain real.
                Replace<IMatchLauncher>(services);
                services.AddSingleton<IMatchLauncher, FakeMatchLauncher>();

                // Deterministic clock for the 5/5s message quota, 20/10s
                // control budget, and any backlog-eviction timing — no
                // sleeps needed to cross a window boundary.
                Replace<TimeProvider>(services);
                services.AddSingleton<TimeProvider>(clock);
            });
        });
        _factories.Add(factory);
        return (factory, factory.CreateClient(), clock);
    }

    private static void Replace<TService>(IServiceCollection services)
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(TService));
        if (descriptor is not null)
            services.Remove(descriptor);
    }

    private static async Task<GuestAuthResponse> CreateGuestAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/auth/guest", new { });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GuestAuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SetDisplayNameRawAsync(HttpClient client, string token, string displayName)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/auth/name")
        {
            Content = JsonContent.Create(new SetDisplayNameRequest(displayName))
        };
        request.Headers.Authorization = new("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<ChatPlayer> SetDisplayNameAsync(HttpClient client, string token, string displayName)
    {
        var response = await SetDisplayNameRawAsync(client, token, displayName);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChatPlayer>())!;
    }

    /// <summary>
    /// Full registration flow the real client uses: create a guest, THEN
    /// choose a display name before ever starting the hub connection, so no
    /// generated placeholder name is ever exposed to chat.
    /// </summary>
    private static async Task<(string Token, ChatPlayer Profile)> RegisterPlayerAsync(HttpClient client, string displayName)
    {
        var guest = await CreateGuestAsync(client);
        var profile = await SetDisplayNameAsync(client, guest.Token, displayName);
        return (guest.Token, profile);
    }

    private async Task<ServerRegisterResponse> RegisterFreshServerWithTokenAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/servers/register", new ServerRegistrationRequest(
            Name: name,
            IpAddress: "203.0.113.50",
            Port: _nextServerPort++,
            Region: "EU",
            IsOfficial: false,
            MaxConcurrentMatches: 8,
            CustomRulesJson: null));
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<ServerRegisterResponse>())!;
        return body;
    }

    private async Task<Guid> RegisterFreshServerAsync(HttpClient client, string name) =>
        (await RegisterFreshServerWithTokenAsync(client, name)).ServerId;

    private async Task<HubConnection> ConnectAsync(WebApplicationFactory<Program> factory, string token, bool readState = true)
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
        if (readState)
            await connection.InvokeAsync<ChatSnapshot>("GetChatState");
        return connection;
    }

    private async Task<HubConnection> ConnectWebSocketAsync(
        WebApplicationFactory<Program> factory, string token, bool readState = true)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "lobby"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.WebSocketFactory = (context, cancellationToken) =>
                {
                    var uri = new UriBuilder(context.Uri);
                    var query = uri.Query.TrimStart('?');
                    uri.Query = string.IsNullOrEmpty(query)
                        ? $"access_token={Uri.EscapeDataString(token)}"
                        : $"{query}&access_token={Uri.EscapeDataString(token)}";
                    return new ValueTask<System.Net.WebSockets.WebSocket>(
                        factory.Server.CreateWebSocketClient().ConnectAsync(uri.Uri, cancellationToken));
                };
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        _connections.Add(connection);
        await connection.StartAsync();
        if (readState)
            await connection.InvokeAsync<ChatSnapshot>("GetChatState");
        return connection;
    }

    private static string IssueTokenWithExpiry(string originalToken, IConfiguration configuration, DateTime expiresAt)
    {
        var handler = new JwtSecurityTokenHandler();
        var original = handler.ReadJwtToken(originalToken);
        var claims = original.Claims
            .Where(claim => claim.Type != JwtRegisteredClaimNames.Exp &&
                claim.Type != JwtRegisteredClaimNames.Jti)
            .Append(new System.Security.Claims.Claim(
                JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")));
        var secret = configuration["Jwt:Secret"]!;
        return handler.WriteToken(new JwtSecurityToken(
            original.Issuer,
            original.Audiences.Single(),
            claims,
            expires: expiresAt,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256)));
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

    // A completed RPC does not mean another client's callback has run.
    // Waiting for a later push makes negative-delivery assertions meaningful.
    private static async Task FlushMessagesAsync(HubConnection sender, params HubConnection[] recipients)
    {
        var marker = $"barrier-{Guid.NewGuid():N}";
        var waits = recipients.Select(connection => WaitForPushAsync<ChatMessage>(
            connection, "ChatMessage", message => message.Text == marker)).ToArray();
        await sender.InvokeAsync<ChatMessage>("SendGlobal", marker);
        await Task.WhenAll(waits);
    }

    /// <summary>
    /// Sends <paramref name="texts"/> via SendGlobal, advancing the shared
    /// ManualTimeProvider past the 5-per-5s window every 5 sends so a bulk
    /// backlog scenario never trips the message quota it isn't testing.
    /// </summary>
    private static async Task<List<ChatMessage>> SendManyGlobalAsync(HubConnection connection, ManualTimeProvider clock, IEnumerable<string> texts)
    {
        var results = new List<ChatMessage>();
        var index = 0;
        foreach (var text in texts)
        {
            if (index++ % 5 == 0)
                clock.Advance(TimeSpan.FromSeconds(5));
            results.Add(await connection.InvokeAsync<ChatMessage>("SendGlobal", text));
        }
        return results;
    }

    public void Dispose()
    {
        foreach (var connection in _connections)
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var factory in _factories)
            factory.Dispose();
    }

    [Fact]
    public async Task SendGlobal_ReachesAllOnlineIdentities_RegardlessOfServerMembership()
    {
        var (factory, client, _) = CreateFactory();

        var (aliceToken, alice) = await RegisterPlayerAsync(client, "Alice");
        var (bobToken, _) = await RegisterPlayerAsync(client, "Bob");
        var (carolToken, _) = await RegisterPlayerAsync(client, "Carol");
        var serverA = await RegisterFreshServerAsync(client, "ServerA");

        var aliceConn = await ConnectAsync(factory, aliceToken);
        var bobConn = await ConnectAsync(factory, bobToken);
        var carolConn = await ConnectAsync(factory, carolToken);

        // Bob is in a lobby, Carol is not in any — Global must reach both.
        await bobConn.InvokeAsync("JoinLobby", serverA, 0);

        var bobWait = WaitForPushAsync<ChatMessage>(bobConn, "ChatMessage", m => m.Channel == "global");
        var carolWait = WaitForPushAsync<ChatMessage>(carolConn, "ChatMessage", m => m.Channel == "global");

        var sent = await aliceConn.InvokeAsync<ChatMessage>("SendGlobal", "hello everyone");

        var bobReceived = await bobWait;
        var carolReceived = await carolWait;

        Assert.Equal("global", sent.Channel);
        Assert.Equal(alice.PlayerId, sent.Sender.PlayerId);
        Assert.Equal(sent.MessageId, bobReceived.MessageId);
        Assert.Equal(sent.MessageId, carolReceived.MessageId);
        Assert.Equal("hello everyone", bobReceived.Text);
    }

    [Fact]
    public async Task SendServer_ScopedToCurrentMembership_RejectsNonMemberAndStaleAndUnknownTarget()
    {
        var (factory, client, _) = CreateFactory();

        var (aliceToken, _) = await RegisterPlayerAsync(client, "Alice");
        var (bobToken, _) = await RegisterPlayerAsync(client, "Bob");
        var (carolToken, _) = await RegisterPlayerAsync(client, "Carol");

        var serverA = await RegisterFreshServerAsync(client, "ServerA");
        var serverB = await RegisterFreshServerAsync(client, "ServerB");

        var aliceConn = await ConnectAsync(factory, aliceToken);
        var bobConn = await ConnectAsync(factory, bobToken);
        var carolConn = await ConnectAsync(factory, carolToken);
        var carolMessages = new ConcurrentQueue<ChatMessage>();
        using var carolRegistration = carolConn.On<ChatMessage>("ChatMessage", carolMessages.Enqueue);

        await aliceConn.InvokeAsync("JoinLobby", serverA, 0);
        await bobConn.InvokeAsync("JoinLobby", serverA, 0);
        await carolConn.InvokeAsync("JoinLobby", serverB, 0);

        var bobWait = WaitForPushAsync<ChatMessage>(bobConn, "ChatMessage", m => m.Channel == "server");
        var sent = await aliceConn.InvokeAsync<ChatMessage>("SendServer", serverA, "for server A only");
        var bobReceived = await bobWait;

        Assert.Equal(serverA, sent.ServerId);
        Assert.Equal(sent.MessageId, bobReceived.MessageId);
        await FlushMessagesAsync(bobConn, carolConn);
        Assert.DoesNotContain(carolMessages, message => message.MessageId == sent.MessageId);
        Assert.Empty((await carolConn.InvokeAsync<ChatSnapshot>("GetChatState")).Server.Messages);

        // Carol is a member of ServerB, not ServerA — a forged target is
        // rejected outright, never silently rerouted or delivered.
        var forged = await Assert.ThrowsAsync<HubException>(
            () => carolConn.InvokeAsync<ChatMessage>("SendServer", serverA, "sneaky"));
        Assert.Contains("not_in_server", forged.Message);

        // An unregistered/unknown server id is rejected before any lobby state changes.
        var unknown = await Assert.ThrowsAsync<HubException>(
            () => aliceConn.InvokeAsync("JoinLobby", Guid.NewGuid(), 0));
        Assert.Contains("server_unavailable", unknown.Message);
        Assert.Equal(serverA, (await aliceConn.InvokeAsync<ChatSnapshot>("GetChatState")).Server.ServerId);

        var switched = WaitForPushAsync<ServerChatState>(aliceConn, "ChatServerChanged",
            state => state.ServerId == serverB);
        await aliceConn.InvokeAsync("JoinLobby", serverB, 0);
        Assert.Empty((await switched).Messages);
        var oldServer = await Assert.ThrowsAsync<HubException>(
            () => aliceConn.InvokeAsync<ChatMessage>("SendServer", serverA, "old server draft"));
        Assert.Contains("not_in_server", oldServer.Message);

        var left = WaitForPushAsync<ServerChatState>(aliceConn, "ChatServerChanged", state => state.ServerId is null);
        await aliceConn.InvokeAsync("LeaveLobby");
        Assert.Empty((await left).Messages);
        var stale = await Assert.ThrowsAsync<HubException>(
            () => aliceConn.InvokeAsync<ChatMessage>("SendServer", serverB, "still there?"));
        Assert.Contains("not_in_server", stale.Message);
    }

    [Fact]
    public async Task SendDirect_OnlineOnly_SelfSendNotDuplicated_OfflineRecipientRejected()
    {
        var (factory, client, _) = CreateFactory();
        var (aliceToken, alice) = await RegisterPlayerAsync(client, "Alice");
        var (bobToken, bob) = await RegisterPlayerAsync(client, "Bob");
        var (carolToken, _) = await RegisterPlayerAsync(client, "Carol");
        var aliceConn = await ConnectAsync(factory, aliceToken);
        var bobConn = await ConnectAsync(factory, bobToken);
        var carolConn = await ConnectAsync(factory, carolToken);

        var aliceMessages = new ConcurrentQueue<ChatMessage>();
        var carolMessages = new ConcurrentQueue<ChatMessage>();
        using var aliceReg = aliceConn.On<ChatMessage>("ChatMessage", aliceMessages.Enqueue);
        using var carolReg = carolConn.On<ChatMessage>("ChatMessage", carolMessages.Enqueue);
        var bobWait = WaitForPushAsync<ChatMessage>(bobConn, "ChatMessage", message => message.Channel == "direct");
        var sent = await aliceConn.InvokeAsync<ChatMessage>("SendDirect", bob.PlayerId, "hi bob");
        Assert.Equal(bob.PlayerId, sent.RecipientId);
        Assert.Equal(sent.MessageId, (await bobWait).MessageId);

        var self = await aliceConn.InvokeAsync<ChatMessage>("SendDirect", alice.PlayerId, "note to self");
        await FlushMessagesAsync(aliceConn, aliceConn, bobConn, carolConn);
        Assert.Single(aliceMessages, message => message.MessageId == sent.MessageId);
        Assert.Single(aliceMessages, message => message.MessageId == self.MessageId);
        Assert.DoesNotContain(carolMessages, message => message.Channel == "direct");
        Assert.DoesNotContain(
            (await bobConn.InvokeAsync<ChatSnapshot>("GetChatState")).GlobalMessages,
            message => message.Channel == "direct");

        var bobOffline = WaitForPushAsync<ChatPresence>(aliceConn, "ChatPresenceChanged",
            presence => presence.Player.PlayerId == bob.PlayerId && !presence.Online);
        await bobConn.DisposeAsync();
        await bobOffline;

        // A different guest with the same name must never receive this old conversation.
        var (newBobToken, newBob) = await RegisterPlayerAsync(client, "Bob");
        var newBobConn = await ConnectAsync(factory, newBobToken);
        Assert.NotEqual(bob.PlayerId, newBob.PlayerId);
        var offline = await Assert.ThrowsAsync<HubException>(
            () => aliceConn.InvokeAsync<ChatMessage>("SendDirect", bob.PlayerId, "you there?"));
        Assert.Contains("recipient_offline", offline.Message);

        var newBobWait = WaitForPushAsync<ChatMessage>(newBobConn, "ChatMessage", message => message.Channel == "direct");
        var newConversation = await aliceConn.InvokeAsync<ChatMessage>("SendDirect", newBob.PlayerId, "new conversation");
        Assert.Equal(newConversation.MessageId, (await newBobWait).MessageId);
    }

    [Fact]
    public async Task Rename_DuplicateDisplayNames_UniqueTags_JoinedServerBlocksRename_InvalidNameRejected()
    {
        var (factory, client, _) = CreateFactory();

        // Two independent identities may choose the identical display name —
        // the opaque PlayerId + SessionTag disambiguate them, never the name.
        var (aliceToken, alice) = await RegisterPlayerAsync(client, "Duplicate");
        var (bobToken, bob) = await RegisterPlayerAsync(client, "Duplicate");
        Assert.Equal(alice.DisplayName, bob.DisplayName);
        Assert.NotEqual(alice.PlayerId, bob.PlayerId);
        Assert.NotEqual(alice.SessionTag, bob.SessionTag);

        var serverA = await RegisterFreshServerAsync(client, "ServerA");
        var aliceConn = await ConnectAsync(factory, aliceToken);
        var bobConn = await ConnectAsync(factory, bobToken);

        // A valid rename while online broadcasts the updated profile.
        var renameWait = WaitForPushAsync<ChatPresence>(bobConn, "ChatPresenceChanged",
            p => p.Player.PlayerId == alice.PlayerId && p.Player.DisplayName == "Renamed");
        var renamed = await SetDisplayNameAsync(client, aliceToken, "Renamed");
        Assert.Equal("Renamed", renamed.DisplayName);
        Assert.Equal(alice.PlayerId, renamed.PlayerId);
        Assert.Equal(alice.SessionTag, renamed.SessionTag);
        var presencePush = await renameWait;
        Assert.True(presencePush.Online);

        // Once a connection of this identity has joined a GameServer, rename is blocked.
        await aliceConn.InvokeAsync("JoinLobby", serverA, 0);
        var blocked = await SetDisplayNameRawAsync(client, aliceToken, "TryAgain");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var blockedBody = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("joined_server", blockedBody.GetProperty("error").GetString());

        // Invalid names (too long here) are rejected with a stable code before touching any state.
        var invalid = await SetDisplayNameRawAsync(client, bobToken, new string('x', 25));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidBody = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_name", invalidBody.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Reconnect_PreservesIdentity_NoAutoMembership_RefreshKeepsIdentity_FreshGuestIsNewIdentity()
    {
        var (factory, client, _) = CreateFactory();

        var (aliceToken, alice) = await RegisterPlayerAsync(client, "Alice");
        var (bobToken, _) = await RegisterPlayerAsync(client, "Bob"); // presence observer
        var serverA = await RegisterFreshServerAsync(client, "ServerA");

        var bobConn = await ConnectAsync(factory, bobToken);
        var aliceConn1 = await ConnectAsync(factory, aliceToken);
        await aliceConn1.InvokeAsync("JoinLobby", serverA, 0);

        // Last connection of an identity disconnecting emits exactly one offline.
        var offlineWait = WaitForPushAsync<ChatPresence>(bobConn, "ChatPresenceChanged",
            p => p.Player.PlayerId == alice.PlayerId && !p.Online);
        await aliceConn1.DisposeAsync();
        await offlineWait;

        // Reconnecting with the same token is the same identity, but the
        // client must rejoin/revalidate explicitly — no carried-over membership.
        var aliceConn2 = await ConnectAsync(factory, aliceToken);
        var snapshot = await aliceConn2.InvokeAsync<ChatSnapshot>("GetChatState");
        Assert.Equal(alice.PlayerId, snapshot.Self.PlayerId);
        Assert.Equal(alice.SessionTag, snapshot.Self.SessionTag);
        Assert.Null(snapshot.Server.ServerId);
        Assert.Empty(snapshot.Server.Messages);

        // A second simultaneous connection for the same identity is allowed
        // (up to 4); only one of them may hold lobby membership at a time.
        var aliceConn3 = await ConnectAsync(factory, aliceToken);
        await aliceConn2.InvokeAsync("JoinLobby", serverA, 0);
        var duplicateJoin = await Assert.ThrowsAsync<HubException>(
            () => aliceConn3.InvokeAsync("JoinLobby", serverA, 0));
        Assert.Contains("already_joined", duplicateJoin.Message);

        // Refresh mints a new token for the SAME identity — it never creates a new guest.
        var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
        refreshRequest.Headers.Authorization = new("Bearer", aliceToken);
        var refreshResponse = await client.SendAsync(refreshRequest);
        refreshResponse.EnsureSuccessStatusCode();
        var refreshed = (await refreshResponse.Content.ReadFromJsonAsync<GuestAuthResponse>())!;
        Assert.Equal(alice.PlayerId, refreshed.SteamId.ToString());
        Assert.NotEqual(aliceToken, refreshed.Token);
        Assert.True(refreshed.ExpiresAt > DateTimeOffset.UtcNow);

        var aliceConn4 = await ConnectAsync(factory, refreshed.Token);
        var refreshedSnapshot = await aliceConn4.InvokeAsync<ChatSnapshot>("GetChatState");
        Assert.Equal(alice.PlayerId, refreshedSnapshot.Self.PlayerId);
        Assert.Equal(alice.SessionTag, refreshedSnapshot.Self.SessionTag);

        // A fresh /auth/guest call mints a genuinely new, different identity.
        var (_, dave) = await RegisterPlayerAsync(client, "Dave");
        Assert.NotEqual(alice.PlayerId, dave.PlayerId);

        // Direct sends fan out to EVERY live connection of the recipient
        // identity — never retargeted to a stale or unrelated connection.
        var conn2Wait = WaitForPushAsync<ChatMessage>(aliceConn2, "ChatMessage", m => m.Channel == "direct");
        var conn3Wait = WaitForPushAsync<ChatMessage>(aliceConn3, "ChatMessage", m => m.Channel == "direct");
        var conn4Wait = WaitForPushAsync<ChatMessage>(aliceConn4, "ChatMessage", m => m.Channel == "direct");
        await bobConn.InvokeAsync<ChatMessage>("SendDirect", alice.PlayerId, "multi-connection fanout");
        await conn2Wait;
        await conn3Wait;
        await conn4Wait;
    }

    [Fact]
    public async Task MessageQuota_SharedAcrossConnectionsAndReconnect_ControlBudgetSeparate()
    {
        var (factory, client, clock) = CreateFactory();

        var (aliceToken, alice) = await RegisterPlayerAsync(client, "QuotaAlice");
        var conn1 = await ConnectAsync(factory, aliceToken, readState: false);
        var conn1b = await ConnectAsync(factory, aliceToken, readState: false);

        for (var i = 0; i < 5; i++)
        {
            if (i % 2 == 0)
                await conn1.InvokeAsync<ChatMessage>("SendGlobal", $"msg-{i}");
            else
                await conn1.InvokeAsync<ChatMessage>("SendDirect", alice.PlayerId, $"msg-{i}");
        }
        clock.ShiftWallClock(TimeSpan.FromDays(1)); // wall-clock changes cannot replenish quotas

        // The shared allowance is exhausted — a 6th attempt from a DIFFERENT
        // connection of the same identity is still rejected (shared, not per-connection).
        var sixth = await Assert.ThrowsAsync<HubException>(
            () => conn1b.InvokeAsync<ChatMessage>("SendServer", Guid.NewGuid(), "msg-6"));
        Assert.Contains("rate_limited", sixth.Message);

        // The control budget (20/10s) is a separate counter — the rejected
        // message sends above must not have consumed any of it.
        for (var i = 0; i < 20; i++)
            await conn1.InvokeAsync<ChatPlayer[]>("GetOnlinePlayers");

        var controlLimited = await Assert.ThrowsAsync<HubException>(
            () => conn1b.InvokeAsync<ChatPlayer[]>("GetOnlinePlayers"));
        Assert.Contains("control_rate_limited", controlLimited.Message);

        // Disconnecting entirely does NOT reset the message allowance — it is
        // retained per identity until the window actually expires.
        await conn1.DisposeAsync();
        await conn1b.DisposeAsync();
        var conn2 = await ConnectAsync(factory, aliceToken, readState: false);
        var stillLimited = await Assert.ThrowsAsync<HubException>(
            () => conn2.InvokeAsync<ChatMessage>("SendGlobal", "msg-after-reconnect"));
        Assert.Contains("rate_limited", stillLimited.Message);

        clock.Advance(TimeSpan.FromSeconds(5));
        await conn2.InvokeAsync<ChatMessage>("SendGlobal", "msg-after-window");
        var stillControlLimited = await Assert.ThrowsAsync<HubException>(
            () => conn2.InvokeAsync<ChatPlayer[]>("GetOnlinePlayers"));
        Assert.Contains("control_rate_limited", stillControlLimited.Message);
        clock.Advance(TimeSpan.FromSeconds(5));
        await conn2.InvokeAsync<ChatPlayer[]>("GetOnlinePlayers");
    }

    [Fact]
    public async Task LongPollingHubTraffic_ExcludedFromHttpPostBudget_AuthEndpointsUnaffected()
    {
        // Deliberately default RateLimit:MaxRequestsPerWindow (10/10s) — this
        // proves /lobby transport traffic is excluded, not that the budget
        // was raised to dodge it.
        var (factory, client, _) = CreateFactory(httpRateLimit: 10);

        var (token, _) = await RegisterPlayerAsync(client, "PollBudget");
        var connection = await ConnectAsync(factory, token);

        // Long polling drives repeated POSTs to /lobby under the hood; well
        // more hub calls than the default HTTP POST budget must not 429 —
        // only /lobby/negotiate is protected, per the contract.
        for (var i = 0; i < 15; i++)
            await connection.InvokeAsync<ChatPlayer[]>("GetOnlinePlayers");

        // The transport exemption must not disable the independent guest-creation limit.
        for (var i = 1; i < 10; i++)
            await CreateGuestAsync(client);
        var guestResponse = await client.PostAsJsonAsync("/auth/guest", new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, guestResponse.StatusCode);
    }

    [Fact]
    public async Task SendGlobal_ValidatesUnicodeScalarBoundary_RejectsOversizedAndWhitespaceOnly()
    {
        var (factory, client, _) = CreateFactory();
        var (token, _) = await RegisterPlayerAsync(client, "Validator");
        var conn = await ConnectAsync(factory, token);

        // 500 Unicode SCALAR values, not UTF-16 code units: 499 ASCII chars
        // plus one supplementary-plane emoji (a surrogate pair = 1 scalar,
        // 2 UTF-16 units) lands exactly on the boundary and must be accepted.
        var atLimit = new string('a', 499) + "\U0001F600";
        var accepted = await conn.InvokeAsync<ChatMessage>("SendGlobal", atLimit);
        Assert.Equal(501, accepted.Text.Length);
        Assert.Equal(500, accepted.Text.EnumerateRunes().Count());

        // One more scalar value crosses the boundary and is rejected.
        var overLimit = new string('a', 500) + "\U0001F600";
        var rejected = await Assert.ThrowsAsync<HubException>(
            () => conn.InvokeAsync<ChatMessage>("SendGlobal", overLimit));
        Assert.Contains("invalid_message", rejected.Message);

        // Whitespace-only text is rejected, not silently trimmed to empty and sent.
        var whitespace = await Assert.ThrowsAsync<HubException>(
            () => conn.InvokeAsync<ChatMessage>("SendGlobal", "   "));
        Assert.Contains("invalid_message", whitespace.Message);

        // A literal newline in the body is text, not server-parsed markup.
        var withNewline = await conn.InvokeAsync<ChatMessage>("SendGlobal", "line one\nline two");
        Assert.Equal("line one\nline two", withNewline.Text);
        var control = await Assert.ThrowsAsync<HubException>(
            () => conn.InvokeAsync<ChatMessage>("SendGlobal", "embedded\0control"));
        Assert.Contains("invalid_message", control.Message);
        var quota = await Assert.ThrowsAsync<HubException>(
            () => conn.InvokeAsync<ChatMessage>("SendGlobal", "sixth attempt"));
        Assert.Contains("rate_limited", quota.Message);
    }

    [Fact]
    public async Task GlobalBacklog_BoundedAt50_RetainsImmutableSenderSnapshotAfterRename()
    {
        var (factory, client, clock) = CreateFactory();
        var (aliceToken, _) = await RegisterPlayerAsync(client, "BacklogAlice");
        var aliceConn = await ConnectAsync(factory, aliceToken);

        // A brand-new factory (fresh singleton ChatService) starts with an
        // empty backlog — no bleed across test instances.
        var initialSnapshot = await aliceConn.InvokeAsync<ChatSnapshot>("GetChatState");
        Assert.Empty(initialSnapshot.GlobalMessages);

        var preRenameMessages = await SendManyGlobalAsync(aliceConn, clock,
            Enumerable.Range(0, 10).Select(i => $"pre-rename-{i}"));

        await SetDisplayNameAsync(client, aliceToken, "RenamedAlice");

        await SendManyGlobalAsync(aliceConn, clock,
            Enumerable.Range(0, 45).Select(i => $"post-rename-{i}"));
        // Total sent: 10 + 45 = 55; the backlog retains only the last 50, so
        // the oldest 5 pre-rename messages age out and 5 pre-rename + all 45
        // post-rename messages remain.

        var (observerToken, _) = await RegisterPlayerAsync(client, "Observer");
        var observerConn = await ConnectAsync(factory, observerToken);
        var snapshot = await observerConn.InvokeAsync<ChatSnapshot>("GetChatState");

        Assert.Equal(50, snapshot.GlobalMessages.Length);
        Assert.DoesNotContain(snapshot.GlobalMessages, m => m.MessageId == preRenameMessages[0].MessageId);
        Assert.DoesNotContain(snapshot.GlobalMessages, m => m.MessageId == preRenameMessages[4].MessageId);

        // A retained pre-rename message still carries the sender's name AT
        // THE TIME IT WAS ACCEPTED — never the name applied by a later rename.
        var retainedPreRename = snapshot.GlobalMessages.First(m => m.MessageId == preRenameMessages[9].MessageId);
        Assert.Equal("BacklogAlice", retainedPreRename.Sender.DisplayName);

        // Anything sent after the rename shows the new name.
        Assert.Contains(snapshot.GlobalMessages, m => m.Sender.DisplayName == "RenamedAlice");
    }

    [Fact]
    public async Task ServerBacklog_IsBoundedAndRestoredOnlyForTheJoinedServer()
    {
        var (factory, client, clock) = CreateFactory();
        var (token, _) = await RegisterPlayerAsync(client, "History");
        var serverA = await RegisterFreshServerAsync(client, "A");
        var serverB = await RegisterFreshServerAsync(client, "B");
        var connection = await ConnectAsync(factory, token);
        await connection.InvokeAsync("JoinLobby", serverA, 0);
        var accepted = new List<ChatMessage>();
        for (var i = 0; i < 55; i++)
        {
            if (i % 5 == 0)
                clock.Advance(TimeSpan.FromSeconds(5));
            accepted.Add(await connection.InvokeAsync<ChatMessage>("SendServer", serverA, $"message-{i}"));
        }

        await connection.InvokeAsync("JoinLobby", serverB, 0);
        var otherServer = await connection.InvokeAsync<ChatSnapshot>("GetChatState");
        Assert.Equal(serverB, otherServer.Server.ServerId);
        Assert.Empty(otherServer.Server.Messages);
        Assert.Empty(otherServer.GlobalMessages);

        var replay = WaitForPushAsync<ServerChatState>(connection, "ChatServerChanged", state => state.ServerId == serverA);
        await connection.InvokeAsync("JoinLobby", serverA, 0);
        Assert.Equal(accepted.Skip(5).Select(message => message.MessageId),
            (await replay).Messages.Select(message => message.MessageId));
    }

    [Fact]
    public async Task FifthConnection_IsClosed_WithoutRemovingTheFourAcceptedConnections()
    {
        var (factory, client, _) = CreateFactory();
        var (token, profile) = await RegisterPlayerAsync(client, "FourConnections");
        var (observerToken, _) = await RegisterPlayerAsync(client, "Observer");
        var observer = await ConnectAsync(factory, observerToken);
        var accepted = new List<HubConnection>();
        for (var i = 0; i < 4; i++)
            accepted.Add(await ConnectAsync(factory, token));

        var fifth = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "lobby"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            }).Build();
        _connections.Add(fifth);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fifth.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        var startError = await Record.ExceptionAsync(() => fifth.StartAsync());
        if (startError is null && fifth.State != HubConnectionState.Disconnected)
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HubConnectionState.Disconnected, fifth.State);

        var received = accepted.Select(connection => WaitForPushAsync<ChatMessage>(
            connection, "ChatMessage", message => message.Channel == "direct")).ToArray();
        var direct = await observer.InvokeAsync<ChatMessage>("SendDirect", profile.PlayerId, "all four remain online");
        Assert.All(await Task.WhenAll(received), message => Assert.Equal(direct.MessageId, message.MessageId));

        await accepted[0].DisposeAsync();
        Assert.Contains(await observer.InvokeAsync<ChatPlayer[]>("GetOnlinePlayers"),
            player => player.PlayerId == profile.PlayerId);
    }


    [Fact]
    public async Task ResumeServer_RevalidatesPriorAdmissionWithoutWaitingRoster()
    {
        var (factory, client, _) = CreateFactory();
        var serverA = await RegisterFreshServerAsync(client, "Resume-A");
        var (token, _) = await RegisterPlayerAsync(client, "Resume");
        var first = await ConnectAsync(factory, token);
        await first.InvokeAsync("JoinLobby", serverA, 0);
        await first.DisposeAsync();

        var resumed = await ConnectAsync(factory, token);
        var state = WaitForPushAsync<ServerChatState>(
            resumed, "ChatServerChanged", snapshot => snapshot.ServerId == serverA);
        await resumed.InvokeAsync("ResumeServer", serverA, 0);
        Assert.Equal(serverA, (await state).ServerId);
        await Assert.ThrowsAsync<HubException>(() => resumed.InvokeAsync("HostStart"));
        var accepted = await resumed.InvokeAsync<ChatMessage>(
            "SendServer", serverA, "resume retains chat only");
        Assert.Equal(serverA, accepted.ServerId);

        await resumed.InvokeAsync("LeaveLobby");
        var afterLeave = await ConnectAsync(factory, token);
        var rejected = await Assert.ThrowsAsync<HubException>(
            () => afterLeave.InvokeAsync("ResumeServer", serverA, 0));
        Assert.Contains("not_admitted", rejected.Message);
    }
    [Fact]
    public async Task ServerHistoryCapacity_EvictsAnIdleChannelButPreservesAnActiveOne()
    {
        var (factory, client, clock) = CreateFactory();
        var servers = Enumerable.Range(0, 257).Select(index => new MasterServer.Data.Models.GameServer
        {
            Id = Guid.NewGuid(),
            Name = $"Server-{index}",
            IpAddress = "203.0.113.60",
            Port = 20000 + index,
            LastHeartbeat = DateTime.UtcNow
        }).ToArray();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.GameServers.AddRange(servers);
            await db.SaveChangesAsync();
        }

        var (activeToken, _) = await RegisterPlayerAsync(client, "StaysJoined");
        var (travelerToken, _) = await RegisterPlayerAsync(client, "ChangesServer");
        var active = await ConnectAsync(factory, activeToken);
        var traveler = await ConnectAsync(factory, travelerToken);
        await active.InvokeAsync("JoinLobby", servers[0].Id, 0);
        var oldest = await active.InvokeAsync<ChatMessage>("SendServer", servers[0].Id, "keep active history");
        for (var i = 1; i < servers.Length; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await traveler.InvokeAsync("JoinLobby", servers[i].Id, 0);
            await traveler.InvokeAsync<ChatMessage>("SendServer", servers[i].Id, $"history-{i}");
        }

        var activeHistory = await active.InvokeAsync<ChatSnapshot>("GetChatState");
        Assert.Equal(oldest.MessageId, Assert.Single(activeHistory.Server.Messages).MessageId);
        await traveler.InvokeAsync("JoinLobby", servers[1].Id, 0);
        Assert.Empty((await traveler.InvokeAsync<ChatSnapshot>("GetChatState")).Server.Messages);
        await traveler.InvokeAsync("JoinLobby", servers[2].Id, 0);
        Assert.Equal("history-2", Assert.Single(
            (await traveler.InvokeAsync<ChatSnapshot>("GetChatState")).Server.Messages).Text);
    }

    [Fact]
    public async Task ExpiredToken_CannotRefreshOrRenameTheGuest()
    {
        var (factory, client, _) = CreateFactory();
        var (token, _) = await RegisterPlayerAsync(client, "Expired");
        var handler = new JwtSecurityTokenHandler();
        var original = handler.ReadJwtToken(token);
        var secret = factory.Services.GetRequiredService<IConfiguration>()["Jwt:Secret"]!;
        var expired = handler.WriteToken(new JwtSecurityToken(
            original.Issuer, original.Audiences.Single(),
            original.Claims.Where(claim => claim.Type != JwtRegisteredClaimNames.Exp),
            expires: DateTime.UtcNow.AddMinutes(-1),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256)));

        using var refresh = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
        refresh.Headers.Authorization = new("Bearer", expired);
        using var response = await client.SendAsync(refresh);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var rename = await SetDisplayNameRawAsync(client, expired, "Rejected");
        Assert.Equal(HttpStatusCode.Unauthorized, rename.StatusCode);
    }


    [Fact]
    public async Task WebSocket_ClosesOnJwtExpiry_AndFreshTokenResumesRememberedServerMembership()
    {
        var (factory, client, clock) = CreateFactory();
        clock.ShiftWallClock(DateTimeOffset.UtcNow - clock.GetUtcNow());
        var (token, player) = await RegisterPlayerAsync(client, "Expiry");
        var server = await RegisterFreshServerWithTokenAsync(client, "Expiry-Server");
        var serverId = server.ServerId;
        var (observerToken, _) = await RegisterPlayerAsync(client, "Expiry-Observer");
        var observer = await ConnectAsync(factory, observerToken, readState: false);
        var offline = WaitForPushAsync<ChatPresence>(
            observer,
            "ChatPresenceChanged",
            presence => presence.Player.PlayerId == player.PlayerId && !presence.Online,
            TimeSpan.FromSeconds(25));

        var expiringToken = IssueTokenWithExpiry(
            token,
            factory.Services.GetRequiredService<IConfiguration>(),
            DateTime.UtcNow.AddSeconds(15));
        var connection = await ConnectWebSocketAsync(factory, expiringToken, readState: false);
        var tokenExpiry = new JwtSecurityTokenHandler().ReadJwtToken(expiringToken).ValidTo;
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += error =>
        {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };

        await connection.InvokeAsync("JoinLobby", serverId, 0);
        await observer.InvokeAsync("JoinLobby", serverId, 0);
        await connection.InvokeAsync("SelectCharacter", "Manki");
        await observer.InvokeAsync("SelectCharacter", "FightGuy");
        await connection.InvokeAsync("StartStageSelect");
        await connection.InvokeAsync("StartMatch", "training");
        var beforeExpiry = await connection.InvokeAsync<ChatMessage>(
            "SendServer", serverId, "before token expiry");
        Assert.Equal(serverId, beforeExpiry.ServerId);
        Assert.Equal(HubConnectionState.Connected, connection.State);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(25));
        Assert.True(DateTime.UtcNow >= tokenExpiry);
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
        await offline;
        var survivingMemberMessage = await observer.InvokeAsync<ChatMessage>(
            "SendServer", serverId, "server chat survives disconnect");
        Assert.Equal(serverId, survivingMemberMessage.ServerId);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.InvokeAsync<ChatMessage>("SendGlobal", "after token expiry"));

        using var refresh = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
        refresh.Headers.Authorization = new("Bearer", token);
        using var refreshResponse = await client.SendAsync(refresh);
        refreshResponse.EnsureSuccessStatusCode();
        var freshAuth = (await refreshResponse.Content.ReadFromJsonAsync<GuestAuthResponse>())!;
        Assert.Equal(player.PlayerId, freshAuth.SteamId.ToString());

        // The GameHost continues heartbeating while the player renews its session.
        using (var heartbeat = new HttpRequestMessage(HttpMethod.Post, $"/servers/{serverId}/heartbeat")
            { Content = JsonContent.Create(new HeartbeatRequest(0)) })
        {
            heartbeat.Headers.Authorization = new("Bearer", server.ApiToken);
            using var refreshed = await client.SendAsync(heartbeat);
            refreshed.EnsureSuccessStatusCode();
        }

        var reconnected = await ConnectWebSocketAsync(factory, freshAuth.Token, readState: false);
        var disconnectedState = await reconnected.InvokeAsync<ChatSnapshot>("GetChatState");
        Assert.Equal(player.PlayerId, disconnectedState.Self.PlayerId);
        Assert.Null(disconnectedState.Server.ServerId);

        await reconnected.InvokeAsync("ResumeServer", serverId, 0);
        var resumedState = await reconnected.InvokeAsync<ChatSnapshot>("GetChatState");
        Assert.Equal(serverId, resumedState.Server.ServerId);
        var resumedMessage = await reconnected.InvokeAsync<ChatMessage>(
            "SendServer", serverId, "server chat resumed");
        Assert.Equal(serverId, resumedMessage.ServerId);
        Assert.Equal(player.PlayerId, resumedMessage.Sender.PlayerId);
    }

    [Fact]
    public async Task ServerChat_UsesGameServerMembershipAcrossMatchesAndFullWaitingRoster()
    {
        var (factory, client, _) = CreateFactory();
        var serverA = await RegisterFreshServerAsync(client, "Shared-A");
        var serverB = await RegisterFreshServerAsync(client, "Stranger-B");

        async Task<HubConnection> NewPlayer(string name)
        {
            var (token, _) = await RegisterPlayerAsync(client, name);
            var connection = await ConnectAsync(factory, token);
            return connection;
        }

        // Four players launch a real hub match. MatchStarted releases their
        // waiting-roster slots but retains each authoritative Server membership.
        var fighters = new HubConnection[4];
        for (var i = 0; i < fighters.Length; i++)
        {
            fighters[i] = await NewPlayer($"Fighter-{i}");
            await fighters[i].InvokeAsync("JoinLobby", serverA, 0);
        }
        for (var i = 0; i < fighters.Length; i++)
            await fighters[i].InvokeAsync("SelectCharacter", "Manki");
        await fighters[0].InvokeAsync("StartStageSelect");
        await fighters[0].InvokeAsync("StartMatch", "training");

        // Fill a fresh waiting roster on the same GameServer, then prove a
        // fifth waiting join is still admitted to Server Chat but not the
        // roster. The caller sees both the honest lobby_full error and its
        // authoritative ChatServerChanged state.
        var waiting = new HubConnection[4];
        for (var i = 0; i < waiting.Length; i++)
        {
            waiting[i] = await NewPlayer($"Waiting-{i}");
            await waiting[i].InvokeAsync("JoinLobby", serverA, 0);
        }
        var full = await NewPlayer("Full-Roster");
        var fullState = WaitForPushAsync<ServerChatState>(
            full, "ChatServerChanged", state => state.ServerId == serverA);
        var fullError = await Assert.ThrowsAsync<HubException>(
            () => full.InvokeAsync("JoinLobby", serverA, 0));
        Assert.Contains("lobby_full", fullError.Message);
        Assert.Equal(serverA, (await fullState).ServerId);

        // The waiting four launch a second match on the same GameServer. Both
        // match groups and the waiting-only/full connections must share one
        // Server Chat audience, while another GameServer remains private.
        for (var i = 0; i < waiting.Length; i++)
            await waiting[i].InvokeAsync("SelectCharacter", "FightGuy");
        await waiting[0].InvokeAsync("StartStageSelect");
        await waiting[0].InvokeAsync("StartMatch", "training");

        var stranger = await NewPlayer("Other-Server");
        await stranger.InvokeAsync("JoinLobby", serverB, 0);
        var serverAMembers = fighters.Concat(waiting).Append(full).ToArray();
        var received = serverAMembers
            .Select(connection => WaitForPushAsync<ChatMessage>(
                connection, "ChatMessage", message => message.Channel == "server"
                    && message.ServerId == serverA))
            .ToArray();
        var strangerMessages = new ConcurrentQueue<ChatMessage>();
        using var strangerRegistration = stranger.On<ChatMessage>("ChatMessage", strangerMessages.Enqueue);

        var sent = await fighters[0].InvokeAsync<ChatMessage>(
            "SendServer", serverA, "one GameServer across waiting and matches");
        Assert.All(await Task.WhenAll(received), message => Assert.Equal(sent.MessageId, message.MessageId));
        await Task.Delay(100);
        Assert.DoesNotContain(strangerMessages, message => message.MessageId == sent.MessageId);
    }
}
