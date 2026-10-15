using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Offline unit tests for the PAC-derived authentication foundation. Every test is fully offline
/// and non-interactive: no MSAL client is constructed, no broker/browser/WAM is reached, and no token
/// is acquired. File-backed behaviors run against per-test temporary directories.
/// </summary>
public sealed class ClientAppConfigurationTests
{
    // A syntactically valid, non-forbidden, non-zero application id used purely as a test vector.
    private const string ValidClientId = "11111111-2222-3333-4444-555555555555";

    [Theory]
    [InlineData("windows", "ms-appx-web://microsoft.aad.brokerplugin/11111111-2222-3333-4444-555555555555")]
    [InlineData("macos", "msauth.com.msauth.unsignedapp://auth")]
    [InlineData("linux", "https://login.microsoftonline.com/common/oauth2/nativeclient")]
    [InlineData("unknown", "ms-appx-web://microsoft.aad.brokerplugin/11111111-2222-3333-4444-555555555555")]
    public void Create_WithValidId_UsesThePlatformsBrokerReplyUrl_WithoutATrailingSlash(string platform, string expected)
    {
        var config = AgentHostClientAppConfiguration.Create(ValidClientId, FakePlatformInfo.Named(platform));

        Assert.Equal(Guid.Parse(ValidClientId), config.ClientId);
        Assert.Equal(expected, config.RedirectUri.OriginalString);
        Assert.False(AgentHostClientAppConfiguration.IsLoopbackRedirect(config.RedirectUri.OriginalString));
    }

