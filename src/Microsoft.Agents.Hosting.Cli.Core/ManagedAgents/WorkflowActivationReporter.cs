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

/// <summary>Runs the activation pass that follows a push, records what the service observed and reports what is not running yet.</summary>
public sealed class WorkflowActivationReporter
{
    private readonly ICopilotConnectionPort _connections;
    private readonly WorkflowStateCache _workflowStates;
    private readonly ManagedAgentPrincipalResolver _principals;

    /// <summary>Creates the reporter.</summary>
    /// <param name="connections">Runs the activation pass.</param>
    /// <param name="workflowStates">Records what the service reported.</param>
    /// <param name="principals">Names the identity a connection has to belong to before it is bound without asking.</param>
    public WorkflowActivationReporter(ICopilotConnectionPort connections, WorkflowStateCache workflowStates, ManagedAgentPrincipalResolver principals)
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

        try
        {
            var report = await _connections.ActivateWorkflowsAsync(environment, workspaceDirectory, await _principals.ResolveUsernameAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

            if (report.Workflows.Count == 0)
            {
                _workflowStates.Invalidate(workspaceDirectory, environment.EnvironmentId);

                return new WorkflowActivationOutcome { Warnings = [.. Summarize(0, report.UnboundConnectionCount)], UnboundConnectionCount = report.UnboundConnectionCount };
            }

            _workflowStates.Write(workspaceDirectory, environment.EnvironmentId, report.ObservedById.ToDictionary(observed => observed.Key, observed => observed.Value ? WorkflowStateCache.Activated : WorkflowStateCache.Draft, StringComparer.OrdinalIgnoreCase));

            return new WorkflowActivationOutcome
            {
                Warnings = [.. Summarize(report.NotRunningCount, report.UnboundConnectionCount).Concat(report.Refused)],
                Reported = report.Workflows,
                UnboundConnectionCount = report.UnboundConnectionCount,
                Activated = report.Activated,
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

    private static IEnumerable<string> Summarize(int workflows, int unbound)
    {
        var counts = new List<string>();

        if (workflows > 0)
        {
            counts.Add($"{Count(workflows, "workflow", "workflows")} not running");
        }

        if (unbound > 0)
        {
            counts.Add($"{Count(unbound, "connection reference", "connection references")} not bound");
        }

        return counts.Count == 0 ? [] : [$"{string.Join(" and ", counts)}. Run `msagent deployment connection update` to see and fix them."];
    }

    private static string Count(int count, string singular, string plural) => count == 1 ? $"1 {singular} is" : $"{count} {plural} are";
}