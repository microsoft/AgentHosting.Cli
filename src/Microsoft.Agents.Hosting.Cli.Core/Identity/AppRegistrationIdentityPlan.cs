using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

/// <summary>
/// Pure, deterministic plan derived from <c>msagent identity create app</c> input. It mirrors
/// <see cref="BlueprintIdentityPlan"/> but targets ordinary App Registrations: it accepts
/// <see cref="IdentityCredentialType.UserManagedIdentity"/>, provisions a managed identity for both
/// managed-identity auth types, treats Teams SSO as including the OAuth redirect behavior, and records
/// the multitenant sign-in audience and any explicit App Registration target for idempotent
/// reconciliation. Keeping selection logic independent of REST makes dry-run and apply share the
/// same decisions.
/// </summary>
public sealed record AppRegistrationIdentityPlan
{
    public const string DefaultRedirectUri = BlueprintIdentityPlan.DefaultRedirectUri;

    /// <summary>Multitenant sign-in audience used for every App Registration this command creates.</summary>
    public const string SignInAudience = "AzureADMultipleOrgs";

    public required AppRegistrationIdentityRequest Request { get; init; }
    public required IReadOnlyList<BlueprintPermission> Permissions { get; init; }

    /// <summary>Redirect URI to configure, or <see langword="null"/> when OAuth is not requested.</summary>
    public string? RedirectUri { get; init; }

    /// <summary>Whether to expose <c>access_as_user</c> and preauthorize the first-party Teams clients.</summary>
    public bool TeamsSso { get; init; }

    /// <summary>First-party client ids to preauthorize when <see cref="TeamsSso"/> is set.</summary>
    public IReadOnlyList<string> PreauthorizedClientIds { get; init; } = [];

    /// <summary>Managed identity to create/reconcile for UserManagedIdentity and FederatedCredentials.</summary>
    public ManagedIdentityPlan? ManagedIdentity { get; init; }

    /// <summary>
    /// When <see langword="true"/> the OAuth identity app is a companion to a user-assigned managed
    /// identity rather than the agent's primary credential (UserManagedIdentity auth type).
    /// </summary>
    public bool UsesCompanionApp { get; init; }

    /// <summary>Explicit App Registration client id to target, bypassing display-name lookup.</summary>
    public string? TargetApplicationId { get; init; }

    /// <summary>Explicit App Registration object id to target, bypassing display-name lookup.</summary>
    public string? TargetApplicationObjectId { get; init; }

    /// <summary>
    /// Delegated Microsoft Graph consent selection (consent-capable client + admin-consent URL). Always
    /// set: delegated consent is on by default and uses
    /// <see cref="GraphDelegatedConsent.DefaultGraphClientId"/> unless an override is supplied.
    /// </summary>
    public required GraphDelegatedConsentPlan DelegatedConsent { get; init; }

    public static AppRegistrationIdentityPlan Build(
        AppRegistrationIdentityRequest request,
        GraphConsentClientConfiguration? graphConsentClient = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        graphConsentClient ??= GraphConsentClientConfiguration.Default;

        var permissions = new List<BlueprintPermission>
        {
            IdentityProvisioningConfig.MessagingBotPermission,
            IdentityProvisioningConfig.PowerPlatformPermission,
        };
        if (request.Observability)
        {
            permissions.Add(IdentityProvisioningConfig.ObservabilityPermission);
        }

        if (request.McpDiscovery)
        {
            permissions.Add(IdentityProvisioningConfig.McpDiscoveryPermission);
        }

        var effectiveOAuth = request.OAuth || request.TeamsSso;
        var usesManagedIdentity =
            request.AuthType is IdentityCredentialType.UserManagedIdentity
                or IdentityCredentialType.FederatedCredentials;

        return new AppRegistrationIdentityPlan
        {
            Request = request,
            Permissions = permissions,
            RedirectUri = effectiveOAuth
                ? string.IsNullOrWhiteSpace(request.RedirectUri) ? DefaultRedirectUri : request.RedirectUri
                : null,
            TeamsSso = request.TeamsSso,
            PreauthorizedClientIds = request.TeamsSso
                ? IdentityProvisioningConfig.TeamsHostApplicationIds
                : [],
            ManagedIdentity = usesManagedIdentity
                ? new ManagedIdentityPlan(
                    request.SubscriptionId!,
                    request.ResourceGroup!,
                    request.Location!,
                    request.ManagedIdentityName!)
                : null,
            UsesCompanionApp = request.AuthType == IdentityCredentialType.UserManagedIdentity,
            TargetApplicationId = string.IsNullOrWhiteSpace(request.ApplicationId) ? null : request.ApplicationId,
            TargetApplicationObjectId =
                string.IsNullOrWhiteSpace(request.ApplicationObjectId) ? null : request.ApplicationObjectId,
            DelegatedConsent = GraphDelegatedConsent.Create(
                request.TenantId,
                graphConsentClient,
                includeState: !request.DryRun),
        };
    }

