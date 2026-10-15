using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Abs;

/// <summary>
/// Validates operator inputs and derives the complete <see cref="AbsDesiredState"/> — the single
/// source the review/preflight planner and output contract consume, produced entirely before any
/// Azure read or mutation. A faithful C# reimplementation of the shipped TypeScript
/// <c>buildAbsDesiredState</c>: it enforces explicit Azure context and an
/// HTTPS endpoint, derives <c>msaAppType</c> from the identity mode, defaults the SKU to <c>F0</c>,
/// and builds the ARM correlation anchor by deterministic resource id. Invalid input throws
/// <see cref="UsageException"/> (exit 2, no error kind) with actionable remediation, mirroring the
/// TypeScript <c>UsageError</c>.
///
/// On the scoped supplied-<c>--msa-app-id</c> zero-Graph path the Graph correlation is always a
/// display-name filter; <c>--app-object-id</c> is accepted but has no observable effect on the review
/// (the shipped core stubs Graph reads and neutralizes the Entra plan on this path).
/// </summary>
internal static class AbsDesiredStateBuilder
{
    private static readonly HashSet<string> SupportedSkus = new(StringComparer.Ordinal) { "F0", "S1" };

    // ABS resource-name rules: start alphanumeric, then alphanumeric/'-'/'_', total 2-64 chars.
    private static readonly Regex BotNamePattern = new("^[A-Za-z0-9][A-Za-z0-9-_]{1,63}$", RegexOptions.Compiled);

    // Hostnames that never receive real ABS traffic, so an endpoint using them is a placeholder/guess.
    private static readonly HashSet<string> NonReachableHosts =
        new(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "0.0.0.0", "::1" };

    // Substrings that betray an un-substituted template value.
    private static readonly string[] PlaceholderTokens =
        ["<", ">", "your-endpoint", "placeholder", "changeme", "change-me"];

    /// <summary>The location Azure Bot Service resources are created in when the operator names none.</summary>
    public const string DefaultLocation = "global";

    public static AbsDesiredState Build(AbsProvisioningRequest request)
    {
        var tenantId = RequireNonEmpty(request.Tenant, "tenantId");
        var subscriptionId = RequireNonEmpty(request.Subscription, "subscriptionId");
        var resourceGroup = RequireNonEmpty(request.ResourceGroup, "resourceGroup");
        var location = RequireNonEmpty(request.Location, "location");
        var name = ValidateBotName(request.BotName);
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? name : request.DisplayName.Trim();
        var endpoint = ValidateEndpoint(request.Endpoint ?? string.Empty);

        var identityMode = ResolveIdentityMode(request.IdentityMode);
        var msaAppType = DeriveMsaAppType(identityMode);

        var sku = (request.Sku ?? "F0").Trim().ToUpperInvariant();
        if (!SupportedSkus.Contains(sku))
        {
            throw new UsageException(
                $"Unsupported SKU \"{request.Sku}\". The first slice supports F0 (default) or S1.");
        }

        var botResourceId = ComputeBotResourceId(subscriptionId, resourceGroup, name);

        return new AbsDesiredState
        {
            AzureContext = new AbsAzureContext
            {
                TenantId = tenantId,
                SubscriptionId = subscriptionId,
                ResourceGroup = resourceGroup,
                Location = location,
            },
            BotName = name,
            DisplayName = displayName,
            Endpoint = endpoint,
            Sku = sku,
            MsaAppType = msaAppType,
            IdentityMode = identityMode,
            DeploymentId = string.IsNullOrWhiteSpace(request.DeploymentId) ? null : request.DeploymentId.Trim(),
            ArmCorrelation = new AbsArmCorrelation
            {
                Mode = "resourceId",
                BotResourceId = botResourceId,
                Name = name,
            },
            GraphCorrelation = new AbsGraphCorrelation
            {
                Mode = "displayNameFilter",
                DisplayName = displayName,
            },
        };
    }

