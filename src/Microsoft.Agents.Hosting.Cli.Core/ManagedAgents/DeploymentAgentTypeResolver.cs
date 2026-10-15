using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Resolves which agent type a unified <c>msagent deployment</c> verb targets, preferring stored records over supplied options.</summary>
public sealed class DeploymentAgentTypeResolver
{
    private readonly AgentConfigLocator _locator;
    private readonly IAgentDeploymentRegistry _deployments;
    private readonly IManagedAgentRegistry _agents;

    /// <summary>Creates the resolver.</summary>
    /// <param name="locator">Resolves the project directory holding the configuration.</param>
    /// <param name="deployments">The deployment table.</param>
    /// <param name="agents">The managed-agent table.</param>
    public DeploymentAgentTypeResolver(AgentConfigLocator locator, IAgentDeploymentRegistry deployments, IManagedAgentRegistry agents)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    }

    /// <summary>Resolves the agent type a verb acts on.</summary>
    /// <param name="query">The deployment or agent the verb addresses.</param>
    /// <returns>The resolved type and how it was determined.</returns>
    public DeploymentAgentTypeResult Resolve(DeploymentAgentTypeQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var projectDirectory = _locator.ResolveProjectDirectory(query.Project);
        var configured = _locator.ConfigExists(projectDirectory);

        if (configured)
        {
            if (MatchDeployment(projectDirectory, query) is { } deployment)
            {
                return new DeploymentAgentTypeResult { AgentType = deployment.AgentType, Source = DeploymentAgentTypeSource.DeploymentRecord };
            }

            if (MatchAgent(projectDirectory, query) is { } agent)
            {
                return new DeploymentAgentTypeResult { AgentType = agent.AgentType, Source = DeploymentAgentTypeSource.AgentRecord };
            }
        }

        if (query.HasMcsOptions && !query.HasCodeAgentOptions)
        {
            return new DeploymentAgentTypeResult { AgentType = AgentTypes.McsAgent, Source = DeploymentAgentTypeSource.OptionShape };
        }

        if (query.HasCodeAgentOptions && !query.HasMcsOptions)
        {
            return new DeploymentAgentTypeResult { AgentType = AgentTypes.CodeAgent, Source = DeploymentAgentTypeSource.OptionShape };
        }

        return new DeploymentAgentTypeResult
        {
            AgentType = AgentTypes.CodeAgent,
            Source = configured ? DeploymentAgentTypeSource.Unresolved : DeploymentAgentTypeSource.Default,
        };
    }

    private AgentDeploymentRecord? MatchDeployment(string projectDirectory, DeploymentAgentTypeQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.DeploymentId) && string.IsNullOrWhiteSpace(query.DeploymentName))
        {
            return null;
        }

        var deployments = _deployments.List(projectDirectory);

        if (!string.IsNullOrWhiteSpace(query.DeploymentId))
        {
            return deployments.FirstOrDefault(deployment => Matches(deployment.DeploymentId, query.DeploymentId));
        }

        var named = deployments.Where(deployment => Matches(deployment.DeploymentName, query.DeploymentName)).ToArray();

        if (ScopingAgent(projectDirectory, query) is { } owner)
        {
            named = [.. named.Where(deployment => string.Equals(deployment.AgentId, owner.AgentId, StringComparison.OrdinalIgnoreCase))];
        }

        if (named.Length > 1 && named.Select(deployment => deployment.AgentType).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            throw ManagedAgentException.SlotAmbiguous(query.DeploymentName, named.Select(deployment => $"{deployment.DeploymentName} ({deployment.DeploymentId}) of agent {deployment.AgentId}"));
        }

        return named.FirstOrDefault();
    }

    private ManagedAgentRecord? ScopingAgent(string projectDirectory, DeploymentAgentTypeQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.AgentId) && string.IsNullOrWhiteSpace(query.AgentName))
        {
            return null;
        }

        return _agents.List(projectDirectory).FirstOrDefault(agent =>
            Matches(agent.AgentId, query.AgentId)
            || (string.IsNullOrWhiteSpace(query.AgentId) && Matches(agent.DisplayName, query.AgentName)));
    }

    private ManagedAgentRecord? MatchAgent(string projectDirectory, DeploymentAgentTypeQuery query)
    {
        if (ScopingAgent(projectDirectory, query) is { } selected)
        {
            return selected;
        }

        if (!string.IsNullOrWhiteSpace(query.AgentId) || !string.IsNullOrWhiteSpace(query.AgentName))
        {
            return null;
        }

        var agents = _agents.List(projectDirectory);

        return agents.Count == 1 ? agents[0] : null;
    }

    private static bool Matches(string stored, string? supplied) => !string.IsNullOrWhiteSpace(supplied) && string.Equals(stored, supplied.Trim(), StringComparison.OrdinalIgnoreCase);
}
