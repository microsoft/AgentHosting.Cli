using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using static Microsoft.Agents.Hosting.Cli.Core.Identity.BlueprintIdentityJson;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

/// <summary>
/// Converges an ordinary App Registration and its credentials, API permissions, OAuth redirect, and
/// Teams SSO settings over direct Microsoft Graph and ARM REST. Unlike
/// <see cref="BlueprintIdentityService"/> it targets standard <c>/v1.0/applications</c> routes, accepts
/// a user-assigned managed identity as the runtime credential, and declares bot permissions through
/// <c>requiredResourceAccess</c> rather than Blueprint inheritance.
/// </summary>
public sealed class AppRegistrationIdentityService
{
    private const string AccessAsUser = "access_as_user";
    private readonly IdentityGraphClient _graph;
    private readonly AppRegistrationApplicationReconciler _applications;
    private readonly AppRegistrationPermissionReconciler _permissions;
    private readonly AppRegistrationCredentialReconciler _credentials;
    private readonly GraphConsentClientConfiguration _graphConsentClient;
    private readonly ArmConsentClientConfiguration _armConsentClient;

    public AppRegistrationIdentityService(
        IIdentityRestClient rest,
        TimeSpan? retryDelay = null,
        GraphConsentClientConfiguration? graphConsentClient = null,
        ArmConsentClientConfiguration? armConsentClient = null)
    {
        ArgumentNullException.ThrowIfNull(rest);
        _graphConsentClient = graphConsentClient ?? GraphConsentClientConfiguration.Default;
        _armConsentClient = armConsentClient ?? ArmConsentClientConfiguration.Default;
        _graph = new IdentityGraphClient(rest);
        var propagationRetryDelay = retryDelay ?? TimeSpan.FromSeconds(8);
        _applications = new AppRegistrationApplicationReconciler(
            rest,
            _graph,
            propagationRetryDelay);
        _permissions = new AppRegistrationPermissionReconciler(_graph, propagationRetryDelay);
        _credentials = new AppRegistrationCredentialReconciler(rest, _graph);
    }

