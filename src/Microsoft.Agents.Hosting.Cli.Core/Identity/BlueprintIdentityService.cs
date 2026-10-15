using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using static Microsoft.Agents.Hosting.Cli.Core.Identity.BlueprintIdentityJson;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

/// <summary>
/// Converges an Agent Identity Blueprint and its credentials, permissions, OAuth, and Teams SSO
/// settings over direct Microsoft Graph and ARM REST.
/// </summary>
public sealed class BlueprintIdentityService
{
    private const string AccessAsUser = "access_as_user";
    private readonly IIdentityRestClient _rest;
    private readonly IdentityGraphClient _graph;
    private readonly BlueprintPermissionReconciler _permissions;
    private readonly BlueprintCredentialReconciler _credentials;
    private readonly TimeSpan _retryDelay;
    private readonly ArmConsentClientConfiguration _armConsentClient;

    public BlueprintIdentityService(
        IIdentityRestClient rest,
        TimeSpan? retryDelay = null,
        ArmConsentClientConfiguration? armConsentClient = null)
    {
        _rest = rest ?? throw new ArgumentNullException(nameof(rest));
        _graph = new IdentityGraphClient(rest);
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(8);
        _permissions = new BlueprintPermissionReconciler(_graph, _retryDelay);
        _credentials = new BlueprintCredentialReconciler(rest, _graph);
        _armConsentClient = armConsentClient ?? ArmConsentClientConfiguration.Default;
    }

