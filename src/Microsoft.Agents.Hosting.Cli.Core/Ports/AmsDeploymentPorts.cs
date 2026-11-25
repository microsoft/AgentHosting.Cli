using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>A deployment record to write, with no package and no deployed endpoint.</summary>
public sealed record AmsDeploymentCreateTransportRequest
{
    /// <summary>The control-plane endpoint addressed through the agent's home environment.</summary>
    public required AmsServiceTarget ServiceTarget { get; init; }
    public required string TargetEnvironmentId { get; init; }
    public required string AgentId { get; init; }
    public required string DeploymentName { get; init; }
    public required string DeploymentType { get; init; }
    public required string EntraId { get; init; }
    public required string EntraIdType { get; init; }
    public required string Language { get; init; }
    public required string LanguageVersion { get; init; }

    /// <summary>The dev endpoint that makes the record a dev endpoint deployment, or null for a package deployment.</summary>
    public string? RequestedEndpoint { get; init; }
}

/// <summary>A deployment exactly as AMS returned it from a create, read, list, update or deploy.</summary>
public sealed record AmsDeploymentResult
{
    /// <summary>The state of a record that was never deployed.</summary>
    public const string NotDeployedState = "NotDeployed";

    /// <summary>The state of a record whose deploy is running.</summary>
    public const string DeployingState = "Deploying";

    /// <summary>The state of a record whose last deploy completed.</summary>
    public const string DeployedState = "Deployed";

    /// <summary>The state of a record that is being deleted.</summary>
    public const string DeletingState = "Deleting";

    public string? ClientRequestId { get; init; }
    public string? ServiceRequestId { get; init; }
    public required string DeploymentId { get; init; }
    public required string AgentId { get; init; }
    public required string TenantId { get; init; }
    public required string HomeEnvironmentId { get; init; }
    public required string TargetEnvironmentId { get; init; }
    public string? ETag { get; init; }

    /// <summary>NotDeployed, Deploying, Deployed or Deleting.</summary>
    public required string State { get; init; }
    public string? PackageRevisionId { get; init; }
    public string? PackageSha256 { get; init; }
    public long? PackageSizeInBytes { get; init; }

    /// <summary>The commit of the deployed package, or null before a package deploy.</summary>
    public string? CommitId { get; init; }
    public required string AgentType { get; init; }
    public required string DeploymentName { get; init; }
    public required string DeploymentType { get; init; }
    public required string EntraId { get; init; }
    public required string EntraIdType { get; init; }
    public required string Language { get; init; }
    public required string LanguageVersion { get; init; }

    /// <summary>The deployed messaging endpoint, or null before the first deploy.</summary>
    public string? RuntimeEndpoint { get; init; }

    /// <summary>The dev endpoint the next deploy without a commit puts live, or null when none is requested.</summary>
    public string? RequestedEndpoint { get; init; }
    public bool IsCustomEndpoint { get; init; }
    public bool IsQuarantined { get; init; }

    /// <summary>Whether AMS has started deleting the deployment and refuses changes to it.</summary>
    public bool IsDeleting { get; init; }
    public string? ManagedIdentityId { get; init; }
    public AgentDeploymentBotService? BotService { get; init; }

    /// <summary>Whether the deployment runs, or is being put live, in its target environment.</summary>
    public bool IsLive => State is DeployingState or DeployedState;
}

/// <summary>Every deployment of an agent, with the bots kept from deleted records of live deployments.</summary>
public sealed record AmsDeploymentListResult
{
    public required IReadOnlyList<AmsDeploymentResult> Deployments { get; init; }
    public required IReadOnlyList<AgentAmsKeptBot> KeptBots { get; init; }
}

/// <summary>Coordinates for reading an existing deployment through the public control plane.</summary>
public record AmsDeploymentReadTransportRequest
{
    /// <summary>The control-plane endpoint addressed through the agent's home environment.</summary>
    public required AmsServiceTarget ServiceTarget { get; init; }
    public required string AgentId { get; init; }
    public required string DeploymentId { get; init; }
}

/// <summary>A conditional change of a deployment record's name or requested dev endpoint.</summary>
public sealed record AmsDeploymentUpdateTransportRequest : AmsDeploymentReadTransportRequest
{
    public string? ETag { get; init; }
    public string? DeploymentName { get; init; }
    public string? RequestedEndpoint { get; init; }
}

/// <summary>A conditional deploy of what a deployment record holds, with an optional commit of the agent's uploads.</summary>
public sealed record AmsDeploymentDeployTransportRequest : AmsDeploymentReadTransportRequest
{
    public string? ETag { get; init; }
    public string? CommitId { get; init; }
}

/// <summary>A conditional removal of a deployment record.</summary>
public sealed record AmsDeploymentDeleteTransportRequest : AmsDeploymentReadTransportRequest
{
    public string? ETag { get; init; }
}

/// <summary>Reads, writes, deploys and deletes the deployment records of AMS agents.</summary>
public interface IAmsDeploymentTransport
{
    /// <summary>Reads one deployment.</summary>
    Task<AmsDeploymentResult> ReadAsync(AmsDeploymentReadTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Lists every deployment of an agent with the bots it keeps, reading all pages.</summary>
    Task<AmsDeploymentListResult> ListAsync(AmsAgentReadTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Writes a new deployment record without deploying it.</summary>
    Task<AmsDeploymentResult> CreateAsync(AmsDeploymentCreateTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Renames a deployment record or changes its requested dev endpoint.</summary>
    Task<AmsDeploymentResult> UpdateAsync(AmsDeploymentUpdateTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Puts what a deployment record holds live.</summary>
    Task<AmsDeploymentResult> DeployAsync(AmsDeploymentDeployTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Removes a deployment record, keeping what a live deployment runs.</summary>
    Task DeleteAsync(AmsDeploymentDeleteTransportRequest request, CancellationToken cancellationToken);
}
