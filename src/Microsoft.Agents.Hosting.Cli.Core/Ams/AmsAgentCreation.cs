using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>The AMS step of <c>msagent agent create</c>: after main's checks it chooses the repository, asks, and clones or attaches it; after main's writes it registers the agent, links a new repository, commits and pushes the agent's folder, and keeps a copy of each cloned folder's cloud cache for that commit.</summary>
public sealed class AmsAgentCreation : IAgentCreationHook
{
    private const string Command = "msagent agent create";

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

    /// <summary>Creates the step.</summary>
    /// <param name="git">Runs git.</param>
    /// <param name="repositories">Chooses the repository.</param>
    /// <param name="link">Asks, and links the project folder.</param>
    /// <param name="registrar">Registers the agent.</param>
    /// <param name="committer">Commits and pushes the agent's folder.</param>
    /// <param name="deployRecords">Tells AMS which commit a cloned agent's Copilot Studio agent holds.</param>
    /// <param name="targets">Resolves the home environment's endpoints.</param>
    /// <param name="reads">Reads the existing repository.</param>
    /// <param name="agents">The managed-agent table, which a failed register is undone from.</param>
    /// <param name="clock">Dates the deploy record of a clone.</param>
    /// <param name="shelf">Keeps a copy of each cloned folder's cloud cache for the commit that adds the folder.</param>
    /// <param name="workspaces">Reads the agent each cloned folder is connected to.</param>
    public AmsAgentCreation(IGitPort git, AmsRepositoryResolver repositories, AmsProjectLink link, AmsAgentRegistrar registrar, AmsCommitter committer, AmsDeployRecords deployRecords, AmsTargets targets, IAmsMcsAgentReadTransport reads, IManagedAgentRegistry agents, IClock clock, AmsCloudCacheShelf shelf, ICopilotWorkspacePort workspaces)
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
    }

    /// <inheritdoc />
    public async Task<AgentCreationTicket?> PrepareAsync(AgentCreationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _git.RequireGit();
        var tree = await _git.FindWorkingTreeAsync(plan.ProjectDirectory, cancellationToken).ConfigureAwait(false);
        var choice = _repositories.Choose(plan.ProjectDirectory, tree, plan.RepositoryId, _agents.List(plan.ProjectDirectory), []);
        await _link.RequireNoSyncStateAsync(choice, plan.ProjectDirectory, [], joined: false, cancellationToken).ConfigureAwait(false);
        var home = await _targets.HomeAsync(plan.HomeEnvironmentId, cancellationToken).ConfigureAwait(false);
        var repository = choice.RepositoryId is { } repositoryId ? await _reads.ReadRepositoryAsync(_targets.DataPlane(home), repositoryId, plan.AgentLocation, includeDeployments: false, cancellationToken).ConfigureAwait(false) : null;
        if (repository is { AgentFolderExists: true })
        {
            throw AmsAgentException.FolderExistsInRepository(plan.AgentLocation, repository.Name ?? repository.RepositoryId);
        }

        if (!await _link.ConfirmAsync(choice, plan.ProjectDirectory, AmsProjectLink.Subject(plan.AgentType, plan.DisplayName), repository, home.DisplayName, plan.NonInteractive, cancellationToken).ConfigureAwait(false))
        {
            AmsProjectLink.Undo(AmsProjectLink.Snapshot(plan.ProjectDirectory, plan.ProjectDirectoryCreated));
            return null;
        }

        var snapshot = AmsProjectLink.Snapshot(plan.ProjectDirectory, plan.ProjectDirectoryCreated);
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
