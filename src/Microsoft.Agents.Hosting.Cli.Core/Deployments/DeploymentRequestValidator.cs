using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

/// <summary>
/// The single, in-memory validation home for every <c>msagent deployment</c> request — <c>create</c>,
/// <c>show</c>, <c>update</c>, and <c>delete</c>. Each entry point runs entirely <em>before</em>
/// <see cref="DeploymentService"/> issues any Azure Bot Service / ARM call, so no partial Azure work can
/// occur on a bad request. It owns: required-input checks, allowed-value sets, the deployment-name format,
/// the mutually-exclusive addressing modes (<c>--deployment-id</c> vs <c>--agent-id</c>/<c>--deployment-name</c>),
/// GUID-shape checks, and shared value primitives reused by deployment subgroups.
/// 
/// Each entry point returns a normalized copy: required inputs are trimmed, enum values are
/// canonicalized to their supported casing, and every value-bearing option — required or optional — is
/// rejected when it is provided but blank (empty or whitespace), so no empty value ever reaches the
/// Azure Bot Service provisioning call. An option that is simply omitted stays <see langword="null"/>.
/// </summary>
public static class DeploymentRequestValidator
{
    private static readonly string[] DeploymentTypes = ["Dev", "Test", "Prod"];
    private static readonly string[] Skus = ["F0", "S1"];
    private static readonly string[] SupportedEntraIdTypes = ["AppReg"];

    public static DeploymentRequest Validate(DeploymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var hasDeploymentId = !string.IsNullOrWhiteSpace(request.DeploymentId);
        var hasDeploymentName = !string.IsNullOrWhiteSpace(request.DeploymentName);
        var hasAgentId = !string.IsNullOrWhiteSpace(request.AgentId);

        string? deploymentId = null;
        string? deploymentName = null;
        string? agentId = null;

        if (hasDeploymentId)
        {
            if (hasAgentId || hasDeploymentName)
            {
                throw new UsageException(
                    "--deployment-id cannot be combined with --agent-id/--deployment-name. Provide either --deployment-id <id> or both --agent-id and --deployment-name.");
            }

            deploymentId = OptionalGuid("--deployment-id", request.DeploymentId);
        }
        else
        {
            if (!hasAgentId || !hasDeploymentName)
            {
                throw new UsageException(
                    "Either --deployment-id <id> or both --agent-id and --deployment-name must be provided.");
            }

            agentId = OptionalGuid("--agent-id", request.AgentId);
            deploymentName = DeploymentNameRule.Validate("--deployment-name", request.DeploymentName);
        }

        return request with
        {
            AgentId = agentId,
            DeploymentId = deploymentId,
            DeploymentName = deploymentName,
            ResourceGroup = Optional("--resource-group", request.ResourceGroup),
            SubscriptionId = OptionalGuid("--subscription", request.SubscriptionId),
            TenantId = OptionalGuid("--tenant", request.TenantId),
        };
    }

    public static DeploymentCreateRequest ValidateCreate(DeploymentCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request with
        {
            AgentId = RequiredGuid("--agent-id", request.AgentId),
            DeploymentName = DeploymentNameRule.Validate("--deployment-name", request.DeploymentName),
            EntraId = RequiredGuid("--entra-id", request.EntraId),
            EntraIdType = RequiredEnum("--entra-id-type", request.EntraIdType, SupportedEntraIdTypes),
            DeploymentType = RequiredEnum("--deployment-type", request.DeploymentType, DeploymentTypes),
            Sku = OptionalEnum("--sku", request.Sku, Skus),
            ResourceGroup = Optional("--resource-group", request.ResourceGroup),
            SubscriptionId = OptionalGuid("--subscription", request.SubscriptionId),
            TenantId = OptionalGuid("--tenant", request.TenantId),
            Location = Optional("--location", request.Location),
            DisplayName = Optional("--display-name", request.DisplayName),
            Description = Optional("--description", request.Description),
            Endpoint = OptionalHttpsUrl("--endpoint-url", request.Endpoint),
        };
    }

