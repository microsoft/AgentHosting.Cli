using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>What activating an agent's drafts after a push achieved.</summary>
public sealed record WorkflowActivationOutcome
{
/// <summary>Warnings counting what is not running, and any refusal the service explained.</summary>
public IReadOnlyList<string> Warnings { get; init; } = [];

/// <summary>Every workflow the pass observed, whatever state it ended in.</summary>
public IReadOnlyList<string> Reported { get; init; } = [];

/// <summary>How many connection references are still unbound after the pass.</summary>
public int UnboundConnectionCount { get; init; }

/// <summary>The workflows the service reports as running.</summary>
public IReadOnlyList<string> Activated { get; init; } = [];
}

/// <summary>
/// Activates the workflows a deploy left as drafts whose connections are all bound.
/// </summary>
public sealed class WorkflowActivationPass
{
    private readonly ICopilotConnectionPort _connections;
    private readonly WorkflowStateCache _workflowStates;
    private readonly ManagedAgentPrincipalResolver _principals;

    /// <summary>Creates the pass.</summary>
    /// <param name="connections">Reads workflow eligibility and writes activation state.</param>
    /// <param name="workflowStates">Records what the service reported.</param>
    /// <param name="principals">Names the identity a connection has to belong to before it is bound without asking.</param>
    public WorkflowActivationPass(ICopilotConnectionPort connections, WorkflowStateCache workflowStates, ManagedAgentPrincipalResolver principals)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _workflowStates = workflowStates ?? throw new ArgumentNullException(nameof(workflowStates));
        _principals = principals ?? throw new ArgumentNullException(nameof(principals));
    }

    /// <summary>Activates every eligible draft, and records what the service reported.</summary>
    /// <param name="environment">The environment the deploy targeted.</param>
    /// <param name="workspaceDirectory">The workspace that was pushed.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>Warnings naming the workflows that are not running, and the workflows now running.</returns>
    public async Task<WorkflowActivationOutcome> RunAsync(CopilotEnvironmentContext environment, string workspaceDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        environment = await _principals.BindUserAsync(environment, cancellationToken).ConfigureAwait(false);

        try
        {
            var snapshot = await _connections.ReadConnectionsAsync(environment, workspaceDirectory, cancellationToken).ConfigureAwait(false);
            var ambiguous = await BindUnambiguousAsync(environment, workspaceDirectory, snapshot, cancellationToken).ConfigureAwait(false);

            if (ambiguous.Bound > 0)
            {
                snapshot = await _connections.ReadConnectionsAsync(environment, workspaceDirectory, cancellationToken).ConfigureAwait(false);
            }

            if (snapshot.Workflows.Count == 0)
            {
                _workflowStates.Invalidate(workspaceDirectory, environment.EnvironmentId);

                return new WorkflowActivationOutcome { Warnings = [.. Summarize(0, ambiguous.Unresolved)], UnboundConnectionCount = ambiguous.Unresolved.Count };
            }

            var eligible = snapshot.Workflows.Where(workflow => workflow.CanEnable).ToArray();

            var activation = eligible.Length == 0
                ? new CopilotWorkflowActivation()
                : await _connections.SetWorkflowStatesAsync(
                    environment,
                    workspaceDirectory,
                    [.. eligible.Select(workflow => new CopilotWorkflowStateChange { WorkflowId = workflow.WorkflowId, Activate = true })],
                    cancellationToken).ConfigureAwait(false);

            Record(environment, workspaceDirectory, snapshot.Workflows, activation);
            return new WorkflowActivationOutcome
            {
                Warnings = [.. Summarize(NotRunning(snapshot.Workflows, activation), ambiguous.Unresolved).Concat(activation.Refused)],
                Reported = [.. snapshot.Workflows.Select(workflow => workflow.DisplayName)],
                UnboundConnectionCount = ambiguous.Unresolved.Count,
                Activated = activation.Activated,
            };
        }
        catch (OperationCanceledException)
        {
            _workflowStates.Invalidate(workspaceDirectory, environment.EnvironmentId);

            throw;
        }
        catch (Exception failure)
        {
            _workflowStates.Invalidate(workspaceDirectory, environment.EnvironmentId);

            return new WorkflowActivationOutcome { Warnings = [$"The workflows could not be activated after the push: {failure.Message} Run `msagent deployment connection update` to retry."] };
        }
    }

    /// <summary>
    /// Binds every unbound reference that exactly one connected connection can satisfy.
    ///
    /// The decision is the planner's, not a copy of it: a reference with several connected candidates
    /// is left alone because a connection is an identity, and a reference whose connections could not
    /// be listed offers no candidate to bind blind.
    /// </summary>
    /// <param name="environment">The environment the deploy targeted.</param>
    /// <param name="workspaceDirectory">The workspace that was pushed.</param>
    /// <param name="snapshot">The references as they stand after the push.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>How many references were bound, and the ones left for the operator.</returns>
    private async Task<(int Bound, IReadOnlyList<ConnectionPlanItem> Unresolved)> BindUnambiguousAsync(CopilotEnvironmentContext environment, string workspaceDirectory, CopilotConnectionSnapshot snapshot, CancellationToken cancellationToken)
    {
        var principal = await _principals.ResolveUsernameAsync(environment, cancellationToken).ConfigureAwait(false);
        var plan = snapshot.References.Select(view => ConnectionReadinessPlanner.Plan(view, principal)).ToArray();
        var bindings = plan
            .Where(item => item.State == ConnectionReadinessPlanner.Bound)
            .Select(item => new CopilotConnectionBinding { LogicalName = item.View.LogicalName, ConnectionId = item.ConnectionId! })
            .ToArray();

        if (bindings.Length > 0)
        {
            await _connections.BindAsync(environment, workspaceDirectory, bindings, cancellationToken).ConfigureAwait(false);
        }

        return (bindings.Length, [.. plan.Where(item => !ConnectionReadinessPlanner.IsResolved(item))]);
    }

    /// <summary>States how much of the agent is not running yet, in one line.</summary>
    /// <param name="workflows">How many workflows are not running.</param>
    /// <param name="unresolved">The references auto-binding left alone.</param>
    /// <returns>One warning, or none when there is nothing left to report.</returns>
    private static IEnumerable<string> Summarize(int workflows, IReadOnlyList<ConnectionPlanItem> unresolved)
    {
        var counts = new List<string>();

        if (workflows > 0)
        {
            counts.Add($"{Count(workflows, "workflow", "workflows")} not running");
        }

        if (unresolved.Count > 0)
        {
            counts.Add($"{Count(unresolved.Count, "connection reference", "connection references")} not bound");
        }

        return counts.Count == 0 ? [] : [$"{string.Join(" and ", counts)}. Run `msagent deployment connection update` to see and fix them."];
    }

    /// <summary>Counts a thing with the verb that agrees with it.</summary>
    /// <param name="count">How many there are.</param>
    /// <param name="singular">What one of them is called.</param>
    /// <param name="plural">What several of them are called.</param>
    /// <returns>The count, the noun, and the verb.</returns>
    private static string Count(int count, string singular, string plural) => count == 1 ? $"1 {singular} is" : $"{count} {plural} are";

    /// <summary>
    /// Counts the workflows the pass did not leave running.
    ///
    /// Counted from the same projection the report uses, so a workflow whose name the service could
    /// not attribute is counted as not running rather than assumed started.
    /// </summary>
    /// <param name="workflows">Every workflow the workspace defines.</param>
    /// <param name="activation">What the service reported.</param>
    /// <returns>How many are not running.</returns>
    private static int NotRunning(IReadOnlyList<CopilotWorkflowView> workflows, CopilotWorkflowActivation activation) =>
        WorkflowStateProjection.FromActivation(workflows, activation).Count(workflow => workflow.State != WorkflowStateProjection.Activated);

    /// <summary>Records what the service reported, and only that.</summary>
    private void Record(CopilotEnvironmentContext environment, string workspaceDirectory, IReadOnlyList<CopilotWorkflowView> workflows, CopilotWorkflowActivation activation) =>
        _workflowStates.Write(workspaceDirectory, environment.EnvironmentId, WorkflowStateProjection.ToCacheEntries(workflows, activation));
}

