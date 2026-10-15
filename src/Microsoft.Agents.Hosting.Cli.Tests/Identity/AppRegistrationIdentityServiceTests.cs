using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests.Identity;

public class AppRegistrationIdentityServiceTests
{
    private const string AppObjectId = "app-object-id";
    private const string AppClientId = "app-client-id";
    private const string AppSpId = "app-sp-id";
    private const string OverrideGraphClientId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string OverrideArmClientId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string OverrideRedirectUri = "http://localhost/agenthost-consent";

    [Fact]
    public async Task DryRun_ReturnsPlan_AndNeverMutates()
    {
        var rest = new ScriptedIdentityRestClient();
        var service = new AppRegistrationIdentityService(rest);

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
                change.Detail.Contains("Declare", StringComparison.Ordinal));
        Assert.Contains(
            report.Changes,
            change => change.Resource == "ObservabilityAppRoleAssignment" &&
                change.Detail.Contains("grant", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Changes, change => change.Resource == "McpDiscovery");
        Assert.Contains(report.Changes, change => change.Resource == "TeamsSso");
        Assert.NotNull(report.AdminConsentUrl);
        Assert.Contains($"client_id={GraphDelegatedConsent.DefaultGraphClientId}", report.AdminConsentUrl!);
        Assert.DoesNotContain(rest.Calls, call => call.Method is "POST" or "PUT" or "PATCH" or "DELETE");
    }

    [Fact]
    public async Task DryRun_TeamsSsoIncludesOAuthRedirect()
    {
        var rest = new ScriptedIdentityRestClient();
        var service = new AppRegistrationIdentityService(rest);

        var report = await service.ExecuteAsync(Request() with
        {
            DryRun = true,
            TeamsSso = true,
        }, default);

        Assert.Contains(
            report.Changes,
            change => change.Resource == "OAuth" &&
                change.Detail.Contains(AppRegistrationIdentityPlan.DefaultRedirectUri));
        Assert.Contains(report.Changes, change => change.Resource == "TeamsSso");
        Assert.DoesNotContain(rest.Calls, call => call.Method is "POST" or "PUT" or "PATCH" or "DELETE");
    }

    [Fact]
    public async Task DryRun_ShowsDelegatedConsentHandoff_WithDefaultClient()
    {
        var rest = new ScriptedIdentityRestClient();
        var service = new AppRegistrationIdentityService(rest);

        var report = await service.ExecuteAsync(Request() with { DryRun = true }, default);

        Assert.Equal("planned", report.Status);
        Assert.Contains(
            report.Changes,
            change => change.Resource == "GraphClient" &&
                change.Detail.Contains(GraphDelegatedConsent.DefaultGraphClientId));
        Assert.Contains(report.Changes, change => change.Resource == "Consent" && change.Action == "request");
        Assert.NotNull(report.AdminConsentUrl);
        Assert.Contains($"client_id={GraphDelegatedConsent.DefaultGraphClientId}", report.AdminConsentUrl!);
        Assert.Contains(report.NextActions, action => action.Contains(report.AdminConsentUrl!));
        Assert.DoesNotContain(rest.Calls, call => call.Method is "POST" or "PUT" or "PATCH" or "DELETE");
    }

    [Fact]
    public async Task DryRun_UsesConfiguredGraphClientForPlanAndConsentUrl()
    {
        var rest = new ScriptedIdentityRestClient();
        var service = new AppRegistrationIdentityService(
            rest,
            graphConsentClient: GraphConsentClientConfiguration.Resolve(
                OverrideGraphClientId,
                OverrideRedirectUri));

        var report = await service.ExecuteAsync(Request() with { DryRun = true }, default);

        Assert.Contains(
            report.Changes,
            change => change.Resource == "GraphClient" &&
                change.Detail.Contains(OverrideGraphClientId));
        Assert.Contains($"client_id={OverrideGraphClientId}", report.AdminConsentUrl!);
    }

    [Fact]
    public async Task DryRun_ManagedIdentity_ShowsArmConsentClientSelection()
    {
        var rest = new ScriptedIdentityRestClient();
        var service = new AppRegistrationIdentityService(rest);

        var ficReport = await service.ExecuteAsync(
            ManagedIdentityRequest(IdentityCredentialType.FederatedCredentials) with { DryRun = true }, default);
        var secretReport = await service.ExecuteAsync(Request() with { DryRun = true }, default);

        // FIC/UMI reach ARM, so the plan announces the consent-capable ARM client (transparency parity
        // with the Graph client line); ClientSecret never touches ARM, so no ARM line is emitted.
        Assert.Contains(
            ficReport.Changes,
            change => change.Resource == "ArmClient" && change.Action == "select" &&
                change.Detail.Contains(ArmDelegatedConsent.DefaultArmClientId));
        Assert.DoesNotContain(secretReport.Changes, change => change.Resource == "ArmClient");
        Assert.DoesNotContain(rest.Calls, call => call.Method is "POST" or "PUT" or "PATCH" or "DELETE");
    }

