// MasterServer.Tests/LobbyHubAuthIntegrationTests.cs
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MasterServer.Data;
using MasterServer.DTOs;
using Xunit;

namespace MasterServer.Tests;

/// <summary>
/// Integration test: the SignalR negotiate endpoint enforces JWT auth
/// (issue #32 acceptance: "JWT auth is enforced on hub connection — no token =
/// connection rejected"). We hit the negotiate route directly; a missing token
/// must yield 401, and a valid guest token must yield 200.
/// </summary>
public class LobbyHubAuthIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public LobbyHubAuthIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace the real Postgres DbContext with an in-memory store so the
                // test does not need a database. AddSignalR already wired in Program.
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor is not null)
                    services.Remove(descriptor);

                services.AddDbContext<AppDbContext>(opts =>
                    opts.UseInMemoryDatabase("lobby-auth-test"));
            });
        });
    }

    [Fact]
    public async Task Negotiate_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/lobby/negotiate", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_WithValidGuestToken_Returns200()
    {
        var client = _factory.CreateClient();

        // Mint a guest token via the existing /auth/guest endpoint, then use it.
        var authResponse = await client.PostAsJsonAsync("/auth/guest", value: new { });
        authResponse.EnsureSuccessStatusCode();
        var auth = await authResponse.Content.ReadFromJsonAsync<GuestAuthResponse>();
        Assert.NotNull(auth);

        var request = new HttpRequestMessage(HttpMethod.Post, "/lobby/negotiate");
        request.Headers.Authorization = new("Bearer", auth!.Token);

        var response = await client.SendAsync(request);

        // The negotiate endpoint returns 200 with a connection token on success.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_WithQueryStringToken_Returns200()
    {
        // This is the browser SignalR flow: the client appends access_token as a
        // query parameter because WebSockets cannot set Authorization headers.
        var client = _factory.CreateClient();

        var authResponse = await client.PostAsJsonAsync("/auth/guest", value: new { });
        authResponse.EnsureSuccessStatusCode();
        var auth = await authResponse.Content.ReadFromJsonAsync<GuestAuthResponse>();
        Assert.NotNull(auth);

        // No Authorization header — token is in the query string only.
        var response = await client.PostAsync($"/lobby/negotiate?access_token={auth!.Token}", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_WithInvalidToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/lobby/negotiate?access_token=bogus", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
