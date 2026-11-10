using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent agent create</c> on the MCS path: scaffolds a new local project and registers it, or records
/// a clone source and defers the content to the first deploy.
/// </summary>
public sealed class ManagedAgentCreateService
{
    private const string DefaultPublisherPrefix = "cr";

    private readonly ICopilotScaffoldPort _scaffolds;
    private readonly CopilotSchemaName _schemaNames;
    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly ICopilotCatalogPort _catalog;
    private readonly IManagedAgentRegistry _agents;
    private readonly HomeDeploymentRegistrar _homeDeployments;
    private readonly ManagedAgentCloneMaterialiser _materialiser;
    private readonly AgentConfigLocator _locator;
    private readonly CopilotProjectIdentityReader _projectIdentities;
    private readonly ManagedAgentPrincipalResolver _principals;
    private readonly ICopilotWorkspacePort _workspaces;
    private readonly IAgentCreationHook _creation;

    /// <summary>Creates the service.</summary>
    /// <param name="scaffolds">Checks the display name and writes the project files.</param>
    /// <param name="schemaNames">Derives and validates schema names.</param>
    /// <param name="environmentResolver">Resolves the environment the record is registered in.</param>
    /// <param name="catalog">Resolves a clone source in its environment.</param>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="homeDeployments">Records a clone's home deployment.</param>
    /// <param name="materialiser">Writes a clone's content into the project directory.</param>
    /// <param name="locator">Resolves project and configuration directories.</param>
    /// <param name="projectIdentities">Detects a project already present in the directory.</param>
    /// <param name="principals">Resolves the signed-in operator recorded as owner and creator.</param>
    /// <param name="workspaces">Names the folder the agent's workspace occupies.</param>
    /// <param name="creation">Runs the work that belongs between the checks and the writes, and after the writes, such as AMS mode's.</param>
    public ManagedAgentCreateService(ICopilotScaffoldPort scaffolds, CopilotSchemaName schemaNames, CopilotEnvironmentResolver environmentResolver, ICopilotCatalogPort catalog, IManagedAgentRegistry agents, HomeDeploymentRegistrar homeDeployments, ManagedAgentCloneMaterialiser materialiser, AgentConfigLocator locator, CopilotProjectIdentityReader projectIdentities, ManagedAgentPrincipalResolver principals, ICopilotWorkspacePort workspaces, IAgentCreationHook? creation = null)
    {
        _scaffolds = scaffolds ?? throw new ArgumentNullException(nameof(scaffolds));
        _schemaNames = schemaNames ?? throw new ArgumentNullException(nameof(schemaNames));
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _homeDeployments = homeDeployments ?? throw new ArgumentNullException(nameof(homeDeployments));
        _materialiser = materialiser ?? throw new ArgumentNullException(nameof(materialiser));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _projectIdentities = projectIdentities ?? throw new ArgumentNullException(nameof(projectIdentities));
        _principals = principals ?? throw new ArgumentNullException(nameof(principals));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _creation = creation ?? NoAgentCreationHook.Instance;
    }

    /// <summary>Creates a managed agent, scaffolding a project or recording a clone source.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The created record and what was written alongside it.</returns>
    public async Task<ManagedAgentCreateResult> CreateAsync(ManagedAgentCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var isClone = !string.IsNullOrWhiteSpace(request.CloneSchemaName) || !string.IsNullOrWhiteSpace(request.CloneAgentId);

        if (!isClone && string.IsNullOrWhiteSpace(request.Name))
        {
            throw new UsageException("`msagent agent create` requires --agent-name.");
        }

        if (request.Description is { Length: > 2000 })
        {
            throw new UsageException("--description is longer than 2000 characters.");
        }

        return isClone
            ? await CreateFromCloneAsync(request, cancellationToken).ConfigureAwait(false)
            : await ScaffoldAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the directory the project belongs in, creating it when it does not exist.
    ///
    /// A clone names where its checkout should land, so a directory that is not there yet is an
    /// instruction rather than a mistake.
    /// </summary>
    private (string Directory, bool Created) ResolveProjectDirectory(string? project)
    {
        var projectDirectory = _locator.ResolveProjectDirectory(project);
        var created = !Directory.Exists(projectDirectory);
        Directory.CreateDirectory(projectDirectory);

        return (projectDirectory, created);
    }

    private async Task<ManagedAgentCreateResult> ScaffoldAsync(ManagedAgentCreateRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.CloneEnvironmentId))
        {
            throw new UsageException("--mcs-clone-environment-id selects a clone source, so it needs --mcs-clone-agent-schemaname or --mcs-clone-agent-id.");
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            throw new UsageException("`msagent agent create` requires --description for a new MCS agent.");
        }

        if (string.IsNullOrWhiteSpace(request.InitialPrompt))
        {
            throw new UsageException("`msagent agent create` requires --initial-prompt for a new MCS agent.");
        }

