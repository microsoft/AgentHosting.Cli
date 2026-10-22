using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary><c>msagent agent create --agent-type MCSWorkflow</c>, scaffolding a workflow or cloning one.</summary>
public sealed class WorkflowCreateService
{
    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly IManagedAgentRegistry _agents;
    private readonly IAgentDeploymentRegistry _deployments;
    private readonly IWorkflowStorePort _workflows;
    private readonly IWorkflowScaffoldPort _scaffold;
    private readonly AgentConfigLocator _locator;
    private readonly ManagedAgentPrincipalResolver _principals;
    private readonly IWorkflowWorkspacePort _workspace;

    /// <summary>Creates the service.</summary>
    /// <param name="environmentResolver">Resolves the environments the record and clone source belong to.</param>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="deployments">The agent-deployment table, written for a clone's home slot.</param>
    /// <param name="workflows">Downloads a clone source.</param>
    /// <param name="scaffold">Writes a new workflow from the template.</param>
    /// <param name="locator">Resolves project and configuration directories.</param>
    /// <param name="principals">Resolves the signed-in operator recorded as owner and creator.</param>
    /// <param name="workspace">Finds the workflows a workspace holds.</param>
    public WorkflowCreateService(CopilotEnvironmentResolver environmentResolver, IManagedAgentRegistry agents, IAgentDeploymentRegistry deployments, IWorkflowStorePort workflows, IWorkflowScaffoldPort scaffold, AgentConfigLocator locator, ManagedAgentPrincipalResolver principals, IWorkflowWorkspacePort workspace)
    {
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _scaffold = scaffold ?? throw new ArgumentNullException(nameof(scaffold));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _principals = principals ?? throw new ArgumentNullException(nameof(principals));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <summary>Creates a workflow record, scaffolding the files or cloning them from an environment.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The created record and what was written alongside it.</returns>
    public async Task<ManagedAgentCreateResult> CreateAsync(ManagedAgentCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.IsNullOrWhiteSpace(request.CloneWorkflowId)
            ? await ScaffoldAsync(request, cancellationToken).ConfigureAwait(false)
            : await CloneAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ManagedAgentCreateResult> ScaffoldAsync(ManagedAgentCreateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new UsageException("`msagent agent create --agent-type MCSWorkflow` requires --agent-name.");
        }

        if (!string.IsNullOrWhiteSpace(request.CloneEnvironmentId))
        {
            throw new UsageException("--mcs-clone-environment-id selects a clone source, so it needs --mcs-clone-workflow-id.");
        }

        var environment = await _environmentResolver.ResolveAsync(request.EnvironmentId, cancellationToken).ConfigureAwait(false);
        var displayName = request.Name.Trim();
        var projectDirectory = PrepareProjectDirectory(request.Project);
        var folderName = AgentConfigLocator.WorkspaceFolderName(displayName);
        var workspaceRoot = PrepareWorkspaceRoot(projectDirectory, displayName, folderName);
        var workflowId = Guid.NewGuid();

        var createdFiles = ProjectRelative(folderName, _scaffold.Scaffold(workspaceRoot, displayName, workflowId));
        _workflows.WriteConnectionReferenceDeclaration(workspaceRoot);
        var stored = Register(projectDirectory, displayName, request.Description, folderName, environment, workflowId, await _principals.ResolveAsync(cancellationToken).ConfigureAwait(false));

        return new ManagedAgentCreateResult
        {
            AgentId = stored.AgentId,
            DisplayName = stored.DisplayName,
            AgentType = stored.AgentType,
            McsWorkflowId = stored.McsWorkflowId,
            WorkflowFolder = _workspace.RequireSingle(workspaceRoot).FolderPath,
            WorkflowState = WorkflowStateProjection.NotDeployed,
            TenantId = stored.TenantId,
            EnvironmentId = stored.EnvironmentId,
            ProjectDirectory = projectDirectory,
            ConfigPath = _locator.ConfigFilePath(projectDirectory),
            CreatedFiles = createdFiles,
            CreatedFrom = "scaffold",
        };
    }

    private async Task<ManagedAgentCreateResult> CloneAsync(ManagedAgentCreateRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.CloneWorkflowId!.Trim(), out var workflowId) || workflowId == Guid.Empty)
        {
            throw new UsageException($"--mcs-clone-workflow-id '{request.CloneWorkflowId}' is not a workflow id.");
        }

        var source = await _environmentResolver.ResolveAsync(request.CloneEnvironmentId ?? request.EnvironmentId, cancellationToken).ConfigureAwait(false);
        var target = string.IsNullOrWhiteSpace(request.EnvironmentId) ? source : await _environmentResolver.ResolveAsync(request.EnvironmentId, cancellationToken).ConfigureAwait(false);

        var remote = await _workflows.GetAsync(source, workflowId, cancellationToken).ConfigureAwait(false)
            ?? throw ManagedAgentException.WorkflowSourceNotFound(workflowId.ToString(), source.EnvironmentId);

        var displayName = string.IsNullOrWhiteSpace(request.Name) ? remote.Name : request.Name.Trim();
        var projectDirectory = PrepareProjectDirectory(request.Project);

