using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent deployment list</c>: lists an agent's deployment slots, in promotion order.
/// </summary>
public sealed class AgentDeploymentListService
{
    private readonly ManagedAgentResolver _resolver;

    /// <summary>Creates the service.</summary>
    /// <param name="resolver">Resolves records and orders their deployments.</param>
    public AgentDeploymentListService(ManagedAgentResolver resolver) => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    /// <summary>Lists an agent's deployments, or every deployment when no agent is named.</summary>
    /// <param name="request">The command input.</param>
    /// <returns>The deployments, ordered dev, test, prod.</returns>
    public AgentDeploymentListResult List(AgentDeploymentListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projectDirectory = _resolver.ResolveProjectDirectory(request.Project);
        var agentId = string.IsNullOrWhiteSpace(request.AgentId)
            ? string.IsNullOrWhiteSpace(request.AgentName) ? null : _resolver.ResolveAgent(projectDirectory, null, request.AgentName).AgentId
            : request.AgentId.Trim();
        var names = _resolver.ListAgents(projectDirectory).ToDictionary(record => record.AgentId, record => record.DisplayName, StringComparer.OrdinalIgnoreCase);
        var deployments = agentId is null
            ? _resolver.ListDeployments(projectDirectory)
            : _resolver.ListDeployments(projectDirectory, agentId);

        var slots = deployments.Select(deployment => new AgentDeploymentListItem
        {
            DeploymentId = deployment.DeploymentId,
            DeploymentName = deployment.DeploymentName,
            DeploymentType = deployment.DeploymentType,
            EnvironmentId = deployment.EnvironmentId,
            LastDeployedOn = deployment.LastDeployedTime,
            AgentId = deployment.AgentId,
            AgentName = names.GetValueOrDefault(deployment.AgentId),
        });

        return new AgentDeploymentListResult
        {
            AgentId = agentId is null ? null
                : deployments.Count > 0 ? deployments[0].AgentId
                : _resolver.ResolveAgent(projectDirectory, agentId, null).AgentId,
            Slots = [.. agentId is null ? slots.OrderBy(slot => slot.AgentName ?? slot.AgentId, StringComparer.OrdinalIgnoreCase) : slots],
        };
    }
}
