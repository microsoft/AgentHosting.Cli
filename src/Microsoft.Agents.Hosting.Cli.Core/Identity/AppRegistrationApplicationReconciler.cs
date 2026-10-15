using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using static Microsoft.Agents.Hosting.Cli.Core.Identity.BlueprintIdentityJson;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

/// <summary>Outcome of ensuring an App Registration's service principal exists.</summary>
/// <param name="ObjectId">The service principal object id.</param>
/// <param name="Created">True when the principal was newly created; false when an existing one was
/// reused. Drives created-vs-reused apply wording.</param>
internal sealed record ProvisionedServicePrincipal(string ObjectId, bool Created);

/// <summary>
/// Discovers, creates, and ensures the service principal for an ordinary App Registration over direct
/// Microsoft Graph REST. Unlike the Blueprint flow this uses the standard <c>/v1.0/applications</c> and
/// <c>/v1.0/servicePrincipals</c> routes (no <c>agentIdentityBlueprint</c> cast) and preserves any
/// <c>requiredResourceAccess</c> already declared on the application so reconciliation never clobbers
/// unrelated API permissions.
/// </summary>
internal sealed class AppRegistrationApplicationReconciler
{
    private const string ApplicationSelect =
        "$select=id,appId,displayName,signInAudience,identifierUris,api,web,requiredResourceAccess,passwordCredentials";
    private readonly IIdentityRestClient _rest;
    private readonly IdentityGraphClient _graph;
    private readonly TimeSpan _retryDelay;

    public AppRegistrationApplicationReconciler(
        IIdentityRestClient rest,
        IdentityGraphClient graph,
        TimeSpan retryDelay)
    {
        _rest = rest ?? throw new ArgumentNullException(nameof(rest));
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _retryDelay = retryDelay;
    }

    public async Task<AppRegistrationApplication?> FindAsync(
        AppRegistrationIdentityPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.TargetApplicationObjectId is not null)
        {
            return ValidateAudience(await GetByObjectIdAsync(
                plan.Request.TenantId,
                plan.TargetApplicationObjectId,
                cancellationToken).ConfigureAwait(false));
        }

        if (plan.TargetApplicationId is not null)
        {
            return ValidateAudience(await GetByAppIdAsync(
                plan.Request.TenantId,
                plan.TargetApplicationId,
                cancellationToken).ConfigureAwait(false));
        }

