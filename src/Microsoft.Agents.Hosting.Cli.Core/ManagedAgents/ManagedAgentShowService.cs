using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent agent show</c>: reads the managed-agent and deployment tables out of the project's
/// configuration.
/// </summary>
public sealed class ManagedAgentShowService
{
    private readonly IManagedAgentRegistry _agents;
    private readonly IAgentDeploymentRegistry _deployments;
    private readonly AgentConfigLocator _locator;

    /// <summary>Creates the service.</summary>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="deployments">The deployment table.</param>
    /// <param name="locator">Resolves the project and configuration directories.</param>
    public ManagedAgentShowService(IManagedAgentRegistry agents, IAgentDeploymentRegistry deployments, AgentConfigLocator locator)
    {
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    /// <summary>Shows the registered agents and their deployments, or the one a selector names.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The matching records.</returns>
    public Task<ManagedAgentShowResult> ShowAsync(ManagedAgentShowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var projectDirectory = _locator.ResolveProjectDirectory(request.Project);

        if (!_locator.ConfigExists(projectDirectory))
        {
            throw ManagedAgentException.ProjectNotFound(projectDirectory);
        }

        var agents = Select(_agents.List(projectDirectory), request);

        return Task.FromResult(new ManagedAgentShowResult
        {
            ProjectDirectory = projectDirectory,
            ConfigPath = _locator.ConfigFilePath(projectDirectory),
            Agents = agents,
            Deployments = DeploymentsOf(_deployments.List(projectDirectory), agents),
        });
    }

    /// <summary>
    /// Narrows the deployment table to the agents being shown, keeping file order.
    /// </summary>
    private static IReadOnlyList<AgentDeploymentRecord> DeploymentsOf(IReadOnlyList<AgentDeploymentRecord> deployments, IReadOnlyList<ManagedAgentRecord> agents)
    {
        var shown = agents.Select(agent => agent.AgentId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. deployments.Where(deployment => shown.Contains(deployment.AgentId))];
    }

    /// <summary>
    /// Narrows the table to what the selectors name, or returns all of it.
    /// </summary>
    private static IReadOnlyList<ManagedAgentRecord> Select(IReadOnlyList<ManagedAgentRecord> agents, ManagedAgentShowRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.AgentId))
        {
            return agents.FirstOrDefault(agent => string.Equals(agent.AgentId, request.AgentId.Trim(), StringComparison.OrdinalIgnoreCase)) is { } byId
                ? [byId]
                : throw ManagedAgentException.AgentNotFound(request.AgentId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.AgentName))
        {
            var byName = agents.Where(agent => string.Equals(agent.DisplayName, request.AgentName.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();

            return byName.Length > 0 ? byName : throw ManagedAgentException.AgentNotFound(request.AgentName.Trim());
        }

        return agents;
    }
}