    [Fact]
    public async Task DryRun_UsesConfiguredArmClientAndIsDeterministic()
    {
        var rest = new ScriptedIdentityRestClient();
        var service = new AppRegistrationIdentityService(
            rest,
            armConsentClient: ArmConsentClientConfiguration.Resolve(
                OverrideArmClientId,
                "http://localhost/agenthost-arm-consent"));
        var request = ManagedIdentityRequest(IdentityCredentialType.FederatedCredentials) with
        {
            DryRun = true,
        };

        var first = await service.ExecuteAsync(request, default);
        var second = await service.ExecuteAsync(request, default);

        Assert.Contains(
            first.Changes,
            change => change.Resource == "ArmClient" &&
                change.Detail.Contains(OverrideArmClientId, StringComparison.Ordinal));
        Assert.Equal(first.AdminConsentUrl, second.AdminConsentUrl);
        Assert.DoesNotContain("state=", first.AdminConsentUrl);
    }

    [Fact]
    public async Task Execute_ThrowsNonzeroConsentRequiredFailure_WhenGraphNeedsConsent()
    {
        var rest = new ScriptedIdentityRestClient(
            graph: _ => throw new InteractionRequiredException(
                "Microsoft Graph consent is required.",
                resource: InteractionRequiredResource.MicrosoftGraph));
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<AppRegistrationIdentityProvisioningException>(() =>
            service.ExecuteAsync(Request(), default));
        var report = error.PartialReport;

        Assert.Equal(ExitCodes.NotSignedIn, error.ExitCode);
        Assert.Equal("consent-required", report.Status);
        Assert.NotNull(report.AdminConsentUrl);
        Assert.Contains($"client_id={GraphDelegatedConsent.DefaultGraphClientId}", report.AdminConsentUrl!);
        Assert.Contains(report.Changes, change => change.Resource == "Consent" && change.Action == "required");
        Assert.Contains(report.NextActions, action => action.Contains(report.AdminConsentUrl!));
        Assert.DoesNotContain(rest.Calls, call => call.Method is "POST" or "PUT" or "PATCH" or "DELETE");
    }

    [Fact]
    public async Task Execute_UsesGraphClientOverride_InConsentRequiredFailureUrl()
    {
        var rest = new ScriptedIdentityRestClient(
            graph: _ => throw new InteractionRequiredException(
                "Microsoft Graph consent is required.",
                resource: InteractionRequiredResource.MicrosoftGraph));
        var service = new AppRegistrationIdentityService(
            rest,
            retryDelay: TimeSpan.Zero,
            graphConsentClient: GraphConsentClientConfiguration.Resolve(
                OverrideGraphClientId,
                OverrideRedirectUri));

        var error = await Assert.ThrowsAsync<AppRegistrationIdentityProvisioningException>(() =>
            service.ExecuteAsync(Request(), default));
        var report = error.PartialReport;

        Assert.Equal(ExitCodes.NotSignedIn, error.ExitCode);
        Assert.Equal("consent-required", report.Status);
        Assert.NotNull(report.AdminConsentUrl);
        Assert.Contains($"client_id={OverrideGraphClientId}", report.AdminConsentUrl!);
    }

    [Fact]
    public async Task Execute_LateGraphConsentFailure_PreservesCreatedApplicationCoordinates()
    {
        var rest = new ScriptedIdentityRestClient(NewAppGraph());
        rest.GraphOverride = call =>
            call.Method == "GET" &&
            call.Path == $"/v1.0/servicePrincipals/{AppSpId}/appRoleAssignments"
                ? throw new InteractionRequiredException(
                    "Microsoft Graph consent is required.",
                    resource: InteractionRequiredResource.MicrosoftGraph)
                : null;
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<AppRegistrationIdentityProvisioningException>(() =>
            service.ExecuteAsync(Request() with { Observability = true }, default));
        var report = error.PartialReport;

        Assert.Equal("consent-required", report.Status);
        Assert.Equal(AppClientId, report.ApplicationId);
        Assert.Equal(AppObjectId, report.ApplicationObjectId);
        Assert.Equal(AppSpId, report.ServicePrincipalObjectId);
        Assert.Contains($"client_id={GraphDelegatedConsent.DefaultGraphClientId}", report.AdminConsentUrl!);
    }

