namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>One invocation's literal query and selectors; no target or conversation history is persisted.</summary>
public sealed record LiveTestRequest
{
    public string? Query { get; init; }
    public string? Project { get; init; }
    public string? AgentId { get; init; }
    public string? AgentName { get; init; }
    public string? SchemaName { get; init; }
    public string? DeploymentId { get; init; }
    public string? DeploymentName { get; init; }
    public string? EnvironmentId { get; init; }
    public string? Tenant { get; init; }
    public string AgentType { get; init; } = "standard";
    public string? ConversationId { get; init; }
    public bool Json { get; init; }
    public bool Plain { get; init; }
    public bool NonInteractive { get; init; }
}

/// <summary>A dispatched command that exclusively owns its incremental output and terminal outcome.</summary>
public interface ILiveTestStreamingResult : ITextGridRenderable
{
    Task<int> RenderAsync(TextWriter stdout, TextWriter stderr, bool color, CancellationToken cancellationToken);

    IReadOnlyList<string> ITextGridRenderable.ToTextLines() =>
        throw new InvalidOperationException("A streaming result must own its output.");
}
