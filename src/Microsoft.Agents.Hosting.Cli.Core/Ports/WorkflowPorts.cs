namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>A workflow as it exists in Dataverse or on disk.</summary>
public sealed record WorkflowDocument
{
    /// <summary>The workflow's Dataverse identifier.</summary>
    public required Guid WorkflowId { get; init; }

    /// <summary>The workflow's Dataverse name.</summary>
    public required string Name { get; init; }

    /// <summary>The workflow's description, when it carries one.</summary>
    public string? Description { get; init; }

    /// <summary>The workflow's state, as the sync library names it.</summary>
    public required string State { get; init; }

    /// <summary>When the service last changed the workflow, when it reports one.</summary>
    public DateTimeOffset? ModifiedOn { get; init; }

    /// <summary>The connection reference logical names the workflow uses.</summary>
    public IReadOnlyList<string> ConnectionReferences { get; init; } = [];
}

/// <summary>Names workflow states and the labels the Copilot Studio portal shows for them, by the sync library's rules.</summary>
public interface IWorkflowStateRules
{
    /// <summary>Names the state of a workflow that is running, not running, or not observed.</summary>
    /// <param name="running">Whether the workflow runs, or <see langword="null"/> when nothing observed it.</param>
    /// <returns>The state name.</returns>
    string StateOf(bool? running);

    /// <summary>Reads whether a state means the workflow runs.</summary>
    /// <param name="state">The state name.</param>
    /// <returns>Whether the workflow runs, or <see langword="null"/> when the state is neither running nor stopped.</returns>
    bool? RunningOf(string state);

    /// <summary>Maps a state onto the label the Copilot Studio portal shows.</summary>
    /// <param name="state">The state name.</param>
    /// <returns>The portal label.</returns>
    string PortalLabel(string state);
}

/// <summary>A workflow folder found beneath a workflow workspace root.</summary>
/// <param name="WorkflowId">The workflow's Dataverse identifier, taken from the folder name.</param>
/// <param name="FolderName">The folder's name, relative to the workflows folder.</param>
/// <param name="FolderPath">The absolute folder path.</param>
public sealed record LocalWorkflow(Guid WorkflowId, string FolderName, string FolderPath);

/// <summary>Finds the workflows a workspace root holds.</summary>
public interface IWorkflowWorkspacePort
{
    /// <summary>Every workflow folder beneath a workspace root, in name order.</summary>
    /// <param name="workspaceRoot">The workspace root to search.</param>
    IReadOnlyList<LocalWorkflow> FindAll(string workspaceRoot);

    /// <summary>The only workflow beneath a workspace root, refusing a root holding none or several.</summary>
    /// <param name="workspaceRoot">The workspace root to search.</param>
    LocalWorkflow RequireSingle(string workspaceRoot);

    /// <summary>Whether a directory holds workflows and no agent project of its own.</summary>
    /// <param name="directory">The directory to test.</param>
    bool IsStandaloneWorkspace(string directory);

    /// <summary>Whether a directory holds an agent project of its own.</summary>
    /// <param name="directory">The directory to test.</param>
    bool HasAgentProject(string directory);

    /// <summary>The folder name a project of this display name occupies.</summary>
    /// <param name="displayName">The project's display name.</param>
    /// <returns>A folder name every supported file system accepts.</returns>
    string ProjectFolderName(string displayName);
}

/// <summary>What writing a workflow to Dataverse achieved.</summary>
public sealed record WorkflowUpsertOutcome
{
    /// <summary>The workflow that was written.</summary>
    public required WorkflowDocument Workflow { get; init; }

    /// <summary>Whether the row was created rather than updated.</summary>
    public bool Created { get; init; }

    /// <summary>Whether the environment refused an update, so the row was deleted and written again.</summary>
    public bool Replaced { get; init; }

    /// <summary>The connection references the push had to declare before the workflow would land.</summary>
    public IReadOnlyList<string> CreatedConnectionReferences { get; init; } = [];

    /// <summary>Whether the local workflow asked to run, even if it had to land as a draft.</summary>
    public bool ActivationRequested { get; init; }

    /// <summary>
    /// Whether <see cref="Warning"/> reports only that the workflow landed as a draft because its
    /// connections are not ready, which a later activation pass resolves by starting it.
    /// </summary>
    public bool DraftedForConnections { get; init; }

    /// <summary>What the push could not finish, or an empty string when it finished entirely.</summary>
    public string Warning { get; init; } = string.Empty;

    /// <summary>The connection references the workflow uses that have no connection after the push, empty once the workflow is running.</summary>
    public IReadOnlyList<string> UnboundConnectionReferences { get; init; } = [];
}

/// <summary>What downloading a workflow wrote to disk.</summary>
public sealed record WorkflowDownloadOutcome
{
    /// <summary>The workflow that was downloaded.</summary>
    public required WorkflowDocument Workflow { get; init; }

    /// <summary>The absolute folder the content was written to.</summary>
    public required string FolderPath { get; init; }

    /// <summary>The absolute folder a rename moved the workflow out of, or <see langword="null"/> when the folder kept its name.</summary>
    public string? PreviousFolder { get; init; }

