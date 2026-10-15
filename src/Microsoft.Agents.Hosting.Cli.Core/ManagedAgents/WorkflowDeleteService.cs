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
    /// <param name="workflows">Deletes the cloud workflow.</param>
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

    /// <summary>Deletes the cloud workflow and every record for it.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What was deleted, and what was left behind.</returns>
    public async Task<ManagedAgentDeleteResult> DeleteAsync(ManagedAgentDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projectDirectory = _workflowResolver.ResolveProjectDirectory(request.Project);
        var (owner, selected) = ResolveTarget(projectDirectory, request);
        var agent = ManagedAgentResolver.RequireMcsWorkflow(owner, "msagent agent delete");
        var workflowId = ManagedAgentResolver.RequireWorkflowId(agent);
        var deployment = selected;
        var deployments = _resolver.ListDeployments(projectDirectory, agent.AgentId);
        var environment = await _environmentResolver.ResolveAsync(ManagedAgentResolver.TargetEnvironmentId(agent, deployment), cancellationToken).ConfigureAwait(false);

        var orphaned = OrphanedBy(agent, deployments, environment.EnvironmentId);

        var outcome = await ConfirmationPolicy.ConfirmAsync(_confirmations, request.NonInteractive, Describe(agent, workflowId, environment.EnvironmentId, deployments.Count, orphaned), agent.DisplayName, cancellationToken).ConfigureAwait(false);

        if (outcome != ConfirmationOutcome.Confirmed)
        {
            return new ManagedAgentDeleteResult
            {
                Status = outcome == ConfirmationOutcome.Mismatched ? "name-mismatch" : "cancelled",
                AgentId = agent.AgentId,
                DisplayName = agent.DisplayName,
                McsWorkflowId = workflowId.ToString(),
                EnvironmentId = environment.EnvironmentId,
                DeploymentId = deployment?.DeploymentId,
                OrphanedTargets = orphaned,
            };
        }

        var deleted = await _workflows.DeleteAsync(environment, workflowId, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        int removed;

        try
        {
            removed = _agents.RemoveWithDeployments(projectDirectory, agent.AgentId, agent.EntityTag);
        }
        catch (Exception failure) when (deleted && failure is not OperationCanceledException)
        {
            throw ManagedAgentException.RecordsSurvivedCloudDelete(agent.AgentId, environment.EnvironmentId, failure.Message);
        }

        return new ManagedAgentDeleteResult
        {
            Status = "agent-deleted",
            AgentId = agent.AgentId,
            DisplayName = agent.DisplayName,
            McsWorkflowId = workflowId.ToString(),
            EnvironmentId = environment.EnvironmentId,
            DeploymentId = deployment?.DeploymentId,
            McsAgentAlreadyAbsent = !deleted,
            RemovedAgentDeployments = removed,
            OrphanedTargets = orphaned,
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

    /// <summary>
    /// Names every environment this delete abandons a cloud flow in.
    ///
    /// The deployment rows are not the whole picture: deleting a deployment keeps its cloud flow, so an
    /// agent whose home slot was removed earlier still has a flow in its home environment that no row
    /// names. Dropping the record without reporting it would leave a flow nothing can find again.
    /// </summary>
    /// <param name="agent">The record being deleted.</param>
    /// <param name="deployments">Every deployment the record still has.</param>
    /// <param name="deletedEnvironmentId">The environment this run deletes the flow from.</param>
    /// <returns>One target per abandoned environment, in promotion order with the home slot last.</returns>
    private static ManagedAgentOrphanedTarget[] OrphanedBy(ManagedAgentRecord agent, IReadOnlyList<AgentDeploymentRecord> deployments, string deletedEnvironmentId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { deletedEnvironmentId };
        var orphaned = new List<ManagedAgentOrphanedTarget>();

        foreach (var candidate in deployments)
        {
            var environmentId = ManagedAgentResolver.TargetEnvironmentId(agent, candidate);

            if (seen.Add(environmentId))
            {
                orphaned.Add(new ManagedAgentOrphanedTarget { DeploymentName = candidate.DeploymentName, EnvironmentId = environmentId });
            }
        }

        if (!string.IsNullOrWhiteSpace(agent.EnvironmentId) && seen.Add(agent.EnvironmentId))
        {
            orphaned.Add(new ManagedAgentOrphanedTarget { DeploymentName = HomeTargetName, EnvironmentId = agent.EnvironmentId });
        }

        return [.. orphaned];
    }

    private static string Describe(ManagedAgentRecord agent, Guid workflowId, string environmentId, int deploymentCount, ManagedAgentOrphanedTarget[] orphaned)
    {
        var lines = new List<string>
        {
            "About to delete:",
            $"  workflow     {agent.DisplayName} ({agent.AgentId})",
            $"  cloud flow   {workflowId} in {environmentId}",
            $"  records      1 ManagedAgent + {deploymentCount} AgentDeployment row(s)",
        };

        if (orphaned.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add("  orphaned by this delete:");
            lines.AddRange(orphaned.Select(orphan => orphan.DeploymentName == HomeTargetName
                ? $"    {orphan.EnvironmentId}   (the agent's home environment, which no deployment names)"
                : $"    {orphan.EnvironmentId}   (deployment \"{orphan.DeploymentName}\")"));
        }

        lines.Add(string.Empty);
        lines.Add("This deletes remote resources. Local files are not touched.");

        return string.Join(Environment.NewLine, lines);
    }
}