    public async Task<AppRegistrationIdentityReport> ExecuteAsync(
        AppRegistrationIdentityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = AppRegistrationIdentityPlan.Build(request, _graphConsentClient);
        if (request.DryRun)
        {
            return BuildDryRunReport(plan, _armConsentClient);
        }

        var changes = new List<IdentityChange>();
        var nextActions = new List<string>();
        AppRegistrationApplication? application = null;
        string? principalId = null;
        string? adminConsentUrl = null;
        ProvisionedManagedIdentity? managedIdentity = null;
        var currentStage = "application discovery";
        try
        {
            application = await _applications.FindAsync(plan, cancellationToken).ConfigureAwait(false);
            var created = application is null;
            if (application is null)
            {
                application = await _applications.CreateAsync(request, cancellationToken).ConfigureAwait(false);
                changes.Add(Change("Application", "create", $"Created App Registration {application.AppId}."));
            }
            else
            {
                changes.Add(Change("Application", "noop", $"Reused App Registration {application.AppId}."));
            }

            currentStage = "service principal reconciliation";
            var servicePrincipal = await _applications.EnsureServicePrincipalAsync(
                request.TenantId,
                application.AppId,
                cancellationToken).ConfigureAwait(false);
            principalId = servicePrincipal.ObjectId;
            changes.Add(servicePrincipal.Created
                ? Change("ServicePrincipal", "create", $"Created service principal {principalId}.")
                : Change("ServicePrincipal", "noop", $"Reused service principal {principalId}."));

            currentStage = "API permission reconciliation";
            var permissionResult = await _permissions.EnsureAsync(
                plan,
                application,
                principalId,
                cancellationToken).ConfigureAwait(false);
            changes.Add(permissionResult.DeclarationsChanged
                ? Change("ApiPermissions", "reconcile", "Declared bot API permissions on requiredResourceAccess.")
                : Change("ApiPermissions", "noop", "Requested API permissions already declared."));
            changes.AddRange(permissionResult.AppRoleAssignments.Select(assignment =>
                assignment.Changed
                    ? Change(
                        $"{assignment.Permission.Name}AppRole",
                        "grant",
                        $"Granted application role {assignment.Permission.ApplicationRole}.")
                    : Change(
                        $"{assignment.Permission.Name}AppRole",
                        "noop",
                        $"Application role {assignment.Permission.ApplicationRole} is already granted.")));

            currentStage = "OAuth and SSO reconciliation";
            var applicationChanged = await ReconcileApplicationAsync(
                plan,
                application,
                cancellationToken).ConfigureAwait(false);
            changes.Add(applicationChanged
                ? Change("Application", "reconcile", "Reconciled redirect and SSO settings.")
                : Change("Application", "noop", "Redirect and SSO settings already satisfy the request."));

            adminConsentUrl = BlueprintAdminConsent.BuildUrl(
                request.TenantId,
                application.AppId,
                plan.Permissions);
            changes.Add(Change(
                "Permissions",
                "consent",
                "Prepared a tenant-wide admin-consent handoff."));
            nextActions.Add(
                $"Open this URL as a Global Administrator to grant the declared permissions: {adminConsentUrl}");

            currentStage = $"{request.AuthType} credential reconciliation";
            var credential = await _credentials.EnsureAsync(
                plan,
                application,
                cancellationToken).ConfigureAwait(false);
            managedIdentity = credential.ManagedIdentity;
            changes.Add(DescribeCredentialChange(request.AuthType, credential));
            if (credential.RetainedExistingSecret)
            {
                nextActions.Add(
                    "An existing AgentHost CLI client secret was retained; its value cannot be retrieved. Use your stored value or rotate it separately.");
            }

            var configuration = IdentityConfigurationRenderer.Render(
                request,
                application.AppId,
                credential.FederatedClientId,
                managedIdentity?.ClientId);
            if (credential.Secret is not null)
            {
                nextActions.Add(
                    "Store the emitted one-time client secret in an approved secret store, then inject it into runtime configuration; it is not persisted and cannot be shown again.");
            }

            return new AppRegistrationIdentityReport
            {
                Status = created ? "created" : "reconciled",
                TenantId = request.TenantId,
                DisplayName = request.DisplayName,
                ApplicationId = application.AppId,
                ApplicationObjectId = application.ObjectId,
                ServicePrincipalObjectId = principalId,
                ManagedIdentityClientId = managedIdentity?.ClientId,
                ManagedIdentityPrincipalId = managedIdentity?.PrincipalId,
                ManagedIdentityResourceId = managedIdentity?.ResourceId,
                ClientSecretExpiresOn = credential.SecretExpiresOn,
                OneTimeClientSecret = credential.Secret,
                AdminConsentUrl = adminConsentUrl,
                Changes = changes,
                NextActions = nextActions,
                Configuration = configuration,
            };
        }
        catch (InteractionRequiredException error)
            when (error.Resource == InteractionRequiredResource.MicrosoftGraph)
        {
            throw new AppRegistrationIdentityProvisioningException(
                error,
                BuildConsentRequiredReport(
                    request,
                    plan.DelegatedConsent,
                    application,
                    principalId,
                    managedIdentity,
                    changes));
        }
        catch (InteractionRequiredException error) when (application is not null)
        {
            throw new AppRegistrationIdentityProvisioningException(
                error,
                BuildPartialReport(
                    request,
                    application,
                    principalId,
                    managedIdentity,
                    adminConsentUrl,
                    changes,
                    nextActions,
                    currentStage));
        }
        catch (AppRegistrationFederatedCredentialException error)
            when (application is not null &&
                  error.InnerException is InteractionRequiredException
                  {
                      Resource: InteractionRequiredResource.MicrosoftGraph,
                  })
        {
            managedIdentity = error.ManagedIdentity;
            var interactionRequired = (InteractionRequiredException)error.InnerException;
            throw new AppRegistrationIdentityProvisioningException(
                interactionRequired,
                BuildConsentRequiredReport(
                    request,
                    plan.DelegatedConsent,
                    application,
                    principalId,
                    managedIdentity,
                    changes));
        }
        catch (AppRegistrationFederatedCredentialException error) when (application is not null)
        {
            managedIdentity = error.ManagedIdentity;
            throw new AppRegistrationIdentityProvisioningException(
                error,
                BuildPartialReport(
                    request,
                    application,
                    principalId,
                    managedIdentity,
                    adminConsentUrl,
                    changes,
                    nextActions,
                    currentStage));
        }
        catch (AgentHostingException error) when (application is not null)
        {
            throw new AppRegistrationIdentityProvisioningException(
                error,
                BuildPartialReport(
                    request,
                    application,
                    principalId,
                    managedIdentity,
                    adminConsentUrl,
                    changes,
                    nextActions,
                    currentStage));
        }
    }

