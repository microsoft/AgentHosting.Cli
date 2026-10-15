using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Turns command selectors into the records they name.
/// </summary>
public sealed class ManagedAgentResolver
{
    private readonly IManagedAgentRegistry _agents;
    private readonly IAgentDeploymentRegistry _deployments;
    private readonly AgentConfigLocator _locator;

    /// <summary>Creates the resolver.</summary>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="deployments">The deployment table.</param>
    /// <param name="locator">Resolves project directories.</param>
    public ManagedAgentResolver(IManagedAgentRegistry agents, IAgentDeploymentRegistry deployments, AgentConfigLocator locator)
    {
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    /// <summary>Resolves the project directory a command acts on.</summary>
    /// <param name="projectDirectory">An explicit directory, or <see langword="null"/> for the current one.</param>
    /// <returns>The absolute project directory.</returns>
    public string ResolveProjectDirectory(string? projectDirectory) => _locator.ResolveProjectDirectory(projectDirectory);

    /// <summary>Resolves the directory holding an agent's workspace content.</summary>
    /// <param name="projectDirectory">The project directory.</param>
    /// <param name="agent">The record whose workspace is resolved.</param>
    /// <returns>The agent workspace directory.</returns>
    public string ResolveAgentWorkspaceDirectory(string? projectDirectory, ManagedAgentRecord agent) => FindAgentWorkspaceDirectory(projectDirectory, agent) ?? throw ManagedAgentException.WorkspaceMissing(agent.AgentId, agent.AgentLocation, _locator.ResolveProjectDirectory(projectDirectory));

    /// <summary>Resolves the content directory a record names, for either an agent project or a workflow.</summary>
    /// <param name="projectDirectory">The project directory.</param>
    /// <param name="agent">The record whose workspace is resolved.</param>
    /// <param name="requireExists">
    /// Whether the workflow directory has to be on disk already. A command that is about to recreate it
    /// passes <see langword="false"/>, which still refuses a location that escapes the project root.
    /// </param>
    /// <returns>The workspace directory.</returns>
    public string ResolveWorkspaceDirectory(string? projectDirectory, ManagedAgentRecord agent, bool requireExists = true)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (agent.AgentType != AgentTypes.McsWorkflow)
        {
            return ResolveAgentWorkspaceDirectory(projectDirectory, agent);
        }

        var root = _locator.ResolveProjectDirectory(projectDirectory);

        return _locator.ContainedLocation(root, agent.AgentLocation) is { } located && (!requireExists || Directory.Exists(located))
            ? located
            : throw ManagedAgentException.WorkspaceMissing(agent.AgentId, agent.AgentLocation, root);
    }

    /// <summary>Finds the directory holding an agent's workspace content, when it is still on disk.</summary>
    /// <param name="projectDirectory">The project directory.</param>
    /// <param name="agent">The record whose workspace is sought.</param>
    /// <returns>The agent workspace directory, or <see langword="null"/> when it no longer exists.</returns>
    public string? FindAgentWorkspaceDirectory(string? projectDirectory, ManagedAgentRecord agent) => _locator.AgentWorkspaceDirectory(projectDirectory, agent.AgentLocation);

    /// <summary>Resolves the component-collection workspaces an agent's workspace declares.</summary>
    /// <param name="projectDirectory">The project root the collections must stay beneath.</param>
    /// <param name="agentWorkspaceDirectory">The directory holding the agent's workspace.</param>
    /// <returns>The collection directories that exist, and the declarations that resolve to nothing.</returns>
    public (IReadOnlyList<string> Found, IReadOnlyList<string> Missing) ResolveComponentCollectionDirectories(string? projectDirectory, string agentWorkspaceDirectory) => _locator.ComponentCollectionDirectories(agentWorkspaceDirectory, projectDirectory);

    /// <summary>The schema name an agent deploys under, preferring the value the record carries.</summary>
    /// <param name="agent">The agent record.</param>
    /// <param name="projectSchemaName">The schema name the workspace declares, when it is available.</param>
    /// <returns>The schema name, or <see langword="null"/> when neither source has one.</returns>
    public static string? McsSchemaNameOf(ManagedAgentRecord agent, string? projectSchemaName) => string.IsNullOrWhiteSpace(agent.McsSchemaName) ? projectSchemaName : agent.McsSchemaName;