    [Fact]
    public void Create_WithAConsentClientRedirect_UsesIt()
    {
        var config = AgentHostClientAppConfiguration.Create(ValidClientId, FakePlatformInfo.Linux(), "http://localhost:1234");

        Assert.Equal(new Uri("http://localhost:1234"), config.RedirectUri);
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:1234")]
    [InlineData("http://127.0.0.1:65000/callback")]
    public void IsLoopbackRedirect_AcceptsHttpLoopback(string redirectUri) =>
        Assert.True(AgentHostClientAppConfiguration.IsLoopbackRedirect(redirectUri));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-uri")]
    [InlineData("https://contoso.example/callback")]
    [InlineData("https://localhost")]
    [InlineData("https://localhost:1234")]
    [InlineData("https://127.0.0.1:65000/callback")]
    [InlineData("ms-appx-web://microsoft.aad.brokerplugin/21fadfba-178a-47f0-8ce6-07c05eb346ca")]
    [InlineData("msauth.com.msauth.unsignedapp://auth")]
    [InlineData("https://login.microsoftonline.com/common/oauth2/nativeclient")]
    public void IsLoopbackRedirect_RejectsEverythingElse(string? redirectUri) =>
        Assert.False(AgentHostClientAppConfiguration.IsLoopbackRedirect(redirectUri));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithUnconfiguredId_UsesTheShippedDefault(string? clientId)
    {
        var configuration = AgentHostClientAppConfiguration.Create(clientId, FakePlatformInfo.Windows());

        Assert.Equal(AgentHostClientAppConfiguration.DefaultClientId, configuration.ClientId);
        Assert.Equal(new Guid("21fadfba-178a-47f0-8ce6-07c05eb346ca"), configuration.ClientId);
        Assert.Equal(
            AgentHostClientAppConfiguration.WindowsBrokerRedirectUri(configuration.ClientId),
            configuration.RedirectUri);
    }

    [Fact]
    public void BrokerReplyUrls_MatchTheFirstPartyRegistrationExactly()
    {
        Assert.Equal(
            "ms-appx-web://microsoft.aad.brokerplugin/21fadfba-178a-47f0-8ce6-07c05eb346ca",
            AgentHostClientAppConfiguration.WindowsBrokerRedirectUri(AgentHostClientAppConfiguration.DefaultClientId).OriginalString);
        Assert.Equal("msauth.com.msauth.unsignedapp://auth", AgentHostClientAppConfiguration.MacOsBrokerRedirectUri.OriginalString);
        Assert.Equal("https://login.microsoftonline.com/common/oauth2/nativeclient", AgentHostClientAppConfiguration.LinuxBrokerRedirectUri.OriginalString);
    }

    [Theory]
    [InlineData("windows", "ms-appx-web://microsoft.aad.brokerplugin/11111111-2222-3333-4444-555555555555")]
    [InlineData("macos", "msauth.com.msauth.unsignedapp://auth")]
    [InlineData("linux", "https://login.microsoftonline.com/common/oauth2/nativeclient")]
    [InlineData("unknown", "ms-appx-web://microsoft.aad.brokerplugin/11111111-2222-3333-4444-555555555555")]
    public void PlatformRedirectUri_SelectsThePlatformsReplyUrl(string platform, string expected) =>
        Assert.Equal(
            expected,
            AgentHostClientAppConfiguration.PlatformRedirectUri(FakePlatformInfo.Named(platform), Guid.Parse(ValidClientId)).OriginalString);

    [Fact]
    public void Create_WithAnExplicitId_OverridesTheDefault()
    {
        var configuration = AgentHostClientAppConfiguration.Create("11111111-2222-3333-4444-555555555555", FakePlatformInfo.Windows());

        Assert.Equal(new Guid("11111111-2222-3333-4444-555555555555"), configuration.ClientId);
    }

    [Fact]
    public void Create_WithTheAllZeroId_IsStillUnconfigured()
    {
        var error = Assert.Throws<ClientApplicationConfigurationException>(
            () => AgentHostClientAppConfiguration.Create(Guid.Empty.ToString(), FakePlatformInfo.Windows()));

        Assert.Equal(ExitCodes.Failure, error.ExitCode);
    }

    [Fact]
    public void Create_WithPacClientId_IsForbidden()
    {
        var pacId = AgentHostClientAppConfiguration.ForbiddenPacClientId.ToString();

        var ex = Assert.Throws<ClientApplicationConfigurationException>(
            () => AgentHostClientAppConfiguration.Create(pacId, FakePlatformInfo.Windows()));

        Assert.Equal(ExitCodes.Failure, ex.ExitCode);
        Assert.Contains("PAC", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_WithNonGuidId_Throws()
    {
        var ex = Assert.Throws<ClientApplicationConfigurationException>(
            () => AgentHostClientAppConfiguration.Create("not-a-guid", FakePlatformInfo.Windows()));

        Assert.Contains("GUID", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_WithANonAbsoluteConsentRedirect_Throws()
    {
        Assert.Throws<UriFormatException>(
            () => AgentHostClientAppConfiguration.Create(ValidClientId, FakePlatformInfo.Windows(), "not/absolute"));
    }

    [Fact]
    public void Create_RequiresAPlatform() =>
        Assert.Throws<ArgumentNullException>(() => AgentHostClientAppConfiguration.Create(ValidClientId, null!));
}
public sealed class AuthorityResolverTests
{
    [Fact]
    public void Resolve_PublicCloud_NoTenant_UsesOrganizations()
    {
        var authority = AgentHostAuthorityResolver.Resolve(CloudInstance.Public);

        Assert.Equal(new Uri("https://login.microsoftonline.com/organizations"), authority.AuthorityUrl);
        Assert.Null(authority.TenantId);
        Assert.Equal(CloudInstance.Public, authority.Cloud);
    }

    [Fact]
    public void Resolve_WithTenant_PinsTenant()
    {
        var tenant = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var authority = AgentHostAuthorityResolver.Resolve(CloudInstance.Public, tenant);

        Assert.Equal(new Uri($"https://login.microsoftonline.com/{tenant}"), authority.AuthorityUrl);
        Assert.Equal(tenant, authority.TenantId);
    }

    [Fact]
    public void Resolve_WithEmptyTenant_FallsBackToOrganizations()
    {
        var authority = AgentHostAuthorityResolver.Resolve(CloudInstance.Public, Guid.Empty);

        Assert.Equal(new Uri("https://login.microsoftonline.com/organizations"), authority.AuthorityUrl);
        Assert.Null(authority.TenantId);
    }

    [Theory]
    [InlineData(CloudInstance.Public, "https://login.microsoftonline.com")]
    [InlineData(CloudInstance.UsGov, "https://login.microsoftonline.com")]
    [InlineData(CloudInstance.UsGovHigh, "https://login.microsoftonline.us")]
    [InlineData(CloudInstance.UsGovDod, "https://login.microsoftonline.us")]
    [InlineData(CloudInstance.China, "https://login.partner.microsoftonline.cn")]
    public void GetAuthorityHost_MapsEachCloud(CloudInstance cloud, string expectedHost)
    {
        Assert.Equal(expectedHost, AgentHostAuthorityResolver.GetAuthorityHost(cloud));
    }

    [Fact]
    public void ResolveFromUrl_AbsoluteHttps_Ok()
    {
        var url = new Uri("https://login.microsoftonline.com/organizations");
        var authority = AgentHostAuthorityResolver.ResolveFromUrl(CloudInstance.Public, url);
        Assert.Equal(url, authority.AuthorityUrl);
    }

    [Theory]
    [InlineData("http://login.microsoftonline.com/organizations")]
    [InlineData("ftp://example.com")]
    public void ResolveFromUrl_NonHttps_Throws(string url)
    {
        Assert.Throws<ArgumentException>(
            () => AgentHostAuthorityResolver.ResolveFromUrl(CloudInstance.Public, new Uri(url)));
    }
}

public sealed class ResourceScopeNormalizerTests
{
    [Fact]
    public void DoubleSlash_IsTheDefault_MatchingPac()
    {
        var normalizer = new PacResourceScopeNormalizer();
        Assert.Equal("https://management.azure.com//.default", normalizer.ToDefaultScope("https://management.azure.com/"));
    }

    [Fact]
    public void SingleSlash_WhenDisabled()
    {
        var normalizer = new PacResourceScopeNormalizer(useDoubleSlashSeparator: false);
        Assert.Equal("https://management.azure.com/.default", normalizer.ToDefaultScope("https://management.azure.com/"));
    }

    [Fact]
    public void Normalize_StripsPathToAuthority()
    {
        var normalizer = new PacResourceScopeNormalizer();
        Assert.Equal("https://api.example.com//.default", normalizer.ToDefaultScope("https://api.example.com/some/path"));
    }

    [Fact]
    public void DefaultResourceScope_IsThePowerPlatformApi()
    {
        var normalizer = new PacResourceScopeNormalizer();
        Assert.Equal("https://api.powerplatform.com//.default", normalizer.DefaultResourceScope(PowerPlatformApiRing.Production));
    }

    [Theory]
    [InlineData(PowerPlatformApiRing.Production, "https://api.powerplatform.com//.default")]
    [InlineData(PowerPlatformApiRing.Preprod, "https://api.preprod.powerplatform.com//.default")]
    [InlineData(PowerPlatformApiRing.Test, "https://api.test.powerplatform.com//.default")]
    public void DefaultResourceScope_FollowsTheRing(PowerPlatformApiRing ring, string expected)
    {
        Assert.Equal(expected, new PacResourceScopeNormalizer().DefaultResourceScope(ring));
    }

    [Fact]
    public void DefaultResourceScope_NeverSendsANonProductionRingAtProduction()
    {
        var normalizer = new PacResourceScopeNormalizer();

        foreach (var ring in new[] { PowerPlatformApiRing.Preprod, PowerPlatformApiRing.Test })
        {
            Assert.NotEqual(normalizer.DefaultResourceScope(PowerPlatformApiRing.Production), normalizer.DefaultResourceScope(ring));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("management.azure.com")]
    public void Normalize_NonAbsolute_Throws(string resource)
    {
        var normalizer = new PacResourceScopeNormalizer();
        Assert.Throws<ArgumentException>(() => normalizer.ToDefaultScope(resource));
    }
}