    public async Task<BlueprintIdentityReport> ExecuteAsync(
        BlueprintIdentityRequest request,
        CancellationToken cancellationToken)
    {
        var plan = BlueprintIdentityPlan.Build(request);
        if (request.DryRun)
        {
            return BuildDryRunReport(plan, _armConsentClient);
        }

        var changes = new List<IdentityChange>();
        var nextActions = new List<string>();
        BlueprintApplication? application = null;
        string? principalId = null;
        string? adminConsentUrl = null;
        var currentStage = "Blueprint discovery";
        try
        {
            application = await FindBlueprintAsync(request, cancellationToken).ConfigureAwait(false);
            var created = application is null;
            if (application is null)
            {
                application = await CreateBlueprintAsync(request, cancellationToken).ConfigureAwait(false);
                changes.Add(Change("Blueprint", "create", $"Created Blueprint application {application.AppId}."));
            }
            else
            {
                changes.Add(Change("Blueprint", "noop", $"Reused Blueprint application {application.AppId}."));
            }

            currentStage = "Blueprint principal reconciliation";
            principalId = await EnsureBlueprintPrincipalAsync(
                request.TenantId,
                application.AppId,
                cancellationToken).ConfigureAwait(false);
            changes.Add(Change("BlueprintPrincipal", "ensure", $"Blueprint principal {principalId} is available."));

            currentStage = "permission resolution";
            var resolvedPermissions = new List<ResolvedBlueprintPermission>();
            foreach (var permission in plan.Permissions)
            {
                resolvedPermissions.Add(await _permissions.ResolveAsync(
                    request.TenantId,
                    permission,
                    cancellationToken).ConfigureAwait(false));
            }

            currentStage = "OAuth and SSO reconciliation";
            var applicationChanged = await ReconcileApplicationAsync(
                request,
                plan,
                application,
                cancellationToken).ConfigureAwait(false);
            changes.Add(applicationChanged
                ? Change("Application", "reconcile", "Reconciled API, redirect, and SSO settings.")
                : Change("Application", "noop", "API, redirect, and SSO settings already satisfy the request."));

            foreach (var permission in resolvedPermissions)
            {
                currentStage = $"{permission.Spec.Name} permission reconciliation";
                var permissionResult = await _permissions.EnsureAsync(
                    request.TenantId,
                    principalId,
                    application.ObjectId,
                    permission,
                    cancellationToken).ConfigureAwait(false);
                changes.Add(Change(
                    permission.Spec.Name,
                    "ensure",
                    $"Configured inheritance for {permission.Spec.DelegatedScope}."));
                if (permissionResult.AppRoleAssignmentRequiresAdministrator)
                {
                    changes.Add(Change(
                        $"{permission.Spec.Name}AppRole",
                        "handoff",
                        $"Application role {permission.Spec.ApplicationRole} requires administrator assignment."));
                    nextActions.Add(
                        "Ask an Application Administrator or Global Administrator to assign " +
                        $"application role {permission.Spec.ApplicationRole} from resource application " +
                        $"{permission.Spec.ResourceAppId} to Blueprint principal {principalId}.");
                }
            }

            adminConsentUrl = BlueprintAdminConsent.BuildUrl(
                request.TenantId,
                application.AppId,
                plan.Permissions);
            changes.Add(Change(
                "DelegatedPermissions",
                "consent",
                "Prepared a tenant-wide delegated admin-consent handoff."));
            nextActions.Add(
                $"Open this URL as a Global Administrator to grant tenant-wide delegated permissions: {adminConsentUrl}");

            currentStage = $"{request.AuthType} credential reconciliation";
            string? secret = null;
            string? federatedClientId = null;
            if (request.AuthType == IdentityCredentialType.ClientSecret)
            {
                if (application.HasUsableClientSecret)
                {
                    nextActions.Add(
                        "An existing AgentHost CLI client secret was retained; its value cannot be retrieved. Use your stored value or rotate it separately.");
                }
                else
                {
                    secret = await _credentials.AddClientSecretAsync(
                        request.TenantId,
                        application.ObjectId,
                        cancellationToken).ConfigureAwait(false);
                    changes.Add(Change("ClientSecret", "create", "Created a one-time client secret."));
                }
            }
            else
            {
                federatedClientId = await _credentials.EnsureFederatedCredentialAsync(
                    plan,
                    application.ObjectId,
                    cancellationToken).ConfigureAwait(false);
                changes.Add(Change("FederatedCredentials", "ensure", "Configured managed-identity federation."));
            }

            var configuration = IdentityConfigurationRenderer.Render(
                request,
                application.AppId,
                federatedClientId);
            if (secret is not null)
            {
                nextActions.Add(
                    "Store the emitted one-time client secret in an approved secret store, then inject it into runtime configuration; it is not persisted and cannot be shown again.");
            }

            return new BlueprintIdentityReport
            {
                Status = created ? "created" : "reconciled",
                TenantId = request.TenantId,
                DisplayName = request.DisplayName,
                ApplicationId = application.AppId,
                ApplicationObjectId = application.ObjectId,
                ServicePrincipalObjectId = principalId,
                AdminConsentUrl = adminConsentUrl,
                OneTimeClientSecret = secret,
                Changes = changes,
                NextActions = nextActions,
                Configuration = configuration,
            };
        }
        catch (AgentHostingException error) when (application is not null)
        {
            throw new BlueprintIdentityProvisioningException(
                error,
                BuildPartialReport(
                    request,
                    application,
                    principalId,
                    adminConsentUrl,
                    changes,
                    nextActions,
                    currentStage));
        }
    }

    private static BlueprintIdentityReport BuildPartialReport(
        BlueprintIdentityRequest request,
        BlueprintApplication application,
        string? principalId,
        string? adminConsentUrl,
        IReadOnlyList<IdentityChange> changes,
        IReadOnlyList<string> nextActions,
        string currentStage) =>
        new()
        {
            Status = "partial",
            TenantId = request.TenantId,
            DisplayName = request.DisplayName,
            ApplicationId = application.AppId,
            ApplicationObjectId = application.ObjectId,
            ServicePrincipalObjectId = principalId,
            AdminConsentUrl = adminConsentUrl,
            Changes = [.. changes],
            NextActions =
            [
                .. nextActions,
                $"Resolve the {currentStage} failure, then rerun the command; existing resources will be reused.",
            ],
        };

