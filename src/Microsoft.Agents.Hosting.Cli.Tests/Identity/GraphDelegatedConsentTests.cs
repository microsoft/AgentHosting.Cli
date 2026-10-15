using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Identity;

namespace Microsoft.Agents.Hosting.Cli.Tests.Identity;

public class GraphDelegatedConsentTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string OverrideClientId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string OverrideRedirectUri = "http://localhost/agenthost-consent";
    private const string FirstPartyCliAppId = "21fadfba-178a-47f0-8ce6-07c05eb346ca";

    [Fact]
    public void DefaultGraphClientId_IsTheMicrosoftGraphCommandLineToolsClient()
    {
        // Microsoft Graph Command Line Tools (Connect-MgGraph) — Microsoft-preauthorized for Graph
        // delegated scopes, unlike A365's Azure PowerShell client (1950a258…) which yields AADSTS65002.
        Assert.Equal("14d82eec-204b-4c2f-b7e8-296a70dab67e", GraphDelegatedConsent.DefaultGraphClientId);
    }

    [Fact]
    public void GraphScopes_IncludeAppManagementAndRoleAssignmentPermissions()
    {
        Assert.Equal(
            [
                IdentityProvisioningConfig.GraphApplicationReadWriteScope,
                IdentityProvisioningConfig.GraphApplicationReadScope,
                IdentityProvisioningConfig.GraphAppRoleAssignmentReadWriteScope,
                IdentityProvisioningConfig.GraphUserReadScope,
            ],
            GraphDelegatedConsent.GraphScopes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveClientId_DefaultsToGraphCliClient_WhenNoOverride(string? clientId)
    {
        Assert.Equal(GraphDelegatedConsent.DefaultGraphClientId, GraphDelegatedConsent.ResolveClientId(clientId));
    }

    [Fact]
    public void ResolveClientId_HonorsValidOverride()
    {
        Assert.Equal(OverrideClientId, GraphDelegatedConsent.ResolveClientId(OverrideClientId));
    }

    [Fact]
    public void BuildAdminConsentUrl_TargetsAdminConsentEndpointWithClientAndEncodedScopes()
    {
        var url = GraphDelegatedConsent.BuildAdminConsentUrl(TenantId);

        Assert.StartsWith(
            $"https://login.microsoftonline.com/{TenantId}/v2.0/adminconsent",
            url);
        Assert.Contains($"client_id={GraphDelegatedConsent.DefaultGraphClientId}", url);
        Assert.Contains(
            Uri.EscapeDataString(IdentityProvisioningConfig.GraphApplicationReadWriteScope),
            url);
        Assert.Contains(
            $"redirect_uri={Uri.EscapeDataString(GraphDelegatedConsent.DefaultRedirectUri)}",
            url);
        Assert.Contains("&state=", url);
    }

    [Fact]
    public void Configuration_CustomClientRequiresAndUsesExplicitRedirectUri()
    {
        var missingRedirect = Assert.Throws<UsageException>(() =>
            GraphConsentClientConfiguration.Resolve(OverrideClientId, redirectUriOverride: null));
        Assert.Contains("AH_CLI_GRAPH_REDIRECT_URI", missingRedirect.Message);

        var configuration = GraphConsentClientConfiguration.Resolve(
            OverrideClientId,
            OverrideRedirectUri);
        var plan = GraphDelegatedConsent.Create(TenantId, configuration);

        Assert.Equal(OverrideRedirectUri, configuration.RedirectUri);
        Assert.Contains(
            $"redirect_uri={Uri.EscapeDataString(OverrideRedirectUri)}",
            plan.AdminConsentUrl);
    }

    [Theory]
    [InlineData("https://contoso.example/agenthost-consent")]
    [InlineData("http://contoso.example/agenthost-consent")]
    public void Configuration_RejectsRedirectThatCannotReceiveSystemBrowserCallback(string redirectUri)
    {
        var error = Assert.Throws<UsageException>(() =>
            GraphConsentClientConfiguration.Resolve(OverrideClientId, redirectUri));

        Assert.Contains("HTTP loopback", error.Message);
    }

    [Fact]
    public void Configuration_CanOnlyBeCreatedThroughValidatedFactories()
    {
        Assert.Empty(typeof(GraphConsentClientConfiguration).GetConstructors());
        Assert.False(typeof(GraphConsentClientConfiguration).GetProperty("ClientId")!.CanWrite);
        Assert.False(typeof(GraphConsentClientConfiguration).GetProperty("RedirectUri")!.CanWrite);
    }

    [Fact]
    public void Create_DryRunOmitsRandomState()
    {
        var first = GraphDelegatedConsent.Create(
            TenantId,
            GraphConsentClientConfiguration.Default,
            includeState: false);
        var second = GraphDelegatedConsent.Create(
            TenantId,
            GraphConsentClientConfiguration.Default,
            includeState: false);

        Assert.Equal(first.AdminConsentUrl, second.AdminConsentUrl);
        Assert.DoesNotContain("state=", first.AdminConsentUrl);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void ValidateClientId_RejectsEmptyOrNonGuidOverride(string clientId)
    {
        var error = Assert.Throws<UsageException>(() => GraphDelegatedConsent.ValidateClientId(clientId));
        Assert.Contains("must be a non-empty GUID", error.Message);
    }

    [Fact]
    public void ValidateClientId_RejectsFirstPartyCliAppId()
    {
        var error = Assert.Throws<UsageException>(() => GraphDelegatedConsent.ValidateClientId(FirstPartyCliAppId));
        Assert.Contains("consent-capable client", error.Message);
        Assert.Contains("never consented", error.Message);
    }

    [Fact]
    public void Create_WithoutOverride_UsesDefaultClient()
    {
        var plan = GraphDelegatedConsent.Create(
            TenantId,
            GraphConsentClientConfiguration.Default);

        Assert.Equal(GraphDelegatedConsent.DefaultGraphClientId, plan.GraphClientId);
        Assert.Contains($"client_id={GraphDelegatedConsent.DefaultGraphClientId}", plan.AdminConsentUrl);
    }

    [Fact]
    public void Create_WithOverride_UsesOverrideClient()
    {
        var plan = GraphDelegatedConsent.Create(
            TenantId,
            GraphConsentClientConfiguration.Resolve(OverrideClientId, OverrideRedirectUri));

        Assert.Equal(OverrideClientId, plan.GraphClientId);
        Assert.Contains($"client_id={OverrideClientId}", plan.AdminConsentUrl);
    }
}
