using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

public enum DeploymentType
{
    Dev,
    Test,
    Prod
}

/// <summary>The safe, persistable Azure Bot Service resource state owned by a deployment.</summary>
public sealed record DeploymentBotResource
{
    public required string Name { get; init; }
    /// <summary>The bot's ARM <c>msaAppId</c>.</summary>
    public required string EntraId { get; init; }
    /// <summary>The bot's ARM <c>msaAppType</c>.</summary>
    public required string EntraIdType { get; init; }
    public required string ResourceId { get; init; }
    public required string TenantId { get; init; }
    public required string Sku { get; init; }
    public required string Location { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Endpoint { get; init; }

    /// <summary>
    /// The redacted ARM response retained in memory for debug output. It is never persisted.
    /// </summary>
    [JsonIgnore]
    public JsonElement? RawArmResponse { get; init; }
}

/// <summary>
/// Safe canonical OAuth connection state owned by a deployment. Write-only secrets are never included.
/// </summary>
public sealed record DeploymentOAuthConnection
{
    public required string ResourceId { get; init; }
    public required string ConnectionName { get; init; }
    public required string ServiceProviderId { get; init; }
    public required string ServiceProviderDisplayName { get; init; }
    public string? ClientId { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// The redacted ARM response retained in memory for debug output. It is never persisted.
    /// </summary>
    [JsonIgnore]
    public JsonElement? RawArmResponse { get; init; }
}

/// <summary>A persisted, secret-free projection of a channel configured on a deployment.</summary>
public sealed record DeploymentChannelRecord
{
    public required string ResourceId { get; init; }
    public required string ChannelName { get; init; }
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    /// <summary>
    /// The redacted ARM response retained in memory for debug output. It is never persisted.
    /// </summary>
    [JsonIgnore]
    public JsonElement? RawArmResponse { get; init; }

    /// <summary>The Azure-returned authorization link, retained only for the current write result.</summary>
    [JsonIgnore]
    public Uri? AuthorizationUrl { get; init; }
}

/// <summary>
/// The canonical persisted CodeAgent deployment aggregate. It owns the deployment identity and
/// lifecycle metadata, the provisioned Bot Service resource and its Entra binding, and deployment
/// sub-resources such as OAuth connections. Provider response payloads and write-only secrets are not
/// part of this model.
/// </summary>
public sealed record CodeAgentDeployment
{
    public required string AgentId { get; init; }
    public required string DeploymentId { get; init; }
    public required string DeploymentName { get; init; }
    public required DeploymentType DeploymentType { get; init; }
    public bool IsDefault { get; init; }
    public required DeploymentBotResource Bot { get; init; }
    [JsonPropertyName("oauthConnections")]
    public IReadOnlyList<DeploymentOAuthConnection> OAuthConnections { get; init; } = [];
    public IReadOnlyList<DeploymentChannelRecord> Channels { get; init; } = [];

    internal static CodeAgentDeployment FromSnapshot(
        DeploymentBotResource snapshot,
        string deploymentId,
        string agentId,
        bool isDefault = false,
        DeploymentType deploymentType = DeploymentType.Dev,
        IReadOnlyList<DeploymentOAuthConnection>? oauthConnections = null,
        IReadOnlyList<DeploymentChannelRecord>? channels = null) =>
        new()
        {
            AgentId = agentId,
            DeploymentId = deploymentId,
            DeploymentName = snapshot.Name,
            DeploymentType = deploymentType,
            IsDefault = isDefault,
            Bot = snapshot,
            OAuthConnections = oauthConnections ?? [],
            Channels = channels ?? [],
        };

    internal CodeAgentDeployment WithSnapshot(DeploymentBotResource snapshot) =>
        FromSnapshot(
            snapshot,
            DeploymentId,
            AgentId,
            IsDefault,
            DeploymentType,
            OAuthConnections,
            Channels);

    internal CodeAgentDeployment WithMetadata(bool? isDefault, DeploymentType? deploymentType) =>
        this with
        {
            IsDefault = isDefault ?? IsDefault,
            DeploymentType = deploymentType ?? DeploymentType,
        };
}
