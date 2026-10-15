using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Identity;

namespace Microsoft.Agents.Hosting.Cli.Tests.Identity;

public class AppRegistrationApplicationReconcilerTests
{
    public static TheoryData<string> MalformedApplicationResponses => new()
    {
        """{"id":"object-id","appId":"app-id","signInAudience":"AzureADMultipleOrgs","passwordCredentials":{}}""",
        """{"id":"object-id","appId":"app-id","signInAudience":"AzureADMultipleOrgs","api":"unexpected"}""",
        """{"id":"object-id","appId":"app-id","signInAudience":"AzureADMultipleOrgs","api":{"oauth2PermissionScopes":{}}}""",
        """{"id":"object-id","appId":"app-id","signInAudience":"AzureADMultipleOrgs","requiredResourceAccess":{}}""",
        """{"id":"object-id","appId":"app-id","signInAudience":"AzureADMultipleOrgs","identifierUris":{}}""",
        """{"id":"object-id","appId":"app-id","signInAudience":"AzureADMultipleOrgs","identifierUris":[42]}""",
        """{"id":"object-id","appId":"app-id","signInAudience":"AzureADMultipleOrgs","web":[]}""",
        """{"id":"object-id","appId":"app-id","signInAudience":"AzureADMultipleOrgs","web":{"redirectUris":false}}""",
    };

    [Fact]
    public async Task EnsureServicePrincipalAsync_DoesNotRetryMatchingMessageFromDifferentErrorCode()
    {
        var rest = new ScriptedIdentityRestClient(call =>
        {
            if (call.Method == "GET" &&
                call.Path.StartsWith("/v1.0/servicePrincipals?", StringComparison.Ordinal))
            {
                return new IdentityRestResponse(200, """{"value":[]}""");
            }

            if (call.Method == "POST" && call.Path == "/v1.0/servicePrincipals")
            {
                return new IdentityRestResponse(
                    400,
                    """
                    {
                      "error": {
                        "code": "Authorization_RequestDenied",
                        "message": "Invalid value specified for property 'appId'"
                      }
                    }
                    """);
            }

            throw new Xunit.Sdk.XunitException(
                $"Unexpected REST call: {call.Method} {call.Path}");
        });
        var reconciler = new AppRegistrationApplicationReconciler(
            rest,
            new IdentityGraphClient(rest),
            TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<TargetRequestException>(() =>
            reconciler.EnsureServicePrincipalAsync("tenant", "app-id", default));

        Assert.Equal(TargetErrorKind.RequestFailed, error.Kind);
        Assert.Equal(
            1,
            rest.Calls.Count(call =>
                call.Method == "POST" && call.Path == "/v1.0/servicePrincipals"));
    }

    [Theory]
    [MemberData(nameof(MalformedApplicationResponses))]
    public void ApplicationFrom_MalformedGraphShape_ThrowsTypedFailure(string json)
    {
        var response = JsonNode.Parse(json)!.AsObject();

        var error = Assert.Throws<TargetRequestException>(() =>
            AppRegistrationApplication.From(response));

        Assert.Equal(TargetErrorKind.RequestFailed, error.Kind);
        Assert.Contains("unexpected shape", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