        var displayName = _scaffolds.ValidateDisplayName(request.Name!.Trim());
        var environment = await _environmentResolver.ResolveAsync(request.EnvironmentId, cancellationToken).ConfigureAwait(false);
        var (projectDirectory, projectCreated) = ResolveProjectDirectory(request.Project);
        var agentLocation = _workspaces.ProjectFolderName(displayName);
        var workspaceDirectory = Path.Combine(projectDirectory, agentLocation);

        RequireProjectIsFree(projectDirectory, workspaceDirectory, displayName);

        var template = CopilotAuthoringModes.Resolve(request.McsAgentType, request.InitTemplate);
        var schemaName = _schemaNames.Derive(request.Name, DefaultPublisherPrefix, request.SchemaName);

        if (await _creation.PrepareAsync(new AgentCreationPlan(projectDirectory, projectCreated, agentLocation, displayName, AgentTypes.McsAgent, environment.EnvironmentId, request.RepositoryId, request.NonInteractive, null, null), cancellationToken).ConfigureAwait(false) is not { } ticket)
        {
            return Cancelled(projectDirectory, displayName, environment.TenantId, environment.EnvironmentId, "scaffold");
        }

        var createdFiles = _scaffolds.Scaffold(new CopilotScaffoldCommand { ProjectDirectory = workspaceDirectory, TemplateFolder = template.TemplateFolder, DisplayName = displayName, SchemaName = schemaName, Instructions = request.InitialPrompt });

        var stored = Register(projectDirectory, request, agentLocation, environment.TenantId, environment.EnvironmentId, await _principals.ResolveAsync(cancellationToken).ConfigureAwait(false), schemaName);

        return new ManagedAgentCreateResult
        {
            AgentId = stored.AgentId,
            DisplayName = stored.DisplayName,
            AgentType = stored.AgentType,
            Template = template.TemplateName,
            SchemaName = schemaName,
            TenantId = stored.TenantId,
            EnvironmentId = stored.EnvironmentId,
            ProjectDirectory = projectDirectory,
            ConfigPath = _locator.ConfigFilePath(projectDirectory),
            CreatedFiles = createdFiles,
            CreatedFrom = "scaffold",
            ClonedWorkspaces = [new CopilotClonedWorkspace { Kind = "agent", Path = workspaceDirectory, DisplayName = displayName }],
            Ams = await _creation.CompleteAsync(ticket, stored, null, [], cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Registers a checkout of an existing agent and writes its content into the project directory.
    /// </summary>
    private async Task<ManagedAgentCreateResult> CreateFromCloneAsync(ManagedAgentCreateRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.CloneSchemaName) && !string.IsNullOrWhiteSpace(request.CloneAgentId))
        {
            throw new UsageException("--mcs-clone-agent-schemaname and --mcs-clone-agent-id both select a clone source. Supply exactly one.");
        }

        if (!string.IsNullOrWhiteSpace(request.SchemaName))
        {
            throw new UsageException("--schema-name is not used in clone mode. The agent's schema name comes from the source agent.");
        }

        var requestedName = string.IsNullOrWhiteSpace(request.Name) ? null : _scaffolds.ValidateDisplayName(request.Name.Trim());
        var (projectDirectory, projectCreated) = ResolveProjectDirectory(request.Project);
        var source = await _environmentResolver.ResolveAsync(request.CloneEnvironmentId ?? request.EnvironmentId, cancellationToken).ConfigureAwait(false);
        var target = string.IsNullOrWhiteSpace(request.EnvironmentId)
            ? source
            : await _environmentResolver.ResolveAsync(request.EnvironmentId, cancellationToken).ConfigureAwait(false);

        var selector = string.IsNullOrWhiteSpace(request.CloneAgentId) ? request.CloneSchemaName!.Trim() : request.CloneAgentId!.Trim();
        var mcsAgentId = await _catalog.ResolveAgentIdAsync(source, selector, cancellationToken).ConfigureAwait(false)
            ?? throw ManagedAgentException.CloneSourceNotFound(selector, source.EnvironmentId);

        var sourceAgent = await _catalog.GetAgentAsync(source, mcsAgentId, cancellationToken).ConfigureAwait(false);
        var displayName = requestedName ?? sourceAgent.DisplayName;
        var agentLocation = _workspaces.ProjectFolderName(displayName);
        var workspaceDirectory = Path.Combine(projectDirectory, agentLocation);

        RequireProjectIsFree(projectDirectory, workspaceDirectory, displayName);

        if (await _creation.PrepareAsync(new AgentCreationPlan(projectDirectory, projectCreated, agentLocation, displayName, AgentTypes.McsAgent, target.EnvironmentId, request.RepositoryId, request.NonInteractive, CloneEnvironments.IsCopy(source, target) ? null : mcsAgentId, null), cancellationToken).ConfigureAwait(false) is not { } ticket)
        {
            return Cancelled(projectDirectory, displayName, target.TenantId, target.EnvironmentId, "clone");
        }

        var materialised = await MaterialiseOrFailAsync(projectDirectory, workspaceDirectory, source, mcsAgentId, CloneEnvironments.NameOverride(requestedName, source, target), cancellationToken).ConfigureAwait(false);
        var clonedWorkspaces = materialised?.Workspaces ?? [];
        var project = _projectIdentities.Find(workspaceDirectory);
        var description = string.IsNullOrWhiteSpace(request.Description) ? project?.Description : request.Description;
        var stored = Register(projectDirectory, request with { Name = displayName, Description = description }, agentLocation, target.TenantId, target.EnvironmentId, await _principals.ResolveAsync(cancellationToken).ConfigureAwait(false), project?.SchemaName, CloneEnvironments.IsCopy(source, target) ? null : mcsAgentId);
        var deployment = _homeDeployments.Register(projectDirectory, stored, target.EnvironmentId);

        return new ManagedAgentCreateResult
        {
            AgentId = stored.AgentId,
            DisplayName = stored.DisplayName,
            AgentType = stored.AgentType,
            SchemaName = project?.SchemaName ?? sourceAgent.SchemaName,
            McsAgentId = mcsAgentId,
            TenantId = stored.TenantId,
            EnvironmentId = stored.EnvironmentId,
            SourceEnvironmentId = source.EnvironmentId,
            ProjectDirectory = projectDirectory,
            ConfigPath = _locator.ConfigFilePath(projectDirectory),
            CreatedFrom = "clone",
            DeploymentId = deployment.DeploymentId,
            DeploymentName = deployment.DeploymentName,
            DeploymentType = deployment.DeploymentType,
            ClonedComponentCount = materialised?.ComponentCount ?? 0,
            ClonedWorkspaces = clonedWorkspaces,
            Warnings = IgnoredCloneOptions(request),
            Ams = await _creation.CompleteAsync(ticket, stored, deployment, [.. clonedWorkspaces.Select(workspace => workspace.Path)], cancellationToken).ConfigureAwait(false),
        };
    }