    [Fact]
    public async Task Execute_FederationConsentFailure_PreservesManagedIdentityCoordinates()
    {
        var rest = new ScriptedIdentityRestClient(NewAppGraph(), ManagedIdentityArm());
        rest.GraphOverride = call =>
            call.Method == "GET" &&
            call.Path.StartsWith(
                $"/v1.0/applications/{AppObjectId}/federatedIdentityCredentials/",
                StringComparison.Ordinal)
                ? throw new InteractionRequiredException(
                    "Microsoft Graph consent is required.",
                    resource: InteractionRequiredResource.MicrosoftGraph)
                : null;
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<AppRegistrationIdentityProvisioningException>(() =>
            service.ExecuteAsync(
                ManagedIdentityRequest(IdentityCredentialType.FederatedCredentials),
                default));
        var report = error.PartialReport;

        Assert.Equal("consent-required", report.Status);
        Assert.Equal(AppClientId, report.ApplicationId);
        Assert.Equal(AppSpId, report.ServicePrincipalObjectId);
        Assert.Equal("mi-client-id", report.ManagedIdentityClientId);
        Assert.Equal("mi-principal-id", report.ManagedIdentityPrincipalId);
        Assert.Equal("mi-resource-id", report.ManagedIdentityResourceId);
        Assert.Contains($"client_id={GraphDelegatedConsent.DefaultGraphClientId}", report.AdminConsentUrl!);
    }

    [Fact]
    public async Task Observability_RetriesAssignmentAfterPrincipalPropagationDelay()
    {
        var assignmentReads = 0;
        var rest = new ScriptedIdentityRestClient(NewAppGraph());
        rest.GraphOverride = call =>
        {
            if (call.Method != "GET" ||
                call.Path != $"/v1.0/servicePrincipals/{AppSpId}/appRoleAssignments")
            {
                return null;
            }

            assignmentReads++;
            return assignmentReads == 1
                ? new IdentityRestResponse(
                    404,
                    """{"error":{"code":"Request_ResourceNotFound"}}""")
                : null;
        };
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(Request() with { Observability = true }, default);

        Assert.Equal("created", report.Status);
        Assert.Equal(2, assignmentReads);
        Assert.Contains(
            report.Changes,
            change => change.Resource == "ObservabilityAppRole" && change.Action == "grant");
    }

    [Fact]
    public async Task Observability_RetriesAssignmentCreateAfterResourcePropagationDelay()
    {
        var assignmentCreates = 0;
        var rest = new ScriptedIdentityRestClient(NewAppGraph());
        rest.GraphOverride = call =>
        {
            if (call.Method != "POST" ||
                call.Path != $"/v1.0/servicePrincipals/{AppSpId}/appRoleAssignments")
            {
                return null;
            }

            assignmentCreates++;
            return assignmentCreates == 1
                ? new IdentityRestResponse(
                    404,
                    """{"error":{"code":"Request_ResourceNotFound"}}""")
                : null;
        };
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(Request() with { Observability = true }, default);

        Assert.Equal("created", report.Status);
        Assert.Equal(2, assignmentCreates);
        Assert.Contains(
            report.Changes,
            change => change.Resource == "ObservabilityAppRole" && change.Action == "grant");
    }

    [Fact]
    public async Task ClientSecret_CreatesAppPrincipalPermissionsRedirectSsoAndSecret()
    {
        var rest = new ScriptedIdentityRestClient(NewAppGraph());
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(Request() with
        {
            OAuth = true,
            TeamsSso = true,
            Observability = true,
            McpDiscovery = true,
        }, default);

        Assert.Equal("created", report.Status);
        Assert.Equal(AppClientId, report.ApplicationId);
        Assert.Equal(AppObjectId, report.ApplicationObjectId);
        Assert.Equal(AppSpId, report.ServicePrincipalObjectId);
        Assert.NotNull(report.AdminConsentUrl);
        Assert.NotNull(report.Configuration);
        Assert.Equal("one-time-secret", report.OneTimeClientSecret);
        Assert.DoesNotContain("one-time-secret", report.Configuration!.Content, StringComparison.Ordinal);
        Assert.Equal(
            DateTimeOffset.Parse("2027-07-31T00:00:00Z"),
            report.ClientSecretExpiresOn);
        Assert.Contains(
            report.NextActions,
            action => action.Contains("approved secret store", StringComparison.OrdinalIgnoreCase));

        // New app => the service principal is created, reported as such (not a static "ensure").
        var spCreate = report.Changes.Single(change => change.Resource == "ServicePrincipal");
        Assert.Equal("create", spCreate.Action);
        Assert.Contains("Created service principal", spCreate.Detail);

        // Standard application routes, never the Blueprint cast.
        Assert.Contains(rest.Calls, call => call.Method == "POST" && call.Path == "/v1.0/applications");
        Assert.Contains(rest.Calls, call => call.Method == "POST" && call.Path == "/v1.0/servicePrincipals");
        Assert.DoesNotContain(rest.Calls, call => call.Path.Contains("agentIdentityBlueprint", StringComparison.Ordinal));

        // Bot permissions declared through requiredResourceAccess, including the observability app role.
        var permissionPatch = rest.Calls.Single(call =>
            call.Method == "PATCH" &&
            call.Path == $"/v1.0/applications/{AppObjectId}" &&
            call.Body!.Contains("requiredResourceAccess", StringComparison.Ordinal));
        Assert.Contains(IdentityProvisioningConfig.MessagingBotPermission.ResourceAppId, permissionPatch.Body!);
        Assert.Contains("\"Role\"", permissionPatch.Body!);

        // Teams SSO exposes access_as_user and preauthorizes both first-party clients.
        Assert.Contains(rest.Calls, call =>
            call.Method == "PATCH" &&
            call.Body!.Contains("access_as_user", StringComparison.Ordinal));
        Assert.Contains(rest.Calls, call =>
            call.Method == "PATCH" &&
            call.Body!.Contains(IdentityProvisioningConfig.TeamsHostApplicationIds[0], StringComparison.Ordinal) &&
            call.Body!.Contains(IdentityProvisioningConfig.TeamsHostApplicationIds[1], StringComparison.Ordinal));
        Assert.Contains(rest.Calls, call =>
            call.Method == "PATCH" &&
            call.Body!.Contains(BlueprintAdminConsent.RedirectUri, StringComparison.Ordinal));
        Assert.Contains(rest.Calls, call =>
            call.Method == "PATCH" &&
            call.Body!.Contains("\"requestedAccessTokenVersion\":2", StringComparison.Ordinal));

        var roleAssignment = Assert.Single(rest.Calls, call =>
            call.Method == "POST" &&
            call.Path == $"/v1.0/servicePrincipals/{AppSpId}/appRoleAssignments");
        Assert.Contains("\"resourceId\":\"Observability-sp\"", roleAssignment.Body);
        Assert.Contains("\"appRoleId\":\"Observability-role\"", roleAssignment.Body);

        Assert.Contains(rest.Calls, call =>
            call.Method == "POST" && call.Path == $"/v1.0/applications/{AppObjectId}/addPassword");
    }

