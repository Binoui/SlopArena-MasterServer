using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MasterServer.Tests;

public sealed class ProxyAndHealthIntegrationTests
{
    [Fact]
    public async Task Development_IgnoresForwardedForWhenPartitioningRateLimits()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Deployment:Profile"] = "development",
            ["RateLimit:MaxRequestsPerWindow"] = "1"
        });
        using var client = factory.CreateClient();

        using var first = await GuestRequest(client, "198.51.100.10");
        using var second = await GuestRequest(client, "198.51.100.11");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task Vps_TrustedProxyPartitionsRateLimitsByForwardedClientIp()
    {
        var configuration = VpsConfiguration();
        configuration["Proxy:TrustedAddress"] = "127.0.0.1";
        configuration["RateLimit:MaxRequestsPerWindow"] = "1";
        using var factory = CreateFactory(configuration);
        using var client = factory.CreateClient();

        using var first = await GuestRequest(client, "198.51.100.20");
        using var second = await GuestRequest(client, "198.51.100.21");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Health_RemainsLiveWhenDatabaseIsUnavailableAndReadinessFails()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Deployment:Profile"] = "development",
                    ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Password=test;Timeout=1;Command Timeout=1"
                }));
        });
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health");
        using var ready = await client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public void VpsStartup_RequiresSpecificIpv4TrustedProxy()
    {
        foreach (var address in new string?[] { null, "0.0.0.0", "::1" })
        {
            var configuration = VpsConfiguration();
            configuration["Proxy:TrustedAddress"] = address;
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration)));

            var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            Assert.Contains("Proxy:TrustedAddress", exception.ToString(), StringComparison.Ordinal);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?> config) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(config));
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<MasterServer.Data.AppDbContext>));
                if (descriptor is not null)
                    services.Remove(descriptor);
                services.AddDbContext<MasterServer.Data.AppDbContext>(options =>
                    options.UseInMemoryDatabase($"proxy-health-{Guid.NewGuid():N}"));
            });
        });

    private static Task<HttpResponseMessage> GuestRequest(HttpClient client, string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/guest")
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        return client.SendAsync(request);
    }

    private static Dictionary<string, string?> VpsConfiguration() => new()
    {
        ["Deployment:Profile"] = "vps",
        ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Database=test;Username=test;Password=test",
        ["ApprovedHost:Id"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        ["ApprovedHost:RegistrationKey"] = "registration-secret-0123456789abcdef",
        ["ApprovedHost:PublicHost"] = "game.example.com",
        ["ApprovedHost:PublicPort"] = "28765",
        ["ApprovedHost:ControlUrl"] = "http://gameserver.internal:28765/match/start",
        ["MatchControl:Key"] = "control-secret-0123456789abcdef01234567",
        ["Jwt:Secret"] = "vps-test-jwt-secret-0123456789abcdefghijklmnopqrstuvwxyz"
    };
}