    private ManagedAgentCreateResult Cancelled(string projectDirectory, string displayName, string tenantId, string environmentId, string createdFrom) => new()
    {
        Status = "cancelled",
        AgentId = string.Empty,
        DisplayName = displayName,
        AgentType = AgentTypes.McsAgent,
        TenantId = tenantId,
        EnvironmentId = environmentId,
        ProjectDirectory = projectDirectory,
        ConfigPath = _locator.ConfigFilePath(projectDirectory),
        CreatedFrom = createdFrom,
    };

    private async Task<CopilotCloneOutcome?> MaterialiseOrFailAsync(string projectDirectory, string workspaceDirectory, CopilotEnvironmentContext source, string mcsAgentId, string? displayName, CancellationToken cancellationToken)
    {
        try
        {
            return await _materialiser.MaterialiseAsync(projectDirectory, workspaceDirectory, source, mcsAgentId, displayName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ManagedAgentException)
        {
            throw ManagedAgentException.CloneNotMaterialised(mcsAgentId, source.EnvironmentId, projectDirectory, exception);
        }
    }

    private ManagedAgentRecord Register(string projectDirectory, ManagedAgentCreateRequest request, string agentLocation, string tenantId, string environmentId, PrincipalReference? caller, string? mcsSchemaName = null, string? mcsAgentId = null) =>
        _agents.Add(projectDirectory, new ManagedAgentRecord
        {
            AgentId = Guid.NewGuid().ToString(),
            DisplayName = request.Name!.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            AgentType = AgentTypes.McsAgent,
            AgentLocation = agentLocation,
            McsSchemaName = mcsSchemaName,
            McsAgentId = mcsAgentId,
            TenantId = tenantId,
            EnvironmentId = environmentId,
            Origin = ManagedAgentOrigins.AgentHostCli,
            Owner = caller,
            CreatedBy = caller,
            EntityTag = string.Empty,
            AgentState = EntityStates.Saved,
        });

    /// <summary>
    /// Rejects a name or folder already taken, before anything is written.
    /// </summary>
    private void RequireProjectIsFree(string projectDirectory, string workspaceDirectory, string name)
    {
        if (_agents.List(projectDirectory).FirstOrDefault(agent => string.Equals(agent.DisplayName, name, StringComparison.OrdinalIgnoreCase)) is { } duplicate)
        {
            throw ManagedAgentException.AlreadyRegistered(duplicate.DisplayName, duplicate.AgentId, _locator.ConfigFilePath(projectDirectory));
        }

        WorkspaceDirectoryGuard.RequireEmpty(workspaceDirectory);
    }

    private static IReadOnlyList<string> IgnoredCloneOptions(ManagedAgentCreateRequest request)
    {
        var ignored = new[]
        {
            (Option: "--init-template", Supplied: !string.IsNullOrWhiteSpace(request.InitTemplate)),
            (Option: "--mcs-agent-type", Supplied: !string.IsNullOrWhiteSpace(request.McsAgentType)),
            (Option: "--initial-prompt", Supplied: !string.IsNullOrWhiteSpace(request.InitialPrompt)),
        };

        return [.. ignored.Where(candidate => candidate.Supplied).Select(candidate => $"{candidate.Option} is ignored in clone mode: there is nothing to scaffold.")];
    }
}