        WorkflowRegistrationGuard.RequireUnregistered(_agents, _locator, projectDirectory, workflowId);

        var folderName = AgentConfigLocator.WorkspaceFolderName(displayName);
        var workspaceRoot = PrepareWorkspaceRoot(projectDirectory, displayName, folderName);

        var downloaded = await _workflows.DownloadAsync(source, workflowId, workspaceRoot, cancellationToken).ConfigureAwait(false);
        var stored = Register(projectDirectory, displayName, request.Description ?? remote.Description, folderName, target, workflowId, await _principals.ResolveAsync(cancellationToken).ConfigureAwait(false));
        var deployment = RegisterHomeDeploymentOrRollBack(projectDirectory, stored, target.EnvironmentId);

        return new ManagedAgentCreateResult
        {
            AgentId = stored.AgentId,
            DisplayName = stored.DisplayName,
            AgentType = stored.AgentType,
            McsWorkflowId = stored.McsWorkflowId,
            WorkflowFolder = downloaded.FolderPath,
            WorkflowState = WorkflowStateProjection.ToPortalLabel(downloaded.Workflow.State),
            TenantId = stored.TenantId,
            EnvironmentId = stored.EnvironmentId,
            SourceEnvironmentId = source.EnvironmentId,
            ProjectDirectory = projectDirectory,
            ConfigPath = _locator.ConfigFilePath(projectDirectory),
            CreatedFrom = "clone",
            DeploymentId = deployment.DeploymentId,
            DeploymentName = deployment.DeploymentName,
            DeploymentType = deployment.DeploymentType,
        };
    }

    private string PrepareProjectDirectory(string? project)
    {
        var projectDirectory = _locator.ResolveProjectDirectory(project);
        Directory.CreateDirectory(projectDirectory);

        return projectDirectory;
    }

    private string PrepareWorkspaceRoot(string projectDirectory, string displayName, string folderName)
    {
        if (_agents.List(projectDirectory).FirstOrDefault(agent => string.Equals(agent.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)) is { } duplicate)
        {
            throw ManagedAgentException.AlreadyRegistered(duplicate.DisplayName, duplicate.AgentId, _locator.ConfigFilePath(projectDirectory));
        }

        var workspaceRoot = Path.Combine(projectDirectory, folderName);

        if (_workspace.FindAll(workspaceRoot).Count > 0)
        {
            throw ManagedAgentException.WorkspaceNotEmpty(workspaceRoot, "a workflow");
        }

        if (_workspace.HasAgentProject(workspaceRoot))
        {
            throw ManagedAgentException.WorkspaceNotEmpty(workspaceRoot, "an agent project");
        }

        Directory.CreateDirectory(workspaceRoot);

        return workspaceRoot;
    }

    /// <summary>
    /// Restates the scaffolder's workspace-relative paths against the project directory the result reports.
    /// </summary>
    /// <param name="folderName">The workspace folder's name within the project directory.</param>
    /// <param name="workspaceRelativeFiles">The files the scaffolder wrote, relative to the workspace root.</param>
    /// <returns>The same files, relative to the project directory.</returns>
    private static IReadOnlyList<string> ProjectRelative(string folderName, IReadOnlyList<string> workspaceRelativeFiles) =>
        [.. workspaceRelativeFiles.Select(file => $"{folderName}/{file.Replace('\\', '/')}")];

    private ManagedAgentRecord Register(string projectDirectory, string displayName, string? description, string agentLocation, CopilotEnvironmentContext environment, Guid workflowId, PrincipalReference? caller) =>
        _agents.Add(projectDirectory, new ManagedAgentRecord
        {
            AgentId = Guid.NewGuid().ToString(),
            DisplayName = displayName,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            AgentType = AgentTypes.McsWorkflow,
            AgentLocation = agentLocation,
            McsWorkflowId = workflowId.ToString(),
            TenantId = environment.TenantId,
            EnvironmentId = environment.EnvironmentId,
            Origin = ManagedAgentOrigins.AgentHostCli,
            Owner = caller,
            CreatedBy = caller,
            EntityTag = string.Empty,
            AgentState = EntityStates.Saved,
        });

    private AgentDeploymentRecord RegisterHomeDeploymentOrRollBack(string projectDirectory, ManagedAgentRecord agent, string targetEnvironmentId)
    {
        try
        {
            return _deployments.Add(projectDirectory, new AgentDeploymentRecord
            {
                DeploymentId = Guid.NewGuid().ToString(),
                AgentId = agent.AgentId,
                AgentType = agent.AgentType,
                DeploymentType = DeploymentStages.Dev,
                DeploymentName = DeploymentStages.Dev,
                TenantId = agent.TenantId,
                HomeEnvironmentId = agent.EnvironmentId,
                EnvironmentId = targetEnvironmentId,
                CreatedBy = agent.CreatedBy,
                EntityTag = string.Empty,
            });
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            _agents.RemoveWithDeployments(projectDirectory, agent.AgentId, agent.EntityTag);

            throw ManagedAgentException.CloneNotRegistered(agent.DisplayName, failure);
        }
    }
}
