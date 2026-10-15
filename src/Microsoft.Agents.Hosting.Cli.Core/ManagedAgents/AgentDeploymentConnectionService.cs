using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent deployment update connection</c>: reports a deployment's connection readiness, binds the
/// references that resolve unambiguously, and lets the operator change any binding.
/// </summary>
public sealed class AgentDeploymentConnectionService
{
    private readonly AgentDeploymentConnectionResolver _targets;
    private readonly ICopilotConnectionPort _connections;
    private readonly ConnectionBindingCoordinator _bindings;
    private readonly IConnectionSelectionPrompt _selectionPrompt;
    private readonly WorkflowStateCache _workflowStates;
    private readonly ManagedAgentPrincipalResolver _principals;
    private readonly CoreServicesClusterCategory _cluster;

    /// <summary>Creates the service.</summary>
    /// <param name="targets">Resolves the deployment, environment and workspace the verb acts on.</param>
    /// <param name="connections">Reads references and candidates, and writes bindings.</param>
    /// <param name="bindings">Resolves one connector to a connection and points its references at it.</param>
    /// <param name="selectionPrompt">Asks which connector to resolve, and reports progress.</param>
    /// <param name="workflowStates">Records the workflow states the service reports back.</param>
    /// <param name="principals">Names the identity a connection has to belong to before it is bound without asking.</param>
    /// <param name="cluster">The cluster category the environment belongs to.</param>
    public AgentDeploymentConnectionService(AgentDeploymentConnectionResolver targets, ICopilotConnectionPort connections, ConnectionBindingCoordinator bindings, IConnectionSelectionPrompt selectionPrompt, WorkflowStateCache workflowStates, ManagedAgentPrincipalResolver principals, CoreServicesClusterCategory cluster)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _selectionPrompt = selectionPrompt ?? throw new ArgumentNullException(nameof(selectionPrompt));
        _workflowStates = workflowStates ?? throw new ArgumentNullException(nameof(workflowStates));
        _principals = principals ?? throw new ArgumentNullException(nameof(principals));
        _cluster = cluster;
    }

    /// <summary>Computes readiness and binds what is unambiguous.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Readiness for every reference the agent declares.</returns>
    public async Task<AgentDeploymentConnectionResult> UpdateAsync(AgentDeploymentConnectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await _targets.ResolveAsync(request, "msagent deployment connection update", cancellationToken).ConfigureAwait(false);
        var environment = target.Environment;
        var workspaceDirectory = target.WorkspaceDirectory;

        try
        {
            return await ResolveAndBindAsync(request, target, environment, workspaceDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not UsageException)
        {
            _workflowStates.Invalidate(workspaceDirectory, environment.EnvironmentId);

            throw;
        }
    }

    /// <summary>
    /// Resolves what the run can, writes the bindings, and reports what the service returned.
    /// </summary>
    private async Task<AgentDeploymentConnectionResult> ResolveAndBindAsync(AgentDeploymentConnectionRequest request, AgentDeploymentConnectionTarget target, CopilotEnvironmentContext environment, string workspaceDirectory, CancellationToken cancellationToken)
    {
        var snapshot = await _connections.ReadConnectionsAsync(environment, workspaceDirectory, cancellationToken).ConfigureAwait(false);
        var principal = await _principals.ResolveUsernameAsync(cancellationToken).ConfigureAwait(false);
        var observed = snapshot.References.Select(ConnectionReadinessPlanner.Observe).ToList();
        var selected = ConnectionSelectionScope.Apply(observed, request);
        var plan = Propose(observed, selected, principal);
        var diagnostics = new List<string>();
        var settlement = new WorkflowSettlement(snapshot.Workflows);

        if (!string.IsNullOrWhiteSpace(request.ConnectionId))
        {
            ConnectionBindingCoordinator.BindExplicitly(plan, ConnectionSelectionScope.RequireExactlyOneConnector(plan, selected, request), request.ConnectionId);
            await SettleAsync(plan, selected, settlement, target, environment, workspaceDirectory, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SettleAsync(plan, selected, settlement, target, environment, workspaceDirectory, cancellationToken).ConfigureAwait(false);
            await ResolveInteractivelyAsync(request, plan, selected, settlement, target, environment, workspaceDirectory, diagnostics, cancellationToken).ConfigureAwait(false);
        }

        diagnostics.AddRange(ConnectionReport.UnownedCandidates(selected.Select(index => plan[index])));

        var activation = settlement.Activation;
        var workflows = WorkflowStateProjection.FromActivation(snapshot.Workflows, activation);
        var connectionsReady = ConnectionReadinessPlanner.IsReady(plan);
        var workflowsReady = WorkflowStateProjection.AllActivated(workflows) && activation.Refused.Count == 0;

        _workflowStates.Write(workspaceDirectory, environment.EnvironmentId, WorkflowStateProjection.ToCacheEntries(snapshot.Workflows, activation));

        return new AgentDeploymentConnectionResult
        {
            Status = "connections-updated",
            AgentId = target.Agent.AgentId,
            DeploymentId = target.Deployment.DeploymentId,
            DeploymentName = target.Deployment.DeploymentName,
            EnvironmentId = environment.EnvironmentId,
            ConnectionsReady = connectionsReady,
            WorkflowsReady = workflowsReady,
            Ready = ConnectionReport.IsReady(connectionsReady, workflowsReady),
            Connections = ConnectionReport.Connections(plan, _cluster, environment.EnvironmentId),
            Workflows = workflows,
            ActivatedWorkflows = activation.Activated,
            RefusedWorkflows = activation.Refused,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// Proposes a connection only for the references this run will actually write.
    ///
    /// A reference outside the scope is left as it was observed, because nothing will bind it:
    /// proposing one anyway would count it toward readiness and toward a workflow's eligibility on
    /// the strength of a binding the run never made.
    /// </summary>
    /// <param name="observed">What every reference is, before this run decides anything.</param>
    /// <param name="selected">The references this run was scoped to.</param>
    /// <param name="principal">The signed-in identity a connection must belong to.</param>
    /// <returns>The decision for each reference, in the order given.</returns>
    private static List<ConnectionPlanItem> Propose(IReadOnlyList<ConnectionPlanItem> observed, IReadOnlyList<int> selected, string? principal)
    {
        var scoped = selected.ToHashSet();

        return [.. observed.Select((item, index) => scoped.Contains(index) ? ConnectionReadinessPlanner.Plan(item.View, principal) : item)];
    }

    /// <summary>
    /// Writes the bindings this run has decided and starts whatever they just unblocked.
    ///
    /// Called as each connector is settled rather than once at the end, so a workflow starts the
    /// moment its last reference is bound and the operator sees it happen beside the decision that
    /// caused it, instead of after a silent pause when they leave the menu.
    /// </summary>
    private async Task SettleAsync(List<ConnectionPlanItem> plan, IReadOnlyList<int> selected, WorkflowSettlement settlement, AgentDeploymentConnectionTarget target, CopilotEnvironmentContext environment, string workspaceDirectory, CancellationToken cancellationToken)
    {
        var bindings = selected
            .Select(index => plan[index])
            .Where(ConnectionReadinessPlanner.IsBinding)
            .Where(item => settlement.NotYetBound(item.View.LogicalName, item.ConnectionId!))
            .Select(item => new CopilotConnectionBinding { LogicalName = item.View.LogicalName, ConnectionId = item.ConnectionId! })
            .ToArray();

        if (bindings.Length > 0)
        {
            _selectionPrompt.Report($"Binding {Count(bindings.Length, "connection reference")}…");

            await _connections.BindAsync(environment, workspaceDirectory, bindings, cancellationToken).ConfigureAwait(false);

            settlement.RecordBound(bindings);
        }

        var changes = settlement.NewlyStartable(plan);

        if (changes.Length == 0)
        {
            return;
        }

        _selectionPrompt.Report($"Enabling {Count(changes.Length, "workflow")}…");

        settlement.Record(await SetWorkflowStatesAsync(environment, workspaceDirectory, target.TargetAgentId, changes, cancellationToken).ConfigureAwait(false), changes);
    }

    /// <summary>Asks the service to start workflows, naming the agent when it refuses.</summary>
    private async Task<CopilotWorkflowActivation> SetWorkflowStatesAsync(CopilotEnvironmentContext environment, string workspaceDirectory, string targetAgentId, IReadOnlyList<CopilotWorkflowStateChange> changes, CancellationToken cancellationToken)
    {
        try
        {
            return await _connections.SetWorkflowStatesAsync(environment, workspaceDirectory, changes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw ManagedAgentException.WorkflowActivationFailed(targetAgentId, environment.EnvironmentId, failure);
        }
    }

    /// <summary>Counts a thing for a progress line.</summary>
    /// <param name="count">How many there are.</param>
    /// <param name="noun">What one of them is called.</param>
    /// <returns>The count and the noun, pluralised.</returns>
    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>
    /// Walks the operator through the connectors, one at a time, until they stop.
    ///
    /// The connector is the unit because the catalog lists connections per connector, so every
    /// reference on one faces the identical choice; deciding once and binding the whole group is the
    /// same decision, made once instead of per reference.
    ///
    /// Settled connectors are offered too, so a binding can be changed and not only filled in, and
    /// the loop ends only when the operator says so: having fixed the last unbound reference is no
    /// reason to stop somebody who came to change a bound one.
    /// </summary>
    private async Task ResolveInteractivelyAsync(AgentDeploymentConnectionRequest request, List<ConnectionPlanItem> plan, IReadOnlyList<int> selected, WorkflowSettlement settlement, AgentDeploymentConnectionTarget target, CopilotEnvironmentContext environment, string workspaceDirectory, List<string> diagnostics, CancellationToken cancellationToken)
    {
        var groups = ConnectionSelectionScope.Group(plan, selected, request.ByReference);
        var openBrowser = !request.NonInteractive;

        if (!groups.Any(group => ConnectionSelectionScope.IsSelectable(plan, group)))
        {
            return;
        }

        if (groups.Count == 1)
        {
            await _bindings.ResolveConnectorAsync(plan, groups[0], environment, null, openBrowser, diagnostics, cancellationToken).ConfigureAwait(false);
            await SettleAsync(plan, selected, settlement, target, environment, workspaceDirectory, cancellationToken).ConfigureAwait(false);

            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var chosen = await _selectionPrompt.ChooseConnectorsAsync(ConnectionSelectionScope.Menu(plan, groups), cancellationToken).ConfigureAwait(false);

            if (chosen.Count == 0)
            {
                return;
            }

            foreach (var position in chosen.Where(position => position >= 0 && position < groups.Count))
            {
                await _bindings.ResolveConnectorAsync(plan, groups[position], environment, null, openBrowser, diagnostics, cancellationToken).ConfigureAwait(false);
                await SettleAsync(plan, selected, settlement, target, environment, workspaceDirectory, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