    public static DeploymentUpdateRequest ValidateUpdate(DeploymentUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validRequest = Validate(request);

        var deploymentType = OptionalEnum("--deployment-type", request.DeploymentType, DeploymentTypes);
        var sku = OptionalEnum("--sku", request.Sku, Skus);
        var displayName = Optional("--display-name", request.DisplayName);
        var description = Optional("--description", request.Description);
        var endpoint = OptionalHttpsUrl("--endpoint-url", request.Endpoint);

        if (request.IsDefault is null && deploymentType is null && sku is null && displayName is null && description is null && endpoint is null)
        {
            throw new UsageException("At least one of these fields must be specified: --deployment-type, --sku, --display-name, --description, --endpoint-url, --default");
        }

        return request with
        {
            DeploymentId = validRequest.DeploymentId,
            AgentId = validRequest.AgentId,
            DeploymentName = validRequest.DeploymentName,
            ResourceGroup = validRequest.ResourceGroup,
            SubscriptionId = validRequest.SubscriptionId,
            TenantId = validRequest.TenantId,
            DeploymentType = deploymentType,
            Sku = sku,
            DisplayName = displayName,
            Description = description,
            Endpoint = endpoint,
        };
    }

    /// <summary>
    /// Trims a value. Null stays null; blank maps to null unless <paramref name="preserveEmpty"/> is true,
    /// in which case an explicitly supplied blank is preserved as an empty-string clear operation.
    /// </summary>
    public static string? Trim(string? value, bool preserveEmpty = false)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 && !preserveEmpty ? null : trimmed;
    }

    /// <summary>Normalizes an optional value; a supplied blank value is invalid.</summary>
    public static string? Optional(string option, string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UsageException($"{option} cannot be empty. Provide a value or omit the option.");
        }

        return value.Trim();
    }

    /// <summary>Normalizes a required value.</summary>
    public static string Required(string option, string? value) =>
        Optional(option, value) ?? throw new UsageException($"{option} is required.");

    /// <summary>Validates and canonicalizes an optional GUID.</summary>
    public static string? OptionalGuid(string option, string? value)
    {
        var normalized = Optional(option, value);
        if (normalized is null)
        {
            return null;
        }

        if (!Guid.TryParse(normalized, out var guid))
        {
            throw new UsageException($"{option} must be a valid GUID.");
        }

        return guid.ToString("D");
    }

    /// <summary>Validates and canonicalizes a required GUID.</summary>
    public static string RequiredGuid(string option, string? value) =>
        OptionalGuid(option, Required(option, value))!;

    /// <summary>Validates an optional value against a case-insensitive allowed set.</summary>
    public static string? OptionalEnum(
        string option,
        string? value,
        IReadOnlyList<string> allowedValues,
        string? invalidValueRemediation = null)
    {
        ArgumentNullException.ThrowIfNull(allowedValues);

        var normalized = Optional(option, value);
        if (normalized is null)
        {
            return null;
        }

        return allowedValues.FirstOrDefault(allowed =>
                   string.Equals(allowed, normalized, StringComparison.OrdinalIgnoreCase))
               ?? throw new UsageException(
                   $"{option} is not supported. Supported values are: {string.Join(", ", allowedValues)}." +
                   (invalidValueRemediation is null ? string.Empty : $" {invalidValueRemediation}"));
    }

    /// <summary>Validates and canonicalizes a required value against a case-insensitive allowed set.</summary>
    public static string RequiredEnum(
        string option,
        string? value,
        IReadOnlyList<string> allowedValues,
        string? invalidValueRemediation = null)
    {
        ArgumentNullException.ThrowIfNull(allowedValues);

        var normalized = Required(option, value);
        return allowedValues.FirstOrDefault(allowed =>
                   string.Equals(allowed, normalized, StringComparison.OrdinalIgnoreCase))
               ?? throw new UsageException(
                   $"{option} is not supported. Supported values are: {string.Join(", ", allowedValues)}." +
                   (invalidValueRemediation is null ? string.Empty : $" {invalidValueRemediation}"));
    }

    /// <summary>Validates an optional absolute HTTPS URL.</summary>
    public static string? OptionalHttpsUrl(string option, string? value)
    {
        var normalized = Optional(option, value);
        if (normalized is not null &&
            (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new UsageException($"{option} must be a valid HTTPS URL.");
        }

        return normalized;
    }
}
