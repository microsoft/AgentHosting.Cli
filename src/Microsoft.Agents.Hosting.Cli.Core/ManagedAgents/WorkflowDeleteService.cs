using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary><c>msagent agent delete</c> for a standalone workflow, which never touches local files.</summary>
public sealed class WorkflowDeleteService
{
    /// <summary>The name reported for an orphaned home environment that no deployment row names.</summary>
    public const string HomeTargetName = "(home)";

    private readonly ManagedAgentResolver _resolver;
    private readonly WorkflowProjectResolver _workflowResolver;
    private readonly IManagedAgentRegistry _agents;
    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly IWorkflowStorePort _workflows;
    private readonly IConfirmationPrompt _confirmations;

    /// <summary>Creates the service.</summary>
    /// <param name="resolver">Resolves records and deployments from selectors.</param>
    /// <param name="workflowResolver">Resolves the project directory.</param>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="environmentResolver">Resolves the environment the target lives in.</param>
    /// <param name="workflows">Reads which Copilot Studio agents use the cloud workflow, and deletes it.</param>
    /// <param name="confirmations">Confirms the delete with the operator.</param>
    public WorkflowDeleteService(ManagedAgentResolver resolver, WorkflowProjectResolver workflowResolver, IManagedAgentRegistry agents, CopilotEnvironmentResolver environmentResolver, IWorkflowStorePort workflows, IConfirmationPrompt confirmations)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _workflowResolver = workflowResolver ?? throw new ArgumentNullException(nameof(workflowResolver));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _confirmations = confirmations ?? throw new ArgumentNullException(nameof(confirmations));
    }

    /// <summary>Deletes the cloud workflow and every record for it, or reports what that would delete.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What was deleted, and what was left behind.</returns>
    public async Task<ManagedAgentDeleteResult> DeleteAsync(ManagedAgentDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.AllResources)
        {
            throw new UsageException("--all-resources deletes the flows, AI prompts, connection references, and custom connectors of an MCS agent; a standalone workflow has none of its own. Remove --all-resources.");
        }

        var projectDirectory = _workflowResolver.ResolveProjectDirectory(request.Project);
        var (owner, deployment) = ResolveTarget(projectDirectory, request);
        var agent = ManagedAgentResolver.RequireMcsWorkflow(owner, "msagent agent delete");
        var workflowId = ManagedAgentResolver.RequireWorkflowId(agent);
        var deployments = _resolver.ListDeployments(projectDirectory, agent.AgentId);
        var environment = await _environmentResolver.ResolveAsync(ManagedAgentResolver.TargetEnvironmentId(agent, deployment), cancellationToken).ConfigureAwait(false);
        var agentUseWarning = await _workflows.GetAgentUseWarningAsync(environment, workflowId, cancellationToken).ConfigureAwait(false);
        var planned = new ManagedAgentDeleteResult
        {
            Status = ManagedAgentDeleteTargets.DeletedStatus,
            AgentId = agent.AgentId,
            DisplayName = agent.DisplayName,
            McsWorkflowId = workflowId.ToString(),
            EnvironmentId = environment.EnvironmentId,
            DeploymentId = deployment?.DeploymentId,
            RemovedAgentDeployments = deployments.Count,
            OrphanedTargets = ManagedAgentDeleteTargets.OrphanedBy(agent, deployments, environment.EnvironmentId, includeHome: true),
            Warnings = agentUseWarning.Length == 0 ? [] : [agentUseWarning],
        };

        if (await ManagedAgentDeleteTargets.StopBeforeDeletingAsync(_confirmations, request, planned, cancellationToken).ConfigureAwait(false) is { } stopped)
        {
            return stopped;
        }

        var deleted = await _workflows.DeleteAsync(environment, workflowId, cancellationToken).ConfigureAwait(false);

        return planned with
        {
            McsAgentAlreadyAbsent = !deleted,
            RemovedAgentDeployments = ManagedAgentDeleteTargets.RemoveRecords(_agents, projectDirectory, agent, deleted ? environment.EnvironmentId : null, cancellationToken),
        };
    }

    /// <summary>
    /// Resolves the record this delete acts on, and the deployment that named it.
    /// </summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <param name="request">The command input carrying the selectors.</param>
    /// <returns>The record, and the deployment when one was named.</returns>
    private (ManagedAgentRecord Agent, AgentDeploymentRecord? Deployment) ResolveTarget(string projectDirectory, ManagedAgentDeleteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeploymentId) && string.IsNullOrWhiteSpace(request.DeploymentName))
        {
            return (_resolver.ResolveAgent(projectDirectory, request.AgentId, request.AgentName), null);
        }

        var (owner, deployment) = _resolver.ResolveAgentDeployment(projectDirectory, request.AgentId, request.AgentName, request.DeploymentId, request.DeploymentName);

        return (owner, deployment);
    }
}
