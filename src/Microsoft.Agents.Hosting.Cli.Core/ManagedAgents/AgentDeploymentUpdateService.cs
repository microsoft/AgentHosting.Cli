using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent deployment update</c>: renames a deployment record.
///
/// Stage and environment are immutable, so a rename is the only mutation an MCS deployment accepts and
/// nothing in the cloud is touched. The new name follows the same per-agent uniqueness rule the create
/// command applies, which is what keeps <c>--deployment-name</c> unambiguous.
/// </summary>
public sealed class AgentDeploymentUpdateService
{
    private readonly ManagedAgentResolver _resolver;
    private readonly IAgentDeploymentRegistry _deployments;
    private readonly AgentConfigLocator _locator;

    /// <summary>Creates the service.</summary>
    /// <param name="resolver">Resolves the project, agent, and deployment.</param>
    /// <param name="deployments">The deployment table.</param>
    /// <param name="locator">Resolves the configuration path.</param>
    public AgentDeploymentUpdateService(ManagedAgentResolver resolver, IAgentDeploymentRegistry deployments, AgentConfigLocator locator)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    /// <summary>Renames a deployment.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The updated record.</returns>
    public Task<AgentDeploymentUpdateResult> UpdateAsync(AgentDeploymentUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrEmpty(request.EndpointUrl))
        {
            throw new UsageException("--endpoint-url applies to a CodeAgent deployment. An MCS deployment has no custom endpoint.");
        }

        if (string.IsNullOrWhiteSpace(request.NewName))
        {
            throw new UsageException("`msagent deployment update` requires --new-name. Deployment type and environment cannot be changed.");
        }

        var projectDirectory = _resolver.ResolveProjectDirectory(request.Project);
        var (owner, deployment) = _resolver.ResolveAgentDeployment(projectDirectory, request.AgentId, request.AgentName, request.DeploymentId, request.DeploymentName);
        var agent = ManagedAgentResolver.RequireMcsAgentOrWorkflow(owner, "msagent deployment update");
        var newName = request.NewName.Trim();

        var updated = string.Equals(deployment.DeploymentName, newName, StringComparison.Ordinal)
            ? deployment
            : _deployments.Update(projectDirectory, deployment with { DeploymentName = newName });

        return Task.FromResult(new AgentDeploymentUpdateResult
        {
            AgentId = agent.AgentId,
            DeploymentId = updated.DeploymentId,
            PreviousDeploymentName = deployment.DeploymentName,
            DeploymentName = updated.DeploymentName,
            DeploymentType = updated.DeploymentType,
            EnvironmentId = updated.McsEnvironmentId(),
            ConfigPath = _locator.ConfigFilePath(projectDirectory),
        });
    }
}
