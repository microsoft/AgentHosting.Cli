using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Runs <c>msagent agent copy</c>, preparing and registering a new agent's or standalone workflow's local content from an existing one without changing the source.</summary>
public sealed class ManagedAgentCopyService
{
    private const string Verb = "msagent agent copy";
    private const string NameOption = "--agent-name";
    private const string CloneAgentOption = "--mcs-clone-agent-id";
    private const string CloneWorkflowOption = "--mcs-clone-workflow-id";
    private const string CloudCacheFolderName = CopyProjectState.CloudCacheFolderName;
    private const string SourceDefinitionFileName = "botdefinition.json";
    private const string StagingFolderName = "staging";
    private const string SourceCacheFolderName = "source";

    private static readonly string[] ExcludedWorkspaceEntries = [".git", AgentConfigLocator.ConfigDirectoryName, CloudCacheFolderName];

    private readonly ManagedAgentResolver _resolver;
    private readonly IManagedAgentRegistry _agents;
    private readonly AgentConfigLocator _locator;
    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly ICopilotCatalogPort _catalog;
    private readonly ManagedAgentCloneMaterialiser _materialiser;
    private readonly CopilotWorkspaceLocator _workspaceLocator;
    private readonly CopilotProjectIdentityReader _projectIdentities;
    private readonly CopilotSchemaName _schemaNames;
    private readonly CopilotAgentName _agentNames;
    private readonly ICopilotCopyPort _copies;
    private readonly ManagedAgentPrincipalResolver _principals;
    private readonly ICopilotWorkspacePort _workspaces;
    private readonly IWorkflowStorePort _workflows;
    private readonly IWorkflowWorkspacePort _workflowWorkspace;
    private readonly IWorkflowScaffoldPort _workflowScaffold;
    private readonly ManagedAgentRegistration _registration;

