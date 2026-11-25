namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>
/// Transport input for one AMS agent-create request. Repository, owner, storage, and runtime-identity
/// coordinates are intentionally absent because AMS owns repository binding and deployment creation
/// owns runtime identity binding.
/// </summary>
public sealed record AmsAgentCreationTransportRequest
{
    /// <summary>The data-plane endpoint of the environment the agent is created in.</summary>
    public required AmsServiceTarget ServiceTarget { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required int AgentType { get; init; }
    public required int Language { get; init; }
    public required string LanguageVersion { get; init; }
    public required string Origin { get; init; }
}

/// <summary>The required fields from a successful AMS agent-create response.</summary>
public sealed record AmsAgentCreationResult
{
    public required string AgentId { get; init; }
    public required string TenantId { get; init; }
    public required string EnvironmentId { get; init; }
    public required string DisplayName { get; init; }
    public required string RepositoryId { get; init; }
}

/// <summary>The raw transport seam for AMS agent creation.</summary>
public interface IAmsAgentCreationTransport
{
    Task<AmsAgentCreationResult> CreateAsync(
        AmsAgentCreationTransportRequest request,
        CancellationToken cancellationToken);
}
