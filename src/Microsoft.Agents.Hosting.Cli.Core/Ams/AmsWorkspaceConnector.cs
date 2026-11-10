using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Reads which environment an agent's folder is connected to, and connects the folder and its collection folders to the Copilot Studio agent a deployment's environment holds.</summary>
public sealed class AmsWorkspaceConnector
{
    private readonly CopilotWorkspaceLocator _workspaces;
    private readonly CopilotEnvironmentResolver _environments;
    private readonly ICopilotComponentCollectionPort _collections;
    private readonly ICopilotWorkspaceConnectPort _connect;

    /// <summary>Creates the connector.</summary>
    /// <param name="workspaces">Reads the environment a folder is connected to.</param>
    /// <param name="environments">Resolves the deployment's environment.</param>
    /// <param name="collections">Finds the collection folders the agent's folder declares.</param>
    /// <param name="connect">Connects the folders.</param>
    public AmsWorkspaceConnector(CopilotWorkspaceLocator workspaces, CopilotEnvironmentResolver environments, ICopilotComponentCollectionPort collections, ICopilotWorkspaceConnectPort connect)
    {
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _environments = environments ?? throw new ArgumentNullException(nameof(environments));
        _collections = collections ?? throw new ArgumentNullException(nameof(collections));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
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
    public async Task<CopilotConnectOutcome> ConnectAsync(string projectDirectory, string workspaceDirectory, string environmentId, string schemaName, CancellationToken cancellationToken) => await _connect.ConnectAsync(new CopilotConnectCommand { Environment = await _environments.ResolveAsync(environmentId, cancellationToken).ConfigureAwait(false), WorkspaceDirectory = workspaceDirectory, CollectionDirectories = _collections.Resolve(projectDirectory, workspaceDirectory).Found, SchemaName = schemaName }, cancellationToken).ConfigureAwait(false);
}
