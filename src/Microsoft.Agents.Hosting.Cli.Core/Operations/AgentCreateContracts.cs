namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Input for <c>msagent agent create</c>.</summary>
public sealed record AgentCreateRequest
{
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? EnvironmentId { get; init; }
    public string? Language { get; init; }
    public string? LanguageVersion { get; init; }
    public string? IdempotencyKey { get; init; }
}

/// <summary>
/// The safe, service-owned coordinates returned after AMS creates the agent and its repository.
/// </summary>
public sealed record AgentCreateResult : ITextGridRenderable
{
    public int SchemaVersion { get; init; } = 1;
    public required string Status { get; init; }
    public required string TenantId { get; init; }
    public required string EnvironmentId { get; init; }
    public required string AgentId { get; init; }
    public required string DisplayName { get; init; }
    public required string RepositoryId { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        "Agent and service-owned repository created.",
        $"  agent: {DisplayName} ({AgentId})",
        $"  environment: {EnvironmentId} (tenant {TenantId})",
        $"  repository: {RepositoryId}",
        "This reports record and repository creation only. It does not report package publication or runtime readiness.",
    ];
}
