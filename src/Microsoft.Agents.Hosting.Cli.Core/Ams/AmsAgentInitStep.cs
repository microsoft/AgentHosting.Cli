using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>The AMS step of <c>msagent agent init</c>: clones or links the repository the folder joins, restores the agents of the repository that .config lacks, registers the agents .config holds that AMS doesn't, registers a new workspace through main's init, and commits and pushes their folders.</summary>
public sealed class AmsAgentInitStep : ICommandHandler<ManagedAgentInitRequest, ManagedAgentInitResult>
{
    private const string Command = "msagent agent init";

    private readonly IAgentHostingOperations _operations;
    private readonly IGitPort _git;
    private readonly AgentConfigLocator _locator;
    private readonly IManagedAgentRegistry _agents;
    private readonly IAgentDeploymentRegistry _deployments;
    private readonly IWorkflowWorkspacePort _workflows;
    private readonly AmsRepositoryResolver _repositories;
    private readonly AmsProjectLink _link;
    private readonly AmsAgentRegistrar _registrar;
    private readonly AmsRecordMirror _mirror;
    private readonly AmsCommitter _committer;
    private readonly AmsTargets _targets;
    private readonly IAmsMcsAgentReadTransport _reads;

    /// <summary>Creates the step.</summary>
    /// <param name="operations">Runs main's init.</param>
    /// <param name="git">Runs git.</param>
    /// <param name="locator">Finds the project folder and its agent workspaces.</param>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="deployments">The deployment table.</param>
    /// <param name="workflows">Finds the standalone workflow folders.</param>
    /// <param name="repositories">Chooses the repository.</param>
    /// <param name="link">Asks, and links the project folder.</param>
    /// <param name="registrar">Registers the agents AMS doesn't hold.</param>
    /// <param name="mirror">Creates the deployments AMS lacks for an agent it holds.</param>
    /// <param name="committer">Commits and pushes the agents' folders.</param>
    /// <param name="targets">Resolves the home environment's endpoints.</param>
    /// <param name="reads">Reads the repository with its agents and their deployments.</param>
    public AmsAgentInitStep(IAgentHostingOperations operations, IGitPort git, AgentConfigLocator locator, IManagedAgentRegistry agents, IAgentDeploymentRegistry deployments, IWorkflowWorkspacePort workflows, AmsRepositoryResolver repositories, AmsProjectLink link, AmsAgentRegistrar registrar, AmsRecordMirror mirror, AmsCommitter committer, AmsTargets targets, IAmsMcsAgentReadTransport reads)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _mirror = mirror ?? throw new ArgumentNullException(nameof(mirror));
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
    }

    /// <inheritdoc />
    public async Task<ManagedAgentInitResult> HandleAsync(ManagedAgentInitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var isWorkflow = AgentTypes.IsMcsWorkflow(request.AgentType);
        var projectDirectory = isWorkflow ? _locator.EnclosingProjectDirectory(Path.GetFullPath(Given(request.McsWorkflowSource) ?? Given(request.McsAgentSource) ?? Directory.GetCurrentDirectory())) : _locator.ResolveProjectDirectory(request.McsAgentSource);
        RequireNoWorkspaceAtRoot(projectDirectory);

        _git.RequireGit();
        var agents = _agents.List(projectDirectory);
        var workflowFolders = WorkflowFolders(projectDirectory);
        var choice = _repositories.Choose(projectDirectory, await _git.FindWorkingTreeAsync(projectDirectory, cancellationToken).ConfigureAwait(false), request.RepositoryId, agents, workflowFolders);
        var home = await HomeAsync(agents, cancellationToken).ConfigureAwait(false);
        var repository = choice.RepositoryId is { } repositoryId ? await _reads.ReadRepositoryAsync(_targets.DataPlane(home), repositoryId, null, includeDeployments: true, cancellationToken).ConfigureAwait(false) : null;
        var plan = Plan(projectDirectory, isWorkflow ? workflowFolders : _locator.WorkspaceLocations(projectDirectory), agents, repository);
        RequireName(request.Name, plan.Fresh, isWorkflow, repository, repositoryHoldsFolder: choice.Action == AmsRepositoryAction.None);
        var clones = repository is not null && choice.Action == AmsRepositoryAction.Clone;
        if (!clones && plan.Restore.Count + plan.Unregistered.Count + plan.Known.Count + plan.Fresh.Count == 0)
        {
            return await _operations.InitManagedAgentAsync(request, cancellationToken).ConfigureAwait(false);
        }

        await _link.RequireNoSyncStateAsync(choice, projectDirectory, [.. RestoredFolders(projectDirectory, plan.Restore), .. plan.Unregistered.Select(agent => agent.AgentLocation), .. plan.Known.Select(pair => pair.Agent.AgentLocation), .. plan.Fresh], joined: false, cancellationToken).ConfigureAwait(false);
        var subject = clones ? null : Subject([.. plan.Restore.Select(agent => (agent.AgentType, agent.DisplayName)), .. plan.Unregistered.Select(agent => (agent.AgentType, agent.DisplayName)), .. plan.Known.Select(pair => (pair.Agent.AgentType, pair.Agent.DisplayName)), .. plan.Fresh.Select(_ => (isWorkflow ? AgentTypes.McsWorkflow : AgentTypes.McsAgent, request.Name!.Trim()))]);
        if (!await _link.ConfirmAsync(choice, projectDirectory, subject, repository, home.DisplayName, request.NonInteractive, cancellationToken).ConfigureAwait(false))
        {
            return Cancelled(request, projectDirectory, home);
        }

        var created = OutermostMissingFolder(projectDirectory);
        var snapshot = AmsProjectLink.Snapshot(created ?? projectDirectory, projectDirectoryCreated: created is not null);
        List<string> warnings = [];
        List<ManagedAgentRecord> restored = [];
        List<ManagedAgentRecord> registered = [];
        ManagedAgentInitResult? main = null;
        var registration = new Registration(choice.RepositoryId, null);
        try
        {
            if (repository is not null && choice.Action is AmsRepositoryAction.Clone or AmsRepositoryAction.Attach)
            {
                warnings.AddRange(await _link.JoinAsync(choice, projectDirectory, repository, cancellationToken).ConfigureAwait(false));
                RequireNoWorkspaceAtRoot(projectDirectory);
                plan = Plan(projectDirectory, isWorkflow ? WorkflowFolders(projectDirectory) : _locator.WorkspaceLocations(projectDirectory), agents, repository);
                await _link.RequireNoSyncStateAsync(choice, projectDirectory, RestoredFolders(projectDirectory, plan.Restore), joined: true, cancellationToken).ConfigureAwait(false);
            }

            foreach (var agent in plan.Restore)
            {
                restored.Add(Restore(projectDirectory, agent, repository!));
            }

            if (plan.Fresh.Count > 0)
            {
                RequireName(request.Name, plan.Fresh, isWorkflow, repository, repositoryHoldsFolder: true);
                main = await _operations.InitManagedAgentAsync(request with { McsAgentSource = isWorkflow ? null : projectDirectory, McsWorkflowSource = isWorkflow ? Path.Combine(projectDirectory, plan.Fresh[0]) : null, ProjectDirectory = isWorkflow ? projectDirectory : null }, cancellationToken).ConfigureAwait(false);
            }
            else if (restored.Count + plan.Unregistered.Count + plan.Known.Count == 0)
            {
                main = await _operations.InitManagedAgentAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var fresh = main is null ? null : _agents.List(projectDirectory).First(agent => agent.AgentId == main.AgentId);
            foreach (var agent in fresh is null ? plan.Unregistered : [fresh, .. plan.Unregistered])
            {
                registration = await RegisterAsync(projectDirectory, agent, registration, choice, registered, warnings, cancellationToken).ConfigureAwait(false);
            }
        }
        catch when (registered.Count == 0)
        {
            Undo(projectDirectory, [.. plan.Restore.Select(agent => agent.AgentId), .. (main is null ? [] : new[] { main.AgentId })], snapshot);
            throw;
        }

        foreach (var (agent, known) in plan.Known)
        {
            registered.Add(await RecordKnownAsync(projectDirectory, agent, known, repository!, cancellationToken).ConfigureAwait(false));
        }

        AmsProjectLink.EnsureGitIgnore(projectDirectory);
        List<ManagedAgentRecord> touched = [.. restored, .. registered];
        var added = registered.Count > 0 ? registered : touched;
        var committed = await _committer.CommitAndPushAsync(projectDirectory, [.. touched.Select(agent => agent.AgentLocation)], added.Count == 1 ? $"msagent: add {added[0].AgentType} {added[0].DisplayName}" : $"msagent: add {added.Count} agents", registration.Link, cancellationToken).ConfigureAwait(false);
        warnings.AddRange(committed.Warnings);
        if (committed.Branch is { } branch && branch != AmsProjectLink.Branch)
        {
            warnings.Add($"Remote deploys take {AmsProjectLink.Branch} by default; this folder is on {branch}.");
        }

        var first = main is null ? touched[0] : registered[0];
        var ams = new AmsAgentRegistration
        {
            Registered = true,
            RepositoryId = first.SourceControlMetadata!.RepositoryId,
            RepositoryName = repository?.Name,
            RepositoryCreated = registration.Created,
            GitUrl = registration.GitUrl ?? repository?.GitUrl ?? string.Empty,
            Folder = first.SourceControlMetadata.Directory,
            CommitSha = committed.CommitSha,
            Pushed = committed.Pushed,
            Branch = committed.Branch ?? AmsProjectLink.Branch,
            Restored = restored.Count == 0 ? null : [.. restored.Select(agent => new AmsRestoredAgent(agent.AgentId, agent.DisplayName, agent.AgentLocation))],
            Warnings = warnings.Count == 0 ? null : warnings,
        };

        return main is null ? Result(projectDirectory, first, ams) : main with { Ams = ams };
    }

    private static string? Given(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static IEnumerable<string> TopLevelFolders(string projectDirectory) => Directory.Exists(projectDirectory) ? Directory.EnumerateDirectories(projectDirectory).Select(Path.GetFileName).Select(name => name!).Where(name => !string.Equals(name, AgentConfigLocator.ConfigDirectoryName, StringComparison.OrdinalIgnoreCase) && !name.StartsWith(".git", StringComparison.OrdinalIgnoreCase)) : [];

    private static string? OutermostMissingFolder(string projectDirectory)
    {
        string? missing = null;
        for (var folder = projectDirectory; folder is not null && !Directory.Exists(folder); folder = Path.GetDirectoryName(folder))
        {
            missing = folder;
        }

        return missing;
    }

    private void RequireNoWorkspaceAtRoot(string projectDirectory)
    {
        if (_locator.WorkspaceLocations(projectDirectory).Contains(AgentConfigLocator.CurrentDirectoryLocation) || _workflows.IsStandaloneWorkspace(projectDirectory))
        {
            throw AmsAgentException.WorkspaceAtProjectRoot(projectDirectory);
        }
    }

    private string[] WorkflowFolders(string projectDirectory) => [.. TopLevelFolders(projectDirectory).Where(folder => _workflows.IsStandaloneWorkspace(Path.Combine(projectDirectory, folder)))];

    private static string Subject(IReadOnlyList<(string AgentType, string DisplayName)> agents) => agents.Count == 1 ? AmsProjectLink.Subject(agents[0].AgentType, agents[0].DisplayName) : $"{agents.Count} agents";

    private static void RequireName(string? name, IReadOnlyList<string> fresh, bool isWorkflow, AmsMcsRepository? repository, bool repositoryHoldsFolder)
    {
        if (fresh.Count == 0 || !string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var kind = AmsProjectLink.Kind(isWorkflow);
        var folder = TerminalText.Sanitize(fresh[0]);
        var (reason, remedy) = repository is null || ListsFolder(repository.Agents, fresh[0]) ? ($"The project's .config doesn't list the {kind} in folder {folder}", "name it") : ($"AMS doesn't list the {kind} in folder {folder} for repository {TerminalText.Sanitize(repository.Name ?? repository.RepositoryId)}", $"do that, or remove the folder{(repositoryHoldsFolder ? " from the repository" : string.Empty)} if its {kind} was deleted");
        throw new UsageException($"{reason}, so `msagent agent init` would register it as a new {kind}. Pass --agent-name to {remedy}.");
    }

    private static bool ListsFolder(IEnumerable<AmsMcsAgentListItem> agents, string folder) => agents.Any(agent => string.Equals(agent.AgentFolder, folder, StringComparison.OrdinalIgnoreCase));

    private static InitPlan Plan(string projectDirectory, IReadOnlyList<string> workspaces, IReadOnlyList<ManagedAgentRecord> agents, AmsMcsRepository? repository)
    {
        var locations = agents.Select(agent => agent.AgentLocation).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = agents.Select(agent => agent.AgentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = TopLevelFolders(projectDirectory).Where(folder => !locations.Contains(folder)).ToArray();
        var restore = (repository?.Agents ?? []).Where(agent => !ids.Contains(agent.AgentId) && agent.AgentFolder is not null && folders.Contains(agent.AgentFolder, StringComparer.OrdinalIgnoreCase)).ToArray();
        var fresh = workspaces.Where(location => location != AgentConfigLocator.CurrentDirectoryLocation && !locations.Contains(location) && !ListsFolder(restore, location)).ToArray();
        if (fresh.Length > 1)
        {
            throw ManagedAgentException.WorkspaceAmbiguous(fresh);
        }

        var unregistered = agents.Where(agent => agent.SourceControlMetadata is null && (agent.AgentType == AgentTypes.McsAgent || agent.AgentType == AgentTypes.McsWorkflow)).ToArray();
        var known = unregistered.Select(agent => (Agent: agent, Known: repository?.Agents.FirstOrDefault(candidate => string.Equals(candidate.AgentId, agent.AgentId, StringComparison.OrdinalIgnoreCase)))).Where(pair => pair.Known is not null).Select(pair => (pair.Agent, pair.Known!)).ToArray();
        return new InitPlan(restore, [.. unregistered.Where(agent => !known.Any(pair => pair.Agent == agent))], known, fresh);
    }

    private static string[] RestoredFolders(string projectDirectory, IReadOnlyList<AmsMcsAgentListItem> restore) => restore.Count == 0 ? [] : [.. TopLevelFolders(projectDirectory).Where(folder => ListsFolder(restore, folder))];

    private async Task<PowerPlatformEnvironment> HomeAsync(IReadOnlyList<ManagedAgentRecord> agents, CancellationToken cancellationToken) =>
        (agents.FirstOrDefault(agent => agent.SourceControlMetadata is not null) ?? agents.FirstOrDefault(agent => agent.AgentType == AgentTypes.McsAgent || agent.AgentType == AgentTypes.McsWorkflow)) is { } agent
            ? await _targets.HomeAsync(agent.EnvironmentId, cancellationToken).ConfigureAwait(false)
            : await _targets.ActiveAsync(cancellationToken).ConfigureAwait(false);

    private ManagedAgentRecord Restore(string projectDirectory, AmsMcsAgentListItem agent, AmsMcsRepository repository)
    {
        var folder = TopLevelFolders(projectDirectory).First(candidate => string.Equals(candidate, agent.AgentFolder, StringComparison.OrdinalIgnoreCase));
        var dev = agent.Deployments.FirstOrDefault(deployment => deployment.DeploymentType == DeploymentStages.ServiceName(DeploymentStages.Dev));
        var isWorkflow = agent.AgentType == AgentTypes.McsWorkflow;
        var stored = _agents.Add(projectDirectory, new ManagedAgentRecord
        {
            AgentId = AmsIds.Local(agent.AgentId),
            DisplayName = agent.DisplayName,
            Description = agent.Description,
            AgentType = agent.AgentType,
            AgentLocation = folder,
            McsAgentId = isWorkflow ? null : AmsIds.Local(dev?.BotId),
            McsSchemaName = isWorkflow ? null : agent.SchemaName,
            McsWorkflowId = isWorkflow ? _workflows.RequireSingle(Path.Combine(projectDirectory, folder)).WorkflowId.ToString() : null,
            SourceControlMetadata = new AgentSourceControlMetadata(AmsIds.Local(repository.RepositoryId), agent.Branch ?? AmsProjectLink.Branch, agent.AgentFolder!),
            TenantId = AmsIds.Local(agent.TenantId),
            EnvironmentId = AmsIds.Local(agent.EnvironmentId),
            Origin = ManagedAgentOrigins.AgentHostCli,
            EntityTag = string.Empty,
            AgentState = EntityStates.Saved,
        });

        foreach (var deployment in agent.Deployments)
        {
            _deployments.Add(projectDirectory, new AgentDeploymentRecord
            {
                DeploymentId = AmsIds.Local(deployment.DeploymentId),
                AgentId = stored.AgentId,
                AgentType = stored.AgentType,
                DeploymentType = DeploymentStages.TryParse(deployment.DeploymentType, out var stage) ? stage : throw new FormatException($"AMS names deployment {deployment.DeploymentId} with an unknown stage."),
                DeploymentName = deployment.DeploymentName,
                TenantId = stored.TenantId,
                HomeEnvironmentId = AmsIds.Local(deployment.HomeEnvironmentId),
                EnvironmentId = AmsIds.Local(deployment.EnvironmentId),
                LastDeployedTime = deployment.LastDeployedTime,
                IsQuarantined = false,
                IsEnabled = true,
                EntityTag = string.Empty,
            });
        }

        return stored;
    }

    private async Task<Registration> RegisterAsync(string projectDirectory, ManagedAgentRecord agent, Registration registration, AmsRepositoryChoice choice, List<ManagedAgentRecord> registered, List<string> warnings, CancellationToken cancellationToken)
    {
        var home = await _targets.HomeAsync(agent.EnvironmentId, cancellationToken).ConfigureAwait(false);
        var result = await _registrar.RegisterAsync(projectDirectory, agent, [.. _deployments.List(projectDirectory).Where(deployment => deployment.AgentId == agent.AgentId)], registration.RepositoryId, null, null, home, Command, cancellationToken).ConfigureAwait(false);
        registered.Add(result.Agent);
        var gitUrl = result.Record.SourceControl?.GitUrl ?? registration.GitUrl;
        var link = registration.RepositoryId is null && gitUrl is not null ? await _link.LinkNewAsync(choice, projectDirectory, result.Agent.SourceControlMetadata!.RepositoryId, gitUrl, cancellationToken).ConfigureAwait(false) : null;
        warnings.AddRange(link?.Warnings ?? []);
        return new Registration(result.Agent.SourceControlMetadata!.RepositoryId, gitUrl, registration.Created || (result.Record.SourceControl?.RepositoryCreated ?? false), link ?? registration.Link);
    }

    private async Task<ManagedAgentRecord> RecordKnownAsync(string projectDirectory, ManagedAgentRecord agent, AmsMcsAgentListItem known, AmsMcsRepository repository, CancellationToken cancellationToken)
    {
        var home = await _targets.HomeAsync(agent.EnvironmentId, cancellationToken).ConfigureAwait(false);
        foreach (var deployment in _deployments.List(projectDirectory).Where(deployment => deployment.AgentId == agent.AgentId && !known.Deployments.Any(existing => string.Equals(existing.DeploymentId, deployment.DeploymentId, StringComparison.OrdinalIgnoreCase))))
        {
            await _mirror.CreateDeploymentAsync(home, agent, deployment, cancellationToken).ConfigureAwait(false);
        }

        return _registrar.RecordFolder(projectDirectory, agent, new AgentSourceControlMetadata(repository.RepositoryId, known.Branch ?? AmsProjectLink.Branch, known.AgentFolder ?? agent.AgentLocation));
    }

    private void Undo(string projectDirectory, IReadOnlyCollection<string> agentIds, AmsFolderSnapshot snapshot)
    {
        try
        {
            foreach (var agent in _agents.List(projectDirectory).Where(agent => agentIds.Contains(agent.AgentId, StringComparer.OrdinalIgnoreCase)).ToArray())
            {
                _agents.RemoveWithDeployments(projectDirectory, agent.AgentId, agent.EntityTag);
            }
        }
        catch (AgentHostingException)
        {
        }

        AmsProjectLink.Undo(snapshot);
    }

    private ManagedAgentInitResult Cancelled(ManagedAgentInitRequest request, string projectDirectory, PowerPlatformEnvironment home) => new()
    {
        Status = "cancelled",
        AgentId = string.Empty,
        DisplayName = request.Name?.Trim() ?? string.Empty,
        AgentType = AgentTypes.IsMcsWorkflow(request.AgentType) ? AgentTypes.McsWorkflow : AgentTypes.McsAgent,
        TenantId = home.TenantId,
        EnvironmentId = home.EnvironmentId,
        ProjectDirectory = projectDirectory,
        ConfigPath = _locator.ConfigFilePath(projectDirectory),
    };

    private ManagedAgentInitResult Result(string projectDirectory, ManagedAgentRecord agent, AmsAgentRegistration ams) => new()
    {
        AgentId = agent.AgentId,
        DisplayName = agent.DisplayName,
        AgentType = agent.AgentType,
        TenantId = agent.TenantId,
        EnvironmentId = agent.EnvironmentId,
        Connected = agent.McsAgentId is not null,
        ProjectDirectory = projectDirectory,
        ConfigPath = _locator.ConfigFilePath(projectDirectory),
        McsWorkflowId = agent.McsWorkflowId,
        WorkflowFolder = agent.AgentType == AgentTypes.McsWorkflow ? Path.Combine(projectDirectory, agent.AgentLocation) : null,
        WorkflowState = agent.AgentType == AgentTypes.McsWorkflow ? TextGrid.Dash : null,
        Ams = ams,
    };

    private sealed record InitPlan(IReadOnlyList<AmsMcsAgentListItem> Restore, IReadOnlyList<ManagedAgentRecord> Unregistered, IReadOnlyList<(ManagedAgentRecord Agent, AmsMcsAgentListItem Known)> Known, IReadOnlyList<string> Fresh);

    private sealed record Registration(string? RepositoryId, string? GitUrl, bool Created = false, AmsNewRepositoryLink? Link = null);
}
