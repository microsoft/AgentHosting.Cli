using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests.Identity;

public class IdentityConfigurationRendererTests
{
    [Fact]
    public void NodeConfiguration_IncludesAuthorityAndBotFrameworkScope()
    {
        var configuration = IdentityConfigurationRenderer.Render(
            new AppRegistrationIdentityRequest
            {
                TenantId = "11111111-1111-1111-1111-111111111111",
                DisplayName = "Contoso Agent",
                AuthType = IdentityCredentialType.ClientSecret,
                ConfigFormat = IdentityConfigFormat.Node,
            },
            oauthApplicationId: "app-client-id",
            federatedClientId: null,
            managedIdentityClientId: null);

        Assert.Contains(
            "connections__serviceConnection__settings__authorityEndpoint=" +
            "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111",
            configuration.Content);
        Assert.Contains(
            "connections__serviceConnection__settings__scopes__0=" +
            "https://api.botframework.com/.default",
            configuration.Content);
    }
}
