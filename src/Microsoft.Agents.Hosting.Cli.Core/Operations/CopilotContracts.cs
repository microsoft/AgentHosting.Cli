using System.Text.Json.Serialization;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>One component difference between a workspace and the agent it is connected to.</summary>
public sealed record CopilotChange
{
    /// <summary>How the component differs: <c>created</c>, <c>updated</c>, or <c>deleted</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The component family, for example <c>topic</c>, <c>action</c>, or <c>knowledge</c>.</summary>
    public required string ComponentType { get; init; }

    /// <summary>The component's schema name.</summary>
    public required string SchemaName { get; init; }

    /// <summary>The component's display name, when it has one.</summary>
    public string? DisplayName { get; init; }
}

/// <summary>The values a <see cref="CopilotChange.Kind"/> can carry.</summary>
public static class CopilotChangeKinds
{
    /// <summary>The component exists on one side only and is absent from the last-synced baseline.</summary>
    public const string Created = "created";

    /// <summary>The component exists in the last-synced baseline and its content differs.</summary>
    public const string Updated = "updated";

    /// <summary>The component exists in the last-synced baseline and has since been removed.</summary>
    public const string Deleted = "deleted";
}

/// <summary>One agent as the environment inventory reports it. Carries no token material.</summary>
public sealed record CopilotAgentSummary
{
    /// <summary>The agent identifier.</summary>
    public required string AgentId { get; init; }

    /// <summary>The agent's schema name, unique within the environment.</summary>
    public required string SchemaName { get; init; }

    /// <summary>The agent's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The agent's provisioning state as reported by the service.</summary>
    public string? State { get; init; }

    /// <summary>When the agent was last modified, as reported by the service.</summary>
    public DateTimeOffset? LastChangedOn { get; init; }

    /// <summary>Whether the agent came from a managed solution and cannot be written to.</summary>
    public bool IsManaged { get; init; }
}


/// <summary>One workspace produced by a clone.</summary>
public sealed record CopilotClonedWorkspace
{
    /// <summary>What was cloned: <c>agent</c> or <c>component-collection</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The workspace directory that was created.</summary>
    public required string Path { get; init; }

    /// <summary>The display name the directory was derived from.</summary>
    public required string DisplayName { get; init; }
}


/// <summary>Input for <c>msagent agent pull</c>.</summary>
public sealed record CopilotPullRequest
{
    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    public string? ProjectDirectory { get; init; }
}

/// <summary>Result of <c>msagent agent pull</c>.</summary>
public sealed record CopilotPullResult
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>pulled</c> or <c>up-to-date</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The workspace that was updated.</summary>
    public required string WorkspacePath { get; init; }

    /// <summary>The agent the workspace is connected to.</summary>
    public required string AgentId { get; init; }

    /// <summary>The remote changes applied to the workspace.</summary>
    public required IReadOnlyList<CopilotChange> Changes { get; init; }

    /// <summary>How many changes were applied.</summary>
    public int ChangeCount { get; init; }

    /// <summary>Files the merge left with unresolved conflict markers.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Input for <c>msagent agent deploy</c>.</summary>
public sealed record CopilotPushRequest
{
    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>
    /// Uploads local content even where the cloud changed the same component, overwriting it.
    ///
    /// Without this a push that would overwrite remote work is refused, because the workspace cannot
    /// tell whose edit is newer and the remote one may be somebody else's.
    /// </summary>
    public bool Overwrite { get; init; }
}

/// <summary>Result of <c>msagent agent deploy</c>.</summary>
public sealed record CopilotPushResult
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>pushed</c> or <c>no-local-changes</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The workspace that was pushed.</summary>
    public required string WorkspacePath { get; init; }

    /// <summary>The agent the workspace is connected to.</summary>
    public required string AgentId { get; init; }

    /// <summary>The local changes that were uploaded.</summary>
    public required IReadOnlyList<CopilotChange> Changes { get; init; }

    /// <summary>How many changes were uploaded.</summary>
    public int ChangeCount { get; init; }

    /// <summary>
    /// How many knowledge and attachment files were sent.
    ///
    /// Counted apart from <see cref="ChangeCount"/> because the file layer uploads every file backed by
    /// a cloud component rather than only the ones that differ.
    /// </summary>
    public int UploadedFileCount { get; init; }

    /// <summary>Connectors, workflows and prompts sent alongside the authored components.</summary>
    public int AuxiliaryChangeCount { get; init; }

    /// <summary>Resources the service declined without failing the push.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Input for <c>msagent agent create</c>.</summary>
public sealed record CopilotInitRequest
{
    /// <summary>The agent's display name.</summary>
    public string? Name { get; init; }

    /// <summary>The publisher prefix the schema name is derived from.</summary>
    public string? PublisherPrefix { get; init; }

    /// <summary>An explicit schema name, overriding the derived one.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The agent's instructions.</summary>
    public string? Instructions { get; init; }

    /// <summary>The directory to scaffold into. Defaults to the current directory.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>The authoring shape to scaffold. Defaults to <c>cli-copilot</c>.</summary>
    public string? AuthoringMode { get; init; }
}

/// <summary>Result of <c>msagent agent create</c>.</summary>
public sealed record CopilotInitResult
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>initialized</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The directory that was scaffolded.</summary>
    public required string ProjectDirectory { get; init; }

    /// <summary>The schema name written into the workspace.</summary>
    public required string SchemaName { get; init; }

    /// <summary>The template that was instantiated.</summary>
    public required string Template { get; init; }

    /// <summary>The authoring shape that was scaffolded.</summary>
    public required string AuthoringMode { get; init; }

    /// <summary>The files that were created, relative to the project directory.</summary>
    public required IReadOnlyList<string> CreatedFiles { get; init; }
}


/// <summary>Input for <c>msagent agent deploy --publish</c>.</summary>
public sealed record CopilotPublishRequest
{
    /// <summary>The agent to publish. When omitted, the connected workspace is used.</summary>
    public string? Bot { get; init; }

    /// <summary>The workspace directory. Defaults to the current directory. Ignored when <see cref="Bot"/> is supplied.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>Confirms the publish. Without it the command reports what it would publish.</summary>
    public bool Force { get; init; }

    /// <summary>How long to wait between polls.</summary>
    public TimeSpan? PollingInterval { get; init; }

    /// <summary>How long to poll before giving up.</summary>
    public TimeSpan? MaximumWait { get; init; }

    /// <summary>An explicit environment id, overriding the workspace's and the active selection.</summary>
    public string? Environment { get; init; }
}

/// <summary>Result of <c>msagent agent deploy --publish</c>.</summary>
public sealed record CopilotPublishResult
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>published</c> or <c>confirmation-required</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The agent that was published, or that publishing was declined for.</summary>
    public required string AgentId { get; init; }

    /// <summary>The agent's schema name.</summary>
    public required string SchemaName { get; init; }

    /// <summary>The environment the agent belongs to.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>How the agent was identified: <c>workspace</c> or <c>bot-argument</c>.</summary>
    public required string ResolvedFrom { get; init; }

    /// <summary>When the agent was last published before this command ran.</summary>
    public DateTimeOffset? PreviousPublishedOn { get; init; }

    /// <summary>When the agent was published by this command.</summary>
    public DateTimeOffset? PublishedOn { get; init; }

    /// <summary>How long polling ran before the publish was confirmed.</summary>
    public TimeSpan Waited { get; init; }
}

