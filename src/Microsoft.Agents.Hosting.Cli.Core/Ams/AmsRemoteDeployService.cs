using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>The <c>msagent agent deploy --remote</c> application service: AMS deploys a commit from GRS to one of the agent's deployments, the command waits for it, connects the agent's folder after a Dev deploy of exactly the commit the folder holds, and records the deploy in .config.</summary>
public sealed class AmsRemoteDeployService : ICommandHandler<McsAmsAgentDeployRequest, McsAmsAgentDeployResult>
{
    private const string Command = "msagent agent deploy --remote";

    private readonly ManagedAgentResolver _resolver;
    private readonly IManagedAgentRegistry _agents;
    private readonly IAgentDeploymentRegistry _deployments;
    private readonly CopilotProjectIdentityReader _identities;
    private readonly ManagedAgentPrincipalResolver _principals;
    private readonly IGitPort _git;
    private readonly AmsGitDeployStates _states;
    private readonly AmsTargets _targets;
    private readonly IAmsMcsOperationTransport _operations;
    private readonly McsOperationWaiter _waiter;
    private readonly AmsWorkspaceConnector _connector;
    private readonly IClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates the service.</summary>
    /// <param name="resolver">Selects the agent and the deployment from .config.</param>
    /// <param name="agents">The managed-agent table, which the deploy updates.</param>
    /// <param name="deployments">The deployment table, which the deploy updates.</param>
    /// <param name="identities">Reads the schema name the workspace declares.</param>
    /// <param name="principals">Resolves the signed-in operator recorded as the deployer.</param>
    /// <param name="git">Resolves a short or symbolic --commit-id.</param>
    /// <param name="states">Reads which commit the folder holds, and whether it holds changes the commit lacks.</param>
    /// <param name="targets">Resolves the home environment's endpoints.</param>
    /// <param name="operations">Starts the deploy.</param>
    /// <param name="waiter">Waits for the deploy operation.</param>
    /// <param name="connector">Reads the agent's folder's connection, and connects the folder to the Copilot Studio agent of the Dev deployment.</param>
    /// <param name="clock">Dates the deploy when the operation names no end.</param>
    /// <param name="delay">Waits before a capacity retry, replaced in tests.</param>
    public AmsRemoteDeployService(ManagedAgentResolver resolver, IManagedAgentRegistry agents, IAgentDeploymentRegistry deployments, CopilotProjectIdentityReader identities, ManagedAgentPrincipalResolver principals, IGitPort git, AmsGitDeployStates states, AmsTargets targets, IAmsMcsOperationTransport operations, McsOperationWaiter waiter, AmsWorkspaceConnector connector, IClock clock, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _identities = identities ?? throw new ArgumentNullException(nameof(identities));
        _principals = principals ?? throw new ArgumentNullException(nameof(principals));
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _states = states ?? throw new ArgumentNullException(nameof(states));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _waiter = waiter ?? throw new ArgumentNullException(nameof(waiter));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delay = delay ?? Task.Delay;
    }