    [Fact]
    public async Task ClientSecret_TeamsSsoAloneCreatesRedirectAndSsoState()
    {
        var rest = new ScriptedIdentityRestClient(NewAppGraph());
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        await service.ExecuteAsync(Request() with { TeamsSso = true }, default);

        var applicationPatches = rest.Calls
            .Where(call => call.Method == "PATCH" && call.Path == $"/v1.0/applications/{AppObjectId}")
            .ToList();
        Assert.Contains(
            applicationPatches,
            call => call.Body!.Contains(AppRegistrationIdentityPlan.DefaultRedirectUri, StringComparison.Ordinal));
        Assert.Contains(
            applicationPatches,
            call => call.Body!.Contains($"api://botid-{AppClientId}", StringComparison.Ordinal));
        Assert.Contains(
            applicationPatches,
            call => call.Body!.Contains("access_as_user", StringComparison.Ordinal));
        Assert.Contains(
            applicationPatches,
            call => call.Body!.Contains(
                IdentityProvisioningConfig.TeamsHostApplicationIds[0],
                StringComparison.Ordinal) &&
                call.Body.Contains(
                    IdentityProvisioningConfig.TeamsHostApplicationIds[1],
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task FederatedCredentials_ProvisionsManagedIdentityAndFederation()
    {
        var rest = new ScriptedIdentityRestClient(NewAppGraph(), ManagedIdentityArm());
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(ManagedIdentityRequest(
            IdentityCredentialType.FederatedCredentials), default);

        Assert.Equal("created", report.Status);
        Assert.Equal("mi-client-id", report.ManagedIdentityClientId);
        Assert.Equal("mi-principal-id", report.ManagedIdentityPrincipalId);
        Assert.Equal("mi-resource-id", report.ManagedIdentityResourceId);
        Assert.NotNull(report.Configuration);
        Assert.Contains("mi-client-id", report.Configuration!.Content);

        // MI is provisioned over ARM (GET-then-PUT) and federated to the app; subject is the MI principal.
        Assert.Contains(rest.Calls, call => call.Method == "PUT" && call.Path.Contains("userAssignedIdentities", StringComparison.Ordinal));
        var fic = rest.Calls.Single(call =>
            call.Method == "PATCH" &&
            call.Path.StartsWith($"/v1.0/applications/{AppObjectId}/federatedIdentityCredentials(", StringComparison.Ordinal));
        Assert.Contains("mi-principal-id", fic.Body!);
        Assert.Equal("create-if-missing", fic.Headers!["Prefer"]);

        // Fresh app + MI + federation (Graph upsert returned 201) => created wording, not static "ensure".
        var ficChange = report.Changes.Single(change => change.Resource == "FederatedCredentials");
        Assert.Equal("create", ficChange.Action);

        Assert.DoesNotContain(rest.Calls, call => call.Path.EndsWith("/addPassword", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FederatedCredentials_MissingCredentialAndNoContentUpsert_EmitsCreate()
    {
        var rest = new ScriptedIdentityRestClient(
            NewAppGraph(freshFederationStatus: 204),
            ManagedIdentityArm());
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(
            ManagedIdentityRequest(IdentityCredentialType.FederatedCredentials),
            default);

        var ficChange = report.Changes.Single(change => change.Resource == "FederatedCredentials");
        Assert.Equal("create", ficChange.Action);
        Assert.Contains("Configured", ficChange.Detail);
    }

    [Fact]
    public async Task FederatedCredentials_Rerun_ReusesFederation_EmitsNoop()
    {
        var rest = new ScriptedIdentityRestClient(NewAppGraph(existing: true), ManagedIdentityArm(existing: true));
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(ManagedIdentityRequest(
            IdentityCredentialType.FederatedCredentials), default);

        Assert.Equal("reconciled", report.Status);
        Assert.Equal("mi-client-id", report.ManagedIdentityClientId);

        // Existing app + existing MI + existing federation (Graph upsert returned 204) => reused, not created.
        var sp = report.Changes.Single(change => change.Resource == "ServicePrincipal");
        Assert.Equal("noop", sp.Action);
        var ficChange = report.Changes.Single(change => change.Resource == "FederatedCredentials");
        Assert.Equal("noop", ficChange.Action);
        Assert.Contains("Reused", ficChange.Detail);
        Assert.Contains(rest.Calls, call =>
            call.Method == "GET" &&
            call.Path.Contains("federatedIdentityCredentials", StringComparison.Ordinal));

        // Existing MI was reused (GET 200), never re-created via PUT.
        Assert.DoesNotContain(rest.Calls, call => call.Method == "PUT" && call.Path.Contains("userAssignedIdentities", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FederatedCredentials_StaleSubject_ReconcilesFederation()
    {
        var rest = new ScriptedIdentityRestClient(
            NewAppGraph(existing: true, staleFederation: true),
            ManagedIdentityArm(existing: true));
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(
            ManagedIdentityRequest(IdentityCredentialType.FederatedCredentials),
            default);

        var ficChange = report.Changes.Single(change => change.Resource == "FederatedCredentials");
        Assert.Equal("reconcile", ficChange.Action);
        Assert.Contains("Reconciled", ficChange.Detail);
        Assert.Contains(rest.Calls, call =>
            call.Method == "PATCH" &&
            call.Path.Contains("federatedIdentityCredentials", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UserManagedIdentity_ProvisionsManagedIdentity_WithoutSecretOrFederation()
    {
        var rest = new ScriptedIdentityRestClient(NewAppGraph(), ManagedIdentityArm());
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(ManagedIdentityRequest(
            IdentityCredentialType.UserManagedIdentity) with { OAuth = true }, default);

        Assert.Equal("created", report.Status);
        Assert.Equal("mi-client-id", report.ManagedIdentityClientId);
        Assert.NotNull(report.Configuration);

        // Runtime credential is the MI client id; the companion app id backs user-facing OAuth.
        Assert.Contains("mi-client-id", report.Configuration!.Content);
        Assert.Contains(AppClientId, report.Configuration!.Content);

        Assert.DoesNotContain(rest.Calls, call => call.Path.EndsWith("/addPassword", StringComparison.Ordinal));
        Assert.DoesNotContain(rest.Calls, call =>
            call.Path.Contains("federatedIdentityCredentials", StringComparison.Ordinal));

        // New managed identity => created wording (GET 404 -> PUT).
        var umiCreate = report.Changes.Single(change => change.Resource == "UserManagedIdentity");
        Assert.Equal("create", umiCreate.Action);
    }

    [Fact]
    public async Task UserManagedIdentity_Rerun_ReusesManagedIdentity_EmitsNoop()
    {
        var rest = new ScriptedIdentityRestClient(NewAppGraph(existing: true), ManagedIdentityArm(existing: true));
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(ManagedIdentityRequest(
            IdentityCredentialType.UserManagedIdentity) with { OAuth = true }, default);

        Assert.Equal("reconciled", report.Status);
        Assert.Equal("mi-client-id", report.ManagedIdentityClientId);

        // Existing app + existing MI => both reused, reported as noop (not a static "ensure").
        var sp = report.Changes.Single(change => change.Resource == "ServicePrincipal");
        Assert.Equal("noop", sp.Action);
        Assert.Contains("Reused service principal", sp.Detail);
        var umi = report.Changes.Single(change => change.Resource == "UserManagedIdentity");
        Assert.Equal("noop", umi.Action);
        Assert.Contains("Reused", umi.Detail);

        // Existing MI was reused (GET 200), never re-created via PUT.
        Assert.DoesNotContain(rest.Calls, call => call.Method == "PUT" && call.Path.Contains("userAssignedIdentities", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TargetAppId_UsesAppIdLookup_NotDisplayName()
    {
        var rest = new ScriptedIdentityRestClient(NewAppGraph(existing: true));
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(Request() with
        {
            ApplicationId = "22222222-2222-2222-2222-222222222222",
        }, default);

        Assert.Equal("reconciled", report.Status);
        Assert.Contains(rest.Calls, call =>
            call.Method == "GET" &&
            call.Path.StartsWith("/v1.0/applications?$filter=", StringComparison.Ordinal) &&
            call.Path.Contains("appId", StringComparison.Ordinal));
        Assert.DoesNotContain(rest.Calls, call =>
            call.Path.Contains("$filter=displayName", StringComparison.Ordinal));

        // Existing service principal => reused, reported as noop (not a static "ensure").
        var spReuse = report.Changes.Single(change => change.Resource == "ServicePrincipal");
        Assert.Equal("noop", spReuse.Action);
        Assert.Contains("Reused service principal", spReuse.Detail);
    }

    [Fact]
    public async Task ExistingSingleTenantApp_FailsBeforeMutation()
    {
        var rest = new ScriptedIdentityRestClient(
            NewAppGraph(existing: true, signInAudience: "AzureADMyOrg"));
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<ProvisioningException>(() =>
            service.ExecuteAsync(Request() with
            {
                ApplicationId = "22222222-2222-2222-2222-222222222222",
            }, default));

        Assert.Equal(ProvisioningErrorKind.NamingConflict, error.Kind);
        Assert.Contains("AzureADMyOrg", error.Message);
        Assert.Contains(AppRegistrationIdentityPlan.SignInAudience, error.Remediation);
        Assert.DoesNotContain(rest.Calls, call =>
            call.Method is "POST" or "PUT" or "PATCH" or "DELETE");
    }

    [Fact]
    public async Task ExistingConvergedApp_ReconcilesIdempotently_WithoutRedundantWrites()
    {
        var rest = new ScriptedIdentityRestClient(ConvergedAppGraph());
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var report = await service.ExecuteAsync(Request() with
        {
            OAuth = true,
            TeamsSso = true,
            Observability = true,
            McpDiscovery = true,
        }, default);

        Assert.Equal("reconciled", report.Status);
        Assert.Null(report.OneTimeClientSecret);
        Assert.NotNull(report.Configuration);
        Assert.DoesNotContain("one-time-secret", report.Configuration!.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(rest.Calls, call => call.Path.EndsWith("/addPassword", StringComparison.Ordinal));
        // Fully converged: no application PATCH at all.
        Assert.DoesNotContain(rest.Calls, call =>
            call.Method == "PATCH" && call.Path == $"/v1.0/applications/{AppObjectId}");
        Assert.DoesNotContain(rest.Calls, call =>
            call.Method == "POST" && call.Path.EndsWith("/appRoleAssignments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PartialFailure_AfterAppCreated_WrapsWithSecretFreeReport()
    {
        // Resource service principals are unavailable, so permission resolution fails after the
        // application and its principal already exist.
        var rest = new ScriptedIdentityRestClient(NewAppGraph(missingResourceSp: true));
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<AppRegistrationIdentityProvisioningException>(() =>
            service.ExecuteAsync(Request(), default));

        Assert.Equal("partial", error.PartialReport.Status);
        Assert.Equal(AppClientId, error.PartialReport.ApplicationId);
        Assert.Null(error.PartialReport.Configuration);
    }

    [Fact]
    public async Task ArmConsentFailure_AfterAppCreated_RemainsArmSpecificAndPartial()
    {
        var rest = new ScriptedIdentityRestClient(
            graph: NewAppGraph(),
            arm: _ => throw new InteractionRequiredException(
                "Azure Resource Manager consent is required.",
                remediation: "Rerun interactively for ARM consent.",
                resource: InteractionRequiredResource.AzureResourceManager));
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<AppRegistrationIdentityProvisioningException>(() =>
            service.ExecuteAsync(
                ManagedIdentityRequest(IdentityCredentialType.UserManagedIdentity),
                default));

        Assert.Contains("Azure Resource Manager", error.Message);
        Assert.Equal("Rerun interactively for ARM consent.", error.Remediation);
        Assert.Equal("partial", error.PartialReport.Status);
        Assert.Equal(AppClientId, error.PartialReport.ApplicationId);
        Assert.DoesNotContain(
            error.PartialReport.Changes,
            change => change.Resource == "Consent" && change.Action == "required");
        Assert.Null(error.PartialReport.Configuration);
    }

    [Fact]
    public async Task FederatedCredentialFailure_PartialReportPreservesManagedIdentityCoordinates()
    {
        var rest = new ScriptedIdentityRestClient(
            NewAppGraph(federationStatus: 403),
            ManagedIdentityArm());
        var service = new AppRegistrationIdentityService(rest, retryDelay: TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<AppRegistrationIdentityProvisioningException>(() =>
            service.ExecuteAsync(
                ManagedIdentityRequest(IdentityCredentialType.FederatedCredentials),
                default));

        Assert.Equal("mi-client-id", error.PartialReport.ManagedIdentityClientId);
        Assert.Equal("mi-principal-id", error.PartialReport.ManagedIdentityPrincipalId);
        Assert.Equal("mi-resource-id", error.PartialReport.ManagedIdentityResourceId);
        Assert.Null(error.PartialReport.Configuration);
    }

    private static AppRegistrationIdentityRequest Request() => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111",
        DisplayName = "Contoso Agent",
        AuthType = IdentityCredentialType.ClientSecret,
        ConfigFormat = IdentityConfigFormat.DotNet,
    };

    private static AppRegistrationIdentityRequest ManagedIdentityRequest(IdentityCredentialType authType) =>
        Request() with
        {
            AuthType = authType,
            SubscriptionId = "33333333-3333-3333-3333-333333333333",
            ResourceGroup = "rg-agents",
            Location = "eastus",
            ManagedIdentityName = "agent-mi",
        };

    private static Func<IdentityRestCall, IdentityRestResponse> ManagedIdentityArm(bool existing = false) => call => call.Method switch
    {
        "GET" => existing
            ? new IdentityRestResponse(200, """
                {"id":"mi-resource-id","properties":{"clientId":"mi-client-id","principalId":"mi-principal-id"}}
                """)
            : new IdentityRestResponse(404, """{"error":{"code":"NotFound"}}"""),
        "PUT" => new IdentityRestResponse(200, """
            {"id":"mi-resource-id","properties":{"clientId":"mi-client-id","principalId":"mi-principal-id"}}
            """),
        _ => throw new Xunit.Sdk.XunitException($"Unexpected ARM call: {call.Method} {call.Path}"),
    };

    private static Func<IdentityRestCall, IdentityRestResponse> NewAppGraph(
        bool existing = false,
        bool missingResourceSp = false,
        bool staleFederation = false,
        int freshFederationStatus = 201,
        int federationStatus = 204,
        string signInAudience = AppRegistrationIdentityPlan.SignInAudience) => call =>
    {
        if (call.Method == "GET" && call.Path.StartsWith("/v1.0/applications?$filter=", StringComparison.Ordinal))
        {
            return existing
                ? new IdentityRestResponse(200, $$"""{"value":[{"id":"{{AppObjectId}}"}]}""")
                : new IdentityRestResponse(200, """{"value":[]}""");
        }

        if (call.Method == "GET" && call.Path == "/v1.0/me?$select=id")
        {
            return new IdentityRestResponse(200, """{"id":"owner-id"}""");
        }

        if (call.Method == "POST" && call.Path == "/v1.0/applications")
        {
            return new IdentityRestResponse(201, ApplicationBody(hasUsableSecret: false));
        }

        if (call.Method == "GET" &&
            call.Path.StartsWith($"/v1.0/applications/{AppObjectId}?", StringComparison.Ordinal))
        {
            return new IdentityRestResponse(
                200,
                ApplicationBody(hasUsableSecret: false, signInAudience));
        }

        if (call.Method == "GET" && call.Path.Contains("servicePrincipals?", StringComparison.OrdinalIgnoreCase))
        {
            if (call.Path.Contains(AppClientId, StringComparison.Ordinal))
            {
                return existing
                    ? new IdentityRestResponse(200, $$"""{"value":[{"id":"{{AppSpId}}"}]}""")
                    : new IdentityRestResponse(200, """{"value":[]}""");
            }

            if (missingResourceSp)
            {
                return new IdentityRestResponse(200, """{"value":[]}""");
            }

            return ResourceServicePrincipal(call.Path);
        }

        if (call.Method == "POST" && call.Path == "/v1.0/servicePrincipals")
        {
            return new IdentityRestResponse(201, $$"""{"id":"{{AppSpId}}"}""");
        }

        if (call.Method == "PATCH" && call.Path == $"/v1.0/applications/{AppObjectId}")
        {
            return new IdentityRestResponse(204, null);
        }

        if (call.Method == "PATCH" &&
            call.Path.StartsWith(
                $"/v1.0/applications/{AppObjectId}/federatedIdentityCredentials(",
                StringComparison.Ordinal))
        {
            if (federationStatus != 204)
            {
                return new IdentityRestResponse(
                    federationStatus,
                    """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges."}}""");
            }

            // Graph may return either 201 or 204 for a successful create-if-missing upsert.
            return existing
                ? new IdentityRestResponse(204, null)
                : new IdentityRestResponse(freshFederationStatus, null);
        }

        if (call.Method == "GET" &&
            call.Path.StartsWith(
                $"/v1.0/applications/{AppObjectId}/federatedIdentityCredentials/",
                StringComparison.Ordinal))
        {
            return existing
                ? new IdentityRestResponse(200, $$"""
                    {
                      "issuer":"https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0",
                      "subject":"{{(staleFederation ? "stale-principal-id" : "mi-principal-id")}}",
                      "audiences":["api://AzureADTokenExchange"]
                    }
                    """)
                : new IdentityRestResponse(404, """{"error":{"code":"Request_ResourceNotFound"}}""");
        }

        if (call.Method == "POST" && call.Path == $"/v1.0/applications/{AppObjectId}/addPassword")
        {
            return new IdentityRestResponse(200, """
                {"secretText":"one-time-secret","endDateTime":"2027-07-31T00:00:00Z"}
                """);
        }

        if (call.Method == "GET" &&
            call.Path == $"/v1.0/servicePrincipals/{AppSpId}/appRoleAssignments")
        {
            return new IdentityRestResponse(200, """{"value":[]}""");
        }

        if (call.Method == "POST" &&
            call.Path == $"/v1.0/servicePrincipals/{AppSpId}/appRoleAssignments")
        {
            return new IdentityRestResponse(201, """{"id":"assignment-id"}""");
        }

        throw new Xunit.Sdk.XunitException($"Unexpected REST call: {call.Method} {call.Path}");
    };

    private static Func<IdentityRestCall, IdentityRestResponse> ConvergedAppGraph() => call =>
    {
        if (call.Method == "GET" && call.Path.StartsWith("/v1.0/applications?$filter=", StringComparison.Ordinal))
        {
            return new IdentityRestResponse(200, $$"""{"value":[{"id":"{{AppObjectId}}"}]}""");
        }

        if (call.Method == "GET" &&
            call.Path.StartsWith($"/v1.0/applications/{AppObjectId}?", StringComparison.Ordinal))
        {
            return new IdentityRestResponse(200, ConvergedApplicationBody());
        }

        if (call.Method == "GET" && call.Path.Contains("servicePrincipals?", StringComparison.OrdinalIgnoreCase))
        {
            return call.Path.Contains(AppClientId, StringComparison.Ordinal)
                ? new IdentityRestResponse(200, $$"""{"value":[{"id":"{{AppSpId}}"}]}""")
                : ResourceServicePrincipal(call.Path);
        }

        if (call.Method == "GET" &&
            call.Path == $"/v1.0/servicePrincipals/{AppSpId}/appRoleAssignments")
        {
            return new IdentityRestResponse(200, """
                {"value":[{"resourceId":"Observability-sp","appRoleId":"Observability-role"}]}
                """);
        }

        throw new Xunit.Sdk.XunitException($"Unexpected REST call: {call.Method} {call.Path}");
    };

    private static IdentityRestResponse ResourceServicePrincipal(string path)
    {
        var permission = ResolvePermission(path);
        return new IdentityRestResponse(200, $$"""
            {"value":[{"id":"{{permission.Name}}-sp","oauth2PermissionScopes":[
              {"id":"{{permission.Name}}-scope","value":"{{permission.DelegatedScope}}","isEnabled":true}
            ],"appRoles":[
              {"id":"{{permission.Name}}-role","value":"{{permission.ApplicationRole}}","isEnabled":true}
            ]}]}
            """);
    }

    private static string ApplicationBody(
        bool hasUsableSecret,
        string signInAudience = AppRegistrationIdentityPlan.SignInAudience)
    {
        var passwords = hasUsableSecret
            ? ""","passwordCredentials":[{"displayName":"AgentHost CLI","endDateTime":"2099-01-01T00:00:00Z"}]"""
            : string.Empty;
        return $$"""
            {"id":"{{AppObjectId}}","appId":"{{AppClientId}}","displayName":"Contoso Agent","signInAudience":"{{signInAudience}}"{{passwords}}}
            """;
    }

    private static string ConvergedApplicationBody()
    {
        var resources = new[]
        {
            IdentityProvisioningConfig.MessagingBotPermission,
            IdentityProvisioningConfig.PowerPlatformPermission,
            IdentityProvisioningConfig.ObservabilityPermission,
            IdentityProvisioningConfig.McpDiscoveryPermission,
        };
        var requiredResourceAccess = string.Join(",", resources.Select(permission =>
        {
            var access = permission.ApplicationRole is null
                ? $$"""{"id":"{{permission.Name}}-scope","type":"Scope"}"""
                : $$"""{"id":"{{permission.Name}}-scope","type":"Scope"},{"id":"{{permission.Name}}-role","type":"Role"}""";
            return $$"""{"resourceAppId":"{{permission.ResourceAppId}}","resourceAccess":[{{access}}]}""";
        }));
        return $$"""
            {
              "id":"{{AppObjectId}}",
              "appId":"{{AppClientId}}",
              "displayName":"Contoso Agent",
              "signInAudience":"{{AppRegistrationIdentityPlan.SignInAudience}}",
              "passwordCredentials":[{"displayName":"AgentHost CLI","endDateTime":"2099-01-01T00:00:00Z"}],
              "identifierUris":["api://botid-{{AppClientId}}"],
              "web":{"redirectUris":[
                "{{AppRegistrationIdentityPlan.DefaultRedirectUri}}",
                "{{BlueprintAdminConsent.RedirectUri}}"
              ]},
              "requiredResourceAccess":[{{requiredResourceAccess}}],
              "api":{
                "requestedAccessTokenVersion":2,
                "oauth2PermissionScopes":[{"id":"access-scope-id","value":"access_as_user","isEnabled":true}],
                "preAuthorizedApplications":[
                  {"appId":"{{IdentityProvisioningConfig.TeamsHostApplicationIds[0]}}","delegatedPermissionIds":["access-scope-id"]},
                  {"appId":"{{IdentityProvisioningConfig.TeamsHostApplicationIds[1]}}","delegatedPermissionIds":["access-scope-id"]}
                ]
              }
            }
            """;
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
        return permissions.Single(permission =>
            path.Contains(permission.ResourceAppId, StringComparison.Ordinal));
    }
}
