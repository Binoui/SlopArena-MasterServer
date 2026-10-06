using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MasterServer.Chat;
using MasterServer.Data;
using MasterServer.Data.Models;
using MasterServer.DTOs;
using MasterServer.Rooms;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MasterServer.Tests;

public sealed class OwnerObservationIntegrationTests : IDisposable
{
    private const string OwnerKey = "owner-console-test-key-0123456789abcdef";
    private readonly List<WebApplicationFactory<Program>> _factories = new();
    private readonly List<HubConnection> _connections = new();

    [Fact]
    public async Task Endpoint_requires_dedicated_key_and_is_disabled_without_a_long_key()
    {
        var (factory, client) = CreateFactory();
        using (client)
        {
            using var anonymous = await client.GetAsync("/owner/observation");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal("no-store", anonymous.Headers.CacheControl?.ToString());

            using var wrong = new HttpRequestMessage(HttpMethod.Get, "/owner/observation");
            wrong.Headers.Add("X-Console-Key", "wrong-console-key-0123456789abcdef");
            using var wrongResponse = await client.SendAsync(wrong);
            Assert.Equal(HttpStatusCode.Unauthorized, wrongResponse.StatusCode);

            var (_, shortKeyClient) = CreateFactory(consoleKey: "too-short");
            using (shortKeyClient)
            {
                using var shortKeyResponse = await shortKeyClient.GetAsync("/owner/observation");
                Assert.Equal(HttpStatusCode.NotFound, shortKeyResponse.StatusCode);
                Assert.Equal("no-store", shortKeyResponse.Headers.CacheControl?.ToString());
            }

            var (_, missingKeyClient) = CreateFactory(consoleKey: null);
            using (missingKeyClient)
            {
                using var missingKeyResponse = await missingKeyClient.GetAsync("/owner/observation");
                Assert.Equal(HttpStatusCode.NotFound, missingKeyResponse.StatusCode);
            }

            var session = await RegisterPlayerAsync(client, "Player JWT");
            using var playerRequest = new HttpRequestMessage(HttpMethod.Get, "/owner/observation");
            playerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            using var playerResponse = await client.SendAsync(playerRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, playerResponse.StatusCode);

            using var ownerRequest = new HttpRequestMessage(HttpMethod.Get, "/owner/observation");
            ownerRequest.Headers.Add("X-Console-Key", OwnerKey);
            using var ownerResponse = await client.SendAsync(ownerRequest);
            Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
            Assert.Equal("no-store", ownerResponse.Headers.CacheControl?.ToString());
            using var json = await JsonDocument.ParseAsync(await ownerResponse.Content.ReadAsStreamAsync());
            Assert.Empty(json.RootElement.GetProperty("players").EnumerateArray());
            Assert.Equal(0, json.RootElement.GetProperty("rooms").GetArrayLength());
            Assert.Equal(0, json.RootElement.GetProperty("lobbies").GetArrayLength());
            Assert.Equal(0, json.RootElement.GetProperty("matches").GetArrayLength());
            Assert.Equal(0, json.RootElement.GetProperty("globalChat").GetArrayLength());
            Assert.Equal(0, json.RootElement.GetProperty("servers").GetArrayLength());
            using var scope = factory.Services.CreateScope();
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync());
        }
    }

    [Fact]
    public async Task Owner_gets_safe_live_projection_with_links_presence_and_bounded_terminal_matches()
    {
        var (factory, client) = CreateFactory();
        using (client)
        {
            var leader = await RegisterPlayerAsync(client, "Leader");
            var member = await RegisterPlayerAsync(client, "Member");
            var lobbyPlayer = await RegisterPlayerAsync(client, "Lobby Player");
            var unassociated = await RegisterPlayerAsync(client, "Unassociated");
            var leaderHub = await ConnectAsync(factory, leader.Token);
            var memberHub = await ConnectAsync(factory, member.Token);
            var lobbyHub = await ConnectAsync(factory, lobbyPlayer.Token);
            await ConnectAsync(factory, unassociated.Token);
            var room = await leaderHub.InvokeAsync<RoomSnapshot>("CreateRoom", "Observation room");
            await memberHub.InvokeAsync<RoomSnapshot>("JoinRoom", room.Id);
            await leaderHub.InvokeAsync<RoomSnapshot>("RoomStartCharacterSelect");
            await leaderHub.InvokeAsync<RoomSnapshot>("RoomSelectCharacter", "Manki");
            await memberHub.InvokeAsync<RoomSnapshot>("RoomSelectCharacter", "Manki");
            await leaderHub.InvokeAsync<RoomSnapshot>("RoomStartStageSelect");
            await leaderHub.InvokeAsync<RoomSnapshot>("RoomChooseArena", "arena");
            var globalMessage = await leaderHub.InvokeAsync<ChatMessage>("SendGlobal", "Owner-visible global");

            var serverId = Guid.NewGuid();
            var serverSteamId = "76561198000000009";
            var serverInstanceId = Guid.NewGuid();
            RoomLaunchPreparation preparation;
            using (var scope = factory.Services.CreateScope())
            {
                var roomManager = scope.ServiceProvider.GetRequiredService<RoomManager>();
                var launch = roomManager.BeginMatch(leader.SteamId, out var error);
                Assert.Null(error);
                Assert.NotNull(launch);
                preparation = launch!;
                Assert.NotNull(roomManager.CompleteLaunch(room.Id, preparation.MatchId));
            }
            var now = DateTime.UtcNow;
            var activeMatchId = preparation.MatchId;
            var canceledMatchId = Guid.NewGuid();
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.GameServers.Add(new GameServer
                {
                    Id = serverId,
                    Name = "Fresh host",
                    IpAddress = "203.0.113.51",
                    Port = 24101,
                    SteamId = serverSteamId,
                    InstanceId = serverInstanceId,
                    Region = "EU",
                    CurrentMatches = 1,
                    MaxConcurrentMatches = 4,
                    LastHeartbeat = now,
                    ApiTokenHash = new string('x', 64),
                    CatalogHash = new string('c', 64),
                    CustomRulesJson = "{\"private\":true}"
                });
                db.GameServers.Add(new GameServer
                {
                    Id = Guid.NewGuid(),
                    Name = "Stale host",
                    IpAddress = "203.0.113.52",
                    Port = 24102,
                    Region = "NA",
                    CurrentMatches = 5,
                    MaxConcurrentMatches = 4,
                    LastHeartbeat = now.AddSeconds(-20),
                    ApiTokenHash = new string('y', 64),
                    CatalogHash = new string('d', 64)
                });
                db.Matches.Add(new Match
                {
                    Id = activeMatchId,
                    Player1SteamId = leader.SteamId,
                    Player2SteamId = member.SteamId,
                    RoomId = room.Id,
                    ServerId = serverId,
                    ServerRegion = "EU",
                    StartedAt = now.AddSeconds(-30)
                });
                db.Matches.Add(new Match
                {
                    Id = canceledMatchId,
                    Player1SteamId = leader.SteamId,
                    Player2SteamId = member.SteamId,
                    ServerRegion = "EU",
                    StartedAt = now.AddMinutes(-12),
                    CanceledAt = now.AddMinutes(-11),
                    CancelReason = "host_shutdown"
                });
                for (var index = 0; index < 51; index++)
                {
                    var startedAt = now.AddHours(-1).AddSeconds(index);
                    db.Matches.Add(new Match
                    {
                        Id = Guid.NewGuid(),
                        Player1SteamId = leader.SteamId,
                        Player2SteamId = member.SteamId,
                        ServerRegion = "EU",
                        StartedAt = startedAt,
                        EndedAt = startedAt.AddSeconds(60)
                    });
                }
                await db.SaveChangesAsync();
            }
            await lobbyHub.InvokeAsync("JoinLobby", serverId, 0);

            using var firstRead = await GetOwnerObservationAsync(client);
            var snapshot = firstRead.RootElement;
            var capturedAt = DateTimeOffset.Parse(snapshot.GetProperty("capturedAtUtc").GetString()!, CultureInfo.InvariantCulture);
            Assert.Equal(TimeSpan.Zero, capturedAt.Offset);

            var players = snapshot.GetProperty("players").EnumerateArray().ToArray();
            Assert.Equal(4, players.Length);
            var observedLeader = players.Single(player => player.GetProperty("steamId").GetString() == leader.SteamId.ToString(CultureInfo.InvariantCulture));
            Assert.Equal("Leader", observedLeader.GetProperty("name").GetString());
            Assert.True(observedLeader.GetProperty("masterConnected").GetBoolean());
            Assert.Equal(room.Id.ToString(), observedLeader.GetProperty("roomId").GetString());
            Assert.Equal(activeMatchId.ToString(), observedLeader.GetProperty("matchId").GetString());
            Assert.Equal(serverId.ToString(), observedLeader.GetProperty("serverId").GetString());
            Assert.Equal("unknown", observedLeader.GetProperty("gameServerConnectivity").GetString());

            var noLinks = players.Single(player => player.GetProperty("steamId").GetString() == unassociated.SteamId.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(JsonValueKind.Null, noLinks.GetProperty("roomId").ValueKind);
            Assert.Equal(JsonValueKind.Null, noLinks.GetProperty("matchId").ValueKind);
            Assert.Equal(JsonValueKind.Null, noLinks.GetProperty("serverId").ValueKind);
            Assert.Equal("unknown", noLinks.GetProperty("gameServerConnectivity").GetString());
            var lobbyObserved = players.Single(player => player.GetProperty("steamId").GetString() == lobbyPlayer.SteamId.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(JsonValueKind.Null, lobbyObserved.GetProperty("roomId").ValueKind);
            Assert.Equal(JsonValueKind.Null, lobbyObserved.GetProperty("matchId").ValueKind);
            Assert.Equal(serverId.ToString(), lobbyObserved.GetProperty("serverId").GetString());

            var rooms = snapshot.GetProperty("rooms").EnumerateArray().ToArray();
            Assert.Single(rooms);
            Assert.Equal(room.Id.ToString(), rooms[0].GetProperty("id").GetString());
            Assert.Equal("Observation room", rooms[0].GetProperty("name").GetString());
            Assert.Equal("In Match", rooms[0].GetProperty("phase").GetString());
            Assert.Equal(leader.SteamId.ToString(CultureInfo.InvariantCulture), rooms[0].GetProperty("leaderSteamId").GetString());
            Assert.Equal(2, rooms[0].GetProperty("memberCount").GetInt32());
            Assert.Equal(4, rooms[0].GetProperty("capacity").GetInt32());
            Assert.Equal("arena", rooms[0].GetProperty("arena").GetString());
            Assert.Equal(activeMatchId.ToString(), rooms[0].GetProperty("activeMatchId").GetString());
            Assert.Equal(leader.SteamId.ToString(CultureInfo.InvariantCulture), rooms[0].GetProperty("members")[0].GetProperty("steamId").GetString());
            Assert.False(rooms[0].GetProperty("joinable").GetBoolean());
            Assert.True(rooms[0].GetProperty("members")[0].GetProperty("isLeader").GetBoolean());
            Assert.Equal("Manki", rooms[0].GetProperty("members")[0].GetProperty("characterSelection").GetString());
            Assert.True(rooms[0].GetProperty("members")[0].GetProperty("lockedIn").GetBoolean());

            var lobby = Assert.Single(snapshot.GetProperty("lobbies").EnumerateArray().ToArray());
            Assert.Equal(serverId.ToString(), lobby.GetProperty("serverId").GetString());
            Assert.Equal(lobbyPlayer.SteamId.ToString(CultureInfo.InvariantCulture), lobby.GetProperty("members")[0].GetProperty("steamId").GetString());
            Assert.True(lobby.GetProperty("members")[0].GetProperty("isLeader").GetBoolean());

            var matches = snapshot.GetProperty("matches").EnumerateArray().ToArray();
            Assert.Equal(51, matches.Length); // one active plus only the newest 50 terminal rows
            var active = matches.Single(match => match.GetProperty("id").GetString() == activeMatchId.ToString());
            Assert.Equal("running", active.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, active.GetProperty("endedAtUtc").ValueKind);
            Assert.Equal(JsonValueKind.Null, active.GetProperty("canceledAtUtc").ValueKind);
            Assert.Equal("Leader", active.GetProperty("roster")[0].GetProperty("name").GetString());
            var canceled = matches.Single(match => match.GetProperty("id").GetString() == canceledMatchId.ToString());
            Assert.Equal("canceled", canceled.GetProperty("status").GetString());
            Assert.Equal("host_shutdown", canceled.GetProperty("cancelReason").GetString());
            var completed = matches.First(match => match.GetProperty("status").GetString() == "completed");
            Assert.NotEqual(JsonValueKind.Null, completed.GetProperty("endedAtUtc").ValueKind);
            Assert.Equal(JsonValueKind.Null, completed.GetProperty("canceledAtUtc").ValueKind);
            Assert.Contains(matches, match => match.GetProperty("status").GetString() == "completed");
            Assert.DoesNotContain(matches, match => match.GetProperty("startedAtUtc").GetDateTimeOffset() == now.AddHours(-1));

            var global = Assert.Single(snapshot.GetProperty("globalChat").EnumerateArray().ToArray());
            Assert.Equal(globalMessage.MessageId.ToString(), global.GetProperty("id").GetString());
            Assert.Equal("Global", global.GetProperty("channel").GetString());
            Assert.Equal("Owner-visible global", global.GetProperty("text").GetString());
            Assert.Equal(leader.SteamId.ToString(CultureInfo.InvariantCulture), global.GetProperty("senderId").GetString());

            var servers = snapshot.GetProperty("servers").EnumerateArray().ToArray();
            Assert.Equal(2, servers.Length);
            var fresh = servers.Single(server => server.GetProperty("id").GetString() == serverId.ToString());
            Assert.Equal("fresh", fresh.GetProperty("status").GetString());
            Assert.Equal(3, fresh.GetProperty("availableMatchSlots").GetInt32());
            Assert.Equal(1, fresh.GetProperty("currentMatches").GetInt32());
            Assert.Equal(4, fresh.GetProperty("maxConcurrentMatches").GetInt32());
            var stale = servers.Single(server => server.GetProperty("name").GetString() == "Stale host");
            Assert.Equal("stale", stale.GetProperty("status").GetString());
            Assert.Equal(0, stale.GetProperty("availableMatchSlots").GetInt32());

            var serialized = snapshot.GetRawText();
            Assert.DoesNotContain(new string('x', 64), serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('y', 64), serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('c', 64), serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("private", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(serverSteamId, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(serverInstanceId.ToString(), serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("203.0.113.51", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("24101", serialized, StringComparison.Ordinal);
            using var again = await GetOwnerObservationAsync(client);
            Assert.Equal(4, again.RootElement.GetProperty("players").GetArrayLength());
            var online = await leaderHub.InvokeAsync<ChatPlayer[]>("GetOnlinePlayers");
            Assert.Equal(4, online.Length);
            using var countScope = factory.Services.CreateScope();
            Assert.Equal(4, await countScope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync());

            await memberHub.InvokeAsync("LeaveRoom");
            var movedRoom = await memberHub.InvokeAsync<RoomSnapshot>("CreateRoom", "New current room");
            using var movedObservation = await GetOwnerObservationAsync(client);
            var movedPlayer = movedObservation.RootElement.GetProperty("players").EnumerateArray()
                .Single(player => player.GetProperty("steamId").GetString() ==
                    member.SteamId.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(movedRoom.Id.ToString(), movedPlayer.GetProperty("roomId").GetString());
            Assert.Equal(activeMatchId.ToString(), movedPlayer.GetProperty("matchId").GetString());
            var originalMatch = movedObservation.RootElement.GetProperty("matches").EnumerateArray()
                .Single(match => match.GetProperty("id").GetString() == activeMatchId.ToString());
            Assert.Equal(room.Id.ToString(), originalMatch.GetProperty("roomId").GetString());
        }
    }

    [Fact]
    public async Task Master_restart_clears_volatile_observation_but_keeps_persisted_match_and_host_rows()
    {
        var databaseName = $"owner-observation-restart-{Guid.NewGuid():N}";
        var databaseRoot = new InMemoryDatabaseRoot();
        var (firstFactory, firstClientSource) = CreateFactory(databaseName, OwnerKey, databaseRoot);
        using var firstClient = firstClientSource;
        var otherPlayer = await RegisterPlayerAsync(firstClient, "Other persisted player");
        var player = await RegisterPlayerAsync(firstClient, "Restart player");
        var hub = await ConnectAsync(firstFactory, player.Token);
        var room = await hub.InvokeAsync<RoomSnapshot>("CreateRoom", "Volatile room");
        await hub.InvokeAsync("SendGlobal", "Volatile message");
        var serverId = Guid.NewGuid();
        using (var scope = firstFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.GameServers.Add(new GameServer
            {
                Id = serverId,
                Name = "Persisted host",
                IpAddress = "203.0.113.61",
                Port = 24201,
                Region = "EU",
                CurrentMatches = 1,
                MaxConcurrentMatches = 2,
                LastHeartbeat = DateTime.UtcNow,
                ApiTokenHash = new string('z', 64)
            });
            db.Matches.Add(new Match
            {
                Id = Guid.NewGuid(),
                Player1SteamId = player.SteamId,
                Player2SteamId = otherPlayer.SteamId,
                RoomId = room.Id,
                ServerId = serverId,
                ServerRegion = "EU",
                StartedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using (var before = await GetOwnerObservationAsync(firstClient))
        {
            Assert.Single(before.RootElement.GetProperty("players").EnumerateArray());
            Assert.Single(before.RootElement.GetProperty("rooms").EnumerateArray());
            Assert.Single(before.RootElement.GetProperty("globalChat").EnumerateArray());
            Assert.Single(before.RootElement.GetProperty("matches").EnumerateArray());
            Assert.Single(before.RootElement.GetProperty("servers").EnumerateArray());
        }

        await hub.StopAsync();
        await hub.DisposeAsync();
        _connections.Remove(hub);
        firstClient.Dispose();
        firstFactory.Dispose();
        _factories.Remove(firstFactory);

        var (secondFactory, secondClient) = CreateFactory(databaseName, OwnerKey, databaseRoot);
        using (secondClient)
        using (var after = await GetOwnerObservationAsync(secondClient))
        {
            var snapshot = after.RootElement;
            Assert.Empty(snapshot.GetProperty("players").EnumerateArray());
            Assert.Empty(snapshot.GetProperty("rooms").EnumerateArray());
            Assert.Empty(snapshot.GetProperty("lobbies").EnumerateArray());
            Assert.Empty(snapshot.GetProperty("globalChat").EnumerateArray());
            Assert.Single(snapshot.GetProperty("matches").EnumerateArray());
            Assert.Equal(serverId.ToString(), Assert.Single(snapshot.GetProperty("servers").EnumerateArray()).GetProperty("id").GetString());
        }
    }

    [Fact]
    public async Task Global_history_in_snapshot_is_bounded_to_the_public_fifty_message_backlog()
    {
        var (factory, client) = CreateFactory();
        using (client)
        {
            for (var playerIndex = 0; playerIndex < 11; playerIndex++)
            {
                var player = await RegisterPlayerAsync(client, $"Player {playerIndex}");
                var connection = await ConnectAsync(factory, player.Token);
                var sends = playerIndex == 10 ? 1 : 5;
                for (var messageIndex = 0; messageIndex < sends; messageIndex++)
                    await connection.InvokeAsync("SendGlobal", $"message-{playerIndex}-{messageIndex}");
            }

            using var response = await GetOwnerObservationAsync(client);
            var messages = response.RootElement.GetProperty("globalChat").EnumerateArray().ToArray();
            Assert.Equal(50, messages.Length);
            Assert.Equal("message-0-1", messages[0].GetProperty("text").GetString());
            Assert.Equal("message-10-0", messages[^1].GetProperty("text").GetString());
        }
    }

    private (WebApplicationFactory<Program> Factory, HttpClient Client) CreateFactory(
        string? databaseName = null,
        string? consoleKey = OwnerKey,
        InMemoryDatabaseRoot? databaseRoot = null)
    {
        var resolvedDatabaseName = databaseName ?? $"owner-observation-{Guid.NewGuid():N}";
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Deployment:Profile"] = "development",
                    ["Auth:Mode"] = "development-guest",
                    ["Jwt:Secret"] = "owner-observation-test-jwt-secret-0123456789",
                    ["Room:CatalogHash"] = new string('a', 64),
                    ["Room:AdmittedCharacters:0"] = "Manki",
                    ["Room:AdmittedArenas:0"] = "arena",
                    ["RateLimit:MaxRequestsPerWindow"] = "1000",
                    ["Console:Key"] = consoleKey
                }));
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(
                    service => service.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor is not null)
                    services.Remove(descriptor);
                services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase(resolvedDatabaseName, databaseRoot));
            });
        });
        _factories.Add(factory);
        return (factory, factory.CreateClient());
    }

    private async Task<PlayerSession> RegisterPlayerAsync(HttpClient client, string name)
    {
        using var guestResponse = await client.PostAsJsonAsync("/auth/guest", new { });
        guestResponse.EnsureSuccessStatusCode();
        var guest = (await guestResponse.Content.ReadFromJsonAsync<GuestAuthResponse>())!;
        using var nameRequest = new HttpRequestMessage(HttpMethod.Put, "/auth/name")
        {
            Content = JsonContent.Create(new { displayName = name })
        };
        nameRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", guest.Token);
        using var nameResponse = await client.SendAsync(nameRequest);
        nameResponse.EnsureSuccessStatusCode();
        return new PlayerSession(guest.Token, guest.SteamId);
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

    private static async Task<JsonDocument> GetOwnerObservationAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/owner/observation");
        request.Headers.Add("X-Console-Key", OwnerKey);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }

    public void Dispose()
    {
        foreach (var connection in _connections)
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var factory in _factories)
            factory.Dispose();
    }

    private sealed record PlayerSession(string Token, long SteamId);
}