    private static BlueprintIdentityReport BuildDryRunReport(
        BlueprintIdentityPlan plan,
        ArmConsentClientConfiguration armConsentClient)
    {
        var changes = new List<IdentityChange>
        {
            Change("Blueprint", "ensure", $"Create or reconcile \"{plan.Request.DisplayName}\"."),
            Change("BlueprintPrincipal", "ensure", "Create or reconcile the Blueprint principal."),
        };
        changes.AddRange(plan.Permissions.Select(permission =>
            Change(permission.Name, "ensure", $"Make {permission.DelegatedScope} inheritable.")));
        foreach (var permission in plan.Permissions.Where(permission => permission.ApplicationRole is not null))
        {
            changes.Add(Change(
                $"{permission.Name}AppRole",
                "ensure",
                $"Make application role {permission.ApplicationRole} inheritable."));
            changes.Add(Change(
                $"{permission.Name}AppRoleAssignment",
                "ensure",
                $"Verify or grant application role {permission.ApplicationRole} to the Blueprint principal."));
        }

        changes.Add(Change(
            "DelegatedPermissions",
            "consent",
            "Prepare a tenant-wide delegated admin-consent handoff."));
        changes.Add(Change(
            "AdminConsentRedirect",
            "ensure",
            $"Register the admin-consent callback {BlueprintAdminConsent.RedirectUri}."));
        if (plan.RedirectUri is not null)
        {
            changes.Add(Change("OAuth", "ensure", $"Add redirect URI {plan.RedirectUri}."));
        }

        if (plan.TeamsSso)
        {
            changes.Add(Change("TeamsSso", "ensure", "Expose access_as_user and preauthorize supported clients."));
        }

        if (plan.ManagedIdentity is not null)
        {
            changes.Add(Change(
                "ManagedIdentity",
                "ensure",
                $"Create or reconcile user-assigned managed identity {plan.ManagedIdentity.Name} in resource group " +
                $"{plan.ManagedIdentity.ResourceGroup}, subscription {plan.ManagedIdentity.SubscriptionId}, " +
                $"location {plan.ManagedIdentity.Location}."));
            changes.Add(Change(
                "ArmClient",
                "select",
                "Acquire Azure Resource Manager tokens with consent-capable client " +
                $"{armConsentClient.ClientId} instead of the CLI first-party app (delegated " +
                "consent); consented on first use."));
        }

        changes.Add(Change(plan.Request.AuthType.ToString(), "ensure", "Create or reconcile the selected credential."));
        return new BlueprintIdentityReport
        {
            Status = "planned",
            TenantId = plan.Request.TenantId,
            DisplayName = plan.Request.DisplayName,
            Changes = changes,
            NextActions = ["Run without --dry-run to apply this plan."],
        };
    }

    private async Task<BlueprintApplication?> FindBlueprintAsync(
        BlueprintIdentityRequest request,
        CancellationToken cancellationToken)
    {
        var escapedName = request.DisplayName.Replace("'", "''", StringComparison.Ordinal);
        var path = "/beta/applications/microsoft.graph.agentIdentityBlueprint?$filter=" +
            Uri.EscapeDataString($"displayName eq '{escapedName}'") +
            "&$select=id,appId,displayName,identifierUris,api,web,passwordCredentials";
        var values = await _graph.ReadCollectionAsync(
            path,
            [IdentityProvisioningConfig.GraphReadWriteScope],
            request.TenantId,
            cancellationToken).ConfigureAwait(false);
        if (values.Count > 1)
        {
            throw new ProvisioningException(
                ProvisioningErrorKind.NamingConflict,
                $"More than one Entra application is named \"{request.DisplayName}\".",
                "Use a unique --display-name or remove the duplicate applications.");
        }

        if (values.Count == 0)
        {
            return null;
        }

        var objectId = RequireString(values[0], "id", "Blueprint application");
        var response = await _graph.SendAsync(
            HttpMethod.Get,
            $"/v1.0/applications/{objectId}" +
                "?$select=id,appId,displayName,identifierUris,api,web,passwordCredentials",
            body: null,
            [IdentityProvisioningConfig.GraphApplicationReadScope],
            request.TenantId,
            cancellationToken).ConfigureAwait(false);
        return BlueprintApplication.From(ParseObject(response.Body));
    }

