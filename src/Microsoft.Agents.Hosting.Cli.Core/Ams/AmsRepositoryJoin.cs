using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

internal sealed record AmsJoinPlan(IReadOnlyList<AmsMcsAgentListItem> Restore, IReadOnlyList<ManagedAgentRecord> Unregistered, IReadOnlyList<(ManagedAgentRecord Agent, AmsMcsAgentListItem Known)> Known, IReadOnlyList<string> Fresh);

internal sealed record AmsRestore(ManagedAgentRecord Agent, AgentDeploymentRecord? Dev, AmsMcsDeploymentRecord? DevInAms);

internal sealed record AmsJoined(AmsJoinPlan Plan, IReadOnlyList<AmsRestore> Restored, IReadOnlyList<string> Warnings);

internal sealed record AmsJoinConnection(bool Connected, IReadOnlyList<string>? MergedFiles, IReadOnlyList<string>? ConflictedFiles, IReadOnlyList<string> Warnings);

/// <summary>Clones or links a project folder to a GRS repository, restoring and connecting its agents.</summary>
public sealed class AmsRepositoryJoin
{
    private readonly AgentConfigLocator _locator;
    private readonly IManagedAgentRegistry _agents;
    private readonly IAgentDeploymentRegistry _deployments;
    private readonly IWorkflowWorkspacePort _workflows;
    private readonly AmsProjectLink _link;
    private readonly IGitPort _git;
    private readonly AmsWorkspaceConnector _connector;
    private readonly WorkflowPullService _workflowPull;
    private readonly CopilotProjectIdentityReader _identities;

