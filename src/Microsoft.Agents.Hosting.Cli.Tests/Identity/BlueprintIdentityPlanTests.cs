using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests.Identity;

public class BlueprintIdentityPlanTests
{
    private const string SubscriptionId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void Build_AlwaysIncludesBotAndPowerPlatform_OptionalPermissionsFollowFlags()
    {
        var baseline = BlueprintIdentityPlan.Build(Request());
        Assert.Equal(
            ["MessagingBot", "PowerPlatform"],
            baseline.Permissions.Select(permission => permission.Name));

        var selected = BlueprintIdentityPlan.Build(Request() with
        {
            Observability = true,
            McpDiscovery = true,
        });
        Assert.Equal(
            ["MessagingBot", "PowerPlatform", "Observability", "McpDiscovery"],
            selected.Permissions.Select(permission => permission.Name));
    }

    [Fact]
    public void Build_OAuthAndTeamsSsoAreIndependent()
    {
        var oauth = BlueprintIdentityPlan.Build(Request() with { OAuth = true });
        Assert.Equal(BlueprintIdentityPlan.DefaultRedirectUri, oauth.RedirectUri);
        Assert.False(oauth.TeamsSso);

        var sso = BlueprintIdentityPlan.Build(Request() with { TeamsSso = true });
        Assert.Null(sso.RedirectUri);
        Assert.True(sso.TeamsSso);
    }

    [Fact]
    public void Build_FederatedCredentialsCarriesManagedIdentityCoordinates()
    {
        var plan = BlueprintIdentityPlan.Build(Request() with
        {
            AuthType = IdentityCredentialType.FederatedCredentials,
            SubscriptionId = SubscriptionId,
            ResourceGroup = "rg",
            Location = "westus",
            ManagedIdentityName = "agent-mi",
        });

        Assert.NotNull(plan.ManagedIdentity);
        Assert.Equal("agent-mi", plan.ManagedIdentity!.Name);
    }

    [Fact]
    public void Build_RejectsInvalidTenantAndRedirectUri()
    {
        var tenant = Assert.Throws<UsageException>(() =>
            BlueprintIdentityPlan.Build(Request() with { TenantId = "not-a-guid" }));
        Assert.Contains("--tenant must be a GUID", tenant.Message);

        var redirect = Assert.Throws<UsageException>(() =>
            BlueprintIdentityPlan.Build(Request() with
            {
                OAuth = true,
                RedirectUri = "http://contoso.example/callback",
            }));
        Assert.Contains("absolute HTTPS URI", redirect.Message);
    }

    [Fact]
    public void Build_AcceptsMaximumLengthDisplayName()
    {
        var plan = BlueprintIdentityPlan.Build(Request() with
        {
            DisplayName = new string('a', 256),
        });

        Assert.Equal(256, plan.Request.DisplayName.Length);
    }

    [Fact]
    public void Build_RejectsDisplayNameAboveMaximumLength()
    {
        var error = Assert.Throws<UsageException>(() =>
            BlueprintIdentityPlan.Build(Request() with
            {
                DisplayName = new string('a', 257),
            }));

        Assert.Contains("--display-name must not exceed 256 characters", error.Message);
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("carriage\rreturn")]
    [InlineData("null\0character")]
    public void Build_RejectsDisplayNameControlCharacters(string displayName)
    {
        var error = Assert.Throws<UsageException>(() =>
            BlueprintIdentityPlan.Build(Request() with { DisplayName = displayName }));

        Assert.Contains("--display-name cannot contain control characters", error.Message);
    }

    [Fact]
    public void Build_RejectsCredentialOptionsThatDoNotApplyToSelectedAuthType()
    {
        var clientSecret = Assert.Throws<UsageException>(() =>
            BlueprintIdentityPlan.Build(Request() with { SubscriptionId = "sub" }));
        Assert.Contains("only valid with FederatedCredentials", clientSecret.Message);

        var federated = Assert.Throws<UsageException>(() =>
            BlueprintIdentityPlan.Build(Request() with
            {
                AuthType = IdentityCredentialType.FederatedCredentials,
                SubscriptionId = SubscriptionId,
            }));
        Assert.Contains("requires --subscription", federated.Message);
    }

    [Fact]
    public void Build_RejectsInvalidManagedIdentityCoordinates()
    {
        var subscription = Assert.Throws<UsageException>(() =>
            BlueprintIdentityPlan.Build(FederatedRequest() with { SubscriptionId = "not-a-guid" }));
        Assert.Contains("--subscription must be a non-empty GUID", subscription.Message);

        var resourceGroup = Assert.Throws<UsageException>(() =>
            BlueprintIdentityPlan.Build(FederatedRequest() with { ResourceGroup = "invalid/group" }));
        Assert.Contains("--resource-group", resourceGroup.Message);

        var location = Assert.Throws<UsageException>(() =>
            BlueprintIdentityPlan.Build(FederatedRequest() with { Location = "West US" }));
        Assert.Contains("--location", location.Message);
    }

    [Fact]
    public void Build_RejectsManagedIdentityNameThatCannotBeUsedAsFederatedCredentialName()
    {
        var error = Assert.Throws<UsageException>(() =>
            BlueprintIdentityPlan.Build(Request() with
            {
                AuthType = IdentityCredentialType.FederatedCredentials,
                SubscriptionId = SubscriptionId,
                ResourceGroup = "rg",
                Location = "westus",
                ManagedIdentityName = "invalid name",
            }));

        Assert.Contains("--managed-identity-name", error.Message);
    }

    private static BlueprintIdentityRequest FederatedRequest() => Request() with
    {
        AuthType = IdentityCredentialType.FederatedCredentials,
        SubscriptionId = SubscriptionId,
        ResourceGroup = "rg",
        Location = "westus",
        ManagedIdentityName = "agent-mi",
    };

    private static BlueprintIdentityRequest Request() => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111",
        DisplayName = "Contoso Agent",
        AuthType = IdentityCredentialType.ClientSecret,
        ConfigFormat = IdentityConfigFormat.DotNet,
    };
}
