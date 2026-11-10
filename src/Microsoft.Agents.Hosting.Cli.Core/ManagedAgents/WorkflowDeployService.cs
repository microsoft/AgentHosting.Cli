using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary><c>msagent agent deploy</c> for a standalone workflow.</summary>
public sealed class WorkflowDeployService
{
    private readonly WorkflowProjectResolver _resolver;
    private readonly IManagedAgentRegistry _agents;
    private readonly IAgentDeploymentRegistry _deployments;
    private readonly IWorkflowStorePort _workflows;
    private readonly WorkflowActivationReporter _activation;
    private readonly WorkflowStateCache _workflowStates;
    private readonly IWorkflowStateRules _stateRules;
    private readonly ManagedAgentPrincipalResolver _principals;
    private readonly IClock _clock;

    /// <summary>Creates the service.</summary>
    /// <param name="resolver">Resolves the record, files and environment the deploy targets.</param>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="deployments">The deployment table.</param>
    /// <param name="workflows">Uploads the workflow.</param>
    /// <param name="activation">Binds connections and activates what is eligible.</param>
    /// <param name="workflowStates">Holds what the environment last reported about each workflow.</param>
    /// <param name="stateRules">Names each workflow state and the label the portal shows for it.</param>
    /// <param name="principals">Resolves the signed-in operator recorded as the deployer.</param>
    /// <param name="clock">Timestamps the deploy.</param>
    public WorkflowDeployService(WorkflowProjectResolver resolver, IManagedAgentRegistry agents, IAgentDeploymentRegistry deployments, IWorkflowStorePort workflows, WorkflowActivationReporter activation, WorkflowStateCache workflowStates, IWorkflowStateRules stateRules, ManagedAgentPrincipalResolver principals, IClock clock)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _activation = activation ?? throw new ArgumentNullException(nameof(activation));
        _workflowStates = workflowStates ?? throw new ArgumentNullException(nameof(workflowStates));
        _stateRules = stateRules ?? throw new ArgumentNullException(nameof(stateRules));
        _principals = principals ?? throw new ArgumentNullException(nameof(principals));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Uploads the local workflow to a deployment slot.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the deploy did.</returns>
    public async Task<ManagedAgentDeployResult> DeployAsync(ManagedAgentDeployRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.SchemaName))
        {
            throw new UsageException("--schema-name does not apply to a workflow, which is addressed by its workflow id. Remove --schema-name.");
        }

        var (target, deployment, environment) = await _resolver.ResolveDeploymentAsync(request.Project, request.AgentId, request.AgentName, request.DeploymentId, request.DeploymentName, "msagent agent deploy", cancellationToken, requireExplicitDeployment: true).ConfigureAwait(false);

        await RequireWorkflowOwnedAsync(target, deployment, environment, request.Overwrite, cancellationToken).ConfigureAwait(false);

        if (!request.Overwrite && deployment.DeploymentType == DeploymentStages.Dev)
        {
            await _workflows.RequireNoUnpulledChangesAsync(environment, target.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
        }

        _workflowStates.Invalidate(target.WorkspaceRoot, environment.EnvironmentId);

        var upserted = await _workflows.UpsertAsync(environment, target.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
        var activating = upserted.ActivationRequested || request.Publish;
        var activation = activating
            ? await _activation.RunAsync(environment, target.WorkspaceRoot, cancellationToken).ConfigureAwait(false)
            : new WorkflowActivationOutcome();

        var running = activating && activation.Activated.Contains(upserted.Workflow.Name, StringComparer.OrdinalIgnoreCase);
        var observedState = running ? _stateRules.StateOf(true) : upserted.Workflow.State;
        var published = _stateRules.RunningOf(observedState) == true;
        var reportedByActivation = activation.Reported.Count > 0;

        var deployedOn = _clock.UtcNow;
        var deployedBy = await _principals.ResolveAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _deployments.Update(target.ProjectDirectory, deployment with { LastDeployedTime = deployedOn, LastDeployedBy = deployedBy });
            _agents.Update(target.ProjectDirectory, target.Agent with { LastDeployedBy = deployedBy });
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw ManagedAgentException.DeployRecordedNowhere(target.Agent.DisplayName, environment.EnvironmentId, target.WorkflowId.ToString(), deployment.DeploymentName, failure);
        }

        return new ManagedAgentDeployResult
        {
            AgentId = target.Agent.AgentId,
            DisplayName = target.Agent.DisplayName,
            AgentType = target.Agent.AgentType,
            McsWorkflowId = target.WorkflowId.ToString(),
            WorkflowName = upserted.Workflow.Name,
            DeploymentId = deployment.DeploymentId,
            DeploymentName = deployment.DeploymentName,
            DeploymentType = deployment.DeploymentType,
            Action = upserted.Created ? "create" : upserted.Replaced ? "replace" : "update",
            TargetEnvironmentId = environment.EnvironmentId,
            TargetAgentCreated = upserted.Created,
            WorkflowState = _stateRules.PortalLabel(observedState),
            Published = published,
            PublishedOn = published ? deployedOn : null,
            UnboundConnectionCount = reportedByActivation ? activation.UnboundConnectionCount : upserted.UnboundConnectionReferences.Count,
            PushedComponentCount = 1,
            DeployedOn = deployedOn,
            Warnings = [.. DeclaredConnectionReferenceWarnings(upserted.CreatedConnectionReferences), .. PushWarnings(upserted, running), .. activation.Warnings, .. (reportedByActivation ? [] : UnboundConnectionReferenceWarnings(upserted))],
        };
    }

    private async Task RequireWorkflowOwnedAsync(WorkflowTarget target, AgentDeploymentRecord deployment, CopilotEnvironmentContext environment, bool acknowledged, CancellationToken cancellationToken)
    {
        if (UnownedTargetGuard.Applies(target.Agent, deployment, environment.EnvironmentId, acknowledged) && deployment.LastDeployedTime is null && await _workflows.GetAsync(environment, target.WorkflowId, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            throw ManagedAgentException.UnverifiedWorkflowInTarget(existing.Name, target.WorkflowId.ToString(), environment.EnvironmentId);
        }
    }

    private static IEnumerable<string> PushWarnings(WorkflowUpsertOutcome upserted, bool activatedByThisDeploy) =>
        upserted.Warning.Length == 0 || (upserted.DraftedForConnections && activatedByThisDeploy) ? [] : [upserted.Warning];

    private static IEnumerable<string> DeclaredConnectionReferenceWarnings(IReadOnlyList<string> created) =>
        created.Count == 0
            ? []
            : [$"Declared {created.Count} connection reference(s) the environment did not have, so the workflow could land: {string.Join(", ", created)}. They are unbound until `msagent deployment connection update` binds them."];

    private static IEnumerable<string> UnboundConnectionReferenceWarnings(WorkflowUpsertOutcome upserted)
    {
        var unbound = upserted.UnboundConnectionReferences.Except(upserted.CreatedConnectionReferences, StringComparer.OrdinalIgnoreCase).ToArray();

        return unbound.Length == 0 ? [] : [$"{unbound.Length} connection reference(s) have no connection: {string.Join(", ", unbound)}. Run `msagent deployment connection update` to bind them."];
    }
}
