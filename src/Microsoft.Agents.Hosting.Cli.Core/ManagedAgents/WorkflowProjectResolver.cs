using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>A workflow record resolved to the files and environment a command acts on.</summary>
/// <param name="ProjectDirectory">The project root holding the configuration.</param>
/// <param name="Agent">The managed-agent record.</param>
/// <param name="WorkspaceRoot">The directory the record's workflows folder sits beneath.</param>
/// <param name="Folder">The workflow on disk, absent when a download is about to put it there.</param>
/// <param name="WorkflowId">The Dataverse workflow the record addresses.</param>
public sealed record WorkflowTarget(string ProjectDirectory, ManagedAgentRecord Agent, string WorkspaceRoot, LocalWorkflow? Folder, Guid WorkflowId);

/// <summary>Turns workflow command selectors into the record, files and environment they name.</summary>
public sealed class WorkflowProjectResolver
{
    private readonly ManagedAgentResolver _resolver;
    private readonly AgentConfigLocator _locator;
    private readonly CopilotEnvironmentResolver _environments;
    private readonly IWorkflowWorkspacePort _workspace;

    /// <summary>Creates the resolver.</summary>
    /// <param name="resolver">Resolves records and deployments from selectors.</param>
    /// <param name="locator">Resolves project and workspace directories.</param>
    /// <param name="environments">Resolves environment coordinates.</param>
    /// <param name="workspace">Finds the workflows a workspace holds.</param>
    public WorkflowProjectResolver(ManagedAgentResolver resolver, AgentConfigLocator locator, CopilotEnvironmentResolver environments, IWorkflowWorkspacePort workspace)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _environments = environments ?? throw new ArgumentNullException(nameof(environments));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <summary>Resolves the project directory a command acts on.</summary>
    /// <param name="project">An explicit directory, or <see langword="null"/> for the enclosing project.</param>
    /// <returns>The absolute project directory.</returns>
    public string ResolveProjectDirectory(string? project) => _resolver.ResolveProjectDirectory(project);

    /// <summary>Resolves a workflow record and the files it names.</summary>
    /// <param name="project">The project directory, or <see langword="null"/> for the enclosing project.</param>
    /// <param name="agentId">The record to resolve. Wins over the name.</param>
    /// <param name="agentName">The record to resolve, by name.</param>
    /// <param name="verb">The command being run, named in any failure.</param>
    /// <returns>The resolved target.</returns>
    public WorkflowTarget Resolve(string? project, string? agentId, string? agentName, string verb)
    {
        var projectDirectory = ResolveProjectDirectory(project);
        var agent = ManagedAgentResolver.RequireMcsWorkflow(_resolver.ResolveAgent(projectDirectory, agentId, agentName), verb);

        return Target(projectDirectory, agent, requireLocalFiles: true);
    }

    /// <summary>Resolves a workflow record, the deployment named, and the environment it targets.</summary>
    /// <param name="project">The project directory, or <see langword="null"/> for the enclosing project.</param>
    /// <param name="agentId">The record to resolve. Wins over the name.</param>
    /// <param name="agentName">The record to resolve, by name.</param>
    /// <param name="deploymentId">The deployment to target. Wins over the name.</param>
    /// <param name="deploymentName">The deployment to target, by name.</param>
    /// <param name="verb">The command being run, named in any failure.</param>
    /// <param name="requireLocalFiles">Whether the command needs the workflow to already be on disk.</param>
    /// <param name="requireExplicitDeployment">Whether deploy requires an explicit target and actionable selection errors.</param>
    /// <param name="cancellationToken">Cancels the environment lookup.</param>
    /// <returns>The target, the deployment, and the environment it resolves to.</returns>
    public async Task<(WorkflowTarget Target, AgentDeploymentRecord Deployment, CopilotEnvironmentContext Environment)> ResolveDeploymentAsync(string? project, string? agentId, string? agentName, string? deploymentId, string? deploymentName, string verb, CancellationToken cancellationToken, bool requireLocalFiles = true, bool requireExplicitDeployment = false)
    {
        var projectDirectory = ResolveProjectDirectory(project);
        var (owner, deployment) = _resolver.ResolveAgentDeployment(projectDirectory, agentId, agentName, deploymentId, deploymentName, requireExplicitDeployment);
        var agent = ManagedAgentResolver.RequireMcsWorkflow(owner, verb);
        var environment = await _environments.ResolveAsync(ManagedAgentResolver.TargetEnvironmentId(agent, deployment), cancellationToken).ConfigureAwait(false);

        return (Target(projectDirectory, agent, requireLocalFiles), deployment, environment);
    }

    /// <summary>
    /// Binds a record to the workflow on disk, refusing a folder that holds a different workflow.
    ///
    /// Nothing else downstream compares the two: a push takes its identity from the files, so a
    /// workspace whose contents were swapped would otherwise write one workflow while the record,
    /// the report and the deployment history all name another.
    /// </summary>
    private WorkflowTarget Target(string projectDirectory, ManagedAgentRecord agent, bool requireLocalFiles)
    {
        var workspaceRoot = ResolveWorkspaceRoot(projectDirectory, agent, requireExists: requireLocalFiles);
        var workflowId = ManagedAgentResolver.RequireWorkflowId(agent);

        if (!requireLocalFiles && (!Directory.Exists(workspaceRoot) || _workspace.FindAll(workspaceRoot).Count == 0))
        {
            return new WorkflowTarget(projectDirectory, agent, workspaceRoot, null, workflowId);
        }

        return new WorkflowTarget(projectDirectory, agent, workspaceRoot, RequireMatchingWorkflow(_workspace, workspaceRoot, agent), workflowId);
    }

    /// <summary>Binds a record to the workflow on disk, refusing a folder that holds a different workflow.</summary>
    /// <param name="workspace">Finds the workflows a workspace holds.</param>
    /// <param name="workspaceRoot">The directory the record's workflows folder sits beneath.</param>
    /// <param name="agent">The record whose declared workflow the folder has to hold.</param>
    /// <returns>The workflow the workspace holds.</returns>
    public static LocalWorkflow RequireMatchingWorkflow(IWorkflowWorkspacePort workspace, string workspaceRoot, ManagedAgentRecord agent)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var workflowId = ManagedAgentResolver.RequireWorkflowId(agent);
        var folder = workspace.RequireSingle(workspaceRoot);

        return folder.WorkflowId == workflowId
            ? folder
            : throw ManagedAgentException.WorkflowFolderAddressesAnotherWorkflow(agent.DisplayName, workflowId.ToString(), folder.FolderName, folder.WorkflowId.ToString());
    }

    /// <summary>Resolves the directory a workflow record's files sit beneath.</summary>
    /// <param name="projectDirectory">The project root.</param>
    /// <param name="agent">The record whose workspace is resolved.</param>
    /// <param name="requireExists">Whether the directory has to be on disk already.</param>
    /// <returns>The absolute workspace root.</returns>
    public string ResolveWorkspaceRoot(string projectDirectory, ManagedAgentRecord agent, bool requireExists = true) => _resolver.ResolveWorkspaceDirectory(projectDirectory, agent, requireExists);

    /// <summary>The configuration file path for a project directory.</summary>
    /// <param name="projectDirectory">The project directory.</param>
    /// <returns>The absolute configuration file path.</returns>
    public string ConfigPath(string projectDirectory) => _locator.ConfigFilePath(projectDirectory);
}
