namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>The HTTP route shape used for an AMS agent-create request.</summary>
public enum AmsAgentCreationRouteMode
{
    /// <summary>
    /// The environment-scoped Power Platform API front door, which injects tenant and environment
    /// route segments before forwarding the request.
    /// </summary>
    PowerPlatformApiFrontDoor,

    /// <summary>
    /// A direct AMSDataPlane endpoint that requires tenant and environment route segments.
    /// </summary>
    DirectDataPlane,
}

/// <summary>
/// Transport input for one AMS agent-create request. Repository, owner, storage, and runtime-identity
/// coordinates are intentionally absent because AMS owns repository binding and deployment creation
/// owns runtime identity binding.
/// </summary>
public sealed record AmsAgentCreationTransportRequest
{
    public required Uri Endpoint { get; init; }
    public required AmsAgentCreationRouteMode RouteMode { get; init; }
    public required string Audience { get; init; }
    public required string TenantId { get; init; }
    public required string EnvironmentId { get; init; }
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
