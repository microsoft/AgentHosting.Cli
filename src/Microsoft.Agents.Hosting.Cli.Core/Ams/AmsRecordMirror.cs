using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Applies a .config change of a registered agent to AMS's copy of the records first, then writes .config, undoing a create or rename in AMS when that write fails.</summary>
public sealed class AmsRecordMirror : IAgentRecordMirror
{
    private const int NotFound = 404;

    private readonly IAmsMcsDeploymentTransport _deployments;
    private readonly AmsAgentDeletion _agentDeletion;
    private readonly AmsTargets _targets;

    /// <summary>Creates the mirror.</summary>
    /// <param name="deployments">Creates, renames and deletes deployment records.</param>
    /// <param name="agentDeletion">Deletes an agent's records.</param>
    /// <param name="targets">Resolves the endpoints of the agent's home environment.</param>
    public AmsRecordMirror(IAmsMcsDeploymentTransport deployments, AmsAgentDeletion agentDeletion, AmsTargets targets)
    {
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _agentDeletion = agentDeletion ?? throw new ArgumentNullException(nameof(agentDeletion));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    /// <inheritdoc />
    public async Task<AgentRecordMirrored<T>> ApplyAsync<T>(string projectDirectory, AgentRecordChange change, Func<T> writeConfig, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(writeConfig);

        if (change.Agent.SourceControlMetadata is null)
        {
            return new(writeConfig(), change is DeploymentCreated or DeploymentRenamed ? $"AMS wasn't updated: agent {change.Agent.DisplayName} isn't registered. Run `msagent agent init` to register it." : null);
        }

        var home = await _targets.HomeAsync(change.Agent.EnvironmentId, cancellationToken).ConfigureAwait(false);
        var undo = await ApplyToAmsAsync(projectDirectory, home, change, cancellationToken).ConfigureAwait(false);
        try
        {
            return new(writeConfig(), null, Mirrored: true);
        }
        catch (Exception failure) when (failure is not OperationCanceledException && undo is not null)
        {
            await UndoAsync(undo).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Creates a deployment of .config in AMS with the id .config holds; the same deployment again answers the stored record.</summary>
    /// <param name="home">The agent's home environment.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="deployment">The deployment.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<AmsMcsDeploymentRecord> CreateDeploymentAsync(PowerPlatformEnvironment home, ManagedAgentRecord agent, AgentDeploymentRecord deployment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(deployment);

        return _deployments.CreateMcsDeploymentAsync(new AmsMcsDeploymentCreateTransportRequest { Target = _targets.ControlPlane(home), AgentId = agent.AgentId, DeploymentId = deployment.DeploymentId, AgentType = agent.AgentType, DeploymentType = DeploymentStages.ServiceName(deployment.DeploymentType), DeploymentName = deployment.DeploymentName, TargetEnvironmentId = deployment.McsEnvironmentId() }, cancellationToken);
    }

    private static async Task UndoAsync(Func<Task> undo)
    {
        try
        {
            await undo().ConfigureAwait(false);
        }
        catch (AgentHostingException)
        {
        }
    }

    private async Task<Func<Task>?> ApplyToAmsAsync(string projectDirectory, PowerPlatformEnvironment home, AgentRecordChange change, CancellationToken cancellationToken)
    {
        var target = _targets.ControlPlane(home);
        switch (change)
        {
            case DeploymentCreated created:
                await CreateDeploymentAsync(home, created.Agent, created.Deployment, cancellationToken).ConfigureAwait(false);
                return () => _deployments.DeleteDeploymentAsync(target, created.Agent.AgentId, created.Deployment.DeploymentId, created.Agent.AgentType, CancellationToken.None);
            case DeploymentRenamed renamed:
                await RenameAsync(target, renamed.Agent, renamed.Deployment.DeploymentId, renamed.NewName, cancellationToken).ConfigureAwait(false);
                return () => RenameAsync(target, renamed.Agent, renamed.Deployment.DeploymentId, renamed.Deployment.DeploymentName, CancellationToken.None);
            case DeploymentDeleted deleted:
                await DeleteDeploymentAsync(target, deleted.Agent, deleted.Deployment.DeploymentId, cancellationToken).ConfigureAwait(false);
                return null;
            case AgentDeleted deleted:
                await _agentDeletion.DeleteAndForgetAsync(projectDirectory, home, deleted.Agent, cancellationToken).ConfigureAwait(false);
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change.GetType().Name, "The change has no AMS counterpart.");
        }
    }

    private Task<AmsMcsDeploymentRecord> RenameAsync(AmsServiceTarget target, ManagedAgentRecord agent, string deploymentId, string deploymentName, CancellationToken cancellationToken) =>
        _deployments.RenameMcsDeploymentAsync(new AmsMcsDeploymentRenameTransportRequest { Target = target, AgentId = agent.AgentId, DeploymentId = deploymentId, AgentType = agent.AgentType, DeploymentName = deploymentName }, cancellationToken);

    private async Task DeleteDeploymentAsync(AmsServiceTarget target, ManagedAgentRecord agent, string deploymentId, CancellationToken cancellationToken)
    {
        try
        {
            await _deployments.DeleteDeploymentAsync(target, agent.AgentId, deploymentId, agent.AgentType, cancellationToken).ConfigureAwait(false);
        }
        catch (TargetRequestException failure) when (failure.Status == NotFound)
        {
        }
    }
}