    /// <summary>
    /// Computes the deterministic ARM resource id — the ARM correlation anchor. Derivable before any
    /// call because the <c>botServices</c> PUT is keyed by resource name, which is why a rerun upserts
    /// instead of duplicating.
    /// </summary>
    public static string ComputeBotResourceId(string subscriptionId, string resourceGroup, string name) =>
        $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}" +
        $"/providers/Microsoft.BotService/botServices/{name}";

    private static string RequireNonEmpty(string? value, string field)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new UsageException(
                $"Missing required Azure context: {field}. Provide an explicit {field} — " +
                "ABS scaffolding never guesses or silently switches Azure context.");
        }

        return trimmed;
    }

    private static string ValidateBotName(string? raw)
    {
        var name = RequireNonEmpty(raw, "botName");
        if (!BotNamePattern.IsMatch(name))
        {
            throw new UsageException(
                $"Bot name \"{name}\" is invalid. Use 2–64 characters, starting with a letter or digit, " +
                "containing only letters, digits, hyphens, or underscores.");
        }

        return name;
    }

    private static string ValidateEndpoint(string raw)
    {
        var endpoint = raw.Trim();
        if (endpoint.Length == 0)
        {
            throw new UsageException(
                "Missing required messaging endpoint. Provide the AgentHost/gateway HTTPS `/api/messages` " +
                "endpoint — the stopgap will not create a bot with an empty endpoint.");
        }

        var lower = endpoint.ToLowerInvariant();
        foreach (var token in PlaceholderTokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
            {
                throw new UsageException(
                    $"Messaging endpoint \"{endpoint}\" looks like an unfilled placeholder. " +
                    "Provide the real AgentHost/gateway HTTPS endpoint.");
            }
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var url))
        {
            throw new UsageException(
                $"Messaging endpoint \"{endpoint}\" is not a valid URL. Provide an absolute HTTPS URL " +
                "such as https://gateway.contoso.com/api/messages.");
        }

        if (!string.Equals(url.Scheme, "https", StringComparison.Ordinal))
        {
            throw new UsageException(
                $"Messaging endpoint \"{endpoint}\" must use HTTPS. Azure Bot Service requires an HTTPS " +
                "messaging endpoint.");
        }

        var host = url.Host.Trim('[', ']');
        if (host.Length == 0 || NonReachableHosts.Contains(host))
        {
            throw new UsageException(
                $"Messaging endpoint \"{endpoint}\" points at a non-reachable host. Provide a publicly " +
                "reachable HTTPS endpoint (a local tunnel host is fine).");
        }

        var lowerHost = host.ToLowerInvariant();
        if (lowerHost == "example.com" || lowerHost.EndsWith(".example.com", StringComparison.Ordinal))
        {
            throw new UsageException(
                $"Messaging endpoint \"{endpoint}\" uses the reserved example.com domain. " +
                "Provide the real AgentHost/gateway endpoint.");
        }

        return endpoint;
    }

    private static string ResolveIdentityMode(string? mode)
    {
        var resolved = mode?.Trim();
        resolved = resolved is null ? "ClientSecret" : resolved;

        if (resolved == "ClientSecret")
        {
            return resolved;
        }

        if (resolved == "ServiceManaged")
        {
            throw new UsageException(
                "Identity mode `ServiceManaged` is reserved for service-owned provisioning and cannot be " +
                "requested from the CLI. Use `ClientSecret` (the default), or connect an existing app " +
                "registration with `--msa-app-id`.");
        }

        throw new UsageException(
            $"Identity mode `{resolved}` is not yet supported. Only `ClientSecret` (the default) can be " +
            "provisioned today; to connect an existing app registration without creating a credential, " +
            "pass `--msa-app-id <appId>`.");
    }

    private static string DeriveMsaAppType(string identityMode) => identityMode switch
    {
        "UserAssignedMSI" => "UserAssignedMSI",
        "ClientSecret" or "FederatedCredentials" => "SingleTenant",
        _ => throw new UsageException($"Unsupported identity mode: {identityMode}."),
    };
}
