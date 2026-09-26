using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using MasterServer.Data;
using MasterServer.DTOs;
using MasterServer.Steam;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using MasterServer.Chat;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace MasterServer.Tests;

public sealed class SteamSessionIntegrationTests
{
    private const string Secret = "steam-auth-test-secret-0123456789abcdefghijklmnopqrstuvwxyz";
    private const long Alice = 76561198000000001;
    private const long Bob = 76561198000000002;

    private sealed class ValveHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.Contains("AuthenticateUserTicket", StringComparison.Ordinal))
            {
                var id = uri.Query.Contains("ticket=DD44", StringComparison.Ordinal) ? Bob : Alice;
                return Task.FromResult(Json(System.Text.Json.JsonSerializer.Serialize(
                    new { response = new { @params = new { result = "OK", steamid = id.ToString() } } })));
            }
            return Task.FromResult(Json("""{"appownership":{"ownsapp":true}}"""));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var databaseName = $"steam-auth-{Guid.NewGuid():N}";
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deployment:Profile"] = "development",
                ["Auth:Mode"] = "steam",
                ["Steam:ApiKey"] = "test-publisher-key",
                ["Steam:AppId"] = "5325920",
                ["Steam:Identity"] = "sloparena-playtest",
                ["Jwt:Secret"] = Secret,
                ["RateLimit:MaxRequestsPerWindow"] = "100000"
            }));
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<AppDbContext>(opts => opts.UseInMemoryDatabase(databaseName));
                services.AddTransient(sp => new SteamTicketVerifier(
                    new HttpClient(new ValveHandler()),
                    sp.GetRequiredService<SteamAuthOptions>(),
                    sp.GetRequiredService<ILogger<SteamTicketVerifier>>()));
            });
        });
    }

    [Fact]
    public async Task SteamLogin_RenewalAndSecondLaunchPreserveAccountAndProfile_RejectReplayedTicket()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var login = await client.PostAsJsonAsync("/auth/steam", new { ticket = "AA11" });
        login.EnsureSuccessStatusCode();
        var alice = (await login.Content.ReadFromJsonAsync<GuestAuthResponse>())!;
        Assert.Equal(Alice, alice.SteamId);
        Assert.InRange(alice.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));

        using var replay = await client.PostAsJsonAsync("/auth/steam", new { ticket = "AA11" });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using var rename = new HttpRequestMessage(HttpMethod.Put, "/auth/name")
        {
            Content = JsonContent.Create(new { displayName = "Saved Alice" })
        };
        rename.Headers.Authorization = new("Bearer", alice.Token);
        using var renamed = await client.SendAsync(rename);
        renamed.EnsureSuccessStatusCode();

        using var refresh = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh")
        {
            Content = JsonContent.Create(new { ticket = "BB22" })
        };
        refresh.Headers.Authorization = new("Bearer", alice.Token);
        using var renewedResponse = await client.SendAsync(refresh);
        renewedResponse.EnsureSuccessStatusCode();
        var renewed = (await renewedResponse.Content.ReadFromJsonAsync<GuestAuthResponse>())!;
        Assert.Equal(alice.SteamId, renewed.SteamId);
        Assert.NotEqual(alice.Token, renewed.Token);

        using var relaunch = await client.PostAsJsonAsync("/auth/steam", new { ticket = "CC33" });
        relaunch.EnsureSuccessStatusCode();
        var secondLaunch = (await relaunch.Content.ReadFromJsonAsync<GuestAuthResponse>())!;
        Assert.Equal(alice.SteamId, secondLaunch.SteamId);
        using var me = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        me.Headers.Authorization = new("Bearer", secondLaunch.Token);
        using var profile = await client.SendAsync(me);
        profile.EnsureSuccessStatusCode();
        Assert.Equal("Saved Alice", (await profile.Content.ReadFromJsonAsync<GuestUserInfo>())!.Username);

        using var wrongAccountRefresh = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh")
        {
            Content = JsonContent.Create(new { ticket = "DD44" })
        };
        wrongAccountRefresh.Headers.Authorization = new("Bearer", renewed.Token);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(wrongAccountRefresh)).StatusCode);

        using var bobResponse = await client.PostAsJsonAsync("/auth/steam", new { ticket = "DD44" });
        bobResponse.EnsureSuccessStatusCode();
        Assert.Equal(Bob, (await bobResponse.Content.ReadFromJsonAsync<GuestAuthResponse>())!.SteamId);
    }

    [Fact]
    public async Task VerifiedSteamSession_UsesExistingBrowserAndChatHub()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var login = await client.PostAsJsonAsync("/auth/steam", new { ticket = "EE55" });
        login.EnsureSuccessStatusCode();
        var auth = (await login.Content.ReadFromJsonAsync<GuestAuthResponse>())!;
        using var rename = new HttpRequestMessage(HttpMethod.Put, "/auth/name")
        {
            Content = JsonContent.Create(new { displayName = "Steam Player" })
        };
        rename.Headers.Authorization = new("Bearer", auth.Token);
        (await client.SendAsync(rename)).EnsureSuccessStatusCode();

        using var browser = new HttpRequestMessage(HttpMethod.Get, "/servers");
        browser.Headers.Authorization = new("Bearer", auth.Token);
        (await client.SendAsync(browser)).EnsureSuccessStatusCode();

        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "lobby"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(auth.Token);
            }).Build();
        await hub.StartAsync();
        var state = await hub.InvokeAsync<ChatSnapshot>("GetChatState");
        Assert.Equal("Steam Player", state.Self.DisplayName);
        var message = await hub.InvokeAsync<ChatMessage>("SendGlobal", "Hello from Steam");
        Assert.Equal(auth.SteamId.ToString(), message.Sender.PlayerId);
        Assert.Equal("Hello from Steam", message.Text);
    }

    [Fact]
    public async Task SteamLogin_RejectsNonHttpsAndOversizedTicketBody()
    {
        using var factory = CreateFactory();
        using var http = factory.CreateClient();
        using var insecure = await http.PostAsJsonAsync("/auth/steam", new { ticket = "AA11" });
        Assert.Equal(HttpStatusCode.Forbidden, insecure.StatusCode);

        using var https = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var oversized = await https.PostAsJsonAsync("/auth/steam",
            new { ticket = new string('A', 20_000) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
    }

    [Fact]
    public async Task SteamMode_RejectsPreviouslySignedGuestOnRestSignalRAndRefresh()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var guestIssuance = await client.PostAsync("/auth/guest", null);
        Assert.Equal(HttpStatusCode.NotFound, guestIssuance.StatusCode);

        var signedGuest = new JwtSecurityToken(
            issuer: "SlopArena.Master", audience: "SlopArena.Client",
            claims: new[] { new Claim(ClaimTypes.NameIdentifier, "42"), new Claim("provider", "guest") },
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),
                SecurityAlgorithms.HmacSha256));
        var token = new JwtSecurityTokenHandler().WriteToken(signedGuest);
        using var servers = new HttpRequestMessage(HttpMethod.Get, "/servers");
        servers.Headers.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(servers)).StatusCode);
        using var refresh = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
        refresh.Headers.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(refresh)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync($"/lobby/negotiate?access_token={token}", null)).StatusCode);
    }
}