    private async Task<BlueprintApplication> CreateBlueprintAsync(
        BlueprintIdentityRequest request,
        CancellationToken cancellationToken)
    {
        string? ownerId = null;
        var ownerResponse = await _graph.SendAsync(
            HttpMethod.Get,
            "/v1.0/me?$select=id",
            body: null,
            [IdentityProvisioningConfig.GraphUserReadScope],
            request.TenantId,
            cancellationToken).ConfigureAwait(false);
        ownerId = ReadString(ParseObject(ownerResponse.Body), "id");

        var payload = new JsonObject
        {
            ["@odata.type"] = "Microsoft.Graph.AgentIdentityBlueprint",
            ["displayName"] = request.DisplayName,
            ["signInAudience"] = "AzureADMultipleOrgs",
            ["managerApplications"] = new JsonArray(IdentityProvisioningConfig.ManagerApplicationId),
        };
        if (ownerId is not null)
        {
            var ownerUrl = $"{IdentityProvisioningConfig.GraphEndpoint}/v1.0/users/{ownerId}";
            payload["owners@odata.bind"] = new JsonArray(ownerUrl);
            payload["sponsors@odata.bind"] = new JsonArray(ownerUrl);
        }

        var response = await _graph.SendAsync(
            HttpMethod.Post,
            "/beta/applications/microsoft.graph.agentIdentityBlueprint",
            payload,
            [IdentityProvisioningConfig.GraphReadWriteScope],
            request.TenantId,
            cancellationToken,
            new Dictionary<string, string>
            {
                ["OData-Version"] = "4.0",
                ["ConsistencyLevel"] = "eventual",
            }).ConfigureAwait(false);
        return BlueprintApplication.From(ParseObject(response.Body));
    }

    private async Task<string> EnsureBlueprintPrincipalAsync(
        string tenantId,
        string appId,
        CancellationToken cancellationToken)
    {
        var query = "/v1.0/servicePrincipals?$filter=" +
            Uri.EscapeDataString($"appId eq '{appId}'") +
            "&$select=id";
        var values = await _graph.ReadCollectionAsync(
            query,
            [IdentityProvisioningConfig.GraphApplicationReadScope],
            tenantId,
            cancellationToken).ConfigureAwait(false);
        if (values.Count > 0)
        {
            return RequireString(values[0], "id", "Blueprint principal lookup");
        }

        var ownerPropagationRetries = 0;
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var response = await _rest.SendGraphAsync(
                HttpMethod.Post,
                "/v1.0/serviceprincipals/graph.agentIdentityBlueprintPrincipal",
                new { appId },
                [IdentityProvisioningConfig.GraphPrincipalCreateScope],
                tenantId,
                cancellationToken).ConfigureAwait(false);
            if (response.IsSuccess)
            {
                return RequireString(ParseObject(response.Body), "id", "Blueprint principal create");
            }

            var retryOwnerPropagation = response.StatusCode == 403 &&
                IsOwnerPropagationDelay(response.Body) &&
                ownerPropagationRetries++ < 3;
            if ((response.StatusCode != 400 && !retryOwnerPropagation) || attempt == 10)
            {
                ThrowTarget(response, HttpMethod.Post, "/v1.0/serviceprincipals/graph.agentIdentityBlueprintPrincipal", "Graph");
            }

            await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Blueprint principal retry loop completed unexpectedly.");
    }

