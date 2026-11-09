using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

public enum DeploymentOperationStatus
{
    Created,
    Existed,
    Retrieved,
    Updated,
    Listed,
    Deleted,
    WriteAccepted,
}

/// <summary>Shared deployment addressing and Azure target options.</summary>
public record DeploymentRequest
{
    /// <summary>The deployment id (from <c>--deployment-id</c>).</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The owning agent id when addressed by <c>--agent-id</c>/<c>--deployment-name</c>.</summary>
    public string? AgentId { get; init; }

    /// <summary>The deployment name when addressed by <c>--agent-id</c>/<c>--deployment-name</c>.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>The target Azure resource group (from <c>--resource-group</c>); auto-resolved when the current subscription has exactly one. </summary>
    public string? ResourceGroup { get; init; }

    /// <summary>Azure subscription id (from <c>--subscription</c>); auto-resolved from sign-in.</summary>
    public string? SubscriptionId { get; init; }

    /// <summary>Azure tenant id (from <c>--tenant</c>); auto-resolved from sign-in.</summary>
    public string? TenantId { get; init; }

    /// <summary>The project directory holding the deployment records (from <c>--project</c>).</summary>
    public string? Project { get; init; }
}

/// <summary>Raw CLI request for <c>msagent deployment show</c>.</summary>
public sealed record DeploymentShowRequest : DeploymentRequest;

/// <summary>Raw CLI request for <c>msagent deployment create</c>.</summary>
public sealed record DeploymentCreateRequest
{
    /// <summary> The agent id (from <c>--agent-id</c>).</summary>
    public required string AgentId { get; init; }

    /// <summary>The deployment name (from <c>--deployment-name</c>).</summary>
    public required string DeploymentName { get; init; }

    /// <summary>The Entra identity (client) id (from <c>--entra-id</c>).</summary>
    public required string EntraId { get; init; }

    /// <summary>The Entra identity type (from <c>--entra-id-type</c>).</summary>
    public required string EntraIdType { get; init; }

    /// <summary>The deployment tag: <c>Dev</c>, <c>Test</c>, or <c>Prod</c> (from <c>--deployment-type</c>).</summary>
    public required string DeploymentType { get; init; }

    /// <summary>Whether the deployment is the agent's default (from <c>--default</c>).</summary>
    public bool? IsDefault { get; init; }

    /// <summary>The target Azure resource group (from <c>--resource-group</c>); auto-resolved when the current subscription has exactly one. </summary>
    public string? ResourceGroup { get; init; }

    /// <summary>Azure subscription id (from <c>--subscription</c>); auto-resolved from sign-in.</summary>
    public string? SubscriptionId { get; init; }

    /// <summary>Azure tenant id (from <c>--tenant</c>); auto-resolved from sign-in.</summary>
    public string? TenantId { get; init; }

    /// <summary>The deployment SKU (from <c>--sku</c>): <c>F0</c> or <c>S1</c>.</summary>
    public string? Sku { get; init; }

    /// <summary>The deployment location (from <c>--location</c>).</summary>
    public string? Location { get; init; }

    /// <summary>The deployment display name (from <c>--display-name</c>).</summary>
    public string? DisplayName { get; init; }

    /// <summary>The deployment description (from <c>--description</c>).</summary>
    public string? Description { get; init; }

    /// <summary>The deployment messaging endpoint (from <c>--endpoint-url</c>).</summary>
    public string? Endpoint { get; init; }

    /// <summary>The project directory receiving the deployment record (from <c>--project</c>).</summary>
    public string? Project { get; init; }
}

/// <summary>Raw CLI request for <c>msagent deployment update</c>.</summary>
public sealed record DeploymentUpdateRequest : DeploymentRequest
{
    /// <summary>The deployment tag: <c>Dev</c>, <c>Test</c>, or <c>Prod</c> (from <c>--deployment-type</c>); left unchanged when omitted.</summary>
    public string? DeploymentType { get; init; }

    /// <summary>Whether the deployment is the agent's default (from <c>--default</c>).</summary>
    public bool? IsDefault { get; init; }

    /// <summary>The deployment SKU (from <c>--sku</c>): <c>F0</c> or <c>S1</c>.</summary>
    public string? Sku { get; init; }

    /// <summary>The deployment display name (from <c>--display-name</c>).</summary>
    public string? DisplayName { get; init; }

    /// <summary>The deployment description (from <c>--description</c>).</summary>
    public string? Description { get; init; }

    /// <summary>The deployment messaging endpoint (from <c>--endpoint-url</c>).</summary>
    public string? Endpoint { get; init; }
}

/// <summary>
/// Raw CLI request for <c>msagent deployment delete</c>.
/// Distinct from the show request so each verb resolves its own command handler.
/// </summary>
public sealed record DeploymentDeleteRequest : DeploymentRequest;

/// <summary>A deployment operation result exposed to the CLI renderer without erasing its typed payload.</summary>
public interface IDeploymentResult
{
    DeploymentOperationStatus Status { get; }
    object? Payload { get; }
    JsonElement? ArmResponse { get; }

    string TargetName { get; }
    string? TargetInstanceName { get; }
    IReadOnlyList<string> Warnings { get; }
    Uri? AuthorizationUrl { get; }
}

/// <summary>Result shared by deployment CRUD and deployment sub-resources such as OAuth connections and channels.</summary>
public sealed record DeploymentResult<TPayload> : IDeploymentResult
{
    private readonly JsonElement? _armResponse;