    /// <summary>Creates the join.</summary>
    /// <param name="locator">Finds the project folder's agent workspaces.</param>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="deployments">The deployment table.</param>
    /// <param name="workflows">Finds the standalone workflow folders.</param>
    /// <param name="link">Clones or links the repository.</param>
    /// <param name="git">Reads the repository's commits and status.</param>
    /// <param name="connector">Merges and connects the restored folders.</param>
    /// <param name="workflowPull">Pulls each restored standalone workflow from Dev.</param>
    /// <param name="identities">Reads the schema name of an agent's folder.</param>
    public AmsRepositoryJoin(AgentConfigLocator locator, IManagedAgentRegistry agents, IAgentDeploymentRegistry deployments, IWorkflowWorkspacePort workflows, AmsProjectLink link, IGitPort git, AmsWorkspaceConnector connector, WorkflowPullService workflowPull, CopilotProjectIdentityReader identities)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _workflowPull = workflowPull ?? throw new ArgumentNullException(nameof(workflowPull));
        _identities = identities ?? throw new ArgumentNullException(nameof(identities));
    }

    internal static bool ListsFolder(IEnumerable<AmsMcsAgentListItem> agents, string folder) => agents.Any(agent => string.Equals(agent.AgentFolder, folder, StringComparison.OrdinalIgnoreCase));

    internal static AmsJoinPlan Plan(string projectDirectory, IReadOnlyList<string> workspaces, IReadOnlyList<ManagedAgentRecord> agents, AmsMcsRepository? repository)
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
        return new AmsJoinPlan(restore, [.. unregistered.Where(agent => !known.Any(pair => pair.Agent == agent))], known, fresh);
    }

    internal static string[] RestoredFolders(string projectDirectory, IReadOnlyList<AmsMcsAgentListItem> restore) => restore.Count == 0 ? [] : [.. TopLevelFolders(projectDirectory).Where(folder => ListsFolder(restore, folder))];

    internal void RequireNoWorkspaceAtRoot(string projectDirectory)
    {
        if (_locator.WorkspaceLocations(projectDirectory).Contains(AgentConfigLocator.CurrentDirectoryLocation) || _workflows.IsStandaloneWorkspace(projectDirectory))
        {
            throw AmsAgentException.WorkspaceAtProjectRoot(projectDirectory);
        }
    }

    internal string[] WorkflowFolders(string projectDirectory) => [.. TopLevelFolders(projectDirectory).Where(folder => _workflows.IsStandaloneWorkspace(Path.Combine(projectDirectory, folder)))];

    internal void RequireRestorable(string projectDirectory, IReadOnlyList<ManagedAgentRecord> agents, AmsMcsAgentListItem holder)
    {
        if (agents.FirstOrDefault(agent => string.Equals(agent.AgentId, holder.AgentId, StringComparison.OrdinalIgnoreCase) || string.Equals(agent.DisplayName, holder.DisplayName, StringComparison.OrdinalIgnoreCase)) is { } registered)
        {
            throw ManagedAgentException.AlreadyRegistered(registered.DisplayName, registered.AgentId, _locator.ConfigFilePath(projectDirectory));
        }

        if (agents.FirstOrDefault(agent => string.Equals(agent.AgentLocation, holder.AgentFolder, StringComparison.OrdinalIgnoreCase)) is { } occupant)
        {
            throw ManagedAgentException.WorkspaceNotEmpty(Path.Combine(projectDirectory, occupant.AgentLocation), AmsProjectLink.Subject(occupant.AgentType, occupant.DisplayName));
        }
    }

    internal async Task<AmsJoined> JoinAsync(AmsRepositoryChoice choice, string projectDirectory, AmsMcsRepository? repository, IReadOnlyList<ManagedAgentRecord> agents, AmsJoinPlan plan, Func<string, IReadOnlyList<string>> workspaces, AmsFolderSnapshot snapshot, CancellationToken cancellationToken)
    {
        List<string> warnings = [];
        List<AmsRestore> restored = [];
        try
        {
            if (repository is not null && choice.Action is AmsRepositoryAction.Clone or AmsRepositoryAction.Attach)
            {
                warnings.AddRange(await _link.JoinAsync(choice, projectDirectory, repository, cancellationToken).ConfigureAwait(false));
                RequireNoWorkspaceAtRoot(projectDirectory);
                plan = Plan(projectDirectory, workspaces(projectDirectory), agents, repository);
                await _link.RequireNoSyncStateAsync(choice, projectDirectory, RestoredFolders(projectDirectory, plan.Restore), joined: true, cancellationToken).ConfigureAwait(false);
            }

            foreach (var agent in plan.Restore)
            {
                restored.Add(Restore(projectDirectory, agent, repository!));
            }

            return new AmsJoined(plan, restored, warnings);
        }
        catch
        {
            Undo(projectDirectory, [.. plan.Restore.Select(agent => agent.AgentId)], snapshot);
            throw;
        }
    }

    internal void Undo(string projectDirectory, IReadOnlyCollection<string> agentIds, AmsFolderSnapshot snapshot)
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

    internal async Task<AmsJoinConnection> ConnectAsync(string projectDirectory, IReadOnlyList<AmsRestore> restored, CancellationToken cancellationToken)
    {
        var fetch = new Lazy<Task>(() => _git.FetchBranchAsync(projectDirectory, AmsProjectLink.Origin, AmsProjectLink.Branch, cancellationToken));
        List<string> warnings = [];
        List<CopilotConnectMergeOutcome> outcomes = [];
        foreach (var restore in restored)
        {
            var isWorkflow = restore.Agent.AgentType == AgentTypes.McsWorkflow;
            if ((isWorkflow ? restore.DevInAms?.WorkflowId : restore.DevInAms?.BotId) is null)
            {
                continue;
            }

            if (isWorkflow)
            {
                await UpdateWorkflowAsync(projectDirectory, restore, warnings, cancellationToken).ConfigureAwait(false);
            }
            else if (await MergeAndConnectAsync(projectDirectory, restore.Agent, restore.DevInAms!, fetch, warnings, cancellationToken).ConfigureAwait(false) is { } outcome)
            {
                outcomes.Add(outcome);
            }
        }

        string[] merged = [.. outcomes.SelectMany(Changed)];
        string[] conflicted = [.. outcomes.SelectMany(outcome => outcome.ConflictedPaths)];
        return new AmsJoinConnection(outcomes.Count == restored.Count, merged.Length == 0 ? null : merged, conflicted.Length == 0 ? null : conflicted, warnings);
    }

    internal async Task<bool> IsConnectedAsync(string projectDirectory, ManagedAgentRecord agent, CancellationToken cancellationToken) => string.Equals(await _connector.ConnectedEnvironmentAsync(Path.Combine(projectDirectory, agent.AgentLocation), cancellationToken).ConfigureAwait(false), agent.EnvironmentId, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> TopLevelFolders(string projectDirectory) => Directory.Exists(projectDirectory) ? Directory.EnumerateDirectories(projectDirectory).Select(Path.GetFileName).Select(name => name!).Where(name => !string.Equals(name, AgentConfigLocator.ConfigDirectoryName, StringComparison.OrdinalIgnoreCase) && !name.StartsWith(".git", StringComparison.OrdinalIgnoreCase)) : [];

    private static IEnumerable<string> Changed(CopilotConnectMergeOutcome outcome) => [.. outcome.MergedPaths, .. outcome.AddedPaths, .. outcome.UpdatedPaths, .. outcome.DeletedPaths, .. outcome.ConflictedPaths, .. outcome.ReplacedPaths];

    private static string Files(IReadOnlyList<string> files, string singular, string plural) => files.Count == 0 ? string.Empty : $" {(files.Count == 1 ? $"1 file {singular}" : $"{files.Count} files {plural}")}: {AmsAgentException.FirstFive(files)}.";

    private static string NewFolders(string projectDirectory, IReadOnlyList<string> added) => added.Count == 0 ? string.Empty : $" and the new collection {(added.Count == 1 ? "folder" : "folders")} {AmsWorkspaceConnector.Folders(projectDirectory, added)}";

    private AmsRestore Restore(string projectDirectory, AmsMcsAgentListItem agent, AmsMcsRepository repository)
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

        List<AgentDeploymentRecord> deployments = [];
        foreach (var deployment in agent.Deployments)
        {
            deployments.Add(_deployments.Add(projectDirectory, new AgentDeploymentRecord
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
            }));
        }

        return new AmsRestore(stored, deployments.Find(deployment => deployment.DeploymentType == DeploymentStages.Dev), dev);
    }

    private async Task<CopilotConnectMergeOutcome?> MergeAndConnectAsync(string projectDirectory, ManagedAgentRecord agent, AmsMcsDeploymentRecord dev, Lazy<Task> fetch, List<string> warnings, CancellationToken cancellationToken)
    {
        var folder = TerminalText.Sanitize(agent.AgentLocation);
        try
        {
            if (dev.CloudBaseCommitSha is not { } cloudBase)
            {
                warnings.Add(AmsWorkspaceConnector.NotConnected($"AMS doesn't know which commit Dev holds for {folder}"));
                return null;
            }

            if ((await _git.ResolveCommitAsync(projectDirectory, cloudBase, cancellationToken).ConfigureAwait(false) ?? await ResolveFetchedAsync(projectDirectory, cloudBase, fetch, cancellationToken).ConfigureAwait(false)) is not { } baseCommit)
            {
                warnings.Add(AmsWorkspaceConnector.NotConnected($"Dev holds {folder} as of commit {McsAmsCommits.Short(cloudBase)}, which the repository lacks even after a fetch"));
                return null;
            }

            if (!await AmsCloudCacheShelf.IsAncestorAsync(_git, projectDirectory, baseCommit, "HEAD", cancellationToken).ConfigureAwait(false))
            {
                warnings.Add(AmsWorkspaceConnector.NotConnected($"Dev holds {folder} as of commit {McsAmsCommits.Short(baseCommit)}, which isn't HEAD or an ancestor of it"));
                return null;
            }

            var workspace = Path.Combine(projectDirectory, agent.AgentLocation);
            if (await _connector.ConnectAndMergeAsync(projectDirectory, workspace, dev.EnvironmentId, ManagedAgentResolver.McsSchemaNameOf(agent, null) ?? _identities.Require(workspace, agent.AgentId).SchemaName, baseCommit, cancellationToken).ConfigureAwait(false) is not { } outcome)
            {
                warnings.Add(AmsWorkspaceConnector.NotConnected(AmsWorkspaceConnector.Uncommitted(folder)));
                return null;
            }

            var name = TerminalText.Sanitize(agent.DisplayName);
            if (Changed(outcome).Any())
            {
                warnings.Add($"Copilot Studio holds edits to {name} that {AmsProjectLink.Branch} lacks. They are merged into {folder}{NewFolders(projectDirectory, outcome.AddedCollectionDirectories)} as uncommitted changes, which `git status` lists.{Files(outcome.ConflictedPaths, "has a conflict to resolve", "have conflicts to resolve")}{Files(outcome.ReplacedPaths, "is Copilot Studio's version, since a text merge can't settle it", "are Copilot Studio's version, since a text merge can't settle them")}");
            }

            if (outcome.Connected.SkippedCollectionDirectories.Count > 0)
            {
                warnings.Add(AmsWorkspaceConnector.CollectionsNotConnected(projectDirectory, outcome.Connected.SkippedCollectionDirectories, name));
            }

            return outcome;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            warnings.Add(AmsWorkspaceConnector.NotConnected($"Merging Copilot Studio's edits into {folder} failed ({failure.Message.TrimEnd('.')})"));
            return null;
        }
    }

    private async Task<string?> ResolveFetchedAsync(string projectDirectory, string commit, Lazy<Task> fetch, CancellationToken cancellationToken)
    {
        await fetch.Value.ConfigureAwait(false);
        return await _git.ResolveCommitAsync(projectDirectory, commit, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateWorkflowAsync(string projectDirectory, AmsRestore restore, List<string> warnings, CancellationToken cancellationToken)
    {
        var name = TerminalText.Sanitize(restore.Agent.DisplayName);
        var folder = TerminalText.Sanitize(restore.Agent.AgentLocation);
        try
        {
            if ((await _git.StatusAsync(projectDirectory, [restore.Agent.AgentLocation], cancellationToken).ConfigureAwait(false)).Count > 0)
            {
                warnings.Add($"Workflow '{name}' in folder {folder} has uncommitted changes, so msagent didn't take Copilot Studio's version; commit or stash them, then run `msagent agent pull --overwrite` there.");
                return;
            }

            var pulled = await _workflowPull.PullAsync(new ManagedAgentPullRequest { Project = projectDirectory, AgentId = restore.Agent.AgentId, DeploymentId = restore.Dev!.DeploymentId, Overwrite = true }, cancellationToken).ConfigureAwait(false);
            warnings.AddRange(pulled.Warnings);
            if (pulled.ChangeCount > 0)
            {
                warnings.Add($"Copilot Studio's version of workflow '{name}' replaced the repository's in folder {folder}, uncommitted: `git status` lists it, and `git restore {DeploymentCommandHints.Quoted(restore.Agent.AgentLocation)}` undoes it.");
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            warnings.Add($"Pulling workflow '{name}' from Dev failed ({failure.Message.TrimEnd('.')}), so folder {folder} holds the repository's version; `msagent agent pull --overwrite` there takes Copilot Studio's.");
        }
    }
}
