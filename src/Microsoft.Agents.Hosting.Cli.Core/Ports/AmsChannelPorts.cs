using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>One channel of the bot AMS bound to a deployment.</summary>
public sealed record AmsChannelTransportRequest
{
    public required AmsDeploymentReadTransportRequest Deployment { get; init; }
    public required string ChannelType { get; init; }
}

/// <summary>A channel to create on the bot AMS bound to a deployment, with typed properties that carry its complete lists of items.</summary>
public sealed record AmsChannelCreateTransportRequest
{
    public required AmsDeploymentReadTransportRequest Deployment { get; init; }
    public required string ChannelType { get; init; }
    public required IReadOnlyDictionary<string, JsonElement> Properties { get; init; }
}

/// <summary>A change to one channel: typed properties AMS merges over the live channel, whole lists included, or one item change AMS applies to the live list.</summary>
public sealed record AmsChannelUpdateTransportRequest
{
    public required AmsDeploymentReadTransportRequest Deployment { get; init; }
    public required string ChannelType { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? Properties { get; init; }
    public AmsChannelItemChange? ItemChange { get; init; }
}

/// <summary>One change of a channel's list, <c>add</c>, <c>set</c> or <c>remove</c>, with the one item it names.</summary>
public sealed record AmsChannelItemChange(string Operation, IReadOnlyList<JsonElement> Items);

/// <summary>The Direct Line site and user a token is requested for.</summary>
public sealed record AmsDirectLineTokenTransportRequest
{
    public required AmsDeploymentReadTransportRequest Deployment { get; init; }
    public string? SiteName { get; init; }
    public string? UserId { get; init; }
}

/// <summary>A channel AMS returned from a write, with whether the write created it and the warnings of a site configuration that did not complete.</summary>
public sealed record AmsChannelWriteResult
{
    public required DeploymentChannelRecord Channel { get; init; }
    public bool IsCreated { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Manages the channels of the Azure Bot Service instance AMS bound to a code-agent deployment.</summary>
public interface IAmsChannelTransport
{
    /// <summary>Lists the channel types AMS can create and edit on the deployment.</summary>
    Task<IReadOnlyList<DeploymentChannelType>> ListTypesAsync(AmsDeploymentReadTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Lists the channels of the deployment's bot that AMS manages.</summary>
    Task<IReadOnlyList<DeploymentChannelRecord>> ListAsync(AmsDeploymentReadTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Reads one channel of the deployment's bot.</summary>
    Task<DeploymentChannelRecord> GetAsync(AmsChannelTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Creates a channel, or returns the existing channel unchanged.</summary>
    Task<AmsChannelWriteResult> CreateAsync(AmsChannelCreateTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Changes a channel and returns it as AMS wrote it.</summary>
    Task<AmsChannelWriteResult> UpdateAsync(AmsChannelUpdateTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes a channel; an absent channel counts as deleted.</summary>
    Task DeleteAsync(AmsChannelTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Asks AMS for a Direct Line token; the site key never leaves AMS.</summary>
    Task<DeploymentChannelToken> CreateDirectLineTokenAsync(AmsDirectLineTokenTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Asks AMS for the callback URL and verify token that a channel's webhook settings in the third-party console need.</summary>
    Task<DeploymentChannelWebhook> GetWebhookAsync(AmsChannelTransportRequest request, CancellationToken cancellationToken);
}
