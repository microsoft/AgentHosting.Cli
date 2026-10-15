using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests.Identity;

public class AppRegistrationIdentityPlanTests
{
    private const string SubscriptionId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void Build_AlwaysIncludesBotAndPowerPlatform_OptionalPermissionsFollowFlags()
    {
        var baseline = AppRegistrationIdentityPlan.Build(Request());
        Assert.Equal(
            ["MessagingBot", "PowerPlatform"],
            baseline.Permissions.Select(permission => permission.Name));

        var selected = AppRegistrationIdentityPlan.Build(Request() with
        {
            Observability = true,
            McpDiscovery = true,
        });
        Assert.Equal(
            ["MessagingBot", "PowerPlatform", "Observability", "McpDiscovery"],
            selected.Permissions.Select(permission => permission.Name));
    }

    [Fact]
    public void Build_TeamsSsoIncludesOAuthRedirect()
    {
        var oauth = AppRegistrationIdentityPlan.Build(Request() with { OAuth = true });
        Assert.Equal(AppRegistrationIdentityPlan.DefaultRedirectUri, oauth.RedirectUri);
        Assert.False(oauth.TeamsSso);
        Assert.Empty(oauth.PreauthorizedClientIds);

        var sso = AppRegistrationIdentityPlan.Build(Request() with { TeamsSso = true });
        Assert.Equal(AppRegistrationIdentityPlan.DefaultRedirectUri, sso.RedirectUri);
        Assert.True(sso.TeamsSso);
        Assert.Equal(
            IdentityProvisioningConfig.TeamsHostApplicationIds,
            sso.PreauthorizedClientIds);
    }

    [Fact]
    public void Build_TeamsSsoAcceptsCustomRedirect()
    {
        var plan = AppRegistrationIdentityPlan.Build(Request() with
        {
            TeamsSso = true,
            RedirectUri = "https://contoso.example/callback",
        });

        Assert.Equal("https://contoso.example/callback", plan.RedirectUri);
    }

    [Fact]
    public void Build_NoOAuthOrSso_LeavesRedirectNull()
    {
        var plan = AppRegistrationIdentityPlan.Build(Request());
        Assert.Null(plan.RedirectUri);
        Assert.False(plan.TeamsSso);
    }

    [Fact]
    public void Build_UsesMultitenantSignInAudience()
    {
        Assert.Equal("AzureADMultipleOrgs", AppRegistrationIdentityPlan.SignInAudience);
    }

    [Fact]
    public void Build_UserManagedIdentityCreatesManagedIdentityAndCompanionApp()
    {
        var plan = AppRegistrationIdentityPlan.Build(ManagedIdentityRequest(
            IdentityCredentialType.UserManagedIdentity));

        Assert.NotNull(plan.ManagedIdentity);
        Assert.Equal("agent-mi", plan.ManagedIdentity!.Name);
        Assert.True(plan.UsesCompanionApp);
    }

    [Fact]
    public void Build_FederatedCredentialsCreatesManagedIdentityWithoutCompanionFlag()
    {
        var plan = AppRegistrationIdentityPlan.Build(ManagedIdentityRequest(
            IdentityCredentialType.FederatedCredentials));

        Assert.NotNull(plan.ManagedIdentity);
        Assert.False(plan.UsesCompanionApp);
    }

    [Fact]
    public void Build_ClientSecretHasNoManagedIdentity()
    {
        var plan = AppRegistrationIdentityPlan.Build(Request());
        Assert.Null(plan.ManagedIdentity);
        Assert.False(plan.UsesCompanionApp);
    }

    [Fact]
    public void Build_RejectsConflictingApplicationTargets()
    {
        var error = Assert.Throws<UsageException>(() =>
            AppRegistrationIdentityPlan.Build(Request() with
            {
                ApplicationId = "33333333-3333-3333-3333-333333333333",
                ApplicationObjectId = "44444444-4444-4444-4444-444444444444",
            }));

        Assert.Contains("--app-id and --object-id cannot be used together", error.Message);
    }

    [Fact]
    public void Build_RejectsInvalidTenantAndRedirectUri()
    {
        var tenant = Assert.Throws<UsageException>(() =>
            AppRegistrationIdentityPlan.Build(Request() with { TenantId = "not-a-guid" }));
        Assert.Contains("--tenant must be a GUID", tenant.Message);

        var redirect = Assert.Throws<UsageException>(() =>
            AppRegistrationIdentityPlan.Build(Request() with
            {
                OAuth = true,
                RedirectUri = "http://contoso.example/callback",
            }));
        Assert.Contains("absolute HTTPS URI", redirect.Message);
    }

