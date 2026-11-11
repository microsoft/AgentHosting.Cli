namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// The managed-agent command services, grouped so the operation facade takes one dependency rather
/// than nine. Registered as a unit, and absent when the host has no Copilot Studio stack composed.
/// </summary>
/// <param name="Create">Scaffolds a project and registers it, or records a clone source.</param>
/// <param name="Copy">Prepares a new agent from an existing one and registers it.</param>
/// <param name="Init">Registers a project that already exists.</param>
/// <param name="List">Lists the agents in an environment or across the tenant.</param>
/// <param name="Show">Reads one cloud agent.</param>
/// <param name="Delete">Deletes a cloud agent and every record for it.</param>
/// <param name="Share">Grants, shows and revokes access to a cloud agent.</param>
/// <param name="Pull">Pulls or clones the agent's content into a directory.</param>
/// <param name="Deploy">Deploys the project to a deployment slot.</param>
/// <param name="DeploymentCreate">Records a deployment slot.</param>
/// <param name="DeploymentList">Lists an agent's deployment slots.</param>
/// <param name="DeploymentUpdates">Renames a deployment slot.</param>
/// <param name="DeploymentDeletes">Deletes a deployment slot.</param>
/// <param name="DeploymentConnections">Computes and updates a deployment's connection readiness.</param>
/// <param name="DeploymentConnectionReads">Reports a deployment's connection and workflow state.</param>
/// <param name="DeploymentConnectors">Lists the connectors an environment serves.</param>
/// <param name="DeploymentConnectionCreates">Creates one connection through browser consent.</param>
/// <param name="DeploymentConnectionReferenceCreates">Declares a connection reference and binds it.</param>
/// <param name="DeploymentConnectionRemoves">Removes a connection reference, deletes a connection, or both.</param>
/// <param name="DeploymentShow">Reads one MCS deployment.</param>
/// <param name="AgentTypes">Resolves which agent type a unified deployment verb targets.</param>
/// <param name="WorkflowCreate">Scaffolds or clones a standalone workflow.</param>
/// <param name="WorkflowInit">Registers a standalone workflow already on disk.</param>
/// <param name="WorkflowDeploy">Uploads a standalone workflow to a deployment slot.</param>
/// <param name="WorkflowPull">Downloads a standalone workflow.</param>
/// <param name="WorkflowDelete">Deletes a standalone workflow and every record for it.</param>
/// <param name="Resolver">Resolves which agent type a record selector names.</param>
public sealed record ManagedAgentServices(
    ManagedAgentCreateService Create,
    ManagedAgentCopyService Copy,
    ManagedAgentInitService Init,
    ManagedAgentListService List,
    ManagedAgentShowService Show,
    ManagedAgentDeleteService Delete,
    ManagedAgentShareService Share,
    ManagedAgentPullService Pull,
    ManagedAgentDeployService Deploy,
    AgentDeploymentCreateService DeploymentCreate,
    AgentDeploymentListService DeploymentList,
    AgentDeploymentShowService DeploymentShow,
    AgentDeploymentUpdateService DeploymentUpdates,
    AgentDeploymentDeleteService DeploymentDeletes,
    AgentDeploymentConnectionService DeploymentConnections,
    AgentDeploymentConnectionReader DeploymentConnectionReads,
    AgentDeploymentConnectorListService DeploymentConnectors,
    AgentDeploymentConnectionCreateService DeploymentConnectionCreates,
    AgentDeploymentConnectionReferenceCreateService DeploymentConnectionReferenceCreates,
    AgentDeploymentConnectionRemoveService DeploymentConnectionRemoves,
    DeploymentAgentTypeResolver AgentTypes,
    WorkflowCreateService WorkflowCreate,
    WorkflowInitService WorkflowInit,
    WorkflowDeployService WorkflowDeploy,
    WorkflowPullService WorkflowPull,
    WorkflowDeleteService WorkflowDelete,
    ManagedAgentResolver Resolver)
{
    /// <summary>Whether a command's selectors name a standalone workflow record.</summary>
    /// <param name="project">The project directory, or <see langword="null"/> for the enclosing project.</param>
    /// <param name="agentId">An explicit record identifier.</param>
    /// <param name="agentName">A record name.</param>
    /// <param name="deploymentId">A deployment identifier.</param>
    /// <param name="deploymentName">A deployment name.</param>
    /// <returns><see langword="true"/> when the selectors name a workflow.</returns>
    public bool SelectsWorkflow(string? project, string? agentId, string? agentName, string? deploymentId = null, string? deploymentName = null) =>
        Storage.AgentTypes.IsMcsWorkflow(Resolver.FindAgentType(Resolver.ResolveProjectDirectory(project), agentId, agentName, deploymentId, deploymentName));
}
