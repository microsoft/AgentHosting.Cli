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
    private readonly HomeDeploymentRegistrar _homeDeployments;
    private readonly IWorkflowStorePort _workflows;
    private readonly IWorkflowStateRules _stateRules;
    private readonly IWorkflowScaffoldPort _scaffold;
    private readonly AgentConfigLocator _locator;
    private readonly ManagedAgentPrincipalResolver _principals;
    private readonly IWorkflowWorkspacePort _workspace;
    private readonly IAgentCreationHook _creation;

    /// <summary>Creates the service.</summary>
    /// <param name="environmentResolver">Resolves the environments the record and clone source belong to.</param>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="homeDeployments">Records a clone's home deployment.</param>
    /// <param name="workflows">Downloads a clone source.</param>
    /// <param name="stateRules">Maps the clone source's state onto the label the portal shows.</param>
    /// <param name="scaffold">Checks the workflow's name and writes a new workflow from the template.</param>
    /// <param name="locator">Resolves project and configuration directories.</param>
    /// <param name="principals">Resolves the signed-in operator recorded as owner and creator.</param>
    /// <param name="workspace">Names the workflow's folder and finds the workflows a workspace holds.</param>
    /// <param name="creation">Runs the work that belongs between the checks and the writes, and after the writes, such as AMS mode's.</param>
    public WorkflowCreateService(CopilotEnvironmentResolver environmentResolver, IManagedAgentRegistry agents, HomeDeploymentRegistrar homeDeployments, IWorkflowStorePort workflows, IWorkflowStateRules stateRules, IWorkflowScaffoldPort scaffold, AgentConfigLocator locator, ManagedAgentPrincipalResolver principals, IWorkflowWorkspacePort workspace, IAgentCreationHook? creation = null)
    {
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _homeDeployments = homeDeployments ?? throw new ArgumentNullException(nameof(homeDeployments));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _stateRules = stateRules ?? throw new ArgumentNullException(nameof(stateRules));
        _scaffold = scaffold ?? throw new ArgumentNullException(nameof(scaffold));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _principals = principals ?? throw new ArgumentNullException(nameof(principals));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _creation = creation ?? NoAgentCreationHook.Instance;
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

        var displayName = RequireValidName(request.Name.Trim());
        var environment = await _environmentResolver.ResolveAsync(request.EnvironmentId, cancellationToken).ConfigureAwait(false);
        var (projectDirectory, projectCreated) = PrepareProjectDirectory(request.Project);
        var folderName = _workspace.ProjectFolderName(displayName);
        var workspaceRoot = RequireWorkspaceRootIsFree(projectDirectory, displayName, folderName);
        if (await _creation.PrepareAsync(new AgentCreationPlan(projectDirectory, projectCreated, folderName, displayName, AgentTypes.McsWorkflow, environment.EnvironmentId, request.RepositoryId, request.NonInteractive, null, null), cancellationToken).ConfigureAwait(false) is not { } ticket)
        {
            return Cancelled(projectDirectory, displayName, environment, "scaffold");
        }

        Directory.CreateDirectory(workspaceRoot);
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
            WorkflowName = displayName,
            WorkflowFolder = _workspace.RequireSingle(workspaceRoot).FolderPath,
            WorkflowState = WorkflowStateProjection.NotDeployed,
            TenantId = stored.TenantId,
            EnvironmentId = stored.EnvironmentId,
            ProjectDirectory = projectDirectory,
            ConfigPath = _locator.ConfigFilePath(projectDirectory),
            CreatedFiles = createdFiles,
            CreatedFrom = "scaffold",
            Ams = await _creation.CompleteAsync(ticket, stored, null, [], cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<ManagedAgentCreateResult> CloneAsync(ManagedAgentCreateRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.CloneWorkflowId!.Trim(), out var workflowId) || workflowId == Guid.Empty)
        {
            throw new UsageException($"--mcs-clone-workflow-id '{request.CloneWorkflowId}' is not a workflow id.");
        }

        var requestedName = string.IsNullOrWhiteSpace(request.Name) ? null : RequireValidName(request.Name.Trim());
        var source = await _environmentResolver.ResolveAsync(request.CloneEnvironmentId ?? request.EnvironmentId, cancellationToken).ConfigureAwait(false);
        var target = string.IsNullOrWhiteSpace(request.EnvironmentId) ? source : await _environmentResolver.ResolveAsync(request.EnvironmentId, cancellationToken).ConfigureAwait(false);

        var remote = await _workflows.GetAsync(source, workflowId, cancellationToken).ConfigureAwait(false)
            ?? throw ManagedAgentException.WorkflowSourceNotFound(workflowId.ToString(), source.EnvironmentId);

        var displayName = requestedName ?? RequireValidName(remote.Name);
        var projectDirectory = _locator.ResolveProjectDirectory(request.Project);
        var projectCreated = !Directory.Exists(projectDirectory);

        WorkflowRegistrationGuard.RequireUnregistered(_agents, _locator, projectDirectory, workflowId);

        var folderName = _workspace.ProjectFolderName(displayName);
        var staged = CloneStaging.Stage(Path.Combine(projectDirectory, folderName));
        var workspaceRoot = RequireWorkspaceRootIsFree(projectDirectory, displayName, folderName);
        if (await _creation.PrepareAsync(new AgentCreationPlan(projectDirectory, projectCreated, folderName, displayName, AgentTypes.McsWorkflow, target.EnvironmentId, request.RepositoryId, request.NonInteractive, null, CloneEnvironments.IsCopy(source, target) ? null : workflowId.ToString()), cancellationToken).ConfigureAwait(false) is not { } ticket)
        {
            return Cancelled(projectDirectory, displayName, target, "clone");
        }

        Directory.CreateDirectory(workspaceRoot);

        var downloaded = await CloneOrDiscardAsync(source, workflowId, workspaceRoot, CloneEnvironments.NameOverride(requestedName, source, target), staged, cancellationToken).ConfigureAwait(false);
        var stored = Register(projectDirectory, displayName, request.Description ?? remote.Description, folderName, target, workflowId, await _principals.ResolveAsync(cancellationToken).ConfigureAwait(false));
        var deployment = _homeDeployments.Register(projectDirectory, stored, target.EnvironmentId);

        return new ManagedAgentCreateResult
        {
            AgentId = stored.AgentId,
            DisplayName = stored.DisplayName,
            AgentType = stored.AgentType,
            McsWorkflowId = stored.McsWorkflowId,
            WorkflowName = downloaded.Workflow.Name,
            WorkflowFolder = downloaded.FolderPath,
            WorkflowState = _stateRules.PortalLabel(downloaded.Workflow.State),
            TenantId = stored.TenantId,
            EnvironmentId = stored.EnvironmentId,
            SourceEnvironmentId = source.EnvironmentId,
            ProjectDirectory = projectDirectory,
            ConfigPath = _locator.ConfigFilePath(projectDirectory),
            CreatedFrom = "clone",
            DeploymentId = deployment.DeploymentId,
            DeploymentName = deployment.DeploymentName,
            DeploymentType = deployment.DeploymentType,
            Warnings = downloaded.Warnings,
            Ams = await _creation.CompleteAsync(ticket, stored, deployment, [workspaceRoot], cancellationToken).ConfigureAwait(false),
        };
    }

    private (string Directory, bool Created) PrepareProjectDirectory(string? project)
    {
        var projectDirectory = _locator.ResolveProjectDirectory(project);
        var created = !Directory.Exists(projectDirectory);
        Directory.CreateDirectory(projectDirectory);

        return (projectDirectory, created);
    }

    private ManagedAgentCreateResult Cancelled(string projectDirectory, string displayName, CopilotEnvironmentContext environment, string createdFrom) => new()
    {
        Status = "cancelled",
        AgentId = string.Empty,
        DisplayName = displayName,
        AgentType = AgentTypes.McsWorkflow,
        TenantId = environment.TenantId,
        EnvironmentId = environment.EnvironmentId,
        ProjectDirectory = projectDirectory,
        ConfigPath = _locator.ConfigFilePath(projectDirectory),
        CreatedFrom = createdFrom,
    };

    private async Task<WorkflowDownloadOutcome> CloneOrDiscardAsync(CopilotEnvironmentContext source, Guid workflowId, string workspaceRoot, string? name, (string Path, bool Existed) staged, CancellationToken cancellationToken)
    {
        try
        {
            return await _workflows.CloneAsync(source, workflowId, workspaceRoot, name, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            CloneStaging.Discard([staged]);
            throw;
        }
    }

    private string RequireValidName(string displayName) => _scaffold.ValidateName(displayName) is { } reason ? throw ManagedAgentException.WorkflowNameInvalid(reason) : displayName;

    private string RequireWorkspaceRootIsFree(string projectDirectory, string displayName, string folderName)
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

        WorkspaceDirectoryGuard.RequireEmpty(workspaceRoot);

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
}
