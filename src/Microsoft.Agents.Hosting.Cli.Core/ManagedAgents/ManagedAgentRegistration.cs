using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Registers new MCS agent and standalone workflow records the same way for every verb that creates one.</summary>
public sealed class ManagedAgentRegistration
{
    private readonly IManagedAgentRegistry _agents;
    private readonly AgentConfigLocator _locator;

    /// <summary>Creates the helper.</summary>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="locator">Resolves the configuration file named in errors.</param>
    public ManagedAgentRegistration(IManagedAgentRegistry agents, AgentConfigLocator locator)
    {
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    /// <summary>Rejects a name or folder already taken, before anything is written.</summary>
    /// <param name="projectDirectory">The project the agent is registered in.</param>
    /// <param name="workspaceDirectory">The folder the agent's workspace will occupy.</param>
    /// <param name="name">The agent's display name.</param>
    public void RequireProjectIsFree(string projectDirectory, string workspaceDirectory, string name) => RequireProjectIsFree(_agents.List(projectDirectory), projectDirectory, workspaceDirectory, name);

    /// <summary>Rejects a name or folder already taken, before anything is written, checking the name against records the caller already read.</summary>
    /// <param name="registeredAgents">The agents registered in the project.</param>
    /// <param name="projectDirectory">The project the agent is registered in.</param>
    /// <param name="workspaceDirectory">The folder the agent's workspace will occupy.</param>
    /// <param name="name">The agent's display name.</param>
    /// <param name="projectChoosesDestination">Whether --project chose the project, so another one can avoid the folder.</param>
    public void RequireProjectIsFree(IEnumerable<ManagedAgentRecord> registeredAgents, string projectDirectory, string workspaceDirectory, string name, bool projectChoosesDestination = true)
    {
        ArgumentNullException.ThrowIfNull(registeredAgents);

        if (registeredAgents.FirstOrDefault(agent => string.Equals(agent.DisplayName, name, StringComparison.OrdinalIgnoreCase)) is { } duplicate)
        {
            throw ManagedAgentException.AlreadyRegistered(duplicate.DisplayName, duplicate.AgentId, _locator.ConfigFilePath(projectDirectory));
        }

        WorkspaceDirectoryGuard.RequireEmpty(workspaceDirectory, projectChoosesDestination);
    }

    /// <summary>Adds an MCS agent record.</summary>
    /// <param name="projectDirectory">The project the agent is registered in.</param>
    /// <param name="displayName">The agent's display name.</param>
    /// <param name="description">The record's description, if any.</param>
    /// <param name="agentLocation">The agent's workspace, relative to the project.</param>
    /// <param name="tenantId">The tenant owning the home environment.</param>
    /// <param name="environmentId">The record's home environment.</param>
    /// <param name="caller">The signed-in operator recorded as owner and creator.</param>
    /// <param name="mcsSchemaName">The agent's schema name, when known.</param>
    /// <param name="mcsAgentId">The cloud agent the record is bound to, when there is one.</param>
    /// <param name="agentId">The record's identifier, or <see langword="null"/> to generate one.</param>
    /// <returns>The stored record.</returns>
    public ManagedAgentRecord Register(string projectDirectory, string displayName, string? description, string agentLocation, string tenantId, string environmentId, PrincipalReference? caller, string? mcsSchemaName = null, string? mcsAgentId = null, string? agentId = null) => _agents.Add(projectDirectory, NewRecord(AgentTypes.McsAgent, displayName, description, agentLocation, tenantId, environmentId, caller, agentId) with { McsSchemaName = mcsSchemaName, McsAgentId = mcsAgentId });

    /// <summary>Adds a standalone workflow record.</summary>
    /// <param name="projectDirectory">The project the workflow is registered in.</param>
    /// <param name="displayName">The record's name.</param>
    /// <param name="description">The record's description, if any.</param>
    /// <param name="agentLocation">The workflow's workspace, relative to the project.</param>
    /// <param name="tenantId">The tenant owning the home environment.</param>
    /// <param name="environmentId">The record's home environment.</param>
    /// <param name="caller">The signed-in operator recorded as owner and creator.</param>
    /// <param name="workflowId">The Dataverse workflow the record addresses.</param>
    /// <param name="agentId">The record's identifier, or <see langword="null"/> to generate one.</param>
    /// <returns>The stored record.</returns>
    public ManagedAgentRecord RegisterWorkflow(string projectDirectory, string displayName, string? description, string agentLocation, string tenantId, string environmentId, PrincipalReference? caller, Guid workflowId, string? agentId = null) => _agents.Add(projectDirectory, NewRecord(AgentTypes.McsWorkflow, displayName, description, agentLocation, tenantId, environmentId, caller, agentId) with { McsWorkflowId = workflowId.ToString() });

    private static ManagedAgentRecord NewRecord(string agentType, string displayName, string? description, string agentLocation, string tenantId, string environmentId, PrincipalReference? caller, string? agentId) => new()
    {
        AgentId = agentId ?? Guid.NewGuid().ToString(),
        DisplayName = displayName.Trim(),
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
        AgentType = agentType,
        AgentLocation = agentLocation,
        TenantId = tenantId,
        EnvironmentId = environmentId,
        Origin = ManagedAgentOrigins.AgentHostCli,
        Owner = caller,
        CreatedBy = caller,
        EntityTag = string.Empty,
        AgentState = EntityStates.Saved,
    };
}
