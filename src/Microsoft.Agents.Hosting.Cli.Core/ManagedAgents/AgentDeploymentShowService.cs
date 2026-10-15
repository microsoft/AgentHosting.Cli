using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent deployment show</c> for an MCS agent: narrows the agent's deployment table to one row.
/// </summary>
public sealed class AgentDeploymentShowService
{
    private readonly ManagedAgentResolver _resolver;

    /// <summary>Creates the service.</summary>
    /// <param name="resolver">Resolves the deployment from whichever selectors were supplied.</param>
    public AgentDeploymentShowService(ManagedAgentResolver resolver) => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    /// <summary>Reads one deployment.</summary>
    /// <param name="request">The command input.</param>
    /// <returns>The deployment as stored.</returns>
    public AgentDeploymentShowResult Show(AgentDeploymentShowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projectDirectory = _resolver.ResolveProjectDirectory(request.Project);
        var (agent, deployment) = _resolver.ResolveAgentDeployment(projectDirectory, request.AgentId, request.AgentName, request.DeploymentId, request.DeploymentName);

        return new AgentDeploymentShowResult
        {
            AgentId = agent.AgentId,
            AgentDisplayName = agent.DisplayName,
            DeploymentId = deployment.DeploymentId,
            DeploymentName = deployment.DeploymentName,
            DeploymentType = deployment.DeploymentType,
            EnvironmentId = deployment.EnvironmentId,
            HomeEnvironmentId = deployment.HomeEnvironmentId,
            LastDeployedOn = deployment.LastDeployedTime,
            IsEnabled = deployment.IsEnabled,
            IsQuarantined = deployment.IsQuarantined,
            QuarantinedReason = deployment.QuarantinedReason,
        };
    }
}