    /// <summary>
    /// When Microsoft Graph reports consent is required, return an actionable "consent then rerun"
    /// report carrying the admin-consent URL instead of a silent-only sign-in failure. Delegated
    /// consent is always on, using a consent-on-first-use model.
    /// </summary>
    private static AppRegistrationIdentityReport BuildConsentRequiredReport(
        AppRegistrationIdentityRequest request,
        GraphDelegatedConsentPlan delegatedConsent,
        AppRegistrationApplication? application,
        string? principalId,
        ProvisionedManagedIdentity? managedIdentity,
        IReadOnlyList<IdentityChange> changes) =>
        new()
        {
            Status = "consent-required",
            TenantId = request.TenantId,
            DisplayName = request.DisplayName,
            ApplicationId = application?.AppId,
            ApplicationObjectId = application?.ObjectId,
            ServicePrincipalObjectId = principalId,
            ManagedIdentityClientId = managedIdentity?.ClientId,
            ManagedIdentityPrincipalId = managedIdentity?.PrincipalId,
            ManagedIdentityResourceId = managedIdentity?.ResourceId,
            AdminConsentUrl = delegatedConsent.AdminConsentUrl,
            Changes =
            [
                .. changes,
                Change(
                    "Consent",
                    "required",
                    $"Microsoft Graph consent is required for client {delegatedConsent.GraphClientId} " +
                    "before provisioning can continue."),
            ],
            NextActions =
            [
                $"Have a tenant admin grant Microsoft Graph consent once, then rerun: {delegatedConsent.AdminConsentUrl}",
            ],
        };

    private static AppRegistrationIdentityReport BuildPartialReport(
        AppRegistrationIdentityRequest request,
        AppRegistrationApplication application,
        string? principalId,
        ProvisionedManagedIdentity? managedIdentity,
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
            ManagedIdentityClientId = managedIdentity?.ClientId,
            ManagedIdentityPrincipalId = managedIdentity?.PrincipalId,
            ManagedIdentityResourceId = managedIdentity?.ResourceId,
            AdminConsentUrl = adminConsentUrl,
            Changes = [.. changes],
            NextActions =
            [
                .. nextActions,
                $"Resolve the {currentStage} failure, then rerun the command; existing resources will be reused.",
            ],
        };