    [Fact]
    public void Build_RejectsRedirectWithoutOAuthOrSso()
    {
        var error = Assert.Throws<UsageException>(() =>
            AppRegistrationIdentityPlan.Build(Request() with
            {
                RedirectUri = "https://contoso.example/callback",
            }));
        Assert.Contains("--redirect-uri requires --oauth or --teams-sso", error.Message);
    }

    [Theory]
    [InlineData(IdentityCredentialType.UserManagedIdentity)]
    [InlineData(IdentityCredentialType.FederatedCredentials)]
    public void Build_ManagedIdentityAuthTypesRequireCoordinates(IdentityCredentialType authType)
    {
        var error = Assert.Throws<UsageException>(() =>
            AppRegistrationIdentityPlan.Build(Request() with { AuthType = authType }));
        Assert.Contains("requires --subscription", error.Message);
    }

    [Fact]
    public void Build_ClientSecretRejectsManagedIdentityCoordinates()
    {
        var error = Assert.Throws<UsageException>(() =>
            AppRegistrationIdentityPlan.Build(Request() with { SubscriptionId = SubscriptionId }));
        Assert.Contains("not valid with ClientSecret", error.Message);
    }

    [Fact]
    public void Build_RejectsInvalidManagedIdentityCoordinates()
    {
        var subscription = Assert.Throws<UsageException>(() =>
            AppRegistrationIdentityPlan.Build(ManagedIdentityRequest(
                IdentityCredentialType.FederatedCredentials) with { SubscriptionId = "not-a-guid" }));
        Assert.Contains("--subscription must be a non-empty GUID", subscription.Message);

        var resourceGroup = Assert.Throws<UsageException>(() =>
            AppRegistrationIdentityPlan.Build(ManagedIdentityRequest(
                IdentityCredentialType.UserManagedIdentity) with { ResourceGroup = "invalid/group" }));
        Assert.Contains("--resource-group", resourceGroup.Message);

        var location = Assert.Throws<UsageException>(() =>
            AppRegistrationIdentityPlan.Build(ManagedIdentityRequest(
                IdentityCredentialType.UserManagedIdentity) with { Location = "West US" }));
        Assert.Contains("--location", location.Message);
    }

    [Fact]
    public void Build_RejectsMalformedApplicationTargets()
    {
        var appId = Assert.Throws<UsageException>(() =>
            AppRegistrationIdentityPlan.Build(Request() with { ApplicationId = "not-a-guid" }));
        Assert.Contains("--app-id must be a GUID", appId.Message);

        var objectId = Assert.Throws<UsageException>(() =>
            AppRegistrationIdentityPlan.Build(Request() with { ApplicationObjectId = "not-a-guid" }));
        Assert.Contains("--object-id must be a GUID", objectId.Message);
    }

    [Fact]
    public void Build_AlwaysAttachesDelegatedConsent_WithDefaultClient()
    {
        var plan = AppRegistrationIdentityPlan.Build(Request());

        Assert.NotNull(plan.DelegatedConsent);
        Assert.Equal(GraphDelegatedConsent.DefaultGraphClientId, plan.DelegatedConsent.GraphClientId);
        Assert.Contains(
            $"client_id={GraphDelegatedConsent.DefaultGraphClientId}",
            plan.DelegatedConsent.AdminConsentUrl);
    }

    [Fact]
    public void Build_UsesGraphClientOverride_WhenProvided()
    {
        const string overrideClientId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var plan = AppRegistrationIdentityPlan.Build(
            Request(),
            GraphConsentClientConfiguration.Resolve(
                overrideClientId,
                "http://localhost/agenthost-consent"));

        Assert.Equal(overrideClientId, plan.DelegatedConsent.GraphClientId);
        Assert.Contains($"client_id={overrideClientId}", plan.DelegatedConsent.AdminConsentUrl);
    }

    [Fact]
    public void Build_RejectsFirstPartyGraphClientOverride()
    {
        var error = Assert.Throws<UsageException>(() =>
            GraphConsentClientConfiguration.Resolve(
                "21fadfba-178a-47f0-8ce6-07c05eb346ca",
                "http://localhost/agenthost-consent"));
        Assert.Contains("consent-capable client", error.Message);
    }

    private static AppRegistrationIdentityRequest ManagedIdentityRequest(IdentityCredentialType authType) =>
        Request() with
        {
            AuthType = authType,
            SubscriptionId = SubscriptionId,
            ResourceGroup = "rg",
            Location = "westus",
            ManagedIdentityName = "agent-mi",
        };

    private static AppRegistrationIdentityRequest Request() => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111",
        DisplayName = "Contoso Agent",
        AuthType = IdentityCredentialType.ClientSecret,
        ConfigFormat = IdentityConfigFormat.DotNet,
    };
}
