namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>The kinds of identity a copy maps from its source, matching the sync library's copy identity kinds.</summary>
public static class CopilotCopyIdentityKinds
{
    /// <summary>An agent's root schema name.</summary>
    public const string Schema = "schema";

    /// <summary>A workflow identifier.</summary>
    public const string Workflow = "workflow";

    /// <summary>An AI prompt's model identifier.</summary>
    public const string AIModel = "aiModel";

    /// <summary>A custom connector identifier.</summary>
    public const string Connector = "connector";

    /// <summary>A connection reference logical name.</summary>
    public const string ConnectionReference = "connectionReference";
}

/// <summary>Where a copy's content is read from.</summary>
public static class CopilotCopySourceKinds
{
    /// <summary>A local project.</summary>
    public const string Local = "local";

    /// <summary>An agent read out of an environment.</summary>
    public const string Cloud = "cloud";
}

/// <summary>One identity a copy maps from its source to itself.</summary>
/// <param name="Kind">One of <see cref="CopilotCopyIdentityKinds"/>.</param>
/// <param name="Source">The source's identity.</param>
/// <param name="Destination">The copy's identity.</param>
public sealed record CopilotCopyIdentity(string Kind, string Source, string Destination);

/// <summary>A resource named by its kind and identity.</summary>
/// <param name="Kind">One of <see cref="CopilotCopyIdentityKinds"/>.</param>
/// <param name="Identity">The resource's identifier, or a connection reference's logical name.</param>
public sealed record CopilotCopyLink(string Kind, string Identity);

/// <summary>Something an audit of a copy found.</summary>
/// <param name="Code">A stable, kebab-case category.</param>
/// <param name="Message">What was found.</param>
/// <param name="Path">The workspace file it was found in, when there is one.</param>
public sealed record CopilotCopyFinding(string Code, string Message, string? Path = null)
{
    /// <summary>The finding as one line of text.</summary>
    /// <returns>The message, followed by the file it concerns.</returns>
    public override string ToString() => Path is null ? Message : $"{Message} ({Path})";
}

/// <summary>What to prepare a staged workspace into.</summary>
public sealed record CopilotCopyPrepareCommand
{
    /// <summary>The staged copy of the source workspace, rewritten in place.</summary>
    public required string WorkspaceDirectory { get; init; }

    /// <summary>The schema name the source is expected to carry, when known.</summary>
    public string? SourceSchemaName { get; init; }

    /// <summary>The source's captured cloud definition, when there is one.</summary>
    public string? SourceDefinitionPath { get; init; }

    /// <summary>The copy's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The copy's schema name.</summary>
    public required string SchemaName { get; init; }

    /// <summary>Whether the source was captured in the environment the copy is prepared for, so its custom connector identifiers already fit there.</summary>
    public bool CapturedInTargetEnvironment { get; init; }

    /// <summary>The environment the copy is prepared for, read to check that its new identities are free there and, when a copied custom connector needs it, for its identifier suffix.</summary>
    public CopilotEnvironmentContext? Environment { get; init; }
}

/// <summary>What preparing a copy wrote, or what stopped it.</summary>
public sealed record CopilotCopyPreparation
{
    /// <summary>Every identity the copy maps from its source, or none when the copy was blocked.</summary>
    public IReadOnlyList<CopilotCopyIdentity> Identities { get; init; } = [];

    /// <summary>Findings that stopped the copy, in which case nothing usable was written.</summary>
    public IReadOnlyList<CopilotCopyFinding> Blockers { get; init; } = [];

    /// <summary>Findings worth reporting that do not stop the copy.</summary>
    public IReadOnlyList<CopilotCopyFinding> Notices { get; init; } = [];

    /// <summary>The workspace files whose content was rewritten, relative to the workspace.</summary>
    public IReadOnlyList<string> RewrittenFiles { get; init; } = [];

    /// <summary>The workspace paths that were renamed.</summary>
    public IReadOnlyList<CopilotCopyIdentity> RenamedPaths { get; init; } = [];

    /// <summary>The copied workflows, which deploy as drafts.</summary>
    public IReadOnlyList<string> DraftWorkflows { get; init; } = [];
}

/// <summary>Prepares copies through the sync library's copy implementation.</summary>
public interface ICopilotCopyPort
{
    /// <summary>Rewrites a staged workspace into a new agent and checks, read-only, that its new identities are free in the environment it is prepared for.</summary>
    /// <param name="command">What to prepare, and into what.</param>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    /// <returns>What was written, or the findings that stopped the copy.</returns>
    Task<CopilotCopyPreparation> PrepareAsync(CopilotCopyPrepareCommand command, CancellationToken cancellationToken);

    /// <summary>Proposes a copy's schema name that leaves room for the longest component schema name in its staged workspace.</summary>
    /// <param name="workspaceDirectory">The staged copy of the source workspace.</param>
    /// <param name="sourceSchemaName">The source agent's schema name, whose publisher prefix the proposal keeps.</param>
    /// <param name="displayName">The copy's display name.</param>
    /// <param name="cancellationToken">Cancels the proposal.</param>
    /// <returns>The proposed schema name, or <see langword="null"/> when no generated name fits.</returns>
    Task<string?> ProposeSchemaNameAsync(string workspaceDirectory, string sourceSchemaName, string displayName, CancellationToken cancellationToken);
}

/// <summary>Lists, finds, and deletes the flows, AI prompts, connection references, and custom connectors an agent uses.</summary>
public interface ICopilotAgentResourcePort
{
    /// <summary>Lists the flows, AI prompts, custom connectors, and connection references an agent workspace holds, reading no service.</summary>
    /// <param name="workspaceDirectory">The agent's workspace.</param>
    /// <returns>The resources.</returns>
    IReadOnlyList<CopilotCopyLink> List(string workspaceDirectory);

    /// <summary>Finds which of the resources the environment holds.</summary>
    /// <param name="environment">The environment to read.</param>
    /// <param name="resources">The resources to look for.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The resources the environment holds, in the order given.</returns>
    Task<IReadOnlyList<CopilotCopyLink>> FindExistingAsync(CopilotEnvironmentContext environment, IReadOnlyList<CopilotCopyLink> resources, CancellationToken cancellationToken);

    /// <summary>Deletes one resource, turning a flow off first and treating a resource already gone as deleted.</summary>
    /// <param name="environment">The environment holding the resource.</param>
    /// <param name="resource">The resource's kind and identity.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>A task that completes once the resource is gone.</returns>
    Task DeleteAsync(CopilotEnvironmentContext environment, CopilotCopyLink resource, CancellationToken cancellationToken);
}