        var application = await FindByDisplayNameAsync(plan, cancellationToken).ConfigureAwait(false);
        return application is null ? null : ValidateAudience(application);
    }

    private static AppRegistrationApplication ValidateAudience(AppRegistrationApplication application)
    {
        if (!string.Equals(
                application.SignInAudience,
                AppRegistrationIdentityPlan.SignInAudience,
                StringComparison.Ordinal))
        {
            throw new ProvisioningException(
                ProvisioningErrorKind.NamingConflict,
                $"The selected application uses sign-in audience {application.SignInAudience}, which cannot be safely reconciled automatically.",
                $"Convert the application to {AppRegistrationIdentityPlan.SignInAudience} after validating its identifier URIs, or target a compatible application.");
        }

        return application;
    }

    public async Task<AppRegistrationApplication> CreateAsync(
        AppRegistrationIdentityRequest request,
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
            ["displayName"] = request.DisplayName,
            ["signInAudience"] = AppRegistrationIdentityPlan.SignInAudience,
        };
        if (ownerId is not null)
        {
            payload["owners@odata.bind"] = new JsonArray(
                $"{IdentityProvisioningConfig.GraphEndpoint}/v1.0/directoryObjects/{ownerId}");
        }

        var response = await _graph.SendAsync(
            HttpMethod.Post,
            "/v1.0/applications",
            payload,
            [IdentityProvisioningConfig.GraphApplicationReadWriteScope],
            request.TenantId,
            cancellationToken).ConfigureAwait(false);
        return AppRegistrationApplication.From(ParseObject(response.Body));
    }

    public async Task<ProvisionedServicePrincipal> EnsureServicePrincipalAsync(
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
            return new ProvisionedServicePrincipal(
                RequireString(values[0], "id", "service principal lookup"),
                Created: false);
        }

        // A service principal cannot be created until the new application has replicated across Entra.
        // Bounded retry on the propagation-delay 4xx that Graph returns in that window.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var response = await _rest.SendGraphAsync(
                HttpMethod.Post,
                "/v1.0/servicePrincipals",
                new { appId },
                [IdentityProvisioningConfig.GraphApplicationReadWriteScope],
                tenantId,
                cancellationToken).ConfigureAwait(false);
            if (response.IsSuccess)
            {
                return new ProvisionedServicePrincipal(
                    RequireString(ParseObject(response.Body), "id", "service principal create"),
                    Created: true);
            }

            var retryable = (response.StatusCode == 400 || response.StatusCode == 404) &&
                IsApplicationPropagationDelay(response.Body);
            if (!retryable || attempt == 5)
            {
                throw TargetRequestException.MapTargetResponseError(
                    response.StatusCode,
                    response.Body,
                    HttpMethod.Post.Method,
                    "/v1.0/servicePrincipals",
                    "Graph",
                    "msagent");
            }

            await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Service principal retry loop completed unexpectedly.");
    }

    private static bool IsApplicationPropagationDelay(string? responseBody)
    {
        var (code, message) = TargetRequestException.ParseTargetError(responseBody);
        return string.Equals(code, "Request_BadRequest", StringComparison.OrdinalIgnoreCase) &&
            (message?.Contains("does not reference", StringComparison.OrdinalIgnoreCase) == true ||
                message?.Contains(
                    "Invalid value specified for property 'appId'",
                    StringComparison.OrdinalIgnoreCase) == true);
    }

    private async Task<AppRegistrationApplication?> FindByDisplayNameAsync(
        AppRegistrationIdentityPlan plan,
        CancellationToken cancellationToken)
    {
        var escapedName = plan.Request.DisplayName.Replace("'", "''", StringComparison.Ordinal);
        var path = "/v1.0/applications?$filter=" +
            Uri.EscapeDataString($"displayName eq '{escapedName}'") +
            "&$select=id";
        var values = await _graph.ReadCollectionAsync(
            path,
            [IdentityProvisioningConfig.GraphApplicationReadScope],
            plan.Request.TenantId,
            cancellationToken).ConfigureAwait(false);
        if (values.Count > 1)
        {
            throw new ProvisioningException(
                ProvisioningErrorKind.NamingConflict,
                $"More than one Entra application is named \"{plan.Request.DisplayName}\".",
                "Pass --app-id or --object-id to target one, or remove the duplicate applications.");
        }

        if (values.Count == 0)
        {
            return null;
        }

        return await GetByObjectIdAsync(
            plan.Request.TenantId,
            RequireString(values[0], "id", "application lookup"),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AppRegistrationApplication> GetByObjectIdAsync(
        string tenantId,
        string objectId,
        CancellationToken cancellationToken)
    {
        var response = await _graph.SendAsync(
            HttpMethod.Get,
            $"/v1.0/applications/{objectId}?{ApplicationSelect}",
            body: null,
            [IdentityProvisioningConfig.GraphApplicationReadScope],
            tenantId,
            cancellationToken).ConfigureAwait(false);
        return AppRegistrationApplication.From(ParseObject(response.Body));
    }

    private async Task<AppRegistrationApplication> GetByAppIdAsync(
        string tenantId,
        string appId,
        CancellationToken cancellationToken)
    {
        var query = "/v1.0/applications?$filter=" +
            Uri.EscapeDataString($"appId eq '{appId}'") +
            "&$select=id";
        var values = await _graph.ReadCollectionAsync(
            query,
            [IdentityProvisioningConfig.GraphApplicationReadScope],
            tenantId,
            cancellationToken).ConfigureAwait(false);
        if (values.Count == 0)
        {
            throw new ProvisioningException(
                ProvisioningErrorKind.MissingContext,
                $"No application with app id {appId} exists in the target tenant.",
                "Verify --app-id, or omit it to create a new application.");
        }

        return await GetByObjectIdAsync(
            tenantId,
            RequireString(values[0], "id", "application lookup"),
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Snapshot of the ordinary App Registration state this command reconciles.</summary>
internal sealed record AppRegistrationApplication
{
    public required string ObjectId { get; init; }
    public required string AppId { get; init; }
    public required string SignInAudience { get; init; }
    public required JsonArray OAuthScopes { get; init; }
    public required JsonArray PreAuthorizedApplications { get; init; }
    public required JsonArray RequiredResourceAccess { get; init; }
    public required IReadOnlyList<string> IdentifierUris { get; init; }
    public required IReadOnlyList<string> RedirectUris { get; init; }
    public int? RequestedAccessTokenVersion { get; init; }
    public bool HasUsableClientSecret { get; init; }

    public static AppRegistrationApplication From(JsonObject value)
    {
        var now = DateTimeOffset.UtcNow;
        var passwords = ReadArray(
            value["passwordCredentials"],
            "application passwordCredentials");
        var api = ReadObject(value["api"], "application api");
        var web = ReadObject(value["web"], "application web");
        return new AppRegistrationApplication
        {
            ObjectId = RequireString(value, "id", "application"),
            AppId = RequireString(value, "appId", "application"),
            SignInAudience = RequireString(value, "signInAudience", "application"),
            OAuthScopes = ReadArray(
                api?["oauth2PermissionScopes"],
                "application oauth2PermissionScopes").DeepClone().AsArray(),
            PreAuthorizedApplications =
                ReadArray(
                    api?["preAuthorizedApplications"],
                    "application preAuthorizedApplications").DeepClone().AsArray(),
            RequiredResourceAccess = ReadArray(
                value["requiredResourceAccess"],
                "application requiredResourceAccess").DeepClone().AsArray(),
            IdentifierUris = ReadStrings(ReadArray(
                value["identifierUris"],
                "application identifierUris")),
            RedirectUris = ReadStrings(ReadArray(
                web?["redirectUris"],
                "application redirectUris")),
            RequestedAccessTokenVersion = api?["requestedAccessTokenVersion"]?.GetValue<int>(),
            HasUsableClientSecret = passwords.OfType<JsonObject>().Any(password =>
                string.Equals(
                    ReadString(password, "displayName"),
                    AppRegistrationCredentialReconciler.SecretDisplayName,
                    StringComparison.Ordinal) &&
                DateTimeOffset.TryParse(ReadString(password, "endDateTime"), out var expiry) &&
                expiry > now.AddDays(1)),
        };
    }
}