    /// <summary>Resolves an agent by identifier, then by name.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <param name="agentId">An explicit record identifier, which wins.</param>
    /// <param name="agentName">A record name, used when no identifier was supplied.</param>
    /// <returns>The matching record.</returns>
    public ManagedAgentRecord ResolveAgent(string projectDirectory, string? agentId, string? agentName)
    {
        if (!_locator.ConfigExists(projectDirectory))
        {
            throw ManagedAgentException.ConfigNotFound(_locator.ConfigFilePath(projectDirectory));
        }

        var agents = _agents.List(projectDirectory);
        var matches = Matching(agents, agentId, agentName);

        if (!string.IsNullOrWhiteSpace(agentId))
        {
            return (matches.Count > 0 ? matches[0] : null)
                ?? throw (agents.Count == 1
                    ? ManagedAgentException.AgentIdConflictsWithProject(agentId.Trim(), agents[0].AgentId, agents[0].DisplayName)
                    : ManagedAgentException.AgentNotFound(agentId.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(agentName))
        {
            return matches.Count switch
            {
                1 => matches[0],
                0 => throw ManagedAgentException.AgentNotFound(agentName.Trim()),
                _ => throw ManagedAgentException.AgentAmbiguous(matches.Select(agent => $"{agent.DisplayName} ({agent.AgentId})")),
            };
        }

        return agents.Count switch
        {
            1 => agents[0],
            0 => throw ManagedAgentException.AgentNotFound("(none supplied)"),
            _ => throw ManagedAgentException.AgentAmbiguous(agents.Select(agent => $"{agent.DisplayName} ({agent.AgentId})")),
        };
    }

    /// <summary>Resolves an agent the way <see cref="ResolveAgent"/> does, reporting nothing rather than throwing.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <param name="agentId">An explicit record identifier, which wins.</param>
    /// <param name="agentName">A record name, used when no identifier was supplied.</param>
    /// <returns>The single matching record, or <see langword="null"/> when none or several match.</returns>
    public ManagedAgentRecord? FindAgent(string projectDirectory, string? agentId, string? agentName) =>
        _locator.ConfigExists(projectDirectory) && Matching(_agents.List(projectDirectory), agentId, agentName) is [var single]
            ? single
            : null;

    private static IReadOnlyList<ManagedAgentRecord> Matching(IReadOnlyList<ManagedAgentRecord> agents, string? agentId, string? agentName) =>
        !string.IsNullOrWhiteSpace(agentId)
            ? [.. agents.Where(agent => string.Equals(agent.AgentId, agentId.Trim(), StringComparison.OrdinalIgnoreCase))]
            : !string.IsNullOrWhiteSpace(agentName)
                ? [.. agents.Where(agent => string.Equals(agent.DisplayName, agentName.Trim(), StringComparison.OrdinalIgnoreCase))]
                : agents;

    /// <summary>Resolves every deployment a selector names, which for an agent alone is all of its deployments.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <param name="agentId">An explicit record identifier.</param>
    /// <param name="agentName">A record name, used when no identifier was supplied.</param>
    /// <param name="deploymentId">An explicit deployment identifier, which selects exactly one.</param>
    /// <param name="deploymentName">A deployment name, which selects exactly one.</param>
    /// <returns>The owning record and the deployments the selectors name, in promotion order.</returns>
    public (ManagedAgentRecord Agent, IReadOnlyList<AgentDeploymentRecord> Deployments) ResolveAgentDeployments(string projectDirectory, string? agentId, string? agentName, string? deploymentId, string? deploymentName)
    {
        if (!string.IsNullOrWhiteSpace(deploymentId) || !string.IsNullOrWhiteSpace(deploymentName))
        {
            var (owner, deployment) = ResolveAgentDeployment(projectDirectory, agentId, agentName, deploymentId, deploymentName);

            return (owner, [deployment]);
        }

        if (!_locator.ConfigExists(projectDirectory))
        {
            throw ManagedAgentException.ConfigNotFound(_locator.ConfigFilePath(projectDirectory));
        }

        var agent = ResolveAgent(projectDirectory, agentId, agentName);
        var owned = ListDeployments(projectDirectory, agent.AgentId);

        return owned.Count > 0 ? (agent, owned) : throw ManagedAgentException.SlotNotFound("(none supplied)");
    }

    /// <summary>Resolves a deployment and the agent that owns it from whichever selectors were supplied.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <param name="agentId">An explicit record identifier.</param>
    /// <param name="agentName">A record name, used when no identifier was supplied.</param>
    /// <param name="deploymentId">An explicit deployment identifier, which wins over every other selector.</param>
    /// <param name="deploymentName">A deployment name.</param>
    /// <returns>The deployment and its owning record.</returns>
    public (ManagedAgentRecord Agent, AgentDeploymentRecord Deployment) ResolveAgentDeployment(string projectDirectory, string? agentId, string? agentName, string? deploymentId, string? deploymentName)
    {
        if (!_locator.ConfigExists(projectDirectory))
        {
            throw ManagedAgentException.ConfigNotFound(_locator.ConfigFilePath(projectDirectory));
        }

        var owner = string.IsNullOrWhiteSpace(agentId) && string.IsNullOrWhiteSpace(agentName) ? null : ResolveAgent(projectDirectory, agentId, agentName);
        var deployment = ResolveDeploymentAcross(projectDirectory, owner, deploymentId, deploymentName);

        return (owner ?? OwnerOf(projectDirectory, deployment), deployment);
    }

    private AgentDeploymentRecord ResolveDeploymentAcross(string projectDirectory, ManagedAgentRecord? owner, string? deploymentId, string? deploymentName)
    {
        var candidates = owner is null ? _deployments.List(projectDirectory) : ListDeployments(projectDirectory, owner.AgentId);

        if (!string.IsNullOrWhiteSpace(deploymentId))
        {
            return candidates.FirstOrDefault(deployment => string.Equals(deployment.DeploymentId, deploymentId.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw ManagedAgentException.SlotNotFound(deploymentId.Trim());
        }

        var selector = string.IsNullOrWhiteSpace(deploymentName) ? null : deploymentName.Trim();
        var matches = selector is null
            ? [.. candidates]
            : candidates.Where(deployment => string.Equals(deployment.DeploymentName, selector, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (matches.Length == 1)
        {
            return matches[0];
        }

        if (matches.Length == 0)
        {
            throw ManagedAgentException.SlotNotFound(selector ?? "(none supplied)");
        }

        var agents = _agents.List(projectDirectory);

        throw ManagedAgentException.SlotAmbiguous(selector, matches.Select(deployment => $"{deployment.DeploymentName} ({deployment.DeploymentId}) of agent {agents.FirstOrDefault(agent => string.Equals(agent.AgentId, deployment.AgentId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? deployment.AgentId}"));
    }

    private ManagedAgentRecord OwnerOf(string projectDirectory, AgentDeploymentRecord deployment) =>
        _agents.List(projectDirectory).FirstOrDefault(agent => string.Equals(agent.AgentId, deployment.AgentId, StringComparison.OrdinalIgnoreCase))
            ?? throw ManagedAgentException.AgentNotFound(deployment.AgentId);

    /// <summary>The agent type a command's selectors name, without failing when they resolve to nothing.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <param name="agentId">An explicit record identifier.</param>
    /// <param name="agentName">A record name.</param>
    /// <param name="deploymentId">A deployment identifier, used when no record selector was supplied.</param>
    /// <param name="deploymentName">A deployment name, used when no record selector was supplied.</param>
    /// <returns>The agent type, or <see langword="null"/> when the selectors name nothing.</returns>
    public string? FindAgentType(string projectDirectory, string? agentId, string? agentName, string? deploymentId, string? deploymentName)
    {
        if (!_locator.ConfigExists(projectDirectory))
        {
            return null;
        }

        var agents = _agents.List(projectDirectory);

        if (!string.IsNullOrWhiteSpace(agentId) || !string.IsNullOrWhiteSpace(agentName))
        {
            return agents.FirstOrDefault(agent =>
                (!string.IsNullOrWhiteSpace(agentId) && string.Equals(agent.AgentId, agentId.Trim(), StringComparison.OrdinalIgnoreCase))
                || (string.IsNullOrWhiteSpace(agentId) && string.Equals(agent.DisplayName, agentName?.Trim(), StringComparison.OrdinalIgnoreCase)))?.AgentType;
        }

        if (!string.IsNullOrWhiteSpace(deploymentId) || !string.IsNullOrWhiteSpace(deploymentName))
        {
            return _deployments.List(projectDirectory).FirstOrDefault(deployment =>
                (!string.IsNullOrWhiteSpace(deploymentId) && string.Equals(deployment.DeploymentId, deploymentId.Trim(), StringComparison.OrdinalIgnoreCase))
                || (string.IsNullOrWhiteSpace(deploymentId) && string.Equals(deployment.DeploymentName, deploymentName?.Trim(), StringComparison.OrdinalIgnoreCase)))?.AgentType;
        }

        return agents.Count == 1 ? agents[0].AgentType : null;
    }

    /// <summary>Requires an agent to be an MCS agent.</summary>
    /// <param name="agent">The record to check.</param>
    /// <param name="verb">The command being run, named in the failure.</param>
    /// <returns>The same record.</returns>
    public static ManagedAgentRecord RequireMcsAgent(ManagedAgentRecord agent, string verb) =>
        agent.AgentType == AgentTypes.McsAgent ? agent : throw ManagedAgentException.NotAnMcsAgent(agent.AgentId, verb);

    /// <summary>Requires an agent to be a standalone workflow.</summary>
    /// <param name="agent">The record to check.</param>
    /// <param name="verb">The command being run, named in the failure.</param>
    /// <returns>The same record.</returns>
    public static ManagedAgentRecord RequireMcsWorkflow(ManagedAgentRecord agent, string verb) =>
        agent.AgentType == AgentTypes.McsWorkflow ? agent : throw ManagedAgentException.NotAnMcsWorkflow(agent.AgentId, verb);

    /// <summary>Requires an agent to be an MCS agent or a standalone workflow.</summary>
    /// <param name="agent">The record to check.</param>
    /// <param name="verb">The command being run, named in the failure.</param>
    /// <returns>The same record.</returns>
    public static ManagedAgentRecord RequireMcsAgentOrWorkflow(ManagedAgentRecord agent, string verb) =>
        AgentTypes.IsMcs(agent.AgentType) ? agent : throw ManagedAgentException.NotAnMcsAgent(agent.AgentId, verb);

    /// <summary>The Dataverse workflow identifier a workflow record carries.</summary>
    /// <param name="agent">The workflow record.</param>
    /// <returns>The workflow identifier.</returns>
    public static Guid RequireWorkflowId(ManagedAgentRecord agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return Guid.TryParse(agent.McsWorkflowId, out var workflowId) && workflowId != Guid.Empty
            ? workflowId
            : throw ManagedAgentException.WorkflowIdMissing(agent.AgentId, agent.DisplayName);
    }

    /// <summary>Lists an agent's deployments, in promotion order.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <param name="agentId">The record whose deployments are listed.</param>
    /// <returns>The deployments, ordered dev, test, prod.</returns>
    public IReadOnlyList<AgentDeploymentRecord> ListDeployments(string projectDirectory, string agentId) =>
        [.. InPromotionOrder(_deployments.List(projectDirectory)
            .Where(deployment => string.Equals(deployment.AgentId, agentId, StringComparison.OrdinalIgnoreCase)))];

    /// <summary>Lists every deployment the project holds, in promotion order.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <returns>The deployments, ordered dev, test, prod.</returns>
    public IReadOnlyList<AgentDeploymentRecord> ListDeployments(string projectDirectory) =>
        [.. InPromotionOrder(_deployments.List(projectDirectory))];

    /// <summary>Lists every agent the project registers.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <returns>The registered agents.</returns>
    public IReadOnlyList<ManagedAgentRecord> ListAgents(string projectDirectory) => _agents.List(projectDirectory);

    private static IOrderedEnumerable<AgentDeploymentRecord> InPromotionOrder(IEnumerable<AgentDeploymentRecord> deployments) =>
        deployments
            .OrderBy(deployment => DeploymentStages.Rank(deployment.DeploymentType))
            .ThenBy(deployment => deployment.DeploymentName, StringComparer.Ordinal);

    /// <summary>Finds one of an agent's deployments, or <see langword="null"/> when no selector was supplied.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <param name="agent">The record the deployment must belong to.</param>
    /// <param name="deploymentId">An explicit deployment identifier, which wins.</param>
    /// <param name="deploymentName">A deployment name, used when no identifier was supplied.</param>
    /// <returns>The matching deployment, or <see langword="null"/>.</returns>
    public AgentDeploymentRecord? FindDeployment(string projectDirectory, ManagedAgentRecord agent, string? deploymentId, string? deploymentName) =>
        string.IsNullOrWhiteSpace(deploymentId) && string.IsNullOrWhiteSpace(deploymentName)
            ? null
            : ResolveDeploymentAcross(projectDirectory, agent, deploymentId, deploymentName);

    /// <summary>The environment a deployment resolves to, which for a dev deployment is the agent's home.</summary>
    /// <param name="agent">The record owning the deployment.</param>
    /// <param name="deployment">The deployment, or <see langword="null"/> for the agent's home.</param>
    /// <returns>The environment identifier.</returns>
    public static string TargetEnvironmentId(ManagedAgentRecord agent, AgentDeploymentRecord? deployment) =>
        deployment is null || string.IsNullOrWhiteSpace(deployment.EnvironmentId) ? agent.EnvironmentId : deployment.EnvironmentId;
}
