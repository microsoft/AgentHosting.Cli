namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>Input for <c>msagent env list</c>, which takes no options.</summary>
public sealed record EnvironmentListRequest;

/// <summary>Input for <c>msagent env select</c>.</summary>
public sealed record EnvironmentSelectRequest
{
    /// <summary>The environment to make active.</summary>
    public required string EnvironmentId { get; init; }
}
