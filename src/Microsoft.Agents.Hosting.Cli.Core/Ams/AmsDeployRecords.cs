using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Tells AMS about a deploy msagent made from the working folder, with the commit git says it holds; a failure is a warning, since the deploy itself stands.</summary>
public sealed class AmsDeployRecords
{
    private readonly AmsGitDeployStates _states;
    private readonly IAmsMcsOperationTransport _operations;
    private readonly AmsTargets _targets;

    /// <summary>Creates the writer.</summary>
    /// <param name="states">Reads which commit the folder holds.</param>
    /// <param name="operations">Sends the deploy record.</param>
    /// <param name="targets">Resolves the endpoints of the agent's home environment.</param>
    public AmsDeployRecords(AmsGitDeployStates states, IAmsMcsOperationTransport operations, AmsTargets targets)
    {
        _states = states ?? throw new ArgumentNullException(nameof(states));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    /// <summary>Records a deploy.</summary>
    /// <param name="projectDirectory">The project folder.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="deploymentId">The deployment that was deployed to.</param>
    /// <param name="botId">The Copilot Studio agent the deploy wrote, for an agent.</param>
    /// <param name="workflowId">The workflow the deploy wrote, for a standalone workflow.</param>
    /// <param name="published">Whether the deploy published.</param>
    /// <param name="deployedTime">When the deploy completed.</param>
    /// <param name="command">The command, named when the on-behalf-of endpoint isn't usable.</param>
    /// <param name="cancellationToken">Cancels git and the call.</param>
    /// <returns>Why AMS doesn't know about the deploy, or doesn't know its commit, or null.</returns>
    public async Task<string?> RecordAsync(string projectDirectory, ManagedAgentRecord agent, string deploymentId, string? botId, string? workflowId, bool published, DateTimeOffset deployedTime, string command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        try
        {
            var state = await _states.ReadAsync(projectDirectory, agent, cancellationToken).ConfigureAwait(false);
            var home = await _targets.HomeAsync(agent.EnvironmentId, cancellationToken).ConfigureAwait(false);
            await _operations.RecordDeployAsync(new AmsMcsDeployRecordRequest { Target = _targets.OnBehalfOf(home, command), AgentId = agent.AgentId, DeploymentId = deploymentId, BotId = botId, WorkflowId = workflowId, CommitSha = state.CommitSha, Exact = state.Exact, Published = published, DeployedTime = deployedTime }, cancellationToken).ConfigureAwait(false);
            return state.Note is null ? null : $"{state.Note}, so AMS doesn't know which commit was deployed.";
        }
        catch (AgentHostingException failure)
        {
            return $"AMS doesn't know about this deploy: {failure.Message}";
        }
    }
}
