using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Records the home <c>develop</c> deployment a clone is checked out against, removing the new agent record when that deployment cannot be written.</summary>
public sealed class HomeDeploymentRegistrar
{
    /// <summary>The name of the home deployment registered for a new agent.</summary>
    public const string DeploymentName = "develop";

    private readonly IManagedAgentRegistry _agents;
    private readonly IAgentDeploymentRegistry _deployments;

    /// <summary>Creates the registrar.</summary>
    /// <param name="agents">The managed-agent table, from which the new record is removed when its home deployment cannot be written.</param>
    /// <param name="deployments">The agent-deployment table the home deployment is written to.</param>
    public HomeDeploymentRegistrar(IManagedAgentRegistry agents, IAgentDeploymentRegistry deployments)
    {
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
    }

    /// <summary>Writes the agent's home <c>develop</c> deployment, or removes the agent and fails when it cannot be written.</summary>
    /// <param name="projectDirectory">The project whose configuration holds the agent.</param>
    /// <param name="agent">The agent that was just registered.</param>
    /// <param name="targetEnvironmentId">The environment the home deployment targets.</param>
    /// <returns>The deployment that was written.</returns>
    public AgentDeploymentRecord Register(string projectDirectory, ManagedAgentRecord agent, string targetEnvironmentId)
    {
        ArgumentNullException.ThrowIfNull(agent);

        try
        {
            return _deployments.Add(projectDirectory, new AgentDeploymentRecord
            {
                DeploymentId = Guid.NewGuid().ToString(),
                AgentId = agent.AgentId,
                AgentType = agent.AgentType,
                DeploymentType = DeploymentStages.Dev,
                DeploymentName = HomeDeploymentRegistrar.DeploymentName,
                TenantId = agent.TenantId,
                HomeEnvironmentId = agent.EnvironmentId,
                EnvironmentId = targetEnvironmentId,
                CreatedBy = agent.CreatedBy,
                EntityTag = string.Empty,
            });
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            _agents.RemoveWithDeployments(projectDirectory, agent.AgentId, agent.EntityTag);

            throw ManagedAgentException.CloneNotRegistered(agent.DisplayName, failure);
        }
    }
}
