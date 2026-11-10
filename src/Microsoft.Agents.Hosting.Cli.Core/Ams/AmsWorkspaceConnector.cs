using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Reads which environment an agent's folder is connected to, and connects the folder and its collection folders to the Copilot Studio agent a deployment's environment holds.</summary>
public sealed class AmsWorkspaceConnector
{
    private readonly CopilotWorkspaceLocator _workspaces;
    private readonly CopilotEnvironmentResolver _environments;
    private readonly ICopilotComponentCollectionPort _collections;
    private readonly ICopilotWorkspaceConnectPort _connect;
    private readonly IGitPort _git;

    /// <summary>Creates the connector.</summary>
    /// <param name="workspaces">Reads the environment a folder is connected to.</param>
    /// <param name="environments">Resolves the deployment's environment.</param>
    /// <param name="collections">Finds the collection folders the agent's folder declares.</param>
    /// <param name="connect">Connects the folders.</param>
    /// <param name="git">Reads the folders' status and their files at the base commit.</param>
    public AmsWorkspaceConnector(CopilotWorkspaceLocator workspaces, CopilotEnvironmentResolver environments, ICopilotComponentCollectionPort collections, ICopilotWorkspaceConnectPort connect, IGitPort git)
    {
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _environments = environments ?? throw new ArgumentNullException(nameof(environments));
        _collections = collections ?? throw new ArgumentNullException(nameof(collections));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _git = git ?? throw new ArgumentNullException(nameof(git));
    }

    /// <summary>Reads the environment the agent's folder is connected to, or null when it isn't connected.</summary>
    /// <param name="workspaceDirectory">The agent's folder.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<string?> ConnectedEnvironmentAsync(string workspaceDirectory, CancellationToken cancellationToken) => (await _workspaces.FindAsync(workspaceDirectory, cancellationToken).ConfigureAwait(false))?.EnvironmentId;

    /// <summary>Connects or refreshes the folders, returning the Copilot Studio agent and the collection folders left unconnected.</summary>
    /// <param name="projectDirectory">The project folder, which every collection folder stays beneath.</param>
    /// <param name="workspaceDirectory">The agent's folder.</param>
    /// <param name="environmentId">The deployment's environment.</param>
    /// <param name="schemaName">The schema name the environment holds the agent under.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    public async Task<CopilotConnectOutcome> ConnectAsync(string projectDirectory, string workspaceDirectory, string environmentId, string schemaName, CancellationToken cancellationToken) => await _connect.ConnectAsync(await CommandAsync(workspaceDirectory, environmentId, schemaName, _collections.Resolve(projectDirectory, workspaceDirectory).Found, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>Merges Copilot Studio's changes since a base commit into clean folders and connects them.</summary>
    /// <param name="projectDirectory">The project folder, the root of its git working tree.</param>
    /// <param name="workspaceDirectory">The agent's folder.</param>
    /// <param name="environmentId">The deployment's environment.</param>
    /// <param name="schemaName">The schema name the environment holds the agent under.</param>
    /// <param name="baseCommit">The commit the merge starts from.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    public async Task<CopilotConnectMergeOutcome?> ConnectAndMergeAsync(string projectDirectory, string workspaceDirectory, string environmentId, string schemaName, string baseCommit, CancellationToken cancellationToken)
    {
        var collections = _collections.Resolve(projectDirectory, workspaceDirectory).Found;
        IReadOnlyList<string> paths = [.. collections.Prepend(workspaceDirectory).Select(folder => Path.GetRelativePath(projectDirectory, folder).Replace('\\', '/'))];
        if ((await _git.StatusAsync(projectDirectory, paths, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            return null;
        }

        return await _connect.ConnectAndMergeAsync(new CopilotConnectMergeCommand(await CommandAsync(workspaceDirectory, environmentId, schemaName, collections, cancellationToken).ConfigureAwait(false), await _git.ReadFilesAsync(projectDirectory, baseCommit, paths, cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
    }

    internal static string NotConnected(string reason) => $"{reason}, so msagent didn't connect the folder to Dev; `msagent agent pull` works there after a Dev deploy of the commit the folder holds.";

    internal static string Uncommitted(string folder) => $"{folder}, or a collection folder it declares, has uncommitted changes";

    internal static string CollectionsNotConnected(string projectDirectory, IReadOnlyList<string> skipped, string? agentName = null)
    {
        var folders = Folders(projectDirectory, skipped);
        return (agentName, skipped.Count == 1) switch
        {
            (null, true) => $"Dev doesn't hold the collection in {folders} yet, so that folder isn't connected; a Dev deploy that includes the collection connects it.",
            (null, false) => $"Dev doesn't hold the collections in {folders} yet, so those folders aren't connected; a Dev deploy that includes them connects them.",
            (_, true) => $"{agentName} in Dev doesn't link the collection in {folders}, so that folder isn't connected.",
            _ => $"{agentName} in Dev doesn't link the collections in {folders}, so those folders aren't connected.",
        };
    }

    internal static string Folders(string projectDirectory, IEnumerable<string> folders) => AmsAgentException.FirstFive([.. folders.Select(folder => Path.GetRelativePath(projectDirectory, folder))]);

    private async Task<CopilotConnectCommand> CommandAsync(string workspaceDirectory, string environmentId, string schemaName, IReadOnlyList<string> collectionDirectories, CancellationToken cancellationToken) => new() { Environment = await _environments.ResolveAsync(environmentId, cancellationToken).ConfigureAwait(false), WorkspaceDirectory = workspaceDirectory, CollectionDirectories = collectionDirectories, SchemaName = schemaName };
}
