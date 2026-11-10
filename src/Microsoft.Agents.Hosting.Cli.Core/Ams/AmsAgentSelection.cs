using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>An agent an AMS-only command acts on, with its home environment.</summary>
/// <param name="Home">The agent's home environment.</param>
/// <param name="AgentId">The agent.</param>
/// <param name="AgentType">MCSAgent or MCSWorkflow, or null when the command did not read it.</param>
/// <param name="DevDeploymentId">The agent's Dev deployment, or null when the command does not need it.</param>
public sealed record AmsSelectedAgent(PowerPlatformEnvironment Home, string AgentId, string? AgentType, string? DevDeploymentId);

/// <summary>Selects the agent of <c>agent capture</c> and <c>agent operation show</c>: from the project's .config, or by --agent-id and --environment-id outside a project.</summary>
public sealed class AmsAgentSelection
{
    private readonly ManagedAgentResolver _resolver;
    private readonly AmsTargets _targets;
    private readonly IAmsMcsAgentReadTransport _agents;

    /// <summary>Creates the selection.</summary>
    /// <param name="resolver">Selects the agent from the project's .config.</param>
    /// <param name="targets">Resolves the agent's home environment.</param>
    /// <param name="agents">Reads the agent from AMS when it is selected outside a project.</param>
    public AmsAgentSelection(ManagedAgentResolver resolver, AmsTargets targets, IAmsMcsAgentReadTransport agents)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    }

    /// <summary>Selects an agent AMS registered.</summary>
    /// <param name="project">The --project value.</param>
    /// <param name="agentId">The --agent-id value.</param>
    /// <param name="agentName">The --agent-name value.</param>
    /// <param name="environmentId">The --environment-id value, which selects the agent by --agent-id alone.</param>
    /// <param name="command">The command, named in the errors.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    public async Task<AmsSelectedAgent> AgentAsync(string? project, string? agentId, string? agentName, string? environmentId, string command, CancellationToken cancellationToken)
    {
        if (OutsideProject(project, agentId, agentName, environmentId) is { } selected)
        {
            return new(await _targets.HomeAsync(selected.EnvironmentId, cancellationToken).ConfigureAwait(false), selected.AgentId, null, null);
        }

        var (_, agent) = Registered(project, agentId, agentName, command);
        return new(await _targets.HomeAsync(agent.EnvironmentId, cancellationToken).ConfigureAwait(false), agent.AgentId, agent.AgentType, null);
    }

    /// <summary>Selects an agent AMS registered together with its Dev deployment.</summary>
    /// <param name="project">The --project value.</param>
    /// <param name="agentId">The --agent-id value.</param>
    /// <param name="agentName">The --agent-name value.</param>
    /// <param name="environmentId">The --environment-id value, which selects the agent by --agent-id alone and reads its Dev deployment from AMS.</param>
    /// <param name="command">The command, named in the errors.</param>
    /// <param name="cancellationToken">Cancels the resolution and the read.</param>
    public async Task<AmsSelectedAgent> DevDeploymentAsync(string? project, string? agentId, string? agentName, string? environmentId, string command, CancellationToken cancellationToken)
    {
        if (OutsideProject(project, agentId, agentName, environmentId) is { } selected)
        {
            var home = await _targets.HomeAsync(selected.EnvironmentId, cancellationToken).ConfigureAwait(false);
            var record = await _agents.GetAgentAsync(_targets.DataPlane(home), selected.AgentId, cancellationToken).ConfigureAwait(false);
            return new(home, record.AgentId, record.AgentType, record.Mcs?.HomeDeploymentId ?? throw AmsAgentException.NoDevDeployment(record.AgentId, record.DisplayName));
        }

        var (projectDirectory, agent) = Registered(project, agentId, agentName, command);
        var dev = _resolver.ListDeployments(projectDirectory, agent.AgentId).FirstOrDefault(deployment => deployment.DeploymentType == DeploymentStages.Dev) ?? throw AmsAgentException.NoDevDeployment(agent.AgentId, agent.DisplayName);
        return new(await _targets.HomeAsync(agent.EnvironmentId, cancellationToken).ConfigureAwait(false), agent.AgentId, agent.AgentType, dev.DeploymentId);
    }

    private static (string EnvironmentId, string AgentId)? OutsideProject(string? project, string? agentId, string? agentName, string? environmentId)
    {
        if (string.IsNullOrWhiteSpace(environmentId))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(project) || !string.IsNullOrWhiteSpace(agentName))
        {
            throw new UsageException("--environment-id selects the agent by --agent-id outside a project, so it takes neither --project nor --agent-name.");
        }

        return string.IsNullOrWhiteSpace(agentId) ? throw new UsageException("--environment-id needs --agent-id.") : (environmentId.Trim(), agentId.Trim());
    }

    private (string ProjectDirectory, ManagedAgentRecord Agent) Registered(string? project, string? agentId, string? agentName, string command)
    {
        var projectDirectory = _resolver.ResolveProjectDirectory(project);
        var agent = ManagedAgentResolver.RequireMcsAgentOrWorkflow(_resolver.ResolveAgent(projectDirectory, agentId, agentName), command);
        return agent.SourceControlMetadata is null ? throw AmsAgentException.AgentNotRegistered(agent.DisplayName, agent.AgentId, command, projectDirectory) : (projectDirectory, agent);
    }
}
