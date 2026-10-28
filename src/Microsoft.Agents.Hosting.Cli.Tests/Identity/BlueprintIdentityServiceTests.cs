using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests.Identity;

public class BlueprintIdentityServiceTests
{
    private const string OverrideArmClientId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    [Fact]
    public async Task DryRun_ReturnsSelectedPlan_AndNeverMutates()
    {
        var rest = new ScriptedIdentityRestClient();
        var service = new BlueprintIdentityService(rest);

        var report = await service.ExecuteAsync(Request() with
        {
            DryRun = true,
            Observability = true,
            McpDiscovery = true,
            OAuth = true,
            TeamsSso = true,
        }, default);

        Assert.Equal("planned", report.Status);
        Assert.Contains(report.Changes, change => change.Resource == "MessagingBot");
        Assert.Contains(report.Changes, change => change.Resource == "PowerPlatform");
        Assert.Contains(report.Changes, change => change.Resource == "Observability");
        Assert.Contains(
            report.Changes,
            change => change.Resource == "ObservabilityAppRole" &&
                change.Detail.Contains("inheritable", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            report.Changes,
            change => change.Resource == "ObservabilityAppRoleAssignment" &&
                change.Detail.Contains("grant", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Changes, change => change.Resource == "McpDiscovery");
        Assert.Contains(
            report.Changes,
            change => change.Resource == "DelegatedPermissions" && change.Action == "consent");
        Assert.Null(report.AdminConsentUrl);
        Assert.DoesNotContain(
            rest.Calls,
            call => call.Method is "POST" or "PUT" or "PATCH" or "DELETE");
    }

    [Fact]
    public async Task DryRun_FederatedCredentialsReportsManagedIdentityAndArmClientWithoutRestCalls()
    {
        var rest = new ScriptedIdentityRestClient();
        var service = new BlueprintIdentityService(rest);

        var report = await service.ExecuteAsync(Request() with
        {
            DryRun = true,
            AuthType = IdentityCredentialType.FederatedCredentials,
            ConfigFormat = IdentityConfigFormat.Node,
            SubscriptionId = "22222222-2222-2222-2222-222222222222",
            ResourceGroup = "rg",
            Location = "westus",
            ManagedIdentityName = "agent-mi",
            TeamsSso = true,
        }, default);

        Assert.Equal("planned", report.Status);
        var managedIdentity = Assert.Single(
            report.Changes,
            change => change.Resource == "ManagedIdentity" && change.Action == "ensure");
        Assert.Contains("managed identity agent-mi", managedIdentity.Detail, StringComparison.Ordinal);
        Assert.Contains("resource group rg", managedIdentity.Detail, StringComparison.Ordinal);
        Assert.Contains(
            "subscription 22222222-2222-2222-2222-222222222222",
            managedIdentity.Detail,
            StringComparison.Ordinal);
        Assert.Contains("location westus", managedIdentity.Detail, StringComparison.Ordinal);
        Assert.Contains(
            report.Changes,
            change =>
                change.Resource == "ArmClient" &&
                change.Action == "select" &&
                change.Detail.Contains(ArmDelegatedConsent.DefaultArmClientId, StringComparison.Ordinal));
        Assert.Contains(
            report.Changes,
            change => change.Resource == "FederatedCredentials" && change.Action == "ensure");
        Assert.Contains(report.Changes, change => change.Resource == "TeamsSso");
        Assert.DoesNotContain(report.Changes, change => change.Resource == "OAuth");
        Assert.Empty(rest.Calls);
    }

    [Fact]
    public async Task DryRun_FederatedCredentialsReportsConfiguredArmClient()
    {
        var rest = new ScriptedIdentityRestClient();
        var service = new BlueprintIdentityService(
            rest,
            armConsentClient: ArmConsentClientConfiguration.Resolve(
                OverrideArmClientId,
                "http://localhost/agenthost-arm-consent"));

        var report = await service.ExecuteAsync(Request() with
        {
            DryRun = true,
            AuthType = IdentityCredentialType.FederatedCredentials,
            SubscriptionId = "22222222-2222-2222-2222-222222222222",
            ResourceGroup = "rg",
            Location = "westus",
            ManagedIdentityName = "agent-mi",
        }, default);

        Assert.Contains(
            report.Changes,
            change => change.Resource == "ArmClient" &&
                change.Detail.Contains(OverrideArmClientId, StringComparison.Ordinal));
        Assert.Empty(rest.Calls);
    }

    [Fact]
    public async Task ClientSecret_CreatesBlueprintPrincipalPermissionsAndOneTimeConfig()
    {
        var rest = ScriptedIdentityRestClient.ForNewBlueprint();
        var service = new BlueprintIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(Request() with
        {
            OAuth = true,
            TeamsSso = true,
            Observability = true,
            McpDiscovery = true,
        }, default);

        Assert.Equal("created", report.Status);
        Assert.Equal("blueprint-app-id", report.ApplicationId);
        Assert.Equal("blueprint-object-id", report.ApplicationObjectId);
        Assert.Equal("blueprint-sp-id", report.ServicePrincipalObjectId);
        Assert.NotNull(report.Configuration);
        Assert.Equal("one-time-secret", report.OneTimeClientSecret);
        Assert.DoesNotContain("one-time-secret", report.Configuration!.Content, StringComparison.Ordinal);
        Assert.Contains(
            report.NextActions,
            action => action.Contains("approved secret store", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(rest.Calls, call =>
            call.Method == "GET" &&
            call.Path.StartsWith(
                "/beta/applications/microsoft.graph.agentIdentityBlueprint?",
                StringComparison.Ordinal));
        Assert.DoesNotContain(rest.Calls, call =>
            call.Method == "GET" &&
            call.Path.StartsWith("/beta/applications?", StringComparison.Ordinal));
        Assert.Contains(rest.Calls, call =>
            call.Method == "POST" &&
            call.Path == "/beta/applications/microsoft.graph.agentIdentityBlueprint");
        Assert.Contains(rest.Calls, call =>
            call.Method == "POST" &&
            call.Path == "/v1.0/serviceprincipals/graph.agentIdentityBlueprintPrincipal");
        Assert.Contains(rest.Calls, call =>
            call.Method == "POST" && call.Path.EndsWith("/addPassword", StringComparison.Ordinal));
        Assert.DoesNotContain(
            rest.Calls,
            call => call.Path.StartsWith("/v1.0/oauth2PermissionGrants", StringComparison.Ordinal));
        Assert.DoesNotContain(
            rest.Calls.SelectMany(call => call.Scopes ?? []),
            scope =>
                scope.EndsWith("/DelegatedPermissionGrant.ReadWrite.All", StringComparison.Ordinal) ||
                scope.EndsWith("/AppRoleAssignment.ReadWrite.All", StringComparison.Ordinal));
        Assert.NotNull(report.AdminConsentUrl);
        var decodedConsentUrl = Uri.UnescapeDataString(report.AdminConsentUrl);
        Assert.StartsWith(
            "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0/adminconsent?",
            decodedConsentUrl,
            StringComparison.Ordinal);
        Assert.Contains("client_id=blueprint-app-id", decodedConsentUrl);
        Assert.Contains("https://botapi.skype.com/AgentData.ReadWrite", decodedConsentUrl);
        Assert.Contains(
            "https://api.powerplatform.com/Connectivity.Connections.Read",
            decodedConsentUrl);
        Assert.Contains(
            "api://9b975845-388f-4429-889e-eab1ef63949c/Agent365.Observability.OtelWrite",
            decodedConsentUrl);
        Assert.Contains(
            "https://agent365.svc.cloud.microsoft/McpServersMetadata.Read.All",
            decodedConsentUrl);
        Assert.Contains(
            "redirect_uri=https://entra.microsoft.com/TokenAuthorize",
            decodedConsentUrl);
        Assert.Contains(
            report.NextActions,
            action => action.Contains(report.AdminConsentUrl, StringComparison.Ordinal));
        Assert.Contains(rest.Calls, call =>
            call.Method == "GET" &&
            call.Path ==
                "/beta/applications/blueprint-object-id/microsoft.graph.agentIdentityBlueprint/inheritablePermissions");
        var inheritablePermission = Assert.Single(
            rest.Calls,
            call => call.Method == "POST" &&
                call.Path ==
                    "/beta/applications/blueprint-object-id/microsoft.graph.agentIdentityBlueprint/inheritablePermissions" &&
                call.Body?.Contains(
                    IdentityProvisioningConfig.MessagingBotPermission.ResourceAppId,
                    StringComparison.Ordinal) == true);
        Assert.Contains(
            "\"@odata.type\":\"#microsoft.graph.allAllowedScopes\"",
            inheritablePermission.Body);
        Assert.Contains(
            "\"@odata.type\":\"#microsoft.graph.noRoles\"",
            inheritablePermission.Body);
        Assert.Contains("\"kind\":\"none\"", inheritablePermission.Body);
        var observabilityInheritance = Assert.Single(
            rest.Calls,
            call => call.Method == "POST" &&
                call.Path ==
                    "/beta/applications/blueprint-object-id/microsoft.graph.agentIdentityBlueprint/inheritablePermissions" &&
                call.Body?.Contains(
                    IdentityProvisioningConfig.ObservabilityPermission.ResourceAppId,
                    StringComparison.Ordinal) == true);
        Assert.Contains(
            "\"@odata.type\":\"#microsoft.graph.allAllowedRoles\"",
            observabilityInheritance.Body);

        var appPatches = rest.Calls
            .Where(call =>
                call.Method == "PATCH" &&
                call.Path == "/v1.0/applications/blueprint-object-id")
            .ToArray();
        Assert.Equal(2, appPatches.Length);
        Assert.Contains("access_as_user", appPatches[0].Body);
        Assert.Contains("\"requestedAccessTokenVersion\":2", appPatches[0].Body);
        Assert.Contains(BlueprintIdentityPlan.DefaultRedirectUri, appPatches[0].Body);
        Assert.Contains(BlueprintAdminConsent.RedirectUri, appPatches[0].Body);
        Assert.DoesNotContain("preAuthorizedApplications", appPatches[0].Body);
        Assert.Contains("preAuthorizedApplications", appPatches[1].Body);
        Assert.DoesNotContain("oauth2PermissionScopes", appPatches[1].Body);
    }

    [Fact]
    public async Task FederatedCredentials_CreatesManagedIdentityAndFederatedCredential()
    {
        var rest = ScriptedIdentityRestClient.ForNewBlueprint(call =>
            call.Method == "GET"
                ? new IdentityRestResponse(404, """{"error":{"code":"NotFound"}}""")
                : new IdentityRestResponse(201, """
                    {
                      "id":"/subscriptions/sub/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/agent-mi",
                      "properties":{
                        "clientId":"managed-client-id",
                        "principalId":"managed-principal-id"
                      }
                    }
                    """));
        var service = new BlueprintIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(Request() with
        {
            AuthType = IdentityCredentialType.FederatedCredentials,
            ConfigFormat = IdentityConfigFormat.Node,
            SubscriptionId = "22222222-2222-2222-2222-222222222222",
            ResourceGroup = "rg",
            Location = "westus",
            ManagedIdentityName = "agent-mi",
        }, default);

        Assert.Contains(rest.Calls, call =>
            call.Method == "PUT" && call.Path.Contains("userAssignedIdentities/agent-mi", StringComparison.Ordinal));
        var credential = Assert.Single(rest.Calls, call =>
            call.Method == "PATCH" &&
            call.Path ==
                "/beta/applications/blueprint-object-id/microsoft.graph.agentIdentityBlueprint/" +
                "federatedIdentityCredentials(name='agent-mi')");
        Assert.Contains("managed-principal-id", credential.Body);
        Assert.Equal("create-if-missing", credential.Headers!["Prefer"]);
        Assert.DoesNotContain(rest.Calls, call =>
            call.Method == "GET" &&
            call.Path.Contains("/federatedIdentityCredentials", StringComparison.Ordinal));
        Assert.Contains("managed-client-id", report.Configuration!.Content);
        Assert.DoesNotContain("clientSecret", report.Configuration.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BlueprintPrincipal_ForbiddenFailsWithoutRetry()
    {
        var rest = ScriptedIdentityRestClient.ForNewBlueprint();
        rest.GraphOverride = call =>
            call.Method == "POST" &&
            call.Path == "/v1.0/serviceprincipals/graph.agentIdentityBlueprintPrincipal"
                ? new IdentityRestResponse(
                    403,
                    """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges."}}""")
                : null;
        var service = new BlueprintIdentityService(rest, retryDelay: TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<BlueprintIdentityProvisioningException>(() =>
            service.ExecuteAsync(Request(), default));

        Assert.IsType<TargetRequestException>(error.InnerException);
        Assert.Equal("partial", error.PartialReport.Status);
        Assert.Equal("blueprint-app-id", error.PartialReport.ApplicationId);
        Assert.Equal("blueprint-object-id", error.PartialReport.ApplicationObjectId);
        Assert.Null(error.PartialReport.ServicePrincipalObjectId);
        Assert.Null(error.PartialReport.Configuration);
        Assert.Contains(
            error.PartialReport.Changes,
            change => change.Resource == "Blueprint" && change.Action == "create");
        Assert.Contains(
            error.PartialReport.NextActions,
            action => action.Contains("rerun", StringComparison.OrdinalIgnoreCase));
        Assert.Single(
            rest.Calls,
            call => call.Method == "POST" &&
                call.Path == "/v1.0/serviceprincipals/graph.agentIdentityBlueprintPrincipal");
    }

    [Fact]
    public async Task BlueprintPrincipal_OwnerPropagationForbiddenRetriesWithinBound()
    {
        var rest = ScriptedIdentityRestClient.ForNewBlueprint();
        var attempts = 0;
        rest.GraphOverride = call =>
        {
            if (call.Method != "POST" ||
                call.Path != "/v1.0/serviceprincipals/graph.agentIdentityBlueprintPrincipal")
            {
                return null;
            }

            attempts++;
            return attempts == 1
                ? new IdentityRestResponse(
                    403,
                    """
                    {"error":{"code":"Authorization_RequestDenied","message":"The backing application ownership has not propagated."}}
                    """)
                : new IdentityRestResponse(201, """{"id":"blueprint-sp-id"}""");
        };
        var service = new BlueprintIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(Request(), default);

        Assert.Equal("blueprint-sp-id", report.ServicePrincipalObjectId);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExistingBlueprint_ReconcilesWithoutCreatingDuplicateResourcesOrSecret()
    {
        var rest = ScriptedIdentityRestClient.ForExistingBlueprint();
        var service = new BlueprintIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(Request() with
        {
            Observability = true,
        }, default);

        Assert.Equal("reconciled", report.Status);
        Assert.DoesNotContain(rest.Calls, call =>
            call.Method == "POST" &&
            (call.Path == "/beta/applications/microsoft.graph.agentIdentityBlueprint" ||
             call.Path.Contains("agentIdentityBlueprintPrincipal", StringComparison.Ordinal) ||
             call.Path.EndsWith("/addPassword", StringComparison.Ordinal) ||
             call.Path == "/v1.0/oauth2PermissionGrants"));
        Assert.DoesNotContain(rest.Calls, call =>
            call.Method == "PATCH" &&
            call.Path == "/v1.0/applications/blueprint-object-id");
        Assert.Null(report.OneTimeClientSecret);
        Assert.NotNull(report.Configuration);
        Assert.DoesNotContain("one-time-secret", report.Configuration!.Content, StringComparison.Ordinal);
        Assert.Contains(report.NextActions, action => action.Contains("cannot be retrieved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExistingBlueprint_WithConvergedOAuthAndTeamsSso_DoesNotPatchApplication()
    {
        var rest = ScriptedIdentityRestClient.ForExistingBlueprint();
        rest.GraphOverride = ConvergedBlueprintLookup(scopeEnabled: true);
        var service = new BlueprintIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(Request() with
        {
            OAuth = true,
            TeamsSso = true,
        }, default);

        Assert.Equal("reconciled", report.Status);
        Assert.DoesNotContain(rest.Calls, call =>
            call.Method == "PATCH" &&
            call.Path == "/v1.0/applications/blueprint-object-id");
    }

    [Fact]
    public async Task ExistingBlueprint_WithDisabledAccessScope_ReenablesScope()
    {
        var rest = ScriptedIdentityRestClient.ForExistingBlueprint();
        rest.GraphOverride = ConvergedBlueprintLookup(scopeEnabled: false);
        var service = new BlueprintIdentityService(rest, retryDelay: TimeSpan.Zero);

        await service.ExecuteAsync(Request() with { TeamsSso = true }, default);

        var patch = Assert.Single(rest.Calls, call =>
            call.Method == "PATCH" &&
            call.Path == "/v1.0/applications/blueprint-object-id");
        Assert.Contains("\"value\":\"access_as_user\"", patch.Body);
        Assert.Contains("\"isEnabled\":true", patch.Body);
    }

    [Fact]
    public async Task ExistingBlueprint_OnNextPage_IsReused()
    {
        var rest = ScriptedIdentityRestClient.ForExistingBlueprint();
        rest.GraphOverride = call =>
        {
            var isBlueprintLookup =
                call.Path.StartsWith(
                    "/beta/applications/microsoft.graph.agentIdentityBlueprint?",
                    StringComparison.Ordinal) ||
                call.Path.StartsWith(
                    "https://graph.microsoft.com/beta/applications/microsoft.graph.agentIdentityBlueprint?",
                    StringComparison.Ordinal);
            if (call.Method != "GET" || !isBlueprintLookup)
            {
                return null;
            }

            return call.Path.Contains("$skiptoken=next", StringComparison.Ordinal)
                ? new IdentityRestResponse(200, """
                    {"value":[{
                      "id":"blueprint-object-id",
                      "appId":"blueprint-app-id",
                      "displayName":"Contoso Agent",
                      "passwordCredentials":[{
                        "displayName":"AgentHost CLI",
                        "endDateTime":"2099-01-01T00:00:00Z"
                      }]
                    }]}
                    """)
                : new IdentityRestResponse(200, """
                    {
                      "value":[],
                      "@odata.nextLink":"https://graph.microsoft.com/beta/applications/microsoft.graph.agentIdentityBlueprint?$skiptoken=next"
                    }
                    """);
        };
        var service = new BlueprintIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(Request(), default);

        Assert.Equal("reconciled", report.Status);
        Assert.DoesNotContain(rest.Calls, call =>
            call.Method == "POST" &&
            call.Path == "/beta/applications/microsoft.graph.agentIdentityBlueprint");
        Assert.Contains(rest.Calls, call =>
            call.Method == "GET" &&
            call.Path == "https://graph.microsoft.com/beta/applications/microsoft.graph.agentIdentityBlueprint?$skiptoken=next");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"value":null}""")]
    [InlineData("""{"value":{}}""")]
    [InlineData("""{"value":[null]}""")]
    [InlineData("""{"value":[42]}""")]
    public async Task MalformedBlueprintCollection_FailsBeforeCreatingDuplicate(string responseBody)
    {
        var rest = ScriptedIdentityRestClient.ForNewBlueprint();
        rest.GraphOverride = call =>
            call.Method == "GET" &&
            call.Path.StartsWith(
                "/beta/applications/microsoft.graph.agentIdentityBlueprint?",
                StringComparison.Ordinal)
                ? new IdentityRestResponse(200, responseBody)
                : null;
        var service = new BlueprintIdentityService(rest, retryDelay: TimeSpan.Zero);

        await Assert.ThrowsAsync<TargetRequestException>(() =>
            service.ExecuteAsync(Request(), default));

        Assert.DoesNotContain(rest.Calls, call =>
            call.Method == "POST" &&
            call.Path == "/beta/applications/microsoft.graph.agentIdentityBlueprint");
    }

    [Fact]
    public async Task DelegatedPermissions_ReturnsHandoffWithoutGrantCrud()
    {
        var rest = ScriptedIdentityRestClient.ForExistingBlueprint();
        var service = new BlueprintIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(Request(), default);

        Assert.DoesNotContain(
            rest.Calls,
            call => call.Path.StartsWith("/v1.0/oauth2PermissionGrants", StringComparison.Ordinal));
        Assert.NotNull(report.AdminConsentUrl);
        var decodedConsentUrl = Uri.UnescapeDataString(report.AdminConsentUrl);
        Assert.Contains("https://botapi.skype.com/AgentData.ReadWrite", decodedConsentUrl);
        Assert.Contains(
            "https://api.powerplatform.com/Connectivity.Connections.Read",
            decodedConsentUrl);
        Assert.DoesNotContain("Agent365.Observability.OtelWrite", decodedConsentUrl);
        Assert.DoesNotContain("McpServersMetadata.Read.All", decodedConsentUrl);
        Assert.Matches("[?&]state=[0-9a-f]{32}(?:&|$)", report.AdminConsentUrl);
    }

    [Fact]
    public async Task ObservabilityAppRole_ForbiddenReturnsAdministratorHandoffAndContinues()
    {
        var rest = ScriptedIdentityRestClient.ForNewBlueprint();
        rest.GraphOverride = call =>
            call.Method == "GET" &&
            call.Path.EndsWith("/appRoleAssignments", StringComparison.Ordinal)
                ? new IdentityRestResponse(
                    403,
                    """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges."}}""")
                : null;
        var service = new BlueprintIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(Request() with { Observability = true }, default);

        Assert.Equal("created", report.Status);
        Assert.NotNull(report.AdminConsentUrl);
        Assert.NotNull(report.Configuration);
        Assert.Contains(
            report.Changes,
            change =>
                change.Resource == "ObservabilityAppRole" &&
                change.Action == "handoff");
        Assert.Contains(
            report.NextActions,
            action =>
                action.Contains("Application Administrator or Global Administrator", StringComparison.Ordinal) &&
                action.Contains("Agent365.Observability.OtelWrite", StringComparison.Ordinal));
        Assert.Contains(
            rest.Calls,
            call =>
                call.Method == "GET" &&
                call.Path ==
                    "/beta/applications/blueprint-object-id/microsoft.graph.agentIdentityBlueprint/inheritablePermissions");
    }

    [Fact]
    public async Task CredentialFailure_PartialReportPreservesAdminConsentHandoff()
    {
        var rest = ScriptedIdentityRestClient.ForNewBlueprint();
        rest.GraphOverride = call =>
            call.Method == "POST" && call.Path.EndsWith("/addPassword", StringComparison.Ordinal)
                ? new IdentityRestResponse(
                    403,
                    """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges."}}""")
                : null;
        var service = new BlueprintIdentityService(rest, retryDelay: TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<BlueprintIdentityProvisioningException>(() =>
            service.ExecuteAsync(Request(), default));

        Assert.NotNull(error.PartialReport.AdminConsentUrl);
        Assert.Contains(
            error.PartialReport.NextActions,
            action => action.Contains(error.PartialReport.AdminConsentUrl, StringComparison.Ordinal));
        Assert.Contains(
            error.PartialReport.NextActions,
            action => action.Contains("rerun", StringComparison.OrdinalIgnoreCase));
    }

    private static BlueprintIdentityRequest Request() => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111",
        DisplayName = "Contoso Agent",
        AuthType = IdentityCredentialType.ClientSecret,
        ConfigFormat = IdentityConfigFormat.DotNet,
    };

    private static Func<IdentityRestCall, IdentityRestResponse?> ConvergedBlueprintLookup(
        bool scopeEnabled) =>
        call =>
        {
            if (call.Method == "PATCH" &&
                call.Path == "/v1.0/applications/blueprint-object-id")
            {
                return new IdentityRestResponse(204, null);
            }

            if (call.Method == "GET" &&
                call.Path.StartsWith(
                    "/beta/applications/microsoft.graph.agentIdentityBlueprint?",
                    StringComparison.Ordinal))
            {
                return new IdentityRestResponse(200, """
                    {"value":[{
                      "id":"blueprint-object-id",
                      "appId":"blueprint-app-id",
                      "displayName":"Contoso Agent"
                    }]}
                    """);
            }

            if (call.Method != "GET" ||
                !call.Path.StartsWith(
                    "/v1.0/applications/blueprint-object-id?",
                    StringComparison.Ordinal))
            {
                return null;
            }

            return new IdentityRestResponse(200, $$"""
                {
                  "id":"blueprint-object-id",
                  "appId":"blueprint-app-id",
                  "displayName":"Contoso Agent",
                  "passwordCredentials":[{
                    "displayName":"AgentHost CLI",
                    "endDateTime":"2099-01-01T00:00:00Z"
                  }],
                  "identifierUris":["api://botid-blueprint-app-id"],
                  "web":{"redirectUris":[
                    "{{BlueprintIdentityPlan.DefaultRedirectUri}}",
                    "{{BlueprintAdminConsent.RedirectUri}}"
                  ]},
                  "api":{
                    "requestedAccessTokenVersion":2,
                    "oauth2PermissionScopes":[{
                      "id":"access-scope-id",
                      "value":"access_as_user",
                      "isEnabled":{{scopeEnabled.ToString().ToLowerInvariant()}}
                    }],
                    "preAuthorizedApplications":[
                      {
                        "appId":"{{IdentityProvisioningConfig.TeamsHostApplicationIds[0]}}",
                        "delegatedPermissionIds":["access-scope-id"]
                      },
                      {
                        "appId":"{{IdentityProvisioningConfig.TeamsHostApplicationIds[1]}}",
                        "delegatedPermissionIds":["access-scope-id"]
                      }
                    ]
                  }
                }
                """);
        };
}

internal sealed class ScriptedIdentityRestClient : IIdentityRestClient
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly Func<IdentityRestCall, IdentityRestResponse> _graph;
    private readonly Func<IdentityRestCall, IdentityRestResponse> _arm;

    public ScriptedIdentityRestClient(
        Func<IdentityRestCall, IdentityRestResponse>? graph = null,
        Func<IdentityRestCall, IdentityRestResponse>? arm = null)
    {
        _graph = graph ?? (_ => Json(200, """{"value":[]}"""));
        _arm = arm ?? (_ => Json(404, """{"error":{"code":"NotFound"}}"""));
    }

    public List<IdentityRestCall> Calls { get; } = [];
    public Func<IdentityRestCall, IdentityRestResponse?>? GraphOverride { get; set; }

    public Task<IdentityRestResponse> SendGraphAsync(
        HttpMethod method,
        string path,
        object? body,
        IReadOnlyList<string> scopes,
        string targetTenantId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var call = Record(method, path, body, headers, scopes, targetTenantId);
        return Task.FromResult(GraphOverride?.Invoke(call) ?? _graph(call));
    }

    public Task<IdentityRestResponse> SendArmAsync(
        HttpMethod method,
        string path,
        object? body,
        string targetTenantId,
        CancellationToken cancellationToken)
    {
        var call = Record(method, path, body, headers: null, scopes: null, targetTenantId);
        return Task.FromResult(_arm(call));
    }

    public static ScriptedIdentityRestClient ForNewBlueprint(
        Func<IdentityRestCall, IdentityRestResponse>? arm = null) => new(call =>
    {
        if (call.Method == "GET" &&
            call.Path.StartsWith(
                "/beta/applications/microsoft.graph.agentIdentityBlueprint?",
                StringComparison.Ordinal))
        {
            return Json(200, """{"value":[]}""");
        }

        if (call.Method == "GET" && call.Path == "/v1.0/me?$select=id")
        {
            return Json(200, """{"id":"owner-id"}""");
        }

        if (call.Method == "POST" &&
            call.Path == "/beta/applications/microsoft.graph.agentIdentityBlueprint")
        {
            return Json(201, """
                {"id":"blueprint-object-id","appId":"blueprint-app-id","displayName":"Contoso Agent"}
                """);
        }

        if (call.Method == "GET" &&
            call.Path.Contains("servicePrincipals?", StringComparison.OrdinalIgnoreCase))
        {
            if (call.Path.Contains("blueprint-app-id", StringComparison.Ordinal))
            {
                return Json(200, """{"value":[]}""");
            }

            var permission = ResolvePermission(call.Path);
            return Json(200, $$"""
                {"value":[{"id":"{{permission.Name}}-sp","oauth2PermissionScopes":[
                  {"id":"{{permission.Name}}-scope","value":"{{permission.DelegatedScope}}","isEnabled":true}
                ],"appRoles":[
                  {"id":"{{permission.Name}}-role","value":"{{permission.ApplicationRole}}","isEnabled":true}
                ]}]}
                """);
        }

        if (call.Method == "POST" &&
            call.Path == "/v1.0/serviceprincipals/graph.agentIdentityBlueprintPrincipal")
        {
            return Json(201, """{"id":"blueprint-sp-id"}""");
        }

        if (call.Method == "POST" &&
            call.Path == "/beta/applications/blueprint-object-id/microsoft.graph.agentIdentityBlueprint/addPassword")
        {
            return Json(200, """
                {"secretText":"one-time-secret","endDateTime":"2027-07-31T00:00:00Z"}
                """);
        }

        if (call.Method == "GET" &&
            (call.Path.EndsWith("/appRoleAssignments", StringComparison.Ordinal) ||
             call.Path ==
                "/beta/applications/blueprint-object-id/microsoft.graph.agentIdentityBlueprint/inheritablePermissions"))
        {
            return Json(200, """{"value":[]}""");
        }

        if (call.Method == "POST" &&
            (call.Path.EndsWith("/appRoleAssignments", StringComparison.Ordinal) ||
             call.Path ==
                "/beta/applications/blueprint-object-id/microsoft.graph.agentIdentityBlueprint/inheritablePermissions"))
        {
            return Json(201, """{"id":"created"}""");
        }

        if (call.Method == "PATCH" &&
            (call.Path ==
                "/v1.0/applications/blueprint-object-id" ||
             call.Path.StartsWith(
                 "/beta/applications/blueprint-object-id/microsoft.graph.agentIdentityBlueprint/" +
                 "federatedIdentityCredentials(",
                 StringComparison.Ordinal)))
        {
            return new IdentityRestResponse(204, null);
        }

        throw new Xunit.Sdk.XunitException($"Unexpected REST call: {call.Method} {call.Path}");
    }, arm);

    public static ScriptedIdentityRestClient ForExistingBlueprint() => new(call =>
    {
        if (call.Method == "GET" &&
            call.Path.StartsWith(
                "/beta/applications/microsoft.graph.agentIdentityBlueprint?",
                StringComparison.Ordinal))
        {
            return Json(200, """
                {"value":[{
                  "id":"blueprint-object-id",
                  "appId":"blueprint-app-id",
                  "displayName":"Contoso Agent",
                  "web":{"redirectUris":["https://entra.microsoft.com/TokenAuthorize"]},
                  "passwordCredentials":[{
                    "displayName":"AgentHost CLI",
                    "endDateTime":"2099-01-01T00:00:00Z"
                  }]
                }]}
                """);
        }

        if (call.Method == "GET" &&
            call.Path.StartsWith(
                "/v1.0/applications/blueprint-object-id?",
                StringComparison.Ordinal))
        {
            return Json(200, """
                {
                  "id":"blueprint-object-id",
                  "appId":"blueprint-app-id",
                  "displayName":"Contoso Agent",
                  "web":{"redirectUris":["https://entra.microsoft.com/TokenAuthorize"]},
                  "passwordCredentials":[{
                    "displayName":"AgentHost CLI",
                    "endDateTime":"2099-01-01T00:00:00Z"
                  }]
                }
                """);
        }

        if (call.Method == "GET" &&
            call.Path.Contains("servicePrincipals?", StringComparison.OrdinalIgnoreCase))
        {
            if (call.Path.Contains("blueprint-app-id", StringComparison.Ordinal))
            {
                return Json(200, """{"value":[{"id":"blueprint-sp-id"}]}""");
            }

            var permission = ResolvePermission(call.Path);
            return Json(200, $$"""
                {"value":[{"id":"{{permission.Name}}-sp","oauth2PermissionScopes":[
                  {"id":"{{permission.Name}}-scope","value":"{{permission.DelegatedScope}}","isEnabled":true}
                ],"appRoles":[
                  {"id":"{{permission.Name}}-role","value":"{{permission.ApplicationRole}}","isEnabled":true}
                ]}]}
                """);
        }

        if (call.Method == "GET" && call.Path.EndsWith("/appRoleAssignments", StringComparison.Ordinal))
        {
            return Json(200, """
                {"value":[{"resourceId":"Observability-sp","appRoleId":"Observability-role"}]}
                """);
        }

        if (call.Method == "GET" &&
            call.Path ==
                "/beta/applications/blueprint-object-id/microsoft.graph.agentIdentityBlueprint/inheritablePermissions")
        {
            return Json(200, """
                {"value":[
                  {"resourceAppId":"5a807f24-c9de-44ee-a3a7-329e88a00ffc","inheritableScopes":{"kind":"allAllowed"},"inheritableRoles":{"kind":"none"}},
                  {"resourceAppId":"8578e004-a5c6-46e7-913e-12f58912df43","inheritableScopes":{"kind":"allAllowed"},"inheritableRoles":{"kind":"none"}},
                  {"resourceAppId":"9b975845-388f-4429-889e-eab1ef63949c","inheritableScopes":{"kind":"allAllowed"},"inheritableRoles":{"kind":"allAllowed"}}
                ]}
                """);
        }

        throw new Xunit.Sdk.XunitException($"Unexpected REST call: {call.Method} {call.Path}");
    });

    private IdentityRestCall Record(
        HttpMethod method,
        string path,
        object? body,
        IReadOnlyDictionary<string, string>? headers,
        IReadOnlyList<string>? scopes,
        string targetTenantId)
    {
        var call = new IdentityRestCall(
            method.Method,
            path,
            body is null ? null : JsonSerializer.Serialize(body, WebJson),
            headers is null
                ? null
                : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
            scopes is null ? null : [.. scopes],
            targetTenantId);
        Calls.Add(call);
        return call;
    }

    private static BlueprintPermission ResolvePermission(string path)
    {
        var permissions = new[]
        {
            IdentityProvisioningConfig.MessagingBotPermission,
            IdentityProvisioningConfig.PowerPlatformPermission,
            IdentityProvisioningConfig.ObservabilityPermission,
            IdentityProvisioningConfig.McpDiscoveryPermission,
        };
        return permissions.Single(permission => path.Contains(permission.ResourceAppId, StringComparison.Ordinal));
    }

    private static IdentityRestResponse Json(int status, string body) => new(status, body);
}

internal sealed record IdentityRestCall(
    string Method,
    string Path,
    string? Body,
    IReadOnlyDictionary<string, string>? Headers,
    IReadOnlyList<string>? Scopes,
    string? TenantId = null);