    private static AppRegistrationIdentityReport BuildDryRunReport(
        AppRegistrationIdentityPlan plan,
        ArmConsentClientConfiguration armConsentClient)
    {
        var target = plan.TargetApplicationObjectId is not null
            ? $"object id {plan.TargetApplicationObjectId}"
            : plan.TargetApplicationId is not null
                ? $"app id {plan.TargetApplicationId}"
                : $"\"{plan.Request.DisplayName}\"";
        var changes = new List<IdentityChange>
        {
            Change("Application", "ensure", $"Create or reconcile App Registration {target}."),
            Change("ServicePrincipal", "ensure", "Create or reconcile the service principal."),
        };
        changes.AddRange(plan.Permissions.Select(permission =>
            Change(permission.Name, "ensure", $"Declare {permission.DelegatedScope} on requiredResourceAccess.")));
        foreach (var permission in plan.Permissions.Where(permission => permission.ApplicationRole is not null))
        {
            changes.Add(Change(
                $"{permission.Name}AppRole",
                "ensure",
                $"Declare application role {permission.ApplicationRole} on requiredResourceAccess."));
            changes.Add(Change(
                $"{permission.Name}AppRoleAssignment",
                "ensure",
                $"Verify or grant application role {permission.ApplicationRole} to the agent service principal."));
        }

        changes.Add(Change(
            "Permissions",
            "consent",
            "Prepare a tenant-wide admin-consent handoff."));
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
                $"Create or reconcile user-assigned managed identity {plan.ManagedIdentity.Name}."));
            changes.Add(Change(
                "ArmClient",
                "select",
                "Acquire Azure Resource Manager tokens with consent-capable client " +
                $"{armConsentClient.ClientId} instead of the CLI first-party app (delegated " +
                "consent); consented on first use."));
        }

        changes.Add(Change(plan.Request.AuthType.ToString(), "ensure", "Create or reconcile the selected credential."));
        var delegatedConsent = plan.DelegatedConsent;
        changes.Add(Change(
            "GraphClient",
            "select",
            $"Acquire Microsoft Graph tokens with consent-capable client {delegatedConsent.GraphClientId} " +
            "instead of the CLI first-party app (delegated consent)."));
        changes.Add(Change(
            "Consent",
            "request",
            "Request tenant admin consent for Microsoft Graph on first use rather than relying on central " +
            "preauthorization."));
        var nextActions = new List<string>
        {
            "Run without --dry-run to apply this plan.",
            $"Have a tenant admin grant Microsoft Graph consent once: {delegatedConsent.AdminConsentUrl}",
        };

        return new AppRegistrationIdentityReport
        {
            Status = "planned",
            TenantId = plan.Request.TenantId,
            DisplayName = plan.Request.DisplayName,
            AdminConsentUrl = delegatedConsent.AdminConsentUrl,
            Changes = changes,
            NextActions = nextActions,
        };
    }

    private static IdentityChange DescribeCredentialChange(
        IdentityCredentialType authType,
        AppRegistrationCredentialResult credential) => authType switch
    {
        IdentityCredentialType.ClientSecret => credential.RetainedExistingSecret
            ? Change("ClientSecret", "noop", "Retained an existing AgentHost CLI client secret.")
            : Change("ClientSecret", "create", "Created a one-time client secret."),
        IdentityCredentialType.FederatedCredentials => credential.FederatedCredentialChange switch
        {
            FederatedCredentialChange.Created =>
                Change("FederatedCredentials", "create", "Configured managed-identity federation."),
            FederatedCredentialChange.Reconciled =>
                Change("FederatedCredentials", "reconcile", "Reconciled the managed-identity federation."),
            FederatedCredentialChange.Unchanged =>
                Change("FederatedCredentials", "noop", "Reused the managed-identity federation."),
            _ => throw new InvalidOperationException("Federated credential reconciliation produced no outcome."),
        },
        IdentityCredentialType.UserManagedIdentity => credential.ManagedIdentity?.Created == false
            ? Change("UserManagedIdentity", "noop", "Reused the user-assigned managed identity credential.")
            : Change("UserManagedIdentity", "create", "Provisioned the user-assigned managed identity credential."),
        _ => throw new InvalidOperationException($"Unhandled auth type '{authType}'."),
    };

    private async Task<bool> ReconcileApplicationAsync(
        AppRegistrationIdentityPlan plan,
        AppRegistrationApplication application,
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

            var preauthorized = MergePreauthorizedApplications(
                application.PreAuthorizedApplications,
                plan.PreauthorizedClientIds,
                scopeId);
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
                    followUpBody = new JsonObject { ["api"] = preauthorizationBody };
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
            await PatchApplicationAsync(plan.Request.TenantId, application.ObjectId, body, cancellationToken)
                .ConfigureAwait(false);
            changed = true;
        }

        if (followUpBody is not null)
        {
            await PatchApplicationAsync(plan.Request.TenantId, application.ObjectId, followUpBody, cancellationToken)
                .ConfigureAwait(false);
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
            [IdentityProvisioningConfig.GraphApplicationReadWriteScope],
            tenantId,
            cancellationToken);

    private static JsonArray MergePreauthorizedApplications(
        JsonArray existing,
        IReadOnlyList<string> clientIds,
        string scopeId)
    {
        var result = existing.DeepClone().AsArray();
        foreach (var clientId in clientIds)
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
}
