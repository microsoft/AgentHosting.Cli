using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent agent pull</c>: brings the remote agent's content into a local directory.
/// </summary>
public sealed class ManagedAgentPullService
{
    private readonly ManagedAgentResolver _resolver;
    private readonly CopilotWorkspaceLocator _workspaceLocator;
    private readonly CopilotWorkspaceService _workspaces;
    private readonly CopilotEnvironmentResolver _environmentResolver;

    /// <summary>Creates the service.</summary>
    /// <param name="resolver">Resolves records from selectors.</param>
    /// <param name="workspaceLocator">Reads the project's cloud baseline when it has one.</param>
    /// <param name="workspaces">Applies remote changes, merging where both sides changed.</param>
    /// <param name="environmentResolver">Resolves the environment the content comes from.</param>
    public ManagedAgentPullService(ManagedAgentResolver resolver, CopilotWorkspaceLocator workspaceLocator, CopilotWorkspaceService workspaces, CopilotEnvironmentResolver environmentResolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _workspaceLocator = workspaceLocator ?? throw new ArgumentNullException(nameof(workspaceLocator));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
    }

    /// <summary>Pulls the agent's content into the connected project.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the pull applied.</returns>
    public async Task<ManagedAgentPullResult> PullAsync(ManagedAgentPullRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.DeploymentId) || !string.IsNullOrWhiteSpace(request.DeploymentName))
        {
            throw new UsageException("--deployment-id and --deployment-name apply to a workflow only. `msagent agent pull` reads an agent from the environment its workspace is connected to, so naming a deployment here would be ignored.");
        }

        if (request.Overwrite)
        {
            throw new UsageException("--overwrite applies to a workflow only. `msagent agent pull` merges remote content into the workspace instead of replacing it, so there is nothing to overwrite.");
        }

        var projectDirectory = _resolver.ResolveProjectDirectory(request.Project);

        if (!Directory.Exists(projectDirectory))
        {
            throw new UsageException($"The directory '{projectDirectory}' does not exist.");
        }

        var agent = ManagedAgentResolver.RequireMcsAgent(_resolver.ResolveAgent(projectDirectory, request.AgentId, request.AgentName), "msagent agent pull");
        var workspaceDirectory = _resolver.ResolveAgentWorkspaceDirectory(projectDirectory, agent);
        var workspace = await _workspaceLocator.FindAsync(workspaceDirectory, cancellationToken).ConfigureAwait(false);

        return workspace is not null
            ? await PullConnectedAsync(projectDirectory, workspaceDirectory, agent, workspace, cancellationToken).ConfigureAwait(false)
            : throw ManagedAgentException.WorkspaceDisconnected(projectDirectory);
    }

    private async Task<ManagedAgentPullResult> PullConnectedAsync(string projectDirectory, string workspaceDirectory, ManagedAgentRecord agent, CopilotWorkspaceInfo workspace, CancellationToken cancellationToken)
    {
        var environment = await _environmentResolver.ResolveAsync(workspace.EnvironmentId, cancellationToken).ConfigureAwait(false);
        WorkspaceConnectionVerifier.RequireOriginMatches(workspace, environment);

        var changes = new List<CopilotChange>();
        var warnings = new List<string>();

        foreach (var collection in _resolver.ResolveComponentCollectionDirectories(projectDirectory, workspaceDirectory).Found)
        {
            var collectionWorkspace = await _workspaceLocator.FindAsync(collection, cancellationToken).ConfigureAwait(false);

            if (collectionWorkspace is not null && string.Equals(collectionWorkspace.EnvironmentId, environment.EnvironmentId, StringComparison.OrdinalIgnoreCase))
            {
                var pulledCollection = await _workspaces.PullForEnvironmentAsync(new CopilotPullRequest { ProjectDirectory = collection }, environment, cancellationToken).ConfigureAwait(false);
                changes.AddRange(pulledCollection.Changes);
                warnings.AddRange(pulledCollection.Warnings);
            }
        }

        var pulled = await _workspaces.PullForEnvironmentAsync(new CopilotPullRequest { ProjectDirectory = workspaceDirectory }, environment, cancellationToken).ConfigureAwait(false);
        changes.AddRange(pulled.Changes);
        warnings.AddRange(pulled.Warnings);

        return new ManagedAgentPullResult
        {
            Status = changes.Count == 0 ? "up-to-date" : "agent-pulled",
            AgentId = agent.AgentId,
            EnvironmentId = environment.EnvironmentId,
            ProjectDirectory = projectDirectory,
            Mode = "pull",
            Changes = changes,
            ChangeCount = changes.Count,
            Warnings = warnings,
        };
    }
}
