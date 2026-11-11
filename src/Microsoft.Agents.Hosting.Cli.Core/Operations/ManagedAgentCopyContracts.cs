using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Input for <c>msagent agent copy</c>.</summary>
public sealed record ManagedAgentCopyRequest
{
    /// <summary>The copy's display name.</summary>
    public string? Name { get; init; }

    /// <summary>The cloud agent to copy as it is in its environment, by identifier, instead of a local agent.</summary>
    public string? CloneAgentId { get; init; }

    /// <summary>The environment holding the cloud agent, or the one to copy a local agent in when it names none.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>An explicit schema name for the copy, generated from its name when omitted.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The local agent to copy, by its folder or project, or for a cloud agent the project the copy is registered in.</summary>
    public string? Project { get; init; }
}

/// <summary>Result of <c>msagent agent copy</c>.</summary>
public sealed record ManagedAgentCopyResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>agent-copied</c>).</summary>
    public string Status { get; init; } = "agent-copied";

    /// <summary>The copy's managed-agent identifier.</summary>
    public required string AgentId { get; init; }

    /// <summary>The copy's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The copy's schema name.</summary>
    public required string SchemaName { get; init; }

    /// <summary>Which version of the source was copied.</summary>
    public required string Source { get; init; }

    /// <summary>The source agent's schema name.</summary>
    public required string SourceSchemaName { get; init; }

    /// <summary>The source agent's display name.</summary>
    public string? SourceDisplayName { get; init; }

    /// <summary>The source's cloud agent identifier, when it had one, kept as provenance only.</summary>
    public string? SourceMcsAgentId { get; init; }

    /// <summary>The environment the copy was prepared for and is registered with.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The tenant owning that environment.</summary>
    public required string TenantId { get; init; }

    /// <summary>The project the copy was registered in.</summary>
    public required string ProjectDirectory { get; init; }

    /// <summary>The copy's workspace.</summary>
    public required string WorkspaceDirectory { get; init; }

    /// <summary>Where the record was written.</summary>
    public required string ConfigPath { get; init; }

    /// <summary>Every identity the copy maps from its source.</summary>
    public IReadOnlyList<CopilotCopyIdentity> Identities { get; init; } = [];

    /// <summary>The copied workflows, which deploy as drafts.</summary>
    public IReadOnlyList<string> DraftWorkflows { get; init; } = [];

    /// <summary>How many workspace files were rewritten.</summary>
    public int RewrittenFileCount { get; init; }

    /// <summary>Advisories that did not stop the copy.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        var lines = new List<string>
        {
            $"Copied {SourceSchemaName} ({Source}) into {SchemaName} ({DisplayName}). Nothing was sent to Copilot Studio.",
            $"  agent id      {AgentId}",
            $"  environment   {EnvironmentId}",
            $"  workspace     {WorkspaceDirectory}",
        };

        lines.AddRange(Warnings.Select(warning => $"  warning       {warning}"));

        return lines;
    }
}