    /// <summary>Creates the service.</summary>
    /// <param name="resolver">Resolves a local source's record and workspace.</param>
    /// <param name="agents">The managed-agent table the copy is registered in.</param>
    /// <param name="locator">Resolves project and configuration directories.</param>
    /// <param name="environmentResolver">Resolves the source's environment.</param>
    /// <param name="catalog">Finds a cloud source, and agents already using the new name.</param>
    /// <param name="materialiser">Reads a cloud source through the existing clone.</param>
    /// <param name="workspaceLocator">Reads a local source's cloud binding.</param>
    /// <param name="projectIdentities">Reads a local source's declared identity.</param>
    /// <param name="schemaNames">Validates an explicit schema name.</param>
    /// <param name="agentNames">Refuses a display name Copilot Studio would refuse.</param>
    /// <param name="copies">Prepares the copy, checks its new identities are free in the source's environment, and proposes its schema name, through the shared copy implementation.</param>
    /// <param name="principals">Resolves the signed-in operator recorded as owner and creator.</param>
    /// <param name="workspaces">Names the folder an agent copy's workspace occupies.</param>
    /// <param name="workflows">Reads a local workflow source, and downloads a cloud one into the copy's staging.</param>
    /// <param name="workflowWorkspace">Names the folder a workflow copy's workspace occupies, finds the flow it holds, and names the folders a workflow project owns.</param>
    /// <param name="workflowScaffold">Refuses a workflow name Dataverse would refuse.</param>
    public ManagedAgentCopyService(ManagedAgentResolver resolver, IManagedAgentRegistry agents, AgentConfigLocator locator, CopilotEnvironmentResolver environmentResolver, ICopilotCatalogPort catalog, ManagedAgentCloneMaterialiser materialiser, CopilotWorkspaceLocator workspaceLocator, CopilotProjectIdentityReader projectIdentities, CopilotSchemaName schemaNames, CopilotAgentName agentNames, ICopilotCopyPort copies, ManagedAgentPrincipalResolver principals, ICopilotWorkspacePort workspaces, IWorkflowStorePort workflows, IWorkflowWorkspacePort workflowWorkspace, IWorkflowScaffoldPort workflowScaffold)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _materialiser = materialiser ?? throw new ArgumentNullException(nameof(materialiser));
        _workspaceLocator = workspaceLocator ?? throw new ArgumentNullException(nameof(workspaceLocator));
        _projectIdentities = projectIdentities ?? throw new ArgumentNullException(nameof(projectIdentities));
        _schemaNames = schemaNames ?? throw new ArgumentNullException(nameof(schemaNames));
        _agentNames = agentNames ?? throw new ArgumentNullException(nameof(agentNames));
        _copies = copies ?? throw new ArgumentNullException(nameof(copies));
        _principals = principals ?? throw new ArgumentNullException(nameof(principals));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _workflowWorkspace = workflowWorkspace ?? throw new ArgumentNullException(nameof(workflowWorkspace));
        _workflowScaffold = workflowScaffold ?? throw new ArgumentNullException(nameof(workflowScaffold));
        _registration = new ManagedAgentRegistration(_agents, locator);
    }

    /// <summary>Prepares a copy and registers it.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the copy; whatever was staged is discarded.</param>
    /// <returns>The registered copy.</returns>
    public async Task<ManagedAgentCopyResult> CopyAsync(ManagedAgentCopyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ManagedAgentCopyValidation.Problem(request) is { } invalid)
        {
            throw invalid;
        }

        var sourceKind = string.IsNullOrWhiteSpace(request.CloneAgentId) && string.IsNullOrWhiteSpace(request.CloneWorkflowId) ? CopilotCopySourceKinds.Local : CopilotCopySourceKinds.Cloud;
        var cloudSourceName = sourceKind == CopilotCopySourceKinds.Cloud ? RequireNewName(request.Name!, !string.IsNullOrWhiteSpace(request.CloneWorkflowId)) : null;
        var explicitSchemaName = string.IsNullOrWhiteSpace(request.SchemaName) ? null : _schemaNames.Validate(request.SchemaName.Trim());
        (string ProjectDirectory, ManagedAgentRecord Agent)? located = sourceKind == CopilotCopySourceKinds.Local ? _resolver.ResolveAgentAt(request.Project) : null;
        var copiesWorkflow = !string.IsNullOrWhiteSpace(request.CloneWorkflowId) || located?.Agent.AgentType == AgentTypes.McsWorkflow;

        if (copiesWorkflow && !string.IsNullOrWhiteSpace(request.SchemaName))
        {
            throw ManagedAgentCopyValidation.SchemaNameForWorkflow();
        }

        var newName = cloudSourceName ?? RequireNewName(request.Name!, copiesWorkflow);
        var source = await ((located, copiesWorkflow) switch
        {
            ({ } local, true) => ResolveLocalWorkflowSourceAsync(request, local.ProjectDirectory, local.Agent, cancellationToken),
            ({ } local, false) => ResolveLocalSourceAsync(request, local.ProjectDirectory, local.Agent, cancellationToken),
            (null, true) => ResolveCloudWorkflowSourceAsync(request, cancellationToken),
            (null, false) => ResolveCloudSourceAsync(request, cancellationToken),
        }).ConfigureAwait(false);

        if (!source.IsWorkflow)
        {
            RequireNewAgentNames(newName, explicitSchemaName, source);
        }

        var folderName = source.IsWorkflow ? _workflowWorkspace.ProjectFolderName(newName) : _workspaces.ProjectFolderName(newName);
        var projectDirectory = DestinationProject(request, source, folderName);
        var workspaceDirectory = Path.Combine(projectDirectory, folderName);

        RequireSeparateFromSource(source, workspaceDirectory);

        var agentId = Guid.NewGuid().ToString();
        var staging = new CopyStaging(projectDirectory, agentId);
        using var projectLock = CopyProjectState.TryLockProject(projectDirectory) ?? throw ManagedAgentException.CopyIntoProjectBusy(projectDirectory);

        try
        {
            _registration.RequireProjectIsFree(_agents.List(projectDirectory), projectDirectory, workspaceDirectory, newName, projectChoosesDestination: source.WorkspaceDirectory is null);
            CopyProjectState.RemoveAbandoned(projectDirectory);

            var caller = await _principals.ResolveAsync(cancellationToken).ConfigureAwait(false);
            var (stagedWorkspace, stagedSource) = await StageAsync(source, staging, folderName, cancellationToken).ConfigureAwait(false);
            var schemaName = stagedSource.SchemaName is not { } sourceSchemaName ? null : explicitSchemaName ?? await _copies.ProposeSchemaNameAsync(stagedWorkspace, sourceSchemaName, newName, cancellationToken).ConfigureAwait(false) ?? throw new UsageException($"No schema name can be generated for '{newName}': the name has no ASCII letters or digits, or the source's longest component name leaves no room for one. Pass --schema-name.");
            var sourceDefinition = Path.Combine(staging.SourceCacheDirectory, SourceDefinitionFileName);
            var prepared = await _copies.PrepareAsync(
                new CopilotCopyPrepareCommand
                {
                    WorkspaceDirectory = stagedWorkspace,
                    AgentType = stagedSource.IsWorkflow ? AgentTypes.McsWorkflow : AgentTypes.McsAgent,
                    SourceSchemaName = stagedSource.SchemaName,
                    SourceWorkflowId = stagedSource.WorkflowId,
                    SourceDefinitionPath = !stagedSource.IsWorkflow && File.Exists(sourceDefinition) ? sourceDefinition : null,
                    DisplayName = newName,
                    SchemaName = schemaName,
                    CapturedInTargetEnvironment = stagedSource.CapturedInEnvironment,
                    Environment = stagedSource.Environment,
                },
                cancellationToken).ConfigureAwait(false);

            if (prepared.Blockers.Count > 0)
            {
                throw ManagedAgentException.CopyNotPrepared(newName, prepared.Blockers.Select(blocker => blocker.ToString()));
            }

            staging.Commit(stagedWorkspace, workspaceDirectory);

            var workflow = stagedSource.IsWorkflow ? _workflowWorkspace.RequireSingle(workspaceDirectory) : null;
            var stored = workflow is null ? _registration.Register(projectDirectory, newName, stagedSource.Description, folderName, stagedSource.Environment.TenantId, stagedSource.Environment.EnvironmentId, caller, schemaName, mcsAgentId: null, agentId: agentId) : _registration.RegisterWorkflow(projectDirectory, newName, stagedSource.Description, folderName, stagedSource.Environment.TenantId, stagedSource.Environment.EnvironmentId, caller, workflow.WorkflowId, agentId);

            return new ManagedAgentCopyResult
            {
                AgentId = stored.AgentId,
                DisplayName = stored.DisplayName,
                AgentType = stored.AgentType,
                SchemaName = schemaName,
                McsWorkflowId = stored.McsWorkflowId,
                WorkflowName = workflow is null ? null : newName,
                WorkflowFolder = workflow?.FolderPath,
                WorkflowState = workflow is null ? null : WorkflowStateProjection.NotDeployed,
                Source = sourceKind,
                SourceSchemaName = stagedSource.SchemaName,
                SourceDisplayName = stagedSource.DisplayName,
                SourceMcsAgentId = stagedSource.McsAgentId,
                SourceMcsWorkflowId = stagedSource.WorkflowId?.ToString(),
                EnvironmentId = stored.EnvironmentId,
                TenantId = stored.TenantId,
                ProjectDirectory = projectDirectory,
                WorkspaceDirectory = workspaceDirectory,
                ConfigPath = _locator.ConfigFilePath(projectDirectory),
                Identities = prepared.Identities,
                DraftWorkflows = prepared.DraftWorkflows,
                RewrittenFileCount = prepared.RewrittenFiles.Count,
                Warnings = [.. stagedSource.Warnings, .. prepared.Notices.Select(notice => notice.ToString())],
            };
        }
        catch
        {
            staging.Discard();
            throw;
        }
    }

    private string RequireNewName(string name, bool copiesWorkflow) => copiesWorkflow ? WorkflowRegistrationGuard.RequireValidName(_workflowScaffold, name.Trim()) : _agentNames.Require(name.Trim(), NameOption);

    private static void RequireNewAgentNames(string newName, string? explicitSchemaName, CopySource source)
    {
        if (string.Equals(newName, source.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException($"--agent-name '{newName}' is the source agent's own name. A copy needs a name of its own.");
        }

        if (string.Equals(explicitSchemaName, source.SchemaName, StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException($"--schema-name {explicitSchemaName} is the source agent's own schema name. A copy needs a schema name of its own.");
        }

        if (source.EnvironmentAgents?.FirstOrDefault(agent => string.Equals(agent.DisplayName, newName, StringComparison.OrdinalIgnoreCase)) is { } namesake)
        {
            throw new UsageException($"--agent-name '{newName}' is taken: environment {source.Environment.EnvironmentId} already has an agent named '{namesake.DisplayName}' ({namesake.SchemaName}). Choose another --agent-name.");
        }
    }

    private async Task<(string Workspace, CopySource Source)> StageAsync(CopySource source, CopyStaging staging, string folderName, CancellationToken cancellationToken)
    {
        var stagedWorkspace = Path.Combine(staging.Root, folderName);
        Directory.CreateDirectory(staging.Root);

        if (source.WorkspaceDirectory is { } localWorkspace)
        {
            CopyDirectory(localWorkspace, stagedWorkspace, source.IsWorkflow && source.FillsItsProject ? IsWorkflowProjectEntry : IsWorkspaceEntry, IsNestedWorkspaceEntry);

            if (!source.IsWorkflow && Directory.Exists(Path.Combine(localWorkspace, CloudCacheFolderName)))
            {
                CopyDirectory(Path.Combine(localWorkspace, CloudCacheFolderName), staging.SourceCacheDirectory, IsAnyEntry, IsAnyEntry);
            }

            return (stagedWorkspace, source);
        }

        if (source.WorkflowId is { } workflowId)
        {
            Directory.CreateDirectory(stagedWorkspace);

            var downloaded = await _workflows.DownloadAsync(source.Environment, workflowId, stagedWorkspace, cancellationToken).ConfigureAwait(false);

            MoveCloudCacheAside(stagedWorkspace, staging);

            return (stagedWorkspace, source with { DisplayName = downloaded.Workflow.Name, Description = downloaded.Workflow.Description, Warnings = downloaded.Warnings });
        }

        try
        {
            await _materialiser.MaterialiseAsync(staging.Root, stagedWorkspace, source.Environment, source.McsAgentId!, includeLinkedComponentCollections: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not AgentHostingException)
        {
            throw ManagedAgentException.CloneNotMaterialised(source.McsAgentId!, source.Environment.EnvironmentId, staging.Root, exception);
        }

        MoveCloudCacheAside(stagedWorkspace, staging);

        return (stagedWorkspace, source);
    }

    private static void MoveCloudCacheAside(string stagedWorkspace, CopyStaging staging)
    {
        if (Directory.Exists(Path.Combine(stagedWorkspace, CloudCacheFolderName)))
        {
            FileSystemRetry.WhileHeld(() => Directory.Move(Path.Combine(stagedWorkspace, CloudCacheFolderName), staging.SourceCacheDirectory));
        }
    }

    private async Task<CopySource> ResolveLocalSourceAsync(ManagedAgentCopyRequest request, string sourceProject, ManagedAgentRecord located, CancellationToken cancellationToken)
    {
        var record = ManagedAgentResolver.RequireMcsAgent(located, Verb);
        var workspace = _resolver.ResolveAgentWorkspaceDirectory(sourceProject, record);
        var identity = _projectIdentities.Require(workspace, record.AgentId);
        var binding = await _workspaceLocator.FindAsync(workspace, cancellationToken).ConfigureAwait(false);
        var environment = await _environmentResolver.ResolveAsync(LocalSourceEnvironmentId(request.EnvironmentId, record, binding), cancellationToken).ConfigureAwait(false);

        return new CopySource(environment, identity.SchemaName, identity.DisplayName, string.IsNullOrWhiteSpace(binding?.AgentId) ? record.McsAgentId : binding.AgentId, record.Description ?? identity.Description, await _catalog.ListAgentsAsync(environment, null, cancellationToken).ConfigureAwait(false), workspace, sourceProject, !string.IsNullOrWhiteSpace(binding?.EnvironmentId));
    }

    private async Task<CopySource> ResolveLocalWorkflowSourceAsync(ManagedAgentCopyRequest request, string sourceProject, ManagedAgentRecord located, CancellationToken cancellationToken)
    {
        var workspace = _resolver.ResolveWorkspaceDirectory(sourceProject, ManagedAgentResolver.RequireMcsWorkflow(located, Verb));
        var workflowId = ManagedAgentResolver.RequireWorkflowId(located);
        var workflow = await _workflows.ReadLocalAsync(workspace, cancellationToken).ConfigureAwait(false);

        return new CopySource(await _environmentResolver.ResolveAsync(LocalSourceEnvironmentId(request.EnvironmentId, located, null), cancellationToken).ConfigureAwait(false), null, workflow.Name, null, located.Description ?? workflow.Description, null, workspace, sourceProject, false, workflowId);
    }

    private static string LocalSourceEnvironmentId(string? requestedEnvironmentId, ManagedAgentRecord record, CopilotWorkspaceInfo? binding)
    {
        var environmentId = string.IsNullOrWhiteSpace(binding?.EnvironmentId) ? record.EnvironmentId : binding.EnvironmentId;
        var requested = string.IsNullOrWhiteSpace(requestedEnvironmentId) ? null : requestedEnvironmentId.Trim();

        if (string.IsNullOrWhiteSpace(environmentId))
        {
            return requested ?? throw new UsageException($"The local source '{record.DisplayName}' records no environment, and its workspace is not connected to one. Pass --environment-id with the environment to copy it in.");
        }

        var (noun, cloneOption) = Wording(record.AgentType);

        return requested is null || string.Equals(requested, environmentId, StringComparison.OrdinalIgnoreCase) ? environmentId : throw new UsageException($"--environment-id {requested} is not the environment the local source belongs to ({environmentId}). Remove --environment-id, or copy the {noun} as it is in that environment with {cloneOption}.");
    }

    private async Task<CopySource> ResolveCloudSourceAsync(ManagedAgentCopyRequest request, CancellationToken cancellationToken)
    {
        var cloneAgentId = request.CloneAgentId!.Trim();

        RequireNotManagedRecord(AgentTypes.McsAgent, cloneAgentId, request.Project);

        var environment = await _environmentResolver.ResolveAsync(request.EnvironmentId, cancellationToken).ConfigureAwait(false);
        var agents = await _catalog.ListAgentsAsync(environment, null, cancellationToken).ConfigureAwait(false);
        var agent = agents.FirstOrDefault(candidate => string.Equals(candidate.AgentId, cloneAgentId, StringComparison.OrdinalIgnoreCase)) ?? await _catalog.GetAgentAsync(environment, cloneAgentId, cancellationToken).ConfigureAwait(false);

        return new CopySource(environment, agent.SchemaName, agent.DisplayName, agent.AgentId, null, agents, null, null, true);
    }

    private async Task<CopySource> ResolveCloudWorkflowSourceAsync(ManagedAgentCopyRequest request, CancellationToken cancellationToken)
    {
        var workflowId = WorkflowRegistrationGuard.ParseCloneWorkflowId(request.CloneWorkflowId!);

        RequireNotManagedRecord(AgentTypes.McsWorkflow, workflowId.ToString(), request.Project);

        return new CopySource(await _environmentResolver.ResolveAsync(request.EnvironmentId, cancellationToken).ConfigureAwait(false), null, null, null, null, null, null, null, true, workflowId);
    }

    private void RequireNotManagedRecord(string agentType, string cloneId, string? project)
    {
        var (noun, cloneOption) = Wording(agentType);

        foreach (var projectDirectory in new[] { _locator.ResolveProjectDirectory(project), _locator.ResolveProjectDirectory(null) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_resolver.FindAgent(projectDirectory, cloneId, agentName: null) is { } record)
            {
                var cloudId = agentType == AgentTypes.McsWorkflow ? record.McsWorkflowId : record.McsAgentId;

                throw new UsageException($"{cloneOption} {cloneId} is the managed-agent record '{record.DisplayName}' in {projectDirectory}, not a cloud {noun}. Copy that record without {cloneOption}, passing its folder {_resolver.FindWorkspaceDirectory(projectDirectory, record) ?? projectDirectory} to --project{(string.IsNullOrWhiteSpace(cloudId) ? string.Empty : $", or pass its cloud {noun} id {cloudId} to {cloneOption}")}.");
            }
        }
    }

    private static (string Noun, string CloneOption) Wording(string agentType) => agentType == AgentTypes.McsWorkflow ? ("workflow", CloneWorkflowOption) : ("agent", CloneAgentOption);

    private string DestinationProject(ManagedAgentCopyRequest request, CopySource source, string folderName)
    {
        if (source.WorkspaceDirectory is null || source.ProjectDirectory is not { } sourceProject)
        {
            return _locator.ResolveProjectDirectory(request.Project);
        }

        return source.FillsItsProject && Path.GetDirectoryName(FullDirectory(sourceProject)) is { Length: > 0 } parent ? Path.Combine(parent, folderName) : sourceProject;
    }

    private static void RequireSeparateFromSource(CopySource source, string workspaceDirectory)
    {
        if (source.WorkspaceDirectory is not { } sourceWorkspace)
        {
            return;
        }

        var copy = AgentConfigLocator.PhysicalPath(workspaceDirectory);
        var original = AgentConfigLocator.PhysicalPath(sourceWorkspace);

        if (AgentConfigLocator.IsContainedIn(copy, original) || AgentConfigLocator.IsContainedIn(original, copy))
        {
            throw new UsageException($"The copy's workspace {workspaceDirectory} would overlap the source's workspace {sourceWorkspace}. Choose another --agent-name.");
        }
    }

    private static string FullDirectory(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory, Func<FileSystemInfo, bool> includesEntry, Func<FileSystemInfo, bool> includesNestedEntry)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var entry in new DirectoryInfo(sourceDirectory).EnumerateFileSystemInfos().Where(includesEntry))
        {
            if (entry.LinkTarget is not null)
            {
                throw new UsageException($"The source workspace holds a link ({entry.FullName}). Only real files can be copied; replace the link with its content and copy again.");
            }

            if (entry is DirectoryInfo directory)
            {
                CopyDirectory(directory.FullName, Path.Combine(destinationDirectory, directory.Name), includesNestedEntry, includesNestedEntry);
            }
            else
            {
                File.Copy(entry.FullName, Path.Combine(destinationDirectory, entry.Name));
            }
        }
    }

    private static bool IsAnyEntry(FileSystemInfo entry) => true;

    private static bool IsWorkspaceEntry(FileSystemInfo entry) => !ExcludedWorkspaceEntries.Contains(entry.Name, StringComparer.OrdinalIgnoreCase);

    private static bool IsNestedWorkspaceEntry(FileSystemInfo entry) => entry is FileInfo || !string.Equals(entry.Name, CloudCacheFolderName, StringComparison.OrdinalIgnoreCase);

    private bool IsWorkflowProjectEntry(FileSystemInfo entry) => IsWorkspaceEntry(entry) && (entry is FileInfo || _workflowWorkspace.IsProjectFolder(entry.Name));

    private sealed record CopySource(CopilotEnvironmentContext Environment, string? SchemaName, string? DisplayName, string? McsAgentId, string? Description, IReadOnlyList<CopilotAgentSummary>? EnvironmentAgents, string? WorkspaceDirectory, string? ProjectDirectory, bool CapturedInEnvironment, Guid? WorkflowId = null)
    {
        public IReadOnlyList<string> Warnings { get; init; } = [];

        public bool IsWorkflow => WorkflowId is not null;

        public bool FillsItsProject => WorkspaceDirectory is { } workspace && ProjectDirectory is { } project && string.Equals(AgentConfigLocator.PhysicalPath(workspace), AgentConfigLocator.PhysicalPath(project), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CopyStaging
    {
        private string? _committedWorkspace;

        public CopyStaging(string projectDirectory, string agentId)
        {
            StateDirectory = CopyProjectState.AgentDirectory(projectDirectory, agentId);
            Root = Path.Combine(StateDirectory, StagingFolderName);
            SourceCacheDirectory = Path.Combine(StateDirectory, SourceCacheFolderName);
        }

        public string StateDirectory { get; }

        public string Root { get; }

        public string SourceCacheDirectory { get; }

        public void Commit(string stagedWorkspace, string workspaceDirectory)
        {
            if (Directory.Exists(workspaceDirectory))
            {
                Directory.Delete(workspaceDirectory);
            }

            FileSystemRetry.WhileHeld(() => Directory.Move(stagedWorkspace, workspaceDirectory));
            _committedWorkspace = workspaceDirectory;
            FileSystemRetry.WhileHeld(() => Directory.Delete(StateDirectory, recursive: true));
        }

        public void Discard()
        {
            CopyProjectState.DeleteQuietly(_committedWorkspace);
            CopyProjectState.DeleteQuietly(StateDirectory);
        }
    }
}