    /// <summary>Whether anything on disk changed.</summary>
    public bool Changed { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Reads and writes standalone workflows, in Dataverse and on disk.</summary>
public interface IWorkflowStorePort
{
    /// <summary>Reads the workflow a workspace root defines.</summary>
    /// <param name="workspaceRoot">The workspace root holding the workflow.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The workflow the workspace defines.</returns>
    Task<WorkflowDocument> ReadLocalAsync(string workspaceRoot, CancellationToken cancellationToken);

    /// <summary>Reads a workflow from Dataverse.</summary>
    /// <param name="environment">The environment holding the workflow.</param>
    /// <param name="workflowId">The workflow to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The workflow, or <see langword="null"/> when no such row exists.</returns>
    Task<WorkflowDocument?> GetAsync(CopilotEnvironmentContext environment, Guid workflowId, CancellationToken cancellationToken);

    /// <summary>Lists the unmanaged cloud flows an environment holds, including the ones Copilot Studio agents use.</summary>
    /// <param name="environment">The environment to read.</param>
    /// <param name="nameFilter">Returns only workflows whose name contains this text, when supplied.</param>
    /// <param name="maximumCount">The most rows to return, or <see langword="null"/> for every row.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The workflows the environment holds, in name order.</returns>
    Task<IReadOnlyList<WorkflowDocument>> ListAsync(CopilotEnvironmentContext environment, string? nameFilter, int? maximumCount, CancellationToken cancellationToken);

    /// <summary>Downloads a workflow into a workspace root, overwriting whatever is there.</summary>
    /// <param name="environment">The environment holding the workflow.</param>
    /// <param name="workflowId">The workflow to download.</param>
    /// <param name="workspaceRoot">The workspace root to write beneath.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <param name="overwriteLocalChanges">Whether a local edit the environment has not seen may be replaced.</param>
    /// <returns>What was written.</returns>
    Task<WorkflowDownloadOutcome> DownloadAsync(CopilotEnvironmentContext environment, Guid workflowId, string workspaceRoot, CancellationToken cancellationToken, bool overwriteLocalChanges = false);

    /// <summary>Clones a workflow into a new workspace root.</summary>
    /// <param name="environment">The environment holding the workflow.</param>
    /// <param name="workflowId">The workflow to clone.</param>
    /// <param name="workspaceRoot">The workspace root to write beneath.</param>
    /// <param name="name">The name the local copy declares in place of the workflow's own, or <see langword="null"/> to keep the workflow's.</param>
    /// <param name="cancellationToken">Cancels the clone.</param>
    /// <returns>What was written.</returns>
    Task<WorkflowDownloadOutcome> CloneAsync(CopilotEnvironmentContext environment, Guid workflowId, string workspaceRoot, string? name, CancellationToken cancellationToken);

    /// <summary>Uploads the workflow a workspace root holds, creating the row when the environment has none.</summary>
    /// <param name="environment">The environment to write to.</param>
    /// <param name="workspaceRoot">The workspace root holding the workflow.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>What was written.</returns>
    Task<WorkflowUpsertOutcome> UpsertAsync(CopilotEnvironmentContext environment, string workspaceRoot, CancellationToken cancellationToken);

    Task RequireNoUnpulledChangesAsync(CopilotEnvironmentContext environment, string workspaceRoot, CancellationToken cancellationToken);

    /// <summary>Writes the connection reference declaration the workspace's workflow definition implies.</summary>
    /// <param name="workspaceRoot">The workflow workspace root.</param>
    void WriteConnectionReferenceDeclaration(string workspaceRoot);

    /// <summary>Deletes a workflow, deactivating it first.</summary>
    /// <param name="environment">The environment holding the workflow.</param>
    /// <param name="workflowId">The workflow to delete.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    /// <returns><see langword="false"/> when the row was already absent.</returns>
    Task<bool> DeleteAsync(CopilotEnvironmentContext environment, Guid workflowId, CancellationToken cancellationToken);

    /// <summary>Reads the warning a delete shows about the Copilot Studio agents that use a workflow.</summary>
    /// <param name="environment">The environment holding the workflow.</param>
    /// <param name="workflowId">The workflow to look up.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The warning naming those agents, or an empty string when no agent uses the workflow.</returns>
    Task<string> GetAgentUseWarningAsync(CopilotEnvironmentContext environment, Guid workflowId, CancellationToken cancellationToken);
}

/// <summary>Writes a new local workflow from the built-in template.</summary>
public interface IWorkflowScaffoldPort
{
    /// <summary>Explains why a name cannot be a workflow's name, before anything is written.</summary>
    /// <param name="name">The name to check.</param>
    /// <returns>The reason the name is refused, or <see langword="null"/> when it can be used.</returns>
    string? ValidateName(string name);

    /// <summary>Scaffolds a workflow into a workspace root.</summary>
    /// <param name="workspaceRoot">The workspace root to write beneath.</param>
    /// <param name="name">The workflow's Dataverse name.</param>
    /// <param name="workflowId">The identifier the workflow takes.</param>
    /// <returns>The created files, relative to the workspace root.</returns>
    IReadOnlyList<string> Scaffold(string workspaceRoot, string name, Guid workflowId);
}
