using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MasterServer.Steam;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MasterServer.Tests;

public sealed class SteamTicketVerifierTests
{
    private const string ApiKey = "publisher-secret-not-for-clients";
    private const long VerifiedSteamId = 76561198001234567;

    [Fact]
    public async Task VerifyAsync_AuthenticatesFixedIdentityOverHttpsAndChecksConfiguredAppOwnership()
    {
        var ticket = NewTicket();
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.Contains(
            "AuthenticateUserTicket", StringComparison.Ordinal)
            ? Json("""{"response":{"params":{"result":"OK","steamid":"76561198001234567"}}}""")
            : Json("""{"appownership":{"ownsapp":true}}"""));
        using var http = new HttpClient(handler);
        var verifier = CreateVerifier(http);

        var steamId = await verifier.VerifyAsync(ticket, CancellationToken.None);

        Assert.Equal(VerifiedSteamId, steamId);
        var requests = handler.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal("https", requests[0].Scheme);
        Assert.Equal("partner.steam-api.com", requests[0].Host);
        Assert.Equal("/ISteamUserAuth/AuthenticateUserTicket/v1/", requests[0].AbsolutePath);
        Assert.Equal("/ISteamUser/CheckAppOwnership/v4/", requests[1].AbsolutePath);
        var authentication = Query(requests[0]);
        Assert.Equal(ApiKey, authentication["key"]);
        Assert.Equal("123456", authentication["appid"]);
        Assert.Equal(ticket, authentication["ticket"]);
        Assert.Equal("sloparena-playtest", authentication["identity"]);
        var ownership = Query(requests[1]);
        Assert.Equal(ApiKey, ownership["key"]);
        Assert.Equal("123456", ownership["appid"]);
        Assert.Equal(VerifiedSteamId.ToString(), ownership["steamid"]);
    }

    [Fact]
    public async Task VerifyAsync_RejectsMalformedTicketInvalidAuthenticationAndMissingOwnership()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.Contains(
            "AuthenticateUserTicket", StringComparison.Ordinal)
            ? Json("""{"response":{"params":{"result":"Invalid","steamid":"76561198001234567"}}}""")
            : Json("""{"appownership":{"ownsapp":false}}"""));
        using var http = new HttpClient(handler);
        var verifier = CreateVerifier(http);

        Assert.Null(await verifier.VerifyAsync("not-hex", CancellationToken.None));
        Assert.Empty(handler.Requests);
        Assert.Null(await verifier.VerifyAsync(NewTicket(), CancellationToken.None));
        Assert.Single(handler.Requests);

        var ownerHandler = new StubHandler(request => request.RequestUri!.AbsolutePath.Contains(
            "AuthenticateUserTicket", StringComparison.Ordinal)
            ? Json("""{"response":{"params":{"result":"OK","steamid":"76561198001234567"}}}""")
            : Json("""{"appownership":{"ownsapp":false}}"""));
        using var ownerHttp = new HttpClient(ownerHandler);
        Assert.Null(await CreateVerifier(ownerHttp).VerifyAsync(NewTicket(), CancellationToken.None));
        Assert.Equal(2, ownerHandler.Requests.Count);
    }

    [Fact]
    public async Task VerifyAsync_RejectsAuthenticationHttpErrorsWithoutNullDereference()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var http = new HttpClient(handler);

        var verified = await CreateVerifier(http).VerifyAsync(NewTicket(), CancellationToken.None);

        Assert.Null(verified);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task VerifyAsync_MapsUpstreamFailuresToSanitizedAvailabilityError()
    {
        var ticket = NewTicket();
        var handler = new StubHandler(_ => throw new HttpRequestException($"failed for {ApiKey} {ticket}"));
        using var http = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<SteamApiUnavailableException>(() =>
            CreateVerifier(http).VerifyAsync(ticket, CancellationToken.None));

        Assert.DoesNotContain(ApiKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ticket, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RejectsOversizedUpstreamResponse()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[64 * 1024 + 1])
        });
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<SteamApiUnavailableException>(() =>
            CreateVerifier(http).VerifyAsync(NewTicket(), CancellationToken.None));
    }

    [Fact]
    public void IsValidTicket_RequiresBoundedEvenLengthHex()
    {
        Assert.True(SteamTicketVerifier.IsValidTicket("A1b2"));
        Assert.False(SteamTicketVerifier.IsValidTicket(null));
        Assert.False(SteamTicketVerifier.IsValidTicket(""));
        Assert.False(SteamTicketVerifier.IsValidTicket("ABC"));
        Assert.False(SteamTicketVerifier.IsValidTicket("GG"));
        Assert.False(SteamTicketVerifier.IsValidTicket(new string('A', 8194)));
    }

    private static SteamTicketVerifier CreateVerifier(HttpClient http) => new(
        http,
        new SteamAuthOptions(SteamAuthOptions.SteamMode, ApiKey, 123456, SteamAuthOptions.BackendIdentity),
        NullLogger<SteamTicketVerifier>.Instance);

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static string NewTicket() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(segment => segment.Split('=', 2))
        .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
