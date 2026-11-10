using System.Runtime.CompilerServices;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>The AMS step of <c>msagent agent create</c>: after main's checks it chooses the repository, asks, and clones or attaches it; after main's writes it registers the agent, links a new repository, commits and pushes the agent's folder, and keeps a copy of each cloned folder's cloud cache for that commit.</summary>
public sealed class AmsAgentCreation : IAgentCreationHook
{
    private const string Command = "msagent agent create";

    private const string OnceThatFinishes = "Run the same command again once that finishes.";

    private readonly IGitPort _git;
    private readonly AmsRepositoryResolver _repositories;
    private readonly AmsProjectLink _link;
    private readonly AmsAgentRegistrar _registrar;
    private readonly AmsCommitter _committer;
    private readonly AmsDeployRecords _deployRecords;
    private readonly AmsTargets _targets;
    private readonly IAmsMcsAgentReadTransport _reads;
    private readonly IManagedAgentRegistry _agents;
    private readonly IClock _clock;
    private readonly AmsCloudCacheShelf _shelf;
    private readonly ICopilotWorkspacePort _workspaces;
    private readonly AmsRepositoryJoin _join;
    private readonly ConditionalWeakTable<AgentCreationPlan, PowerPlatformEnvironment> _homes = new();

    /// <summary>Creates the step.</summary>
    /// <param name="git">Runs git.</param>
    /// <param name="repositories">Chooses the repository.</param>
    /// <param name="link">Asks, and links the project folder.</param>
    /// <param name="registrar">Registers the agent.</param>
    /// <param name="committer">Commits and pushes the agent's folder.</param>
    /// <param name="deployRecords">Tells AMS which commit a cloned agent's Copilot Studio agent holds.</param>
    /// <param name="targets">Resolves the home environment's endpoints.</param>
    /// <param name="reads">Reads the holder and the existing repository.</param>
    /// <param name="agents">The managed-agent table, which a failed register is undone from.</param>
    /// <param name="clock">Dates the deploy record of a clone.</param>
    /// <param name="shelf">Keeps a copy of each cloned folder's cloud cache for the commit that adds the folder.</param>
    /// <param name="workspaces">Reads the agent each cloned folder is connected to.</param>
    /// <param name="join">Joins the agent that holds a cloned agent or flow.</param>
    public AmsAgentCreation(IGitPort git, AmsRepositoryResolver repositories, AmsProjectLink link, AmsAgentRegistrar registrar, AmsCommitter committer, AmsDeployRecords deployRecords, AmsTargets targets, IAmsMcsAgentReadTransport reads, IManagedAgentRegistry agents, IClock clock, AmsCloudCacheShelf shelf, ICopilotWorkspacePort workspaces, AmsRepositoryJoin join)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
        _deployRecords = deployRecords ?? throw new ArgumentNullException(nameof(deployRecords));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _shelf = shelf ?? throw new ArgumentNullException(nameof(shelf));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _join = join ?? throw new ArgumentNullException(nameof(join));
    }

    /// <inheritdoc />
    public async Task<AgentCreationJoin?> JoinAsync(AgentCreationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.ClaimedBotId is null && plan.ClaimedWorkflowId is null)
        {
            return null;
        }

        var home = await HomeAsync(plan, cancellationToken).ConfigureAwait(false);
        if (await _reads.FindHoldersAsync(_targets.DataPlane(home), plan.ClaimedBotId, plan.ClaimedWorkflowId, cancellationToken).ConfigureAwait(false) is not [var holder, ..])
        {
            return null;
        }

        var held = Joinable(plan, holder);
        var isWorkflow = plan.AgentType == AgentTypes.McsWorkflow;
        var folder = TerminalText.Sanitize(held.AgentFolder);
        var subject = $"Copilot Studio {AmsProjectLink.Kind(isWorkflow)} '{TerminalText.Sanitize(plan.SourceName)}' is already in AMS as {AmsProjectLink.Subject(held.AgentType, TerminalText.Sanitize(held.DisplayName))}, in repository {AmsIds.Local(held.RepositoryId)} (folder {folder})";
        _git.RequireGit();
        var agents = _agents.List(plan.ProjectDirectory);
        var tree = await _git.FindWorkingTreeAsync(plan.ProjectDirectory, cancellationToken).ConfigureAwait(false);
        if (OtherRepository(plan, held.RepositoryId!, agents, tree) is { } other)
        {
            throw AmsAgentException.CannotJoinHolder(subject, other, pullFirst: false);
        }

        _join.RequireRestorable(plan.ProjectDirectory, agents, held);
        var choice = _repositories.Choose(plan.ProjectDirectory, tree, held.RepositoryId, agents, []);
        var repository = await _reads.ReadRepositoryAsync(_targets.DataPlane(home), held.RepositoryId!, held.AgentFolder, includeDeployments: true, cancellationToken).ConfigureAwait(false);
        if (!repository.AgentFolderExists)
        {
            throw NotOnMain(holder);
        }

        var restore = AmsRepositoryJoin.Plan(plan.ProjectDirectory, [], agents, repository);
        if (choice.Action == AmsRepositoryAction.None && !restore.Restore.Any(agent => Same(agent.AgentId, held.AgentId)))
        {
            throw AmsAgentException.CannotJoinHolder(subject, $"this clone of it lacks folder {folder}", pullFirst: true);
        }

        await _link.RequireNoSyncStateAsync(choice, plan.ProjectDirectory, AmsRepositoryJoin.RestoredFolders(plan.ProjectDirectory, restore.Restore), joined: false, cancellationToken).ConfigureAwait(false);
        if (!await _link.ConfirmJoinAsync(choice, plan.ProjectDirectory, subject, TerminalText.Sanitize(held.DisplayName), isWorkflow, plan.NonInteractive, cancellationToken).ConfigureAwait(false))
        {
            AmsProjectLink.Undo(Snapshot(plan));
            return AgentCreationJoin.Declined;
        }

        var snapshot = Snapshot(plan);
        var joined = await _join.JoinAsync(choice, plan.ProjectDirectory, repository, agents, restore, _ => [], snapshot, cancellationToken).ConfigureAwait(false);
        if (joined.Restored.FirstOrDefault(candidate => Same(candidate.Agent.AgentId, held.AgentId)) is not { } restored)
        {
            _join.Undo(plan.ProjectDirectory, [.. joined.Plan.Restore.Select(agent => agent.AgentId)], snapshot);
            throw NotOnMain(holder);
        }

        var connection = await _join.ConnectAsync(plan.ProjectDirectory, [restored], cancellationToken).ConfigureAwait(false);
        List<string> warnings = [.. joined.Warnings, .. connection.Warnings];
        if (plan.RequestedName is { } requested && !Same(requested, held.DisplayName))
        {
            warnings.Insert(0, $"--agent-name '{TerminalText.Sanitize(requested)}' is ignored: Copilot Studio {AmsProjectLink.Kind(isWorkflow)} '{TerminalText.Sanitize(plan.SourceName)}' is already in AMS as '{TerminalText.Sanitize(held.DisplayName)}', in folder {folder}.");
        }

        return new AgentCreationJoin(restored.Agent, restored.Dev, new AmsAgentRegistration
        {
            Joined = true,
            RepositoryId = restored.Agent.SourceControlMetadata!.RepositoryId,
            RepositoryName = repository.Name,
            GitUrl = repository.GitUrl,
            Folder = restored.Agent.SourceControlMetadata.Directory,
            Restored = [.. joined.Restored.Select(agent => new AmsRestoredAgent(agent.Agent.AgentId, agent.Agent.DisplayName, agent.Agent.AgentLocation))],
            MergedFiles = connection.MergedFiles,
            ConflictedFiles = connection.ConflictedFiles,
            Warnings = warnings.Count == 0 ? null : warnings,
        }, isWorkflow ? null : connection.Connected);
    }

    /// <inheritdoc />
    public async Task<AgentCreationTicket?> PrepareAsync(AgentCreationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _git.RequireGit();
        var tree = await _git.FindWorkingTreeAsync(plan.ProjectDirectory, cancellationToken).ConfigureAwait(false);
        var choice = _repositories.Choose(plan.ProjectDirectory, tree, plan.RepositoryId, _agents.List(plan.ProjectDirectory), []);
        await _link.RequireNoSyncStateAsync(choice, plan.ProjectDirectory, [], joined: false, cancellationToken).ConfigureAwait(false);
        var home = await HomeAsync(plan, cancellationToken).ConfigureAwait(false);
        var repository = choice.RepositoryId is { } repositoryId ? await _reads.ReadRepositoryAsync(_targets.DataPlane(home), repositoryId, plan.AgentLocation, includeDeployments: false, cancellationToken).ConfigureAwait(false) : null;
        if (repository is { AgentFolderExists: true })
        {
            throw AmsAgentException.FolderExistsInRepository(plan.AgentLocation, repository.Name ?? repository.RepositoryId);
        }

        if (!await _link.ConfirmAsync(choice, plan.ProjectDirectory, AmsProjectLink.Subject(plan.AgentType, plan.DisplayName), repository, home.DisplayName, plan.NonInteractive, cancellationToken).ConfigureAwait(false))
        {
            AmsProjectLink.Undo(Snapshot(plan));
            return null;
        }

        var snapshot = Snapshot(plan);
        try
        {
            var warnings = repository is null ? [] : await _link.JoinAsync(choice, plan.ProjectDirectory, repository, cancellationToken).ConfigureAwait(false);
            return new AgentCreationTicket(plan, new Prepared(choice, home, repository, snapshot, warnings));
        }
        catch
        {
            AmsProjectLink.Undo(snapshot);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<AmsAgentRegistration?> CompleteAsync(AgentCreationTicket ticket, ManagedAgentRecord stored, AgentDeploymentRecord? homeDeployment, IReadOnlyList<string> clonedFolders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(clonedFolders);

        var prepared = (Prepared)ticket.State!;
        var plan = ticket.Plan;
        AmsRegisteredAgent registered;
        try
        {
            registered = await _registrar.RegisterAsync(plan.ProjectDirectory, stored, homeDeployment is null ? [] : [homeDeployment], prepared.Choice.RepositoryId, plan.ClaimedBotId, plan.ClaimedWorkflowId, prepared.Home, Command, cancellationToken).ConfigureAwait(false);
        }
        catch (AmsAgentException claimed) when (claimed.ErrorCode == ErrorCodes.AmsAgent.BotClaimed)
        {
            Undo(plan.ProjectDirectory, stored, prepared.Snapshot);
            throw AmsMcsOperationErrorMap.Rejoinable(claimed);
        }
        catch
        {
            Undo(plan.ProjectDirectory, stored, prepared.Snapshot);
            throw;
        }

        var gitUrl = registered.Record.SourceControl?.GitUrl ?? prepared.Repository?.GitUrl ?? throw new FormatException("The register answer names no git url.");
        List<string> warnings = [.. prepared.Warnings];
        var link = prepared.Choice.RepositoryId is null ? await _link.LinkNewAsync(prepared.Choice, plan.ProjectDirectory, registered.Agent.SourceControlMetadata!.RepositoryId, gitUrl, cancellationToken).ConfigureAwait(false) : null;
        warnings.AddRange(link?.Warnings ?? []);
        AmsProjectLink.EnsureGitIgnore(plan.ProjectDirectory);
        var committed = await _committer.CommitAndPushAsync(plan.ProjectDirectory, [stored.AgentLocation], $"msagent: add {stored.AgentType} {stored.DisplayName}", link, cancellationToken).ConfigureAwait(false);
        warnings.AddRange(committed.Warnings);
        foreach (var folder in committed.CommitSha is null ? [] : clonedFolders)
        {
            if (await (plan.AgentType == AgentTypes.McsWorkflow ? _shelf.SaveAsync(folder, null, cancellationToken) : _shelf.SaveConnectedAsync(_workspaces, folder, cancellationToken)).ConfigureAwait(false) is { } unsaved)
            {
                warnings.Add(unsaved);
            }
        }

        if (committed.Pushed && homeDeployment is not null && (plan.ClaimedBotId ?? plan.ClaimedWorkflowId) is not null && await _deployRecords.RecordAsync(plan.ProjectDirectory, registered.Agent, homeDeployment.DeploymentId, plan.ClaimedBotId, plan.ClaimedWorkflowId, published: false, _clock.UtcNow, Command, cancellationToken).ConfigureAwait(false) is { } recordWarning)
        {
            warnings.Add(recordWarning);
        }

        if (committed.Branch is { } branch && branch != AmsProjectLink.Branch)
        {
            warnings.Add($"Remote deploys take {AmsProjectLink.Branch} by default; this folder is on {branch}.");
        }

        return new AmsAgentRegistration
        {
            Registered = true,
            RepositoryId = registered.Agent.SourceControlMetadata!.RepositoryId,
            RepositoryName = prepared.Repository?.Name,
            RepositoryCreated = registered.Record.SourceControl?.RepositoryCreated ?? false,
            GitUrl = gitUrl,
            Folder = registered.Agent.SourceControlMetadata.Directory,
            CommitSha = committed.CommitSha,
            Pushed = committed.Pushed,
            Branch = committed.Branch ?? AmsProjectLink.Branch,
            Warnings = warnings.Count == 0 ? null : warnings,
        };
    }

    private static AmsFolderSnapshot Snapshot(AgentCreationPlan plan) => AmsProjectLink.Snapshot(plan.ProjectDirectory, plan.ProjectDirectoryCreated);

    private async Task<PowerPlatformEnvironment> HomeAsync(AgentCreationPlan plan, CancellationToken cancellationToken)
    {
        if (!_homes.TryGetValue(plan, out var home))
        {
            home = await _targets.HomeAsync(plan.HomeEnvironmentId, cancellationToken).ConfigureAwait(false);
            _homes.AddOrUpdate(plan, home);
        }

        return home;
    }

    private static bool Same(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static AmsMcsAgentListItem Joinable(AgentCreationPlan plan, AmsMcsHolder holder) => holder switch
    {
        { State: AmsMcsHolderState.Active, DeploymentId: not null, Agent: { RepositoryId: not null, AgentFolder: not null } agent } when Same(holder.HomeEnvironmentId, plan.HomeEnvironmentId) && Same(agent.HomeDeploymentId, holder.DeploymentId) => agent,
        { State: AmsMcsHolderState.Pending } => throw AmsMcsOperationErrorMap.BotClaimed($"{Named(holder)}. It is still being registered.", OnceThatFinishes),
        { State: AmsMcsHolderState.Releasing } => throw AmsMcsOperationErrorMap.BotClaimed($"{Named(holder)}. It is being deleted.", OnceThatFinishes),
        _ => throw AmsMcsOperationErrorMap.BotClaimed($"{Named(holder)}."),
    };

    private static string Named(AmsMcsHolder holder) => holder.Agent is { } agent
        ? $"{AmsProjectLink.Subject(agent.AgentType, TerminalText.Sanitize(agent.DisplayName))} ({AmsIds.Local(agent.AgentId)}){(agent.RepositoryId is null ? string.Empty : $", in repository {AmsIds.Local(agent.RepositoryId)}")}{(agent.AgentFolder is null ? string.Empty : $" (folder {TerminalText.Sanitize(agent.AgentFolder)})")}"
        : $"agent {AmsIds.Local(holder.AgentId)}, homed in environment {AmsIds.Local(holder.HomeEnvironmentId)}";

    private static AmsAgentException NotOnMain(AmsMcsHolder holder) => AmsMcsOperationErrorMap.BotClaimed($"{Named(holder)}. Its folder isn't on {AmsProjectLink.Branch} yet, so its owner has to push it first.");

    private string? OtherRepository(AgentCreationPlan plan, string repositoryId, IReadOnlyList<ManagedAgentRecord> agents, GitWorkingTree? tree)
    {
        if (plan.RepositoryId is { } given && GrsRemote.RequireRepositoryId(given) is var requested && !Same(requested, repositoryId))
        {
            return $"--repository-id names repository {requested}";
        }

        if (AmsRepositoryResolver.RegisteredRepository(agents) is { } registered && !Same(registered, repositoryId))
        {
            return $"this project's agents are in repository {registered}";
        }

        return tree is not null && _repositories.RepositoryOf(plan.ProjectDirectory, tree) is { } cloned && !Same(cloned, repositoryId) ? $"{plan.ProjectDirectory} is a clone of repository {cloned}" : null;
    }

    private void Undo(string projectDirectory, ManagedAgentRecord stored, AmsFolderSnapshot snapshot)
    {
        try
        {
            _agents.RemoveWithDeployments(projectDirectory, stored.AgentId, stored.EntityTag);
        }
        catch (AgentHostingException)
        {
        }

        AmsProjectLink.Undo(snapshot);
    }

    private sealed record Prepared(AmsRepositoryChoice Choice, PowerPlatformEnvironment Home, AmsMcsRepository? Repository, AmsFolderSnapshot Snapshot, IReadOnlyList<string> Warnings);
}