    /// <inheritdoc />
    public async Task<McsAmsAgentDeployResult> HandleAsync(McsAmsAgentDeployRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projectDirectory = _resolver.ResolveProjectDirectory(request.Project);
        var (owner, deployment) = _resolver.ResolveAgentDeployment(projectDirectory, request.AgentId, request.AgentName, request.DeploymentId, request.DeploymentName, requireExplicitDeployment: true);
        var agent = ManagedAgentResolver.RequireMcsAgentOrWorkflow(owner, Command);
        if (agent.SourceControlMetadata is null)
        {
            throw AmsAgentException.AgentNotRegistered(agent.DisplayName, agent.AgentId, Command, projectDirectory);
        }

        var state = await _states.ReadAsync(projectDirectory, agent, cancellationToken).ConfigureAwait(false);
        var commit = string.IsNullOrWhiteSpace(request.CommitId) ? null : await ResolveCommitAsync(projectDirectory, request.CommitId.Trim(), cancellationToken).ConfigureAwait(false);
        var home = await _targets.HomeAsync(agent.EnvironmentId, cancellationToken).ConfigureAwait(false);
        var deploy = new AmsMcsDeployTransportRequest { Target = _targets.OnBehalfOf(home, Command), AgentId = agent.AgentId, DeploymentId = deployment.DeploymentId, IdempotencyKey = Guid.NewGuid().ToString("D"), CommitSha = commit, Publish = request.Publish, Overwrite = request.Overwrite, RespondAsync = request.Async };
        var accepted = await McsCapacityRetry.StartAsync(token => _operations.DeployAsync(deploy, token), _delay, "deploy", DeploymentCommandHints.DeploymentShow(agent.AgentId, deployment.DeploymentId), cancellationToken).ConfigureAwait(false);
        var result = new McsAmsAgentDeployResult { Status = "deploying", AgentId = agent.AgentId, DisplayName = agent.DisplayName, AgentType = agent.AgentType, DeploymentId = deployment.DeploymentId, DeploymentName = deployment.DeploymentName, DeploymentType = deployment.DeploymentType, TargetEnvironmentId = deployment.McsEnvironmentId(), CommitSha = accepted.SourceCommitSha ?? commit, OperationId = accepted.OperationId };
        var operation = await _waiter.FinishAsync(_targets.DataPlane(home), agent.AgentId, accepted.OperationId, accepted.Finished, request.Async, failed => AmsMcsOperationErrorMap.OperationFailed(failed, commit, agent.AgentType), cancellationToken).ConfigureAwait(false);
        var deployed = operation?.ResultCommitSha ?? operation?.SourceCommitSha ?? result.CommitSha;
        List<string> warnings = state.Note is null && (state.HasLocalChanges || !state.HeadPushed) ? [$"--remote deploys {(deployed is null ? "the commit AMS chose" : McsAmsCommits.Short(deployed))} from GRS; your local changes aren't in it."] : [];
        if (operation is null)
        {
            return result with { Warnings = warnings };
        }

        var (workspaceConnected, workspaceNotice) = await FinishAsync(projectDirectory, agent, deployment, NotConnectedReason(state, deployed), operation.CompletedTime ?? _clock.UtcNow, warnings, cancellationToken).ConfigureAwait(false);
        return result with { Status = "deployed", CommitSha = deployed, Published = agent.AgentType == AgentTypes.McsWorkflow ? null : operation.Publish, LastPublishedTime = operation.LastPublishedTime, CapturedCommitSha = operation.CapturedCommitSha, WorkspaceConnected = workspaceConnected, WorkspaceNotice = workspaceNotice, Notices = McsAmsViews.Notices(operation.Notices), Warnings = warnings };
    }

    private static string NotConnected(string reason) => $"The deploy finished, but the agent's folder wasn't connected to Dev: {reason} To bring Copilot Studio changes into the repository, run `msagent agent capture`, then `git pull`.";

    private static string? NotConnectedReason(AmsGitDeployState state, string? deployed) => (state, deployed) switch
    {
        ({ Note: not null }, _) => "Git can't tell which commit the agent's folder holds",
        (_, null) => "AMS didn't name the commit it deployed",
        ({ CommitSha: { } held }, { } commit) when !string.Equals(held, commit, StringComparison.OrdinalIgnoreCase) => $"The agent's folder holds {McsAmsCommits.Short(held)}, not the deployed {McsAmsCommits.Short(commit)}",
        ({ HasLocalChanges: true }, _) => AmsWorkspaceConnector.Uncommitted("The agent's folder"),
        ({ Exact: false }, _) => "The agent's folder holds commits that aren't pushed to GRS",
        _ => null,
    };

    private static string NotConnectedNotice(string reason, bool connected) => connected
        ? $"{reason}, so msagent left the folder's connection to Dev as it was; `msagent agent pull` brings the deployed changes into it."
        : AmsWorkspaceConnector.NotConnected(reason);

