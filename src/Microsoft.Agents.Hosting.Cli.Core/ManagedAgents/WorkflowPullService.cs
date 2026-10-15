using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary><c>msagent agent pull</c> for a standalone workflow, which replaces the local copy once nothing unpushed is at stake.</summary>
public sealed class WorkflowPullService
{
    private readonly WorkflowProjectResolver _resolver;
    private readonly IWorkflowStorePort _workflows;
    private readonly WorkflowStateCache _workflowStates;

    /// <summary>Creates the service.</summary>
    /// <param name="resolver">Resolves the record, files and environment the pull reads from.</param>
    /// <param name="workflows">Downloads the workflow.</param>
    /// <param name="workflowStates">Holds what the environment last reported about each workflow.</param>
    public WorkflowPullService(WorkflowProjectResolver resolver, IWorkflowStorePort workflows, WorkflowStateCache workflowStates)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _workflowStates = workflowStates ?? throw new ArgumentNullException(nameof(workflowStates));
    }

    /// <summary>Downloads the workflow, replacing whatever is on disk once nothing unpushed is at stake.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the pull applied.</returns>
    public async Task<ManagedAgentPullResult> PullAsync(ManagedAgentPullRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (target, _, environment) = await _resolver.ResolveDeploymentAsync(request.Project, request.AgentId, request.AgentName, request.DeploymentId, request.DeploymentName, "msagent agent pull", cancellationToken, requireLocalFiles: false).ConfigureAwait(false);

        Directory.CreateDirectory(target.WorkspaceRoot);

        var downloaded = await _workflows.DownloadAsync(environment, target.WorkflowId, target.WorkspaceRoot, cancellationToken, request.Overwrite).ConfigureAwait(false);

        Record(target, environment.EnvironmentId, downloaded.Workflow);

        return new ManagedAgentPullResult
        {
            Status = downloaded.Changed ? "workflow-pulled" : "up-to-date",
            AgentId = target.Agent.AgentId,
            EnvironmentId = environment.EnvironmentId,
            ProjectDirectory = target.ProjectDirectory,
            Mode = "pull",
            Changes = downloaded.Changed ? [new CopilotChange { Kind = "updated", ComponentType = "workflow", SchemaName = target.WorkflowId.ToString(), DisplayName = downloaded.Workflow.Name }] : [],
            ChangeCount = downloaded.Changed ? 1 : 0,
        };
    }

    /// <summary>
    /// Replaces what the environment last reported with what the download just observed.
    ///
    /// A pull is the newest reading there is, so leaving the cache alone would let a later report
    /// describe the state the workflow held before the pull. A state the cache cannot express is
    /// dropped rather than rounded, so the report says <c>unknown</c> instead of guessing.
    /// </summary>
    private void Record(WorkflowTarget target, string environmentId, WorkflowDocument workflow)
    {
        if (workflow.State is not (WorkflowStateProjection.Activated or WorkflowStateProjection.Draft))
        {
            _workflowStates.Invalidate(target.WorkspaceRoot, environmentId);

            return;
        }

        _workflowStates.Write(target.WorkspaceRoot, environmentId, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [target.WorkflowId.ToString()] = workflow.IsActivated ? WorkflowStateCache.Activated : WorkflowStateCache.Draft,
        });
    }
}
