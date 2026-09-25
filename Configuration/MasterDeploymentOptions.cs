using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace MasterServer.Configuration;

public sealed record MasterDeploymentOptions(
    string Profile,
    Guid? ApprovedHostId = null,
    string? RegistrationKey = null,
    string? PublicHost = null,
    int? PublicPort = null,
    Uri? ControlUrl = null,
    string? MatchControlKey = null,
    string? TrustedAddress = null)
{
    public bool IsVps => Profile == "vps";
    public IPAddress? TrustedProxyAddress =>
        TrustedAddress is not null ? IPAddress.Parse(TrustedAddress) : null;

    public static MasterDeploymentOptions Development { get; } = new("development");

    public static MasterDeploymentOptions Load(IConfiguration configuration)
    {
        var profile = configuration["Deployment:Profile"]?.Trim().ToLowerInvariant();
        if (profile == "development")
            return Development;
        if (profile != "vps")
            throw new InvalidOperationException(
                "Deployment:Profile must be explicitly set to 'development' or 'vps'.");
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection")))
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required in VPS mode.");

        var trustedAddressText = Required(configuration, "Proxy:TrustedAddress");
        if (!IPAddress.TryParse(trustedAddressText, out var trustedAddress)
            || trustedAddress.AddressFamily != AddressFamily.InterNetwork
            || trustedAddress.Equals(IPAddress.Any))
            throw new InvalidOperationException(
                "Proxy:TrustedAddress must be a specific IPv4 address in VPS mode.");
        if (!Guid.TryParse(configuration["ApprovedHost:Id"], out var hostId) || hostId == Guid.Empty)
            throw new InvalidOperationException("ApprovedHost:Id must be a non-empty GUID in VPS mode.");

        var registrationKey = Required(configuration, "ApprovedHost:RegistrationKey");
        var publicHost = Required(configuration, "ApprovedHost:PublicHost");
        if (Uri.CheckHostName(publicHost) is not (UriHostNameType.IPv4 or UriHostNameType.Dns))
            throw new InvalidOperationException("ApprovedHost:PublicHost must be an IPv4 address or DNS host name.");

        if (!int.TryParse(configuration["ApprovedHost:PublicPort"], NumberStyles.None,
                CultureInfo.InvariantCulture, out var publicPort) || publicPort is < 1 or > 65535)
            throw new InvalidOperationException("ApprovedHost:PublicPort must be between 1 and 65535.");

        var controlUrlText = Required(configuration, "ApprovedHost:ControlUrl");
        if (!Uri.TryCreate(controlUrlText, UriKind.Absolute, out var controlUrl)
            || controlUrl.Scheme != Uri.UriSchemeHttp
            || !string.IsNullOrEmpty(controlUrl.UserInfo)
            || !string.IsNullOrEmpty(controlUrl.Query)
            || !string.IsNullOrEmpty(controlUrl.Fragment)
            || controlUrl.AbsolutePath != "/match/start"
            || controlUrl.Port != publicPort
            || !IsPrivateControlHost(controlUrl)
            || SameHost(controlUrl.DnsSafeHost, publicHost))
            throw new InvalidOperationException(
                "ApprovedHost:ControlUrl must be a private absolute HTTP URL on the public base port, " +
                "with path /match/start and no user info, query, or fragment; it must differ from PublicHost.");

        var matchControlKey = Required(configuration, "MatchControl:Key");
        var jwtSecret = configuration["Jwt:Secret"];
        if (!IsBearerCredential(registrationKey) || !IsBearerCredential(matchControlKey) ||
            string.IsNullOrWhiteSpace(jwtSecret) || jwtSecret.Length < 32)
            throw new InvalidOperationException(
                "ApprovedHost:RegistrationKey and MatchControl:Key must be 32-4096 character bearer tokens; Jwt:Secret needs at least 32 characters.");
        if (string.Equals(registrationKey, matchControlKey, StringComparison.Ordinal)
            || string.Equals(registrationKey, jwtSecret, StringComparison.Ordinal)
            || string.Equals(matchControlKey, jwtSecret, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "ApprovedHost:RegistrationKey, MatchControl:Key, and Jwt:Secret must be distinct secrets.");

        return new MasterDeploymentOptions(
            profile, hostId, registrationKey, publicHost, publicPort, controlUrl, matchControlKey, trustedAddressText);
    }

    private static string Required(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{key} is required in VPS mode.");
        return value;
    }

    private static bool IsBearerCredential(string value)
    {
        if (value.Length is < 32 or > 4096) return false;
        bool padding = false;
        foreach (char c in value)
        {
            if (c == '=') { padding = true; continue; }
            if (padding || !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' or '+' or '/'))
                return false;
        }
        return true;
    }

    private static bool SameHost(string left, string right)
    {
        if (IPAddress.TryParse(left, out var leftAddress) && IPAddress.TryParse(right, out var rightAddress))
            return leftAddress.Equals(rightAddress);
        return string.Equals(left.TrimEnd('.'), right.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivateControlHost(Uri controlUrl)
    {
        if (IPAddress.TryParse(controlUrl.DnsSafeHost, out var address))
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address))
                return true;
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                return bytes[0] == 10
                    || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168)
                    || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
            }
            var ipv6 = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || (ipv6[0] & 0xfe) == 0xfc;
        }

        var host = controlUrl.IdnHost.TrimEnd('.');
        return !host.Contains('.')
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".svc", StringComparison.OrdinalIgnoreCase);
    }
}
