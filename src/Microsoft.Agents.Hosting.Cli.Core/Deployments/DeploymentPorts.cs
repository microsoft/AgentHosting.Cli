using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

public sealed record BotNameAvailability
{
    /// <summary>Whether the requested bot name is available to create.</summary>
    public required bool IsValid { get; init; }

    /// <summary>The service-provided reason when the name is unavailable.</summary>
    public string? Message { get; init; }
}

public sealed record AzureSubscriptionInfo
{
    public required string SubscriptionId { get; init; }
    public required string TenantId { get; init; }
    public string? DisplayName { get; init; }
}

public sealed record AzureResourceGroupInfo
{
    public required string Name { get; init; }
    public string? Location { get; init; }
}

/// <summary>
/// Executes the external operations required by the deployment lifecycle. The current
/// <see cref="DeploymentAdapter"/> implementation uses Azure Resource Manager directly.
/// </summary>
public interface IDeploymentTransport
{
    Task<AzureSubscriptionInfo> ResolveSubscriptionAsync(
        string? preferredSubscriptionId,
        string? targetTenantId,
        CancellationToken cancellationToken);
    Task<AzureResourceGroupInfo> ResolveResourceGroupAsync(
        string subscriptionId,
        string? preferredResourceGroup,
        string? targetTenantId,
        CancellationToken cancellationToken);
    Task<BotNameAvailability> CheckBotNameAvailabilityAsync(
        string name,
        string? targetTenantId,
        CancellationToken cancellationToken);
    Task<DeploymentBotResource> GetBotAsync(
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken);
    Task<DeploymentBotResource> CreateBotAsync(
        DeploymentCreateRequest request,
        CancellationToken cancellationToken);
    Task<DeploymentBotResource> UpdateBotAsync(
        DeploymentUpdateRequest request,
        CancellationToken cancellationToken);
    Task<bool> DeleteBotAsync(string botResourceId, string? targetTenantId, CancellationToken cancellationToken);
}

/// <summary>
/// The fully resolved provider-specific request used to upsert a Bot Service OAuth connection resource.
/// </summary>
public sealed record BotServiceOAuthUpsertRequest
{
    public required string ConnectionName { get; init; }
    public required string ServiceProviderId { get; init; }
    public required string BotResourceId { get; init; }
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? Scopes { get; init; }
    public IReadOnlyList<KeyValuePair<string, string?>> Parameters { get; init; } = [];
}

/// <summary>One parameter definition from the live Azure Bot Service OAuth provider catalogue.</summary>
public sealed record OAuthProviderParameterDefinition
{
    public required string Type { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public string? DefaultValue { get; init; }
    public bool IsRequired { get; init; }
}

/// <summary>A live Azure Bot Service OAuth provider definition.</summary>
public sealed record OAuthProviderDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public IReadOnlyList<OAuthProviderParameterDefinition> Parameters { get; init; } = [];
}

/// <summary>
/// Provider boundary for the Azure Bot Service OAuth provider catalogue and connection resources.
/// </summary>
public interface IDeploymentOAuthTransport
{
    Task<DeploymentOAuthConnection?> GetOAuthConnectionAsync(
        string connectionName,
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken);
    Task<DeploymentOAuthConnection> UpsertOAuthConnectionAsync(
        BotServiceOAuthUpsertRequest request,
        CancellationToken cancellationToken);
    Task<bool> DeleteOAuthConnectionAsync(
        string connectionName,
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<DeploymentOAuthConnection>> ListOAuthConnectionsAsync(
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<OAuthProviderDefinition>> ListServiceProvidersAsync(
        string subscriptionId,
        string? targetTenantId,
        CancellationToken cancellationToken);
}

public sealed record BotServiceChannelUpsertRequest
{
    /// <summary>The Azure Bot Service channel resource name, such as <c>SlackChannel</c>.</summary>
    public required string ChannelArmName { get; init; }

    public required string BotResourceId { get; init; }

    public string? TenantId { get; init; }

    public IReadOnlyDictionary<string, JsonElement> Parameters { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
}

public interface IDeploymentChannelTransport
{
    Task<DeploymentChannelRecord?> GetChannelAsync(
        string channelArmName,
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken);
    Task<DeploymentChannelRecord> UpsertChannelAsync(
        BotServiceChannelUpsertRequest request,
        CancellationToken cancellationToken);
    Task<bool> DeleteChannelAsync(
        string channelArmName,
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<DeploymentChannelRecord>> ListChannelsAsync(
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken);
}
