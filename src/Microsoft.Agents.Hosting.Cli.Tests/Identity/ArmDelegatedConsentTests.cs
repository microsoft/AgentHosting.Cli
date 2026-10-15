using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Identity;

namespace Microsoft.Agents.Hosting.Cli.Tests.Identity;

public class ArmDelegatedConsentTests
{
    private const string OverrideClientId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string OverrideRedirectUri = "http://localhost/agenthost-arm-consent";
    private const string FirstPartyCliAppId = "21fadfba-178a-47f0-8ce6-07c05eb346ca";

    [Fact]
    public void DefaultArmClientId_IsTheAzureCliPublicClient()
    {
        Assert.Equal("04b07795-8ddb-461a-bbee-02f9e1bf7b46", ArmDelegatedConsent.DefaultArmClientId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveClientId_DefaultsToAzureCliClient_WhenNoOverride(string? clientId)
    {
        Assert.Equal(ArmDelegatedConsent.DefaultArmClientId, ArmDelegatedConsent.ResolveClientId(clientId));
    }

    [Fact]
    public void ResolveClientId_HonorsValidOverride()
    {
        Assert.Equal(OverrideClientId, ArmDelegatedConsent.ResolveClientId(OverrideClientId));
    }

    [Fact]
    public void Configuration_CustomClientRequiresAndUsesExplicitRedirectUri()
    {
        var missingRedirect = Assert.Throws<UsageException>(() =>
            ArmConsentClientConfiguration.Resolve(OverrideClientId, redirectUriOverride: null));
        Assert.Contains("AH_CLI_ARM_REDIRECT_URI", missingRedirect.Message);

        var configuration = ArmConsentClientConfiguration.Resolve(
            OverrideClientId,
            OverrideRedirectUri);

        Assert.Equal(OverrideClientId, configuration.ClientId);
        Assert.Equal(OverrideRedirectUri, configuration.RedirectUri);
    }

    [Fact]
    public void Configuration_RejectsHttpsRedirectThatCannotReceiveSystemBrowserCallback()
    {
        var error = Assert.Throws<UsageException>(() =>
            ArmConsentClientConfiguration.Resolve(
                OverrideClientId,
                "https://contoso.example/agenthost-consent"));

        Assert.Contains("HTTP loopback", error.Message);
    }

    [Fact]
    public void Configuration_CanOnlyBeCreatedThroughValidatedFactories()
    {
        Assert.Empty(typeof(ArmConsentClientConfiguration).GetConstructors());
        Assert.False(typeof(ArmConsentClientConfiguration).GetProperty("ClientId")!.CanWrite);
        Assert.False(typeof(ArmConsentClientConfiguration).GetProperty("RedirectUri")!.CanWrite);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void ValidateClientId_RejectsEmptyOrNonGuidOverride(string clientId)
    {
        var error = Assert.Throws<UsageException>(() => ArmDelegatedConsent.ValidateClientId(clientId));
        Assert.Contains("must be a non-empty GUID", error.Message);
    }

    [Fact]
    public void ValidateClientId_RejectsFirstPartyCliAppId()
    {
        var error = Assert.Throws<UsageException>(() => ArmDelegatedConsent.ValidateClientId(FirstPartyCliAppId));
        Assert.Contains("consent-capable client", error.Message);
        Assert.Contains("never consented", error.Message);
    }
}
