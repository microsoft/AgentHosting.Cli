using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>
/// The managed-agent table of the registry that stands in for the Agent Management Service.
/// </summary>
public interface IManagedAgentRegistry
{
    /// <summary>Reads every managed agent registered in a project.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <returns>The registered agents, in file order.</returns>
    IReadOnlyList<ManagedAgentRecord> List(string projectDirectory);

    /// <summary>Appends a managed agent.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <param name="record">The record to add, without its concurrency token.</param>
    /// <returns>The stored record, carrying its assigned token and timestamps.</returns>
    ManagedAgentRecord Add(string projectDirectory, ManagedAgentRecord record);

    /// <summary>Replaces a managed agent, guarded by its concurrency token.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <param name="record">The record to store, carrying the token it was read with.</param>
    /// <returns>The stored record, carrying its new token and timestamp.</returns>
    ManagedAgentRecord Update(string projectDirectory, ManagedAgentRecord record);

    /// <summary>Removes a managed agent and every deployment referencing it, in one write.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <param name="agentId">The agent to remove.</param>
    /// <param name="expectedETag">The token the agent was read with.</param>
    /// <returns>How many deployment rows were removed alongside it.</returns>
    int RemoveWithDeployments(string projectDirectory, string agentId, string expectedETag);
}

/// <summary>
/// The agent-deployment table of the registry. See <see cref="IManagedAgentRegistry"/> for why the
/// two tables are separate ports over one document.
/// </summary>
public interface IAgentDeploymentRegistry
{
    /// <summary>Reads every deployment registered in a project.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <returns>The registered deployments, in file order.</returns>
    IReadOnlyList<AgentDeploymentRecord> List(string projectDirectory);

    /// <summary>Appends a deployment.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <param name="record">The record to add, without its concurrency token.</param>
    /// <returns>The stored record, carrying its assigned token and timestamp.</returns>
    AgentDeploymentRecord Add(string projectDirectory, AgentDeploymentRecord record);

    /// <summary>Replaces a deployment, guarded by its concurrency token.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <param name="record">The record to store, carrying the token it was read with.</param>
    /// <returns>The stored record, carrying its new token.</returns>
    AgentDeploymentRecord Update(string projectDirectory, AgentDeploymentRecord record);

    /// <summary>Removes a single deployment, guarded by its concurrency token.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <param name="deploymentId">The deployment to remove.</param>
    /// <param name="expectedETag">The token the deployment was read with.</param>
    void Remove(string projectDirectory, string deploymentId, string expectedETag);
}
