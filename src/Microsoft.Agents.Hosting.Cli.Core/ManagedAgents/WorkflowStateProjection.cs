using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Projects workflow state between the service's report, the result shape and the cache.
/// </summary>
public static class WorkflowStateProjection
{
    /// <summary>The reported state of a workflow that exists only on disk.</summary>
    public const string NotDeployed = "not deployed";

    /// <summary>Projects what an activation run just observed.</summary>
    /// <param name="workflows">Every workflow the workspace defines.</param>
    /// <param name="activation">What the service reported for this run.</param>
    /// <param name="states">Names each state and the label the portal shows for it.</param>
    /// <returns>One item per workflow, <c>unknown</c> where the service said nothing about it.</returns>
    public static IReadOnlyList<AgentDeploymentWorkflowItem> FromActivation(IReadOnlyList<CopilotWorkflowView> workflows, CopilotWorkflowActivation activation, IWorkflowStateRules states)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(states);

        var identifying = IdentifyingNames(workflows);

        return [.. workflows.Select(workflow => Item(workflow, Attribute(workflow, activation, identifying), states))];
    }

    private static bool? Attribute(CopilotWorkflowView workflow, CopilotWorkflowActivation activation, HashSet<string> identifying)
    {
        if (activation.ObservedById.TryGetValue(workflow.WorkflowId, out var running))
        {
            return running;
        }

        return identifying.Contains(workflow.DisplayName) && activation.Observed.TryGetValue(workflow.DisplayName, out var named) ? named : workflow.IsActivatedLocally;
    }

    /// <summary>The display names that belong to exactly one workflow in the workspace.</summary>
    /// <param name="workflows">Every workflow the workspace defines.</param>
    /// <returns>The names that identify a workflow on their own.</returns>
    private static HashSet<string> IdentifyingNames(IReadOnlyList<CopilotWorkflowView> workflows) =>
        workflows
            .GroupBy(workflow => workflow.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Projects what was last observed in an environment.</summary>
    /// <param name="workflows">Every workflow the workspace defines.</param>
    /// <param name="observed">The cached states for the environment being reported on.</param>
    /// <param name="states">Names each state and the label the portal shows for it.</param>
    /// <returns>One item per workflow, falling back to the workspace's own record.</returns>
    public static IReadOnlyList<AgentDeploymentWorkflowItem> FromCache(IReadOnlyList<CopilotWorkflowView> workflows, IReadOnlyDictionary<string, int> observed, IWorkflowStateRules states)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(states);

        return [.. workflows.Select(workflow => Item(workflow, observed.TryGetValue(workflow.WorkflowId, out var state) ? state == WorkflowStateCache.Activated : workflow.IsActivatedLocally, states))];
    }

    /// <summary>
    /// Reduces what the service reported to the cache's shape, keyed by the identifier it assigned.
    /// </summary>
    /// <param name="workflows">Every workflow the workspace defines.</param>
    /// <param name="activation">What the service reported for this run.</param>
    /// <returns>Workflow id to the cache's state value, for the workflows the service settled.</returns>
    public static IReadOnlyDictionary<string, int> ToCacheEntries(IReadOnlyList<CopilotWorkflowView> workflows, CopilotWorkflowActivation activation)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(activation);

        var identifying = IdentifyingNames(workflows);
        var entries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var workflow in workflows.Where(workflow => !string.IsNullOrWhiteSpace(workflow.WorkflowId)))
        {
            if (activation.ObservedById.TryGetValue(workflow.WorkflowId, out var running)
                || (identifying.Contains(workflow.DisplayName) && activation.Observed.TryGetValue(workflow.DisplayName, out running)))
            {
                entries[workflow.WorkflowId] = running ? WorkflowStateCache.Activated : WorkflowStateCache.Draft;
            }
        }

        return entries;
    }

    /// <summary>Whether every workflow is known to be running.</summary>
    /// <param name="workflows">The items being reported.</param>
    /// <param name="states">Reads whether each item's state means it runs.</param>
    /// <returns><see langword="true"/> only when every workflow is observed as activated.</returns>
    public static bool AllActivated(IReadOnlyList<AgentDeploymentWorkflowItem> workflows, IWorkflowStateRules states)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(states);

        return workflows.All(workflow => states.RunningOf(workflow.State) == true);
    }

    private static AgentDeploymentWorkflowItem Item(CopilotWorkflowView workflow, bool? running, IWorkflowStateRules states)
    {
        var state = states.StateOf(running);

        return new AgentDeploymentWorkflowItem
        {
            WorkflowId = workflow.WorkflowId,
            DisplayName = workflow.DisplayName,
            FilePath = workflow.FilePath,
            ConnectionReferences = workflow.ConnectionReferences,
            State = state,
            PortalLabel = states.PortalLabel(state),
        };
    }
}
