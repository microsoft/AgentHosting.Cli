using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>The standalone workflow <c>msagent agent init</c> registers or creates a record from, with the folder that holds it.</summary>
/// <param name="WorkspaceRoot">The folder that holds the workflow.</param>
/// <param name="Workflow">The workflow folder it holds.</param>
public sealed record WorkflowInitWorkspace(string WorkspaceRoot, LocalWorkflow Workflow);

/// <summary><c>msagent agent init --agent-type MCSWorkflow</c>, registering a workflow already on disk.</summary>
public sealed class WorkflowInitService
{
    private readonly IManagedAgentRegistry _agents;
    private readonly AgentConfigLocator _locator;
    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly IWorkflowStorePort _workflows;
    private readonly ManagedAgentPrincipalResolver _principals;
    private readonly IWorkflowWorkspacePort _workspace;

    /// <summary>Creates the service.</summary>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="locator">Resolves project and configuration directories.</param>
    /// <param name="environmentResolver">Resolves the environment the record is registered in.</param>
    /// <param name="workflows">Reads the workflow the directory defines.</param>
    /// <param name="principals">Resolves the signed-in operator recorded as owner and creator.</param>
    /// <param name="workspace">Finds the workflows a workspace holds.</param>
    public WorkflowInitService(IManagedAgentRegistry agents, AgentConfigLocator locator, CopilotEnvironmentResolver environmentResolver, IWorkflowStorePort workflows, ManagedAgentPrincipalResolver principals, IWorkflowWorkspacePort workspace)
    {
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _principals = principals ?? throw new ArgumentNullException(nameof(principals));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <summary>Registers a workflow that already exists on disk.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The registered record.</returns>
    public async Task<ManagedAgentInitResult> InitAsync(ManagedAgentInitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new UsageException("`msagent agent init --agent-type MCSWorkflow` requires --agent-name.");
        }

        var (workspaceRoot, folder) = FindInitWorkflow(_workspace, request.McsWorkflowSource, request.McsAgentSource);
        var local = await _workflows.ReadLocalAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
        var projectDirectory = request.ProjectDirectory ?? _locator.EnclosingProjectDirectory(workspaceRoot);
        var agentLocation = Path.GetRelativePath(projectDirectory, workspaceRoot).Replace('\\', '/');
        var displayName = request.Name.Trim();

        WorkflowRegistrationGuard.RequireUnregistered(_agents, _locator, projectDirectory, folder.WorkflowId);

        var environment = await _environmentResolver.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
        var caller = await _principals.ResolveAsync(cancellationToken).ConfigureAwait(false);

        _workflows.WriteConnectionReferenceDeclaration(workspaceRoot);

        var stored = _agents.Add(projectDirectory, new ManagedAgentRecord
        {
            AgentId = Guid.NewGuid().ToString(),
            DisplayName = displayName,
            AgentType = AgentTypes.McsWorkflow,
            AgentLocation = agentLocation,
            McsWorkflowId = folder.WorkflowId.ToString(),
            TenantId = environment.TenantId,
            EnvironmentId = environment.EnvironmentId,
            Origin = ManagedAgentOrigins.AgentHostCli,
            Owner = caller,
            CreatedBy = caller,
            EntityTag = string.Empty,
            AgentState = EntityStates.Saved,
        });

        return new ManagedAgentInitResult
        {
            AgentId = stored.AgentId,
            DisplayName = stored.DisplayName,
            AgentType = stored.AgentType,
            McsWorkflowId = stored.McsWorkflowId,
            WorkflowName = local.Name,
            WorkflowFolder = folder.FolderPath,
            WorkflowState = WorkflowStateProjection.NotDeployed,
            TenantId = stored.TenantId,
            EnvironmentId = stored.EnvironmentId,
            Connected = false,
            ProjectDirectory = projectDirectory,
            ConfigPath = _locator.ConfigFilePath(projectDirectory),
        };
    }

    /// <summary>Finds the only workflow in the folder --mcs-workflow-source, or else --mcs-agent-source, names, or else in the current directory itself rather than its enclosing project, refusing a folder that does not exist or holds no workflow or several.</summary>
    /// <param name="workspace">Finds the workflows the folder holds.</param>
    /// <param name="workflowSource">The --mcs-workflow-source folder.</param>
    /// <param name="agentSource">The --mcs-agent-source folder.</param>
    /// <returns>The folder and the workflow it holds.</returns>
    public static WorkflowInitWorkspace FindInitWorkflow(IWorkflowWorkspacePort workspace, string? workflowSource, string? agentSource)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var workspaceRoot = AgentConfigLocator.RequireExistingDirectory(Path.GetFullPath(!string.IsNullOrWhiteSpace(workflowSource) ? workflowSource : !string.IsNullOrWhiteSpace(agentSource) ? agentSource : Directory.GetCurrentDirectory()));
        return new WorkflowInitWorkspace(workspaceRoot, workspace.RequireSingle(workspaceRoot));
    }
}
