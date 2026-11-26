using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>Addresses one agent through an AMS endpoint of its home environment.</summary>
public record AmsAgentReadTransportRequest
{
    /// <summary>The AMS endpoint of the agent's home environment that the call goes to.</summary>
    public required AmsServiceTarget ServiceTarget { get; init; }
    public required string AgentId { get; init; }
}

/// <summary>A conditional change of an agent's name or description, guarded by the entity tag of the preceding read.</summary>
public sealed record AmsAgentUpdateTransportRequest : AmsAgentReadTransportRequest
{
    public string? ETag { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
}

/// <summary>The deletion of an agent with every deployment record and bot it owns, guarded by the entity tag of the preceding read.</summary>
public sealed record AmsAgentCascadeDeleteTransportRequest : AmsAgentReadTransportRequest
{
    public string? ETag { get; init; }
}

/// <summary>An agent exactly as AMS returned it from a read, a list or an update.</summary>
public sealed record AmsAgentReadResult
{
    public required string AgentId { get; init; }
    public required string TenantId { get; init; }
    public required string EnvironmentId { get; init; }
    public required string AgentType { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? ETag { get; init; }
    public string? State { get; init; }
    public bool IsDeleting { get; init; }
    public string? RepositoryId { get; init; }
    public string? RepositoryBindingStatus { get; init; }
    public string? Language { get; init; }
    public string? LanguageVersion { get; init; }
    public DateTimeOffset? CreatedTime { get; init; }
    public DateTimeOffset? ChangedTime { get; init; }
}

/// <summary>One upload of an agent's package, as the package list returns it.</summary>
public sealed record AmsAgentPackage
{
    public required string PackageRevisionId { get; init; }

    /// <summary>The commit of the upload, or null for an upload made before AMS recorded commits.</summary>
    public string? CommitId { get; init; }
    public string? Sha256 { get; init; }
    public long? SizeInBytes { get; init; }
    public DateTimeOffset? CreatedTime { get; init; }
}

/// <summary>What AMS removed when it deleted an agent with its deployments.</summary>
public sealed record AmsAgentCascadeDeleteResult
{
    public required string AgentId { get; init; }
    public required IReadOnlyList<AgentAmsRemovedDeployment> Deployments { get; init; }
    public required IReadOnlyList<AgentAmsRemovedDeployment> KeptBots { get; init; }
}

/// <summary>Reads the agents of an AMS home environment.</summary>
public interface IAmsAgentReadTransport
{
    /// <summary>Reads one agent.</summary>
    Task<AmsAgentReadResult> GetAsync(AmsAgentReadTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Lists every agent of the home environment, reading all pages.</summary>
    Task<IReadOnlyList<AmsAgentReadResult>> ListAsync(AmsServiceTarget serviceTarget, CancellationToken cancellationToken);
}

/// <summary>Changes and deletes AMS agents and lists their package uploads.</summary>
public interface IAmsAgentTransport
{
    /// <summary>Changes an agent's name or description.</summary>
    Task<AmsAgentReadResult> UpdateAsync(AmsAgentUpdateTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes an agent with its deployment records and bots.</summary>
    Task<AmsAgentCascadeDeleteResult> CascadeDeleteAsync(AmsAgentCascadeDeleteTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Lists every package upload of an agent, newest first, reading all pages.</summary>
    Task<IReadOnlyList<AmsAgentPackage>> ListPackagesAsync(AmsAgentReadTransportRequest request, CancellationToken cancellationToken);
}
