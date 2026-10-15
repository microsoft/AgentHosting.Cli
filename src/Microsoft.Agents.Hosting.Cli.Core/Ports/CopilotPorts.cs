using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>
/// The environment coordinates a Copilot Studio operation runs against. Carries no token material.
/// </summary>
public sealed record CopilotEnvironmentContext
{
    /// <summary>The environment the operation targets.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The tenant that owns the environment.</summary>
    public required string TenantId { get; init; }

    /// <summary>The environment's Dataverse organization url.</summary>
    public required Uri DataverseOrganization { get; init; }

    /// <summary>
    /// The Copilot Studio control-plane endpoint, absent when the environment does not advertise one.
    /// </summary>
    public Uri? AgentManagementUrl { get; init; }
}

/// <summary>Identifies the agent a workspace is connected to.</summary>
public sealed record CopilotWorkspaceInfo
{
    /// <summary>The workspace directory.</summary>
    public required string WorkspacePath { get; init; }

    /// <summary>The agent the workspace is connected to.</summary>
    public required string AgentId { get; init; }

    /// <summary>The environment the agent belongs to.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The environment's Dataverse organization url.</summary>
    public required Uri DataverseOrganization { get; init; }

    /// <summary>
    /// The tenant the connection was established in, when the workspace records one.
    ///
    /// Carried so a connected operation can bind its token to the same tenant an environment-scoped
    /// command would, rather than accepting whichever tenant the token cache offers first.
    /// </summary>
    public string? TenantId { get; init; }
}

/// <summary>The local and remote change sets computed for a workspace.</summary>
public sealed record CopilotChangeSnapshot
{
    /// <summary>Components changed in the workspace since the last sync.</summary>
    public required IReadOnlyList<CopilotChange> Local { get; init; }

    /// <summary>Components changed in the cloud since the last sync.</summary>
    public required IReadOnlyList<CopilotChange> Remote { get; init; }
}

/// <summary>Input for a clone.</summary>
public sealed record CopilotCloneCommand
{
    /// <summary>The environment to clone from.</summary>
    public required CopilotEnvironmentContext Environment { get; init; }

    /// <summary>The agent to clone.</summary>
    public required string AgentId { get; init; }

    /// <summary>
    /// The directory to clone into. Every workspace gets a directory under this root named after its
    /// display name, including the agent's own.
    /// </summary>
    public required string WorkspacePath { get; init; }

    /// <summary>
    /// The component collections to clone alongside the agent, as identifiers or schema names.
    ///
    /// Ignored when <see cref="IncludeLinkedComponentCollections"/> asks for every linked collection.
    /// </summary>
    public IReadOnlyList<string> ComponentCollectionIds { get; init; } = [];

    /// <summary>The folder name the agent's workspace takes, defaulting to the agent's display name.</summary>
    public string? AgentFolderName { get; init; }

    /// <summary>
    /// Whether to clone every component collection the agent is linked to.
    /// </summary>
    public bool IncludeLinkedComponentCollections { get; init; }
}

/// <summary>What a completed clone produced.</summary>
public sealed record CopilotCloneOutcome
{
    /// <summary>The cloned agent's schema name.</summary>
    public required string SchemaName { get; init; }

    /// <summary>The cloned agent's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>How many components were written.</summary>
    public int ComponentCount { get; init; }

    /// <summary>The directory the agent workspace was written to.</summary>
    public required string AgentWorkspacePath { get; init; }

    /// <summary>Every workspace that was created, including the agent's.</summary>
    public required IReadOnlyList<CopilotClonedWorkspace> Workspaces { get; init; }
}

/// <summary>
/// What a pull applied, and the files its merge left for the author to resolve.
/// </summary>
public sealed record CopilotPullOutcome
{
    /// <summary>The remote changes that were applied.</summary>
    public required IReadOnlyList<CopilotChange> Changes { get; init; }

    /// <summary>Files the merge left with unresolved conflict markers.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// What a push sent, and anything the service declined along the way.
///
/// A declined workflow or AI prompt does not fail the push: the sync library keeps it out of the cloud
/// cache and continues, because a flow whose connection reference is not yet bound in the target
/// environment is the expected state during a promotion.
/// </summary>
public sealed record CopilotPushOutcome
{
    /// <summary>The components that were uploaded.</summary>
    public required IReadOnlyList<CopilotChange> Changes { get; init; }