    private static bool IsOwnerPropagationDelay(string? responseBody)
    {
        var (code, message) = TargetRequestException.ParseTargetError(responseBody);
        return string.Equals(code, "Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase) &&
            message?.Contains("backing application", StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task<bool> ReconcileApplicationAsync(
        BlueprintIdentityRequest request,
        BlueprintIdentityPlan plan,
        BlueprintApplication application,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject();
        JsonObject? followUpBody = null;
        var redirects = application.RedirectUris.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (plan.RedirectUri is not null)
        {
            redirects.Add(plan.RedirectUri);
        }

        redirects.Add(BlueprintAdminConsent.RedirectUri);
        if (!redirects.SetEquals(application.RedirectUris))
        {
            body["web"] = new JsonObject
            {
                ["redirectUris"] = new JsonArray(redirects.Order(StringComparer.OrdinalIgnoreCase)
                    .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            };
        }

        if (plan.TeamsSso)
        {
            var apiBody = new JsonObject();
            var scopes = application.OAuthScopes.DeepClone().AsArray();
            var accessScope = scopes
                .OfType<JsonObject>()
                .FirstOrDefault(scope => string.Equals(
                    ReadString(scope, "value"),
                    AccessAsUser,
                    StringComparison.OrdinalIgnoreCase));
            var scopesChanged = false;
            var scopeId = accessScope is null
                ? Guid.NewGuid().ToString()
                : RequireString(accessScope, "id", AccessAsUser);
            if (accessScope is null)
            {
                scopes.Add(new JsonObject
                {
                    ["id"] = scopeId,
                    ["value"] = AccessAsUser,
                    ["type"] = "User",
                    ["isEnabled"] = true,
                    ["adminConsentDisplayName"] = "Access this agent as the signed-in user",
                    ["adminConsentDescription"] = "Allows supported hosts to access this agent as the signed-in user.",
                    ["userConsentDisplayName"] = "Access this agent",
                    ["userConsentDescription"] = "Allows this host to access this agent on your behalf.",
                });
                scopesChanged = true;
            }
            else if (accessScope["isEnabled"]?.GetValue<bool>() != true)
            {
                accessScope["isEnabled"] = true;
                scopesChanged = true;
            }

            var preauthorized = MergePreauthorizedApplications(application.PreAuthorizedApplications, scopeId);
            var preauthorizedChanged = !JsonNode.DeepEquals(
                preauthorized,
                application.PreAuthorizedApplications);
            var identifierUris = application.IdentifierUris.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (identifierUris.Add($"api://botid-{application.AppId}"))
            {
                body["identifierUris"] = new JsonArray(
                    identifierUris
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Select(value => (JsonNode?)JsonValue.Create(value))
                    .ToArray());
            }

            if (scopesChanged)
            {
                apiBody["oauth2PermissionScopes"] = scopes;
            }

            if (application.RequestedAccessTokenVersion != 2)
            {
                apiBody["requestedAccessTokenVersion"] = 2;
            }

            if (preauthorizedChanged)
            {
                var preauthorizationBody = new JsonObject
                {
                    ["preAuthorizedApplications"] = preauthorized,
                };
                if (scopesChanged)
                {
                    followUpBody = new JsonObject
                    {
                        ["api"] = preauthorizationBody,
                    };
                }
                else
                {
                    apiBody["preAuthorizedApplications"] = preauthorized;
                }
            }

            if (apiBody.Count > 0)
            {
                body["api"] = apiBody;
            }
        }

        var changed = false;
        if (body.Count > 0)
        {
            await PatchApplicationAsync(
                request.TenantId,
                application.ObjectId,
                body,
                cancellationToken).ConfigureAwait(false);
            changed = true;
        }

        if (followUpBody is not null)
        {
            await PatchApplicationAsync(
                request.TenantId,
                application.ObjectId,
                followUpBody,
                cancellationToken).ConfigureAwait(false);
            changed = true;
        }

        return changed;
    }

    private Task<IdentityRestResponse> PatchApplicationAsync(
        string tenantId,
        string applicationObjectId,
        JsonObject body,
        CancellationToken cancellationToken) =>
        _graph.SendAsync(
            HttpMethod.Patch,
            $"/v1.0/applications/{applicationObjectId}",
            body,
            [IdentityProvisioningConfig.GraphReadWriteScope],
            tenantId,
            cancellationToken);

    private static void ThrowTarget(
        IdentityRestResponse response,
        HttpMethod method,
        string path,
        string audience) =>
        throw TargetRequestException.MapTargetResponseError(
            response.StatusCode,
            response.Body,
            method.Method,
            path,
            audience,
            "msagent");

    private static JsonArray MergePreauthorizedApplications(JsonArray existing, string scopeId)
    {
        var result = existing.DeepClone().AsArray();
        foreach (var clientId in IdentityProvisioningConfig.TeamsHostApplicationIds)
        {
            var client = result.OfType<JsonObject>().FirstOrDefault(item =>
                string.Equals(ReadString(item, "appId"), clientId, StringComparison.OrdinalIgnoreCase));
            if (client is null)
            {
                result.Add(new JsonObject
                {
                    ["appId"] = clientId,
                    ["delegatedPermissionIds"] = new JsonArray(scopeId),
                });
                continue;
            }

            var ids = client["delegatedPermissionIds"]?.AsArray() ?? new JsonArray();
            client["delegatedPermissionIds"] = ids;
            if (!ids.Any(id => string.Equals(id?.GetValue<string>(), scopeId, StringComparison.OrdinalIgnoreCase)))
            {
                ids.Add(scopeId);
            }
        }

        return result;
    }

    private static IdentityChange Change(string resource, string action, string detail) => new()
    {
        Resource = resource,
        Action = action,
        Detail = detail,
    };

    private sealed record BlueprintApplication
    {
        public required string ObjectId { get; init; }
        public required string AppId { get; init; }
        public required JsonArray OAuthScopes { get; init; }
        public required JsonArray PreAuthorizedApplications { get; init; }
        public required IReadOnlyList<string> IdentifierUris { get; init; }
        public required IReadOnlyList<string> RedirectUris { get; init; }
        public int? RequestedAccessTokenVersion { get; init; }
        public bool HasUsableClientSecret { get; init; }

        public static BlueprintApplication From(JsonObject value)
        {
            var now = DateTimeOffset.UtcNow;
            var passwords = value["passwordCredentials"]?.AsArray() ?? [];
            return new BlueprintApplication
            {
                ObjectId = RequireString(value, "id", "Blueprint application"),
                AppId = RequireString(value, "appId", "Blueprint application"),
                OAuthScopes = value["api"]?["oauth2PermissionScopes"]?.DeepClone().AsArray() ?? [],
                PreAuthorizedApplications =
                    value["api"]?["preAuthorizedApplications"]?.DeepClone().AsArray() ?? [],
                IdentifierUris = ReadStrings(value["identifierUris"]?.AsArray()),
                RedirectUris = ReadStrings(value["web"]?["redirectUris"]?.AsArray()),
                RequestedAccessTokenVersion = value["api"]?["requestedAccessTokenVersion"]?.GetValue<int>(),
                HasUsableClientSecret = passwords.OfType<JsonObject>().Any(password =>
                    string.Equals(
                        ReadString(password, "displayName"),
                        BlueprintCredentialReconciler.SecretDisplayName,
                        StringComparison.Ordinal) &&
                    DateTimeOffset.TryParse(ReadString(password, "endDateTime"), out var expiry) &&
                    expiry > now.AddDays(1)),
            };
        }

        private static string[] ReadStrings(JsonArray? values) =>
            values?.Select(value => value?.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray() ?? [];
    }
}
