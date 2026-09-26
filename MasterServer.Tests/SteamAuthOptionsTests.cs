using MasterServer.Configuration;
using MasterServer.Steam;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MasterServer.Tests;

public sealed class SteamAuthOptionsTests
{
    [Fact]
    public void Load_AllowsSteamInDevelopmentWithFixedBackendIdentity()
    {
        var options = SteamAuthOptions.Load(Configuration(
            ("Auth:Mode", "steam"),
            ("Steam:ApiKey", "publisher-secret"),
            ("Steam:AppId", "123456"),
            ("Steam:Identity", SteamAuthOptions.BackendIdentity)), MasterDeploymentOptions.Development);

        Assert.True(options.UsesSteam);
        Assert.False(options.UsesDevelopmentGuests);
        Assert.Equal(123456u, options.SteamAppId);
        Assert.Equal(SteamAuthOptions.BackendIdentity, options.SteamIdentity);
    }

    [Fact]
    public void LoadAllowsGuestOnlyModeOnlyInDevelopment()
    {
        var configuration = Configuration(("Auth:Mode", "development-guest"));

        var options = SteamAuthOptions.Load(configuration, MasterDeploymentOptions.Development);

        Assert.True(options.UsesDevelopmentGuests);
        Assert.False(options.UsesSteam);
        Assert.Throws<InvalidOperationException>(() =>
            SteamAuthOptions.Load(configuration, new MasterDeploymentOptions("vps")));
    }

    [Fact]
    public void LoadRejectsMissingOrMismatchedSteamConfigurationWithoutPrintingSecrets()
    {
        const string secret = "publisher-secret";
        var configuration = Configuration(
            ("Auth:Mode", "steam"),
            ("Steam:ApiKey", secret),
            ("Steam:AppId", "not-an-app-id"),
            ("Steam:Identity", "wrong-backend"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SteamAuthOptions.Load(configuration, MasterDeploymentOptions.Development));

        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            settings.ToDictionary(setting => setting.Key, setting => (string?)setting.Value)).Build();
}