    /// <summary>
    /// How many workflows and AI prompts the service was actually asked to store.
    ///
    /// The component change set does not describe AI prompts, so a prompt-only edit produces no
    /// component changes at all. Counting what the upserts sent is what keeps such an edit from being
    /// reported as nothing to do. The library skips a prompt whose content is unchanged, so a resource
    /// only appears here when it genuinely differed from the cloud.
    /// </summary>
    public int AuxiliaryChangeCount { get; init; }

    /// <summary>
    /// The knowledge and attachment files the service was sent, by file name.
    ///
    /// The component change set does not describe a skill manifest, which has no sidecar of its own, so
    /// an edit to one produces no component change and the push reports nothing. These names come from
    /// the upload itself, which is the only account of what the file layer sent.
    ///
    /// Every file backed by a cloud component is uploaded, changed or not, so this counts what was sent
    /// rather than what differed.
    /// </summary>
    public IReadOnlyList<string> UploadedFileNames { get; init; } = [];

    /// <summary>Resources the service declined, described for the operator.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Input for scaffolding a local workspace.</summary>
public sealed record CopilotScaffoldCommand
{
    /// <summary>The directory to scaffold into.</summary>
    public required string ProjectDirectory { get; init; }

    /// <summary>The embedded resource folder holding the template content.</summary>
    public required string TemplateFolder { get; init; }

    /// <summary>The agent's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The agent's schema name.</summary>
    public required string SchemaName { get; init; }

    /// <summary>The agent's instructions.</summary>
    public required string Instructions { get; init; }
}

/// <summary>
/// Workspace synchronization: clone, change detection, pull, and push.
/// </summary>
public interface ICopilotWorkspacePort
{
    /// <summary>Reports whether <paramref name="directory"/> is inside a connected workspace.</summary>
    /// <param name="directory">The directory to test.</param>
    /// <returns><see langword="true"/> when a workspace was found at or above the directory.</returns>
    bool IsWorkspace(string directory);

    /// <summary>Reads the connection details a workspace was cloned with.</summary>
    /// <param name="directory">A directory at or below the workspace root.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The workspace's connection details.</returns>
    Task<CopilotWorkspaceInfo> ReadWorkspaceInfoAsync(string directory, CancellationToken cancellationToken);

    /// <summary>Clones an agent into a new workspace.</summary>
    /// <param name="command">What to clone, and where to.</param>
    /// <param name="cancellationToken">Cancels the clone.</param>
    /// <returns>What the clone produced.</returns>
    Task<CopilotCloneOutcome> CloneAsync(CopilotCloneCommand command, CancellationToken cancellationToken);

    /// <summary>Computes the local and remote change sets without mutating the workspace.</summary>
    /// <param name="workspace">The workspace to inspect.</param>
    /// <param name="cancellationToken">Cancels the computation.</param>
    /// <returns>The local and remote changes.</returns>
    Task<CopilotChangeSnapshot> GetChangesAsync(CopilotWorkspaceInfo workspace, CancellationToken cancellationToken);

    /// <summary>
    /// Applies remote changes to the workspace, merging where both sides changed, and refreshes
    /// knowledge file content.
    /// </summary>
    /// <param name="workspace">The workspace to update.</param>
    /// <param name="cancellationToken">Cancels the pull.</param>
    /// <returns>The changes that were applied, and anything the merge left unresolved.</returns>
    Task<CopilotPullOutcome> PullAsync(CopilotWorkspaceInfo workspace, CancellationToken cancellationToken);

    /// <summary>
    /// Uploads local changes to the connected agent, including knowledge file content.
    /// </summary>
    /// <param name="workspace">The workspace to push.</param>
    /// <param name="overwrite">
    /// Whether to re-image the cloud baseline and push again when the service reports the agent
    /// changed underneath. The baseline is always taken from the service, never from local content.
    /// </param>
    /// <param name="cancellationToken">Cancels the push.</param>
    /// <returns>The changes that were uploaded, and anything the service declined.</returns>
    Task<CopilotPushOutcome> PushAsync(CopilotWorkspaceInfo workspace, bool overwrite, CancellationToken cancellationToken);
}

/// <summary>Agent inventory and lifecycle operations against an environment.</summary>
public interface ICopilotCatalogPort
{
    /// <summary>Lists the agents in an environment.</summary>
    /// <param name="environment">The environment to read.</param>
    /// <param name="maximumCount">The most rows to return, or <see langword="null"/> for every row.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The agents available in the environment.</returns>
    Task<IReadOnlyList<CopilotAgentSummary>> ListAgentsAsync(CopilotEnvironmentContext environment, int? maximumCount, CancellationToken cancellationToken);