    private async Task<(bool Connected, string? Notice)> FinishAsync(string projectDirectory, ManagedAgentRecord agent, AgentDeploymentRecord deployment, string? notConnectedReason, DateTimeOffset deployedTime, List<string> warnings, CancellationToken cancellationToken)
    {
        var connectsFolder = agent.AgentType == AgentTypes.McsAgent && deployment.DeploymentType == DeploymentStages.Dev;
        var (workspace, schemaName) = ReadFolder(projectDirectory, agent, connectsFolder, warnings);
        var (connection, notice) = connectsFolder && workspace is not null ? await ConnectAsync(projectDirectory, agent, deployment, workspace, schemaName, notConnectedReason, warnings, cancellationToken).ConfigureAwait(false) : default;
        await RecordAsync(projectDirectory, agent with { McsSchemaName = schemaName, McsAgentId = connection?.AgentId ?? agent.McsAgentId }, deployment, deployedTime, warnings, cancellationToken).ConfigureAwait(false);
        return (connection is not null, notice);
    }

    private (string? Workspace, string? SchemaName) ReadFolder(string projectDirectory, ManagedAgentRecord agent, bool connectsFolder, List<string> warnings)
    {
        try
        {
            var workspace = _resolver.FindAgentWorkspaceDirectory(projectDirectory, agent);
            return (workspace, ManagedAgentResolver.McsSchemaNameOf(agent, workspace is null || agent.AgentType == AgentTypes.McsWorkflow ? null : _identities.Find(workspace)?.SchemaName));
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            warnings.Add(connectsFolder ? NotConnected(failure.Message) : $"The deploy finished, but the agent's folder couldn't be read: {failure.Message}");
            return (null, agent.McsSchemaName);
        }
    }

    private async Task<(CopilotConnectOutcome? Connection, string? Notice)> ConnectAsync(string projectDirectory, ManagedAgentRecord agent, AgentDeploymentRecord deployment, string workspace, string? schemaName, string? notConnectedReason, List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            var environmentId = deployment.McsEnvironmentId();
            var connectedEnvironmentId = await _connector.ConnectedEnvironmentAsync(workspace, cancellationToken).ConfigureAwait(false);
            if (connectedEnvironmentId is not null && !string.Equals(connectedEnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase))
            {
                return default;
            }

            if (notConnectedReason is not null)
            {
                return (null, NotConnectedNotice(notConnectedReason, connectedEnvironmentId is not null));
            }

            var connection = await _connector.ConnectAsync(projectDirectory, workspace, environmentId, schemaName ?? _identities.Require(workspace, agent.AgentId).SchemaName, cancellationToken).ConfigureAwait(false);
            if (connection.SkippedCollectionDirectories.Count > 0)
            {
                warnings.Add(AmsWorkspaceConnector.CollectionsNotConnected(projectDirectory, connection.SkippedCollectionDirectories));
            }

            return (connection, null);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            warnings.Add(NotConnected(failure.Message));
            return default;
        }
    }

    private async Task<string> ResolveCommitAsync(string projectDirectory, string commitId, CancellationToken cancellationToken)
    {
        try
        {
            return await _git.FindWorkingTreeAsync(projectDirectory, cancellationToken).ConfigureAwait(false) is { } tree ? await _git.ResolveCommitAsync(tree.Root, commitId, cancellationToken).ConfigureAwait(false) ?? commitId : commitId;
        }
        catch (AuthenticationException failure) when (failure.ErrorCode == ErrorCodes.Authentication.GitNotFound)
        {
            return commitId;
        }
    }

    private async Task RecordAsync(string projectDirectory, ManagedAgentRecord agent, AgentDeploymentRecord deployment, DateTimeOffset deployedTime, List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            var deployer = await _principals.ResolveAsync(cancellationToken).ConfigureAwait(false);
            _deployments.Update(projectDirectory, deployment with { LastDeployedTime = deployedTime, LastDeployedBy = deployer });
            _agents.Update(projectDirectory, agent with { LastDeployedBy = deployer });
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            warnings.Add($"The deploy finished, but .config wasn't updated: {failure.Message}");
        }
    }
}
