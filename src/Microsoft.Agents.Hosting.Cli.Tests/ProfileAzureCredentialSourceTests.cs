using Azure.Identity;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ProfileAzureCredentialSourceTests
{
    private static readonly AuthorityInfo Authority = AgentHostAuthorityResolver.Resolve(CloudInstance.China, Guid.Parse(ProfileCredentialTests.Tenant));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CandidateOrderRetainsSecretPrecedenceAndTieredPolicy(bool interactive)
    {
        var factory = new ProfileAzureCredentialSourceFactory(new StaticInteractionPolicy(interactive));
        var sources = factory.GetInitialSources(new()
        {
            AzureTenantId = ProfileCredentialTests.Tenant, AzureClientId = ProfileCredentialTests.App,
            AzureClientSecret = "secret", AzureCertificatePath = "source.pfx",
            AzureFederatedTokenFile = "assertion.jwt",
        }, Authority);
        var expected = new List<ProfileAzureSourceId>
        {
            ProfileAzureSourceId.EnvironmentClientSecret, ProfileAzureSourceId.WorkloadIdentity, ProfileAzureSourceId.ManagedIdentity,
        };
        if (interactive) expected.AddRange([ProfileAzureSourceId.AzureCli, ProfileAzureSourceId.AzurePowerShell, ProfileAzureSourceId.AzureDeveloperCli]);
        Assert.Equal(expected, sources);
    }

    [Theory]
    [InlineData("InteractiveBrowserCredential")]
    [InlineData("BrokerCredential")]
    [InlineData("VisualStudioCredential")]
    [InlineData("VisualStudioCodeCredential")]
    [InlineData("SharedTokenCacheCredential")]
    [InlineData("AzurePipelinesCredential")]
    [InlineData("unknown")]
    public void UnsupportedAmbientSelectionCannotBypassAllowlist(string selection)
    {
        var factory = new ProfileAzureCredentialSourceFactory(new StaticInteractionPolicy(true));
        Assert.Throws<UsageException>(() => factory.GetInitialSources(new() { AzureCredentialSelection = selection }, Authority));
    }

    [Theory]
    [InlineData("AzureCliCredential")]
    [InlineData("AzurePowerShellCredential")]
    [InlineData("AzureDeveloperCliCredential")]
    [InlineData("dev")]
    public void ExplicitDeveloperSelectionIsInteractionRequiredWhenForbidden(string selection)
    {
        var factory = new ProfileAzureCredentialSourceFactory(new StaticInteractionPolicy(false));
        Assert.Throws<InteractionRequiredException>(() => factory.GetInitialSources(new() { AzureCredentialSelection = selection }, Authority));
    }

    [Theory]
    [InlineData(ProfileAzureSourceId.AzureCli)]
    [InlineData(ProfileAzureSourceId.AzurePowerShell)]
    [InlineData(ProfileAzureSourceId.AzureDeveloperCli)]
    public void PinnedDeveloperSourceIsRejectedBeforeSdkConstruction(ProfileAzureSourceId source)
    {
        var factory = new ProfileAzureCredentialSourceFactory(new StaticInteractionPolicy(false));
        Assert.Throws<InteractionRequiredException>(() => factory.Create(source, new(), Authority));
    }

    [Fact]
    public void ExplicitSingletonIgnoresDiscoveryExclusionsAndUsesPublicAppCredentialConstructor()
    {
        var factory = new ProfileAzureCredentialSourceFactory(new StaticInteractionPolicy(false));
        var settings = new ProfileCredentialRuntimeOptions
        {
            AzureCredentialSelection = "EnvironmentCredential", AzureTenantId = ProfileCredentialTests.Tenant,
            AzureClientId = ProfileCredentialTests.App, AzureClientSecret = "provided-not-ambient",
            ExcludeEnvironmentCredential = true,
        };
        Assert.Equal([ProfileAzureSourceId.EnvironmentClientSecret], factory.GetInitialSources(settings, Authority));
        Assert.IsType<ClientSecretCredential>(factory.Create(ProfileAzureSourceId.EnvironmentClientSecret, settings, Authority));
    }
}