    /// <summary>Resolves an identifier or schema name to an agent identifier.</summary>
    /// <param name="environment">The environment to resolve within.</param>
    /// <param name="botIdOrSchemaName">The value to resolve.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The resolved agent identifier, or <see langword="null"/> when nothing matched.</returns>
    Task<string?> ResolveAgentIdAsync(CopilotEnvironmentContext environment, string botIdOrSchemaName, CancellationToken cancellationToken);

    /// <summary>Reads an agent's current provisioning state.</summary>
    /// <param name="environment">The environment the agent belongs to.</param>
    /// <param name="agentId">The agent to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The agent's current state.</returns>
    Task<CopilotAgentSummary> GetAgentAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken);

    /// <summary>Deletes an agent.</summary>
    /// <param name="environment">The environment the agent belongs to.</param>
    /// <param name="agentId">The agent to delete.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    /// <returns>A task that completes when the agent is deleted.</returns>
    Task DeleteAgentAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken);

    /// <summary>Reads when an agent was last published.</summary>
    /// <param name="environment">The environment the agent belongs to.</param>
    /// <param name="agentId">The agent to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The last publish time, or <see langword="null"/> when the agent has never been published.</returns>
    Task<DateTimeOffset?> GetPublishedOnAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken);

    /// <summary>Starts publishing an agent's draft content.</summary>
    /// <param name="environment">The environment the agent belongs to.</param>
    /// <param name="agentId">The agent to publish.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the service has accepted the publish request.</returns>
    Task PublishAgentAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken);
}

/// <summary>Writes a new local workspace from a template.</summary>
public interface ICopilotScaffoldPort
{
    /// <summary>Scaffolds a workspace.</summary>
    /// <param name="command">What to scaffold, and where to.</param>
    /// <returns>The created files, relative to the project directory.</returns>
    IReadOnlyList<string> Scaffold(CopilotScaffoldCommand command);
}

/// <summary>Links a scaffolded workspace to a newly created cloud agent.</summary>
public interface ICopilotWorkspaceAttachPort
{
    /// <summary>Creates the agent and writes the cloud baseline into the workspace.</summary>
    /// <param name="command">The workspace to attach, and the agent to create.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The created agent.</returns>
    Task<CopilotAttachOutcome> AttachAsync(CopilotAttachCommand command, CancellationToken cancellationToken);
}

/// <summary>The workspace to attach, and the agent to create for it.</summary>
public sealed record CopilotAttachCommand
{
    /// <summary>The environment the agent is created in.</summary>
    public required CopilotEnvironmentContext Environment { get; init; }

    /// <summary>The scaffolded workspace directory.</summary>
    public required string ProjectDirectory { get; init; }

    /// <summary>The agent's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The agent's schema name.</summary>
    public required string SchemaName { get; init; }

    /// <summary>
    /// Whether a workspace already bound to another environment may be rebound to this one.
    /// </summary>
    public bool AllowRetarget { get; init; }

    /// <summary>
    /// The agent the caller already expects to find under <see cref="SchemaName"/>, when it knows of
    /// one. A different agent under that name is a conflict rather than something to adopt.
    /// </summary>
    public string? ExpectedAgentId { get; init; }
}

/// <summary>The agent an attach created or adopted.</summary>
public sealed record CopilotAttachOutcome
{
    /// <summary>The created agent's identifier.</summary>
    public required string AgentId { get; init; }

    /// <summary>The schema name the service assigned, which may differ from the requested one.</summary>
    public required string SchemaName { get; init; }

    /// <summary>Whether the agent was created, as opposed to an existing one being adopted.</summary>
    public bool AgentCreated { get; init; }

    /// <summary>Whether the workspace was rebound from another environment.</summary>
    public bool Retargeted { get; init; }

    /// <summary>Resources the attach did not leave running, which block a publish.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Advisories worth reporting that do not block a publish.</summary>
    public IReadOnlyList<string> Notices { get; init; } = [];
}