    private static void Validate(AppRegistrationIdentityRequest request)
    {
        if (!Guid.TryParse(request.TenantId, out _))
        {
            throw new UsageException("--tenant must be a GUID.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new UsageException("--display-name is required.");
        }

        if (request.DisplayName.Length > IdentityInputValidation.MaximumDisplayNameLength)
        {
            throw new UsageException(
                $"--display-name must not exceed {IdentityInputValidation.MaximumDisplayNameLength} characters.");
        }

        if (request.DisplayName.Any(char.IsControl))
        {
            throw new UsageException("--display-name cannot contain control characters.");
        }

        if (!Enum.IsDefined(request.AuthType))
        {
            throw new UsageException("--auth-type is invalid.");
        }

        if (!Enum.IsDefined(request.ConfigFormat))
        {
            throw new UsageException("--config-format is invalid.");
        }

        if (!string.IsNullOrWhiteSpace(request.ApplicationId) && !Guid.TryParse(request.ApplicationId, out _))
        {
            throw new UsageException("--app-id must be a GUID.");
        }

        if (!string.IsNullOrWhiteSpace(request.ApplicationObjectId) &&
            !Guid.TryParse(request.ApplicationObjectId, out _))
        {
            throw new UsageException("--object-id must be a GUID.");
        }

        if (!string.IsNullOrWhiteSpace(request.ApplicationId) &&
            !string.IsNullOrWhiteSpace(request.ApplicationObjectId))
        {
            throw new UsageException("--app-id and --object-id cannot be used together.");
        }

        var effectiveOAuth = request.OAuth || request.TeamsSso;
        if (!effectiveOAuth && !string.IsNullOrWhiteSpace(request.RedirectUri))
        {
            throw new UsageException("--redirect-uri requires --oauth or --teams-sso.");
        }

        if (effectiveOAuth)
        {
            var redirectValue = string.IsNullOrWhiteSpace(request.RedirectUri)
                ? DefaultRedirectUri
                : request.RedirectUri;
            if (!Uri.TryCreate(redirectValue, UriKind.Absolute, out var redirect) ||
                redirect.Scheme != Uri.UriSchemeHttps)
            {
                throw new UsageException("--redirect-uri must be an absolute HTTPS URI.");
            }
        }

        var requiresManagedIdentity =
            request.AuthType is IdentityCredentialType.UserManagedIdentity
                or IdentityCredentialType.FederatedCredentials;

        if (requiresManagedIdentity &&
            (string.IsNullOrWhiteSpace(request.SubscriptionId) ||
             string.IsNullOrWhiteSpace(request.ResourceGroup) ||
             string.IsNullOrWhiteSpace(request.Location) ||
             string.IsNullOrWhiteSpace(request.ManagedIdentityName)))
        {
            throw new UsageException(
                $"{request.AuthType} requires --subscription, --resource-group, --location, and --managed-identity-name.");
        }

        if (requiresManagedIdentity)
        {
            if (!IdentityInputValidation.IsValidManagedIdentityName(request.ManagedIdentityName!))
            {
                throw new UsageException(
                    "--managed-identity-name must be 3-120 characters, start with a letter or digit, " +
                    "and contain only letters, digits, hyphens, or underscores.");
            }

            ValidateManagedIdentityCoordinates(request);
        }

        if (request.AuthType == IdentityCredentialType.ClientSecret &&
            (!string.IsNullOrWhiteSpace(request.SubscriptionId) ||
             !string.IsNullOrWhiteSpace(request.ResourceGroup) ||
             !string.IsNullOrWhiteSpace(request.Location) ||
             !string.IsNullOrWhiteSpace(request.ManagedIdentityName)))
        {
            throw new UsageException(
                "--subscription, --resource-group, --location, and --managed-identity-name are not valid with ClientSecret.");
        }
    }

    private static void ValidateManagedIdentityCoordinates(AppRegistrationIdentityRequest request)
    {
        if (!Guid.TryParse(request.SubscriptionId, out var subscriptionId) ||
            subscriptionId == Guid.Empty)
        {
            throw new UsageException("--subscription must be a non-empty GUID.");
        }

        if (!IdentityInputValidation.IsValidResourceGroupName(request.ResourceGroup!))
        {
            throw new UsageException(
                "--resource-group must be 1-90 characters, contain only letters, digits, " +
                "underscores, hyphens, periods, or parentheses, and not end with a period.");
        }

        if (!IdentityInputValidation.IsValidLocation(request.Location!))
        {
            throw new UsageException(
                "--location must be a 1-90 character canonical Azure location name containing only letters or digits.");
        }
    }
}
