using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Routes a deployment channel request to the AMS deployment that the agent id, deployment id and home environment address.</summary>
/// <typeparam name="TChannelRequest">The channel request whose content rules AMS mode shares with local mode.</typeparam>
public sealed record AgentDeploymentAmsChannelRequest<TChannelRequest> : AgentDeploymentAmsRequest
    where TChannelRequest : DeploymentRequest
{
    /// <summary>The channel content, validated with the local-mode rules; its local deployment address is not used.</summary>
    public required TChannelRequest Channel { get; init; }
}

/// <summary>Lists the channel types AMS can create and edit on a deployment.</summary>
public sealed record AgentDeploymentAmsChannelTypesRequest : AgentDeploymentAmsRequest;

/// <summary>Asks AMS for a Direct Line token that starts one conversation with the agent of a deployment.</summary>
public sealed record AgentDeploymentAmsChannelTokenRequest : AgentDeploymentAmsRequest
{
    /// <summary>The channel that issues the token (from <c>--channel</c>); only Direct Line issues tokens.</summary>
    public string? ChannelName { get; init; }

    /// <summary>The site that issues the token (from <c>--site</c>), or null for the only site that can.</summary>
    public string? SiteName { get; init; }

    /// <summary>The user the token is bound to (from <c>--user-id</c>), or null for a user Direct Line picks.</summary>
    public string? UserId { get; init; }
}

/// <summary>Asks AMS for the callback URL and verify token that a channel's webhook settings in the third-party console need.</summary>
public sealed record AgentDeploymentAmsChannelWebhookRequest : AgentDeploymentAmsRequest
{
    /// <summary>The channel whose webhook values AMS hands off (from <c>--channel</c>); only Facebook has them.</summary>
    public string? ChannelName { get; init; }
}

/// <summary>A channel type AMS can create and edit, with the operations and parameters it accepts.</summary>
public sealed record DeploymentChannelType
{
    public required string ChannelType { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<string> Operations { get; init; }
    public required IReadOnlyList<DeploymentChannelTypeParameter> Parameters { get; init; }
}

/// <summary>One parameter of a channel type, as the AMS channel catalog declares it.</summary>
public sealed record DeploymentChannelTypeParameter
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool Required { get; init; }
    public bool ReadOnly { get; init; }
    public bool Clearable { get; init; }

    /// <summary>Whether the parameter is a credential that requests can set but AMS never shows.</summary>
    public bool IsSecret { get; init; }

    /// <summary>Whether the parameter selects an item of a list in a change of one item.</summary>
    public bool IsSelector { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AllowedValues { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? DefaultValue { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DeploymentChannelTypeParameter>? ItemParameters { get; init; }
}

/// <summary>A Direct Line token AMS issued for one conversation with the agent of a deployment.</summary>
public sealed record DeploymentChannelToken
{
    public required string Token { get; init; }
    public required string ConversationId { get; init; }
    public required int ExpiresInSeconds { get; init; }
    public required string SiteName { get; init; }

    /// <summary>The Direct Line host the client must use to start and refresh the conversation.</summary>
    public required string Endpoint { get; init; }
}

/// <summary>The values a channel's webhook settings in the third-party console need, as AMS read them from the deployment's bot.</summary>
public sealed record DeploymentChannelWebhook
{
    public required string CallbackUrl { get; init; }

    /// <summary>The token the third-party console sends back to prove the webhook is the bot's; it is a secret.</summary>
    public required string VerifyToken { get; init; }
}
