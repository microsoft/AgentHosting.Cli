using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent deployment delete</c>: removes one deployment record, and optionally the agent it targets.
///
/// The cloud agent is deleted first so a failure there leaves the record in place to retry from; an
/// agent that is already gone is treated as cleanup that has nothing left to do. A failure after that
/// point is reported as records surviving a cloud delete, because the remote change cannot be undone.
/// The agent record and every other deployment survive, and no local file is touched beyond the
/// configuration entry.
/// </summary>
public sealed class AgentDeploymentDeleteService
{
    private readonly ManagedAgentResolver _resolver;
    private readonly IManagedAgentRegistry _agents;
    private readonly IAgentDeploymentRegistry _deployments;
    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly ICopilotCatalogPort _catalog;
    private readonly CopilotProjectIdentityReader _projectIdentities;
    private readonly IConfirmationPrompt _confirmations;
    private readonly AgentConfigLocator _locator;

    /// <summary>Creates the service.</summary>
    /// <param name="resolver">Resolves the project, agent, and deployment.</param>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="deployments">The deployment table.</param>
    /// <param name="environmentResolver">Resolves the environment the deployment targets.</param>
    /// <param name="catalog">Resolves and deletes the cloud agent.</param>
    /// <param name="projectIdentities">Reads the schema name the workspace declares.</param>
    /// <param name="confirmations">Confirms the delete with the operator.</param>
    /// <param name="locator">Resolves the configuration path.</param>
    public AgentDeploymentDeleteService(ManagedAgentResolver resolver, IManagedAgentRegistry agents, IAgentDeploymentRegistry deployments, CopilotEnvironmentResolver environmentResolver, ICopilotCatalogPort catalog, CopilotProjectIdentityReader projectIdentities, IConfirmationPrompt confirmations, AgentConfigLocator locator)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _projectIdentities = projectIdentities ?? throw new ArgumentNullException(nameof(projectIdentities));
        _confirmations = confirmations ?? throw new ArgumentNullException(nameof(confirmations));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    /// <summary>Deletes a deployment record, and the agent it targets when asked.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What was deleted.</returns>
    public async Task<AgentDeploymentDeleteResult> DeleteAsync(AgentDeploymentDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projectDirectory = _resolver.ResolveProjectDirectory(request.Project);
        var (owner, deployments) = _resolver.ResolveAgentDeployments(projectDirectory, request.AgentId, request.AgentName, request.DeploymentId, request.DeploymentName);
        var agent = ManagedAgentResolver.RequireMcsAgentOrWorkflow(owner, "msagent deployment delete");

        if (request.DeleteDeployedMcsAgent && agent.AgentType == AgentTypes.McsWorkflow)
        {
            throw new UsageException("--delete-deployed-mcs-agent does not apply to a workflow, which has no agent in the environment. Run `msagent agent delete` to remove the cloud flow.");
        }

        var expectedResponse = deployments.Count == 1 ? deployments[0].DeploymentName : agent.DisplayName;

        var planned = new List<(AgentDeploymentRecord Deployment, string? TargetAgentId)>();

        foreach (var deployment in deployments)
        {
            planned.Add((deployment, request.DeleteDeployedMcsAgent ? await FindTargetAgentIdAsync(projectDirectory, agent, deployment, cancellationToken).ConfigureAwait(false) : null));
        }

        var outcome = await ConfirmationPolicy.ConfirmAsync(_confirmations, request.NonInteractive, Describe(agent, planned, request.DeleteDeployedMcsAgent), expectedResponse, cancellationToken).ConfigureAwait(false);

        if (outcome != ConfirmationOutcome.Confirmed)
        {
            return Result(outcome == ConfirmationOutcome.Mismatched ? "name-mismatch" : "cancelled", agent, planned, request.DeleteDeployedMcsAgent, null);
        }

        foreach (var (deployment, targetAgentId) in planned)
        {
            if (targetAgentId is not null)
            {
                await DeleteTargetAgentOrFailAsync(agent, deployment, targetAgentId, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _deployments.Remove(projectDirectory, deployment.DeploymentId, deployment.EntityTag);
                ForgetDeletedCloudAgent(projectDirectory, agent, targetAgentId);
            }
            catch (Exception failure) when (targetAgentId is not null && failure is not OperationCanceledException)
            {
                throw ManagedAgentException.RecordsSurvivedCloudDelete(agent.AgentId, deployment.McsEnvironmentId(), failure.Message);
            }
        }

        return Result("deployment-deleted", agent, planned, request.DeleteDeployedMcsAgent, _locator.ConfigFilePath(projectDirectory));
    }

    private void ForgetDeletedCloudAgent(string projectDirectory, ManagedAgentRecord agent, string? deletedAgentId)
    {
        if (deletedAgentId is not null && string.Equals(agent.McsAgentId, deletedAgentId, StringComparison.OrdinalIgnoreCase))
        {
            _agents.Update(projectDirectory, agent with { McsAgentId = null });
        }
    }

    private async Task<string?> FindTargetAgentIdAsync(string projectDirectory, ManagedAgentRecord agent, AgentDeploymentRecord deployment, CancellationToken cancellationToken)
    {
        var workspaceDirectory = _resolver.FindAgentWorkspaceDirectory(projectDirectory, agent);

        if (ManagedAgentResolver.McsSchemaNameOf(agent, workspaceDirectory is null ? null : _projectIdentities.Find(workspaceDirectory)?.SchemaName) is not { } schemaName)
        {
            return null;
        }

        return await _catalog.ResolveAgentIdAsync(await _environmentResolver.ResolveAsync(deployment.EnvironmentId, cancellationToken).ConfigureAwait(false), schemaName, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteTargetAgentOrFailAsync(ManagedAgentRecord agent, AgentDeploymentRecord deployment, string targetAgentId, CancellationToken cancellationToken)
    {
        try
        {
            await _catalog.DeleteAgentAsync(await _environmentResolver.ResolveAsync(deployment.EnvironmentId, cancellationToken).ConfigureAwait(false), targetAgentId, cancellationToken).ConfigureAwait(false);
        }
        catch (CopilotStudioException failure) when (failure.ErrorKind == CopilotErrorKinds.AgentNotFound)
        {
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw ManagedAgentException.DeploymentTargetDeleteFailed(agent.DisplayName, deployment.DeploymentName, deployment.McsEnvironmentId(), targetAgentId, failure);
        }
    }

    private static AgentDeploymentDeleteResult Result(string status, ManagedAgentRecord agent, IReadOnlyList<(AgentDeploymentRecord Deployment, string? TargetAgentId)> planned, bool deleteTargetAgent, string? configPath) => new()
    {
        Status = status,
        AgentId = agent.AgentId,
        DisplayName = agent.DisplayName,
        ConfigPath = configPath,
        Deployments = [.. planned.Select(entry => new DeletedDeployment
        {
            DeploymentId = entry.Deployment.DeploymentId,
            DeploymentName = entry.Deployment.DeploymentName,
            DeploymentType = entry.Deployment.DeploymentType,
            EnvironmentId = entry.Deployment.McsEnvironmentId(),
            DeletedMcsAgentId = status == "deployment-deleted" ? entry.TargetAgentId : null,
            McsAgentAlreadyAbsent = status == "deployment-deleted" && deleteTargetAgent && entry.TargetAgentId is null,
        })],
    };

    private static string Describe(ManagedAgentRecord agent, List<(AgentDeploymentRecord Deployment, string? TargetAgentId)> planned, bool deleteTargetAgent)
    {
        var lines = new List<string>
        {
            planned.Count == 1
                ? $"This deletes deployment {planned[0].Deployment.DeploymentName} ({planned[0].Deployment.DeploymentType}) of agent {agent.DisplayName}."
                : $"This deletes all {planned.Count} deployments of agent {agent.DisplayName}.",
        };

        foreach (var (deployment, targetAgentId) in planned)
        {
            lines.Add($"  {deployment.DeploymentName} ({deployment.DeploymentType}) in {deployment.EnvironmentId}");
            lines.Add(deleteTargetAgent
                ? targetAgentId is null
                    ? "    cloud agent  none found there — only the record will be removed"
                    : $"    cloud agent  {targetAgentId} will be deleted from Copilot Studio"
                : "    cloud agent  left in place — pass --delete-deployed-mcs-agent to delete it too");
        }

        lines.Add($"The agent record itself is kept. Type {(planned.Count == 1 ? planned[0].Deployment.DeploymentName : agent.DisplayName)} to confirm.");

        return string.Join(System.Environment.NewLine, lines);
    }
}
