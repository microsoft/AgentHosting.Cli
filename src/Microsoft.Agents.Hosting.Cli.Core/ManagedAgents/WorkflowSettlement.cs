using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Remembers what a run has already written, so settling one connector at a time never repeats it.
/// </summary>
public sealed class WorkflowSettlement
{
    private readonly IReadOnlyList<CopilotWorkflowView> _workflows;
    private readonly Dictionary<string, string> _bound = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _started = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _observed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _observedById = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _activated = [];
    private readonly List<string> _refused = [];

    /// <summary>Creates the settlement.</summary>
    /// <param name="workflows">Every workflow the workspace defines.</param>
    public WorkflowSettlement(IReadOnlyList<CopilotWorkflowView> workflows)
    {
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
    }

    /// <summary>Everything the service reported across the run, merged.</summary>
    public CopilotWorkflowActivation Activation => new()
    {
        Activated = [.. _activated.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal)],
        Refused = [.. _refused.Distinct(StringComparer.Ordinal)],
        Observed = _observed,
        ObservedById = _observedById,
    };

    /// <summary>Whether a reference still needs writing to the service.</summary>
    /// <param name="logicalName">The reference.</param>
    /// <param name="connectionId">The connection it should use.</param>
    /// <returns><see langword="true"/> when this run has not already written that pairing.</returns>
    public bool NotYetBound(string logicalName, string connectionId) =>
        !_bound.TryGetValue(logicalName, out var written) || !written.Equals(connectionId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Records the bindings the service accepted.</summary>
    /// <param name="bindings">What was written.</param>
    public void RecordBound(IReadOnlyList<CopilotConnectionBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        foreach (var binding in bindings)
        {
            _bound[binding.LogicalName] = binding.ConnectionId;
        }
    }

    /// <summary>
    /// The workflows whose every reference is now bound and which this run has not yet started.
    /// </summary>
    /// <param name="plan">The decision for each reference.</param>
    /// <returns>One request per workflow that can start now.</returns>
    public CopilotWorkflowStateChange[] NewlyStartable(IReadOnlyList<ConnectionPlanItem> plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var bound = plan
            .Where(ConnectionReadinessPlanner.IsResolved)
            .Select(item => item.View.LogicalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. _workflows
                .Where(workflow => workflow.IsActivatedLocally != true
                    && !_started.Contains(workflow.WorkflowId)
                    && workflow.ConnectionReferences.All(bound.Contains))
                .Select(workflow => new CopilotWorkflowStateChange { WorkflowId = workflow.WorkflowId, Activate = true }),
        ];
    }

    /// <summary>Merges one activation response into what the run has observed.</summary>
    /// <param name="activation">What the service reported.</param>
    /// <param name="requested">The workflows the request covered.</param>
    public void Record(CopilotWorkflowActivation activation, IReadOnlyList<CopilotWorkflowStateChange> requested)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(requested);

        foreach (var change in requested)
        {
            _started.Add(change.WorkflowId);
        }

        _activated.AddRange(activation.Activated);
        _refused.AddRange(activation.Refused);

        foreach (var observation in activation.Observed)
        {
            _observed[observation.Key] = observation.Value;
        }

        foreach (var observation in activation.ObservedById)
        {
            _observedById[observation.Key] = observation.Value;
        }
    }
}
