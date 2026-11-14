using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary><c>msagent agent init --agent-type MCSWorkflow</c>, registering a workflow already on disk.</summary>
public sealed class WorkflowInitService
{
    private readonly IManagedAgentRegistry _agents;
    private readonly AgentConfigLocator _locator;
    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly IWorkflowStorePort _workflows;
    private readonly ManagedAgentPrincipalResolver _principals;
    private readonly IWorkflowWorkspacePort _workspace;
    private readonly ManagedAgentRegistration _registration;

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
        _registration = new ManagedAgentRegistration(_agents, _locator);
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

        var workspaceRoot = Path.GetFullPath(SourceDirectory(request) ?? Directory.GetCurrentDirectory());

        if (!Directory.Exists(workspaceRoot))
        {
            throw new UsageException($"The directory '{workspaceRoot}' does not exist.");
        }

        var folder = _workspace.RequireSingle(workspaceRoot);
        var local = await _workflows.ReadLocalAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
        var projectDirectory = _locator.EnclosingProjectDirectory(workspaceRoot);
        var agentLocation = Path.GetRelativePath(projectDirectory, workspaceRoot).Replace('\\', '/');

        WorkflowRegistrationGuard.RequireUnregistered(_agents, _locator, projectDirectory, folder.WorkflowId);

        var environment = await _environmentResolver.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
        var caller = await _principals.ResolveAsync(cancellationToken).ConfigureAwait(false);

        _workflows.WriteConnectionReferenceDeclaration(workspaceRoot);

        var stored = _registration.RegisterWorkflow(projectDirectory, request.Name, null, agentLocation, environment.TenantId, environment.EnvironmentId, caller, folder.WorkflowId);

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

    /// <summary>
    /// The directory the operator named as the workflow to register.
    ///
    /// The current directory is taken literally rather than resolved to an enclosing project: init is
    /// run from inside the workflow being registered, and discovering an ancestor project instead would
    /// scan the whole project and register a workflow the operator never pointed at.
    /// </summary>
    /// <param name="request">The command input.</param>
    /// <returns>The named source, or <see langword="null"/> when none was supplied.</returns>
    private static string? SourceDirectory(ManagedAgentInitRequest request) =>
        !string.IsNullOrWhiteSpace(request.McsWorkflowSource) ? request.McsWorkflowSource
        : !string.IsNullOrWhiteSpace(request.McsAgentSource) ? request.McsAgentSource
        : null;
}
