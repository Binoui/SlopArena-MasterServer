using System.Globalization;
using MasterServer.Configuration;

namespace MasterServer.Steam;

public sealed record SteamAuthOptions(
    string Mode,
    string? SteamApiKey,
    uint? SteamAppId,
    string SteamIdentity)
{
    public const string SteamMode = "steam";
    public const string DevelopmentGuestMode = "development-guest";
    public const string BackendIdentity = "sloparena-playtest";

    public bool UsesSteam => Mode == SteamMode;
    public bool UsesDevelopmentGuests => Mode == DevelopmentGuestMode;

    public static SteamAuthOptions Load(IConfiguration configuration, MasterDeploymentOptions deployment)
    {
        var mode = configuration["Auth:Mode"]?.Trim().ToLowerInvariant();
        if (deployment.IsVps && mode != SteamMode)
            throw new InvalidOperationException("Auth:Mode must be 'steam' in VPS mode.");
        if (mode is not (SteamMode or DevelopmentGuestMode))
            throw new InvalidOperationException(
                "Auth:Mode must be explicitly set to 'steam' or 'development-guest'.");
        if (mode == DevelopmentGuestMode && deployment.IsVps)
            throw new InvalidOperationException("Development guest authentication is not allowed in VPS mode.");

        var apiKey = configuration["Steam:ApiKey"];
        var appIdText = configuration["Steam:AppId"];
        var identity = configuration["Steam:Identity"];
        var appId = 0u;
        if (mode == SteamMode &&
            (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 4096 || apiKey.Any(char.IsControl) ||
             !uint.TryParse(appIdText, NumberStyles.None, CultureInfo.InvariantCulture, out appId) || appId == 0 ||
             identity != BackendIdentity))
            throw new InvalidOperationException(
                "Steam mode requires Steam:ApiKey, a positive Steam:AppId, and Steam:Identity='sloparena-playtest'.");

        return new SteamAuthOptions(mode, apiKey, mode == SteamMode ? appId : null, identity ?? BackendIdentity);
    }
}