    public required DeploymentOperationStatus Status { get; init; }
    public TPayload? Payload { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public Uri? AuthorizationUrl { get; init; }
    object? IDeploymentResult.Payload => Payload;
    public JsonElement? ArmResponse
    {
        get => _armResponse ?? (Payload switch
        {
            CodeAgentDeployment deployment => deployment.Bot.RawArmResponse,
            DeploymentOAuthConnection connection => connection.RawArmResponse,
            DeploymentOAuthConnection[] connections => JsonSerializer.SerializeToElement(
                connections
                    .Where(connection => connection.RawArmResponse is not null)
                    .Select(connection => connection.RawArmResponse!.Value)
                    .ToArray()),
            DeploymentChannelRecord channel => channel.RawArmResponse,
            DeploymentChannelRecord[] channels => JsonSerializer.SerializeToElement(
                channels
                    .Where(channel => channel.RawArmResponse is not null)
                    .Select(channel => channel.RawArmResponse!.Value)
                    .ToArray()),
            _ => null,
        });
        init => _armResponse = value;
    }

    [JsonIgnore]
    public string TargetName { get; init; } = string.Empty;
    [JsonIgnore]
    public string? TargetInstanceName { get; init; }

}

/// <summary>Shared request for a deployment OAuth connection.</summary>
public record DeploymentOAuthRequest : DeploymentRequest
{
    /// <summary>The OAuth connection setting name (from <c>--connection</c>).</summary>
    public required string ConnectionName { get; init; }
}

/// <summary>Raw CLI request for <c>msagent deployment oauth show</c>.</summary>
public sealed record DeploymentOAuthShowRequest : DeploymentOAuthRequest;

/// <summary>Raw CLI request for <c>msagent deployment oauth update</c>.</summary>
public record DeploymentOAuthUpdateRequest : DeploymentOAuthRequest
{
    /// <summary>The application id supplied through <c>--client-id</c>.</summary>
    public string? ClientId { get; init; }

    /// <summary>The write-only application secret supplied through <c>--client-secret</c>.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>The space-separated scopes supplied through <c>--scopes</c>.</summary>
    public string? Scopes { get; init; }

    /// <summary>
    /// Provider-specific <c>Key=Value</c> assignments from <c>--parameters</c>. Keys are
    /// validated against the live Azure Bot Service provider catalogue.
    /// </summary>
    public IReadOnlyList<string> Parameters { get; init; } = [];
}

/// <summary>
/// Raw CLI request for <c>msagent deployment oauth create</c>.
/// The provider is selected during creation and becomes immutable afterwards.
/// </summary>
public sealed record DeploymentOAuthCreateRequest : DeploymentOAuthUpdateRequest
{
    public required string ServiceProviderName { get; init; }
}

/// <summary>
/// Raw CLI request for <c>msagent deployment oauth delete</c>.
/// Distinct from <see cref="DeploymentOAuthRequest"/> so each verb binds its own command handler.
/// </summary>
public sealed record DeploymentOAuthDeleteRequest : DeploymentOAuthRequest;

/// <summary>
/// Raw CLI request for <c>msagent deployment oauth list</c>.
/// Distinct from <see cref="DeploymentRequest"/> so each verb binds its own command handler.
/// </summary>
public sealed record DeploymentOAuthListRequest : DeploymentRequest;

/// <summary>
/// Raw CLI request for <c>msagent deployment oauth list-providers</c>.
/// Lists or filters the live Azure Bot Service OAuth provider catalogue for a subscription.
/// </summary>
public sealed record DeploymentOAuthProviderListRequest
{
    public string? SubscriptionId { get; init; }
    public string? TenantId { get; init; }
    public string? ServiceProviderName { get; init; }
}

/// <summary>Shared request for a deployment channel.</summary>
public record DeploymentChannelRequest : DeploymentRequest
{
    /// <summary>The channel name (from <c>--channel</c>).</summary>
    public required string ChannelName { get; init; }
}

/// <summary>Raw CLI request for <c>msagent deployment channel show</c>.</summary>
public sealed record DeploymentChannelShowRequest : DeploymentChannelRequest;

/// <summary>Raw CLI request for <c>msagent deployment channel create</c>.</summary>
public record DeploymentChannelCreateRequest : DeploymentChannelRequest
{
    public bool NonInteractive { get; init; }

    /// <summary>Channel-level <c>Key=Value</c> assignments from <c>--parameters</c>.</summary>
    public IReadOnlyList<string> Parameters { get; init; } = [];
}

/// <summary>Raw CLI request for <c>msagent deployment channel update</c>.</summary>
public sealed record DeploymentChannelUpdateRequest : DeploymentChannelCreateRequest
{
    public IReadOnlyList<string> AddSite { get; init; } = [];
    public IReadOnlyList<string> SetSite { get; init; } = [];
    public IReadOnlyList<string> RemoveSite { get; init; } = [];
    public IReadOnlyList<string> AddPage { get; init; } = [];
    public IReadOnlyList<string> SetPage { get; init; } = [];
    public IReadOnlyList<string> RemovePage { get; init; } = [];
    public IReadOnlyList<string> AddRegistration { get; init; } = [];
    public IReadOnlyList<string> SetRegistration { get; init; } = [];
    public IReadOnlyList<string> RemoveRegistration { get; init; } = [];
}

/// <summary>Raw CLI request for <c>msagent deployment channel delete</c>.</summary>
public sealed record DeploymentChannelDeleteRequest : DeploymentChannelRequest;

/// <summary>Raw CLI request for <c>msagent deployment channel list</c>.</summary>
public sealed record DeploymentChannelListRequest : DeploymentRequest;