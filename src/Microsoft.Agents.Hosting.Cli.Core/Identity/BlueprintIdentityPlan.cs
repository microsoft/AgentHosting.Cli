using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

/// <summary>One API permission selected for the Blueprint.</summary>
public sealed record BlueprintPermission(
    string Name,
    string ResourceAppId,
    string DelegatedScope,
    string? ApplicationRole = null,
    string? ResourceIdentifierUri = null);

/// <summary>Azure coordinates for the managed identity backing a federated credential.</summary>
public sealed record ManagedIdentityPlan(
    string SubscriptionId,
    string ResourceGroup,
    string Location,
    string Name);

/// <summary>
/// Pure, deterministic plan derived from command input. Keeping selection logic independent of REST
/// makes dry-run and apply use the same permission and auth-setting decisions.
/// </summary>
public sealed record BlueprintIdentityPlan
{
    public const string DefaultRedirectUri = "https://token.botframework.com/.auth/web/redirect";
    private const int MaximumDisplayNameLength = 256;

    public required BlueprintIdentityRequest Request { get; init; }
    public required IReadOnlyList<BlueprintPermission> Permissions { get; init; }
    public string? RedirectUri { get; init; }
    public bool TeamsSso { get; init; }
    public ManagedIdentityPlan? ManagedIdentity { get; init; }

    public static BlueprintIdentityPlan Build(BlueprintIdentityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

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

        return new BlueprintIdentityPlan
        {
            Request = request,
            Permissions = permissions,
            RedirectUri = request.OAuth
                ? string.IsNullOrWhiteSpace(request.RedirectUri) ? DefaultRedirectUri : request.RedirectUri
                : null,
            TeamsSso = request.TeamsSso,
            ManagedIdentity = request.AuthType == IdentityCredentialType.FederatedCredentials
                ? new ManagedIdentityPlan(
                    request.SubscriptionId!,
                    request.ResourceGroup!,
                    request.Location!,
                    request.ManagedIdentityName!)
                : null,
        };
    }

    private static void Validate(BlueprintIdentityRequest request)
    {
        if (!Guid.TryParse(request.TenantId, out _))
        {
            throw new UsageException("--tenant must be a GUID.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new UsageException("--display-name is required.");
        }

        if (request.DisplayName.Length > MaximumDisplayNameLength)
        {
            throw new UsageException(
                $"--display-name must not exceed {MaximumDisplayNameLength} characters.");
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

        if (!request.OAuth && !string.IsNullOrWhiteSpace(request.RedirectUri))
        {
            throw new UsageException("--redirect-uri requires --oauth.");
        }

        if (request.OAuth)
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

        if (request.AuthType == IdentityCredentialType.FederatedCredentials &&
            (string.IsNullOrWhiteSpace(request.SubscriptionId) ||
             string.IsNullOrWhiteSpace(request.ResourceGroup) ||
             string.IsNullOrWhiteSpace(request.Location) ||
             string.IsNullOrWhiteSpace(request.ManagedIdentityName)))
        {
            throw new UsageException(
                "FederatedCredentials requires --subscription, --resource-group, --location, and --managed-identity-name.");
        }

        if (request.AuthType == IdentityCredentialType.FederatedCredentials &&
            !IdentityInputValidation.IsValidManagedIdentityName(request.ManagedIdentityName!))
        {
            throw new UsageException(
                "--managed-identity-name must be 3-120 characters, start with a letter or digit, " +
                "and contain only letters, digits, hyphens, or underscores.");
        }

        if (request.AuthType == IdentityCredentialType.FederatedCredentials)
        {
            ValidateManagedIdentityCoordinates(request);
        }

        if (request.AuthType == IdentityCredentialType.ClientSecret &&
            (!string.IsNullOrWhiteSpace(request.SubscriptionId) ||
             !string.IsNullOrWhiteSpace(request.ResourceGroup) ||
             !string.IsNullOrWhiteSpace(request.Location) ||
             !string.IsNullOrWhiteSpace(request.ManagedIdentityName)))
        {
            throw new UsageException(
                "--subscription, --resource-group, --location, and --managed-identity-name are only valid with FederatedCredentials.");
        }
    }

    private static void ValidateManagedIdentityCoordinates(BlueprintIdentityRequest request)
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
