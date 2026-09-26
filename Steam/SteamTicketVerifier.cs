using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MasterServer.Steam;

public sealed class SteamTicketVerifier
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly SteamAuthOptions _options;
    private readonly ILogger<SteamTicketVerifier> _logger;

    public SteamTicketVerifier(HttpClient http, SteamAuthOptions options, ILogger<SteamTicketVerifier> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public static bool IsValidTicket(string? ticket) => ticket is { Length: >= 2 and <= 8192 }
        && ticket.Length % 2 == 0
        && ticket.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    public async Task<long?> VerifyAsync(string ticket, CancellationToken cancellationToken)
    {
        if (!IsValidTicket(ticket))
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var authenticatePath = "/ISteamUserAuth/AuthenticateUserTicket/v1/" + Query(
                ("key", _options.SteamApiKey!),
                ("appid", _options.SteamAppId!.Value.ToString(CultureInfo.InvariantCulture)),
                ("ticket", ticket),
                ("identity", _options.SteamIdentity));
            var auth = await GetJsonAsync<AuthenticateEnvelope>(authenticatePath, "ticket authentication", timeout.Token);
            if (auth?.Response?.Parameters is not { Result: "OK", SteamId: { } steamIdText } ||
                !long.TryParse(steamIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId) ||
                steamId <= 0)
                return null;

            var ownershipPath = "/ISteamUser/CheckAppOwnership/v4/" + Query(
                ("key", _options.SteamApiKey!),
                ("steamid", steamId.ToString(CultureInfo.InvariantCulture)),
                ("appid", _options.SteamAppId.Value.ToString(CultureInfo.InvariantCulture)));
            var ownership = await GetJsonAsync<OwnershipEnvelope>(ownershipPath, "app ownership", timeout.Token);
            return ownership?.AppOwnership?.OwnsApp == true ? steamId : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Steam Web API timed out during authentication or ownership verification.");
            throw new SteamApiUnavailableException();
        }
        catch (HttpRequestException)
        {
            _logger.LogWarning("Steam Web API request failed during authentication or ownership verification.");
            throw new SteamApiUnavailableException();
        }
        catch (JsonException)
        {
            _logger.LogWarning("Steam Web API returned an invalid authentication or ownership response.");
            throw new SteamApiUnavailableException();
        }
        catch (InvalidDataException)
        {
            _logger.LogWarning("Steam Web API response exceeded the configured size limit.");
            throw new SteamApiUnavailableException();
        }
    }

    private async Task<T?> GetJsonAsync<T>(string pathAndQuery, string operation, CancellationToken cancellationToken)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://partner.steam-api.com" + pathAndQuery);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (operation == "ticket authentication" &&
                response.StatusCode is (HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                return null;
            throw new HttpRequestException("Steam Web API request failed.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException();

        using var buffer = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(rented.AsMemory(), cancellationToken);
                if (read == 0)
                    break;
                if (buffer.Length + read > MaximumResponseBytes)
                    throw new InvalidDataException();
                await buffer.WriteAsync(rented.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), JsonOptions)
            ?? throw new JsonException();
    }

    private static string Query(params (string Name, string Value)[] values) =>
        "?" + string.Join("&", values.Select(value =>
            Uri.EscapeDataString(value.Name) + "=" + Uri.EscapeDataString(value.Value)));

    private sealed record AuthenticateEnvelope(
        [property: JsonPropertyName("response")] AuthenticateResponse? Response);
    private sealed record AuthenticateResponse(
        [property: JsonPropertyName("params")] AuthenticateParameters? Parameters);
    private sealed record AuthenticateParameters(
        [property: JsonPropertyName("result")] string? Result,
        [property: JsonPropertyName("steamid")] string? SteamId);
    private sealed record OwnershipEnvelope(
        [property: JsonPropertyName("appownership")] AppOwnership? AppOwnership);
    private sealed record AppOwnership(
        [property: JsonPropertyName("ownsapp")] bool OwnsApp);
}

public sealed class SteamApiUnavailableException : Exception
{
}
