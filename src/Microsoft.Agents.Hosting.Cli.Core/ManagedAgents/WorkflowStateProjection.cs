using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using System.Diagnostics.CodeAnalysis;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Projects workflow state between the service's report, the result shape and the cache.
/// </summary>
public static class WorkflowStateProjection
{
    /// <summary>The reported state of a workflow the service says is running.</summary>
    public const string Activated = "activated";

    /// <summary>The reported state of a workflow the service says is not running.</summary>
    public const string Draft = "draft";

    /// <summary>The reported state of a workflow nothing has observed in this environment.</summary>
    public const string Unknown = "unknown";

    /// <summary>The reported state of a workflow the service says is suspended.</summary>
    public const string Suspended = "suspended";

    /// <summary>The reported state of a workflow that exists only on disk.</summary>
    public const string NotDeployed = "not deployed";

    /// <summary>The label the Copilot Studio portal shows for a workflow that is running.</summary>
    public const string Published = "published";

    /// <summary>Maps a reported state onto the draft or published label the Copilot Studio portal shows.</summary>
    /// <param name="state">The reported state name.</param>
    /// <returns>The portal label.</returns>
    [return: NotNullIfNotNull(nameof(state))]
    public static string? ToPortalLabel(string? state) => state switch
    {
        null => null,
        Activated => Published,
        Unknown => Unknown,
        _ => Draft,
    };

    /// <summary>Projects a Dataverse state code onto the state names this build reports.</summary>
    /// <param name="stateCode">The <c>statecode</c> the service reported.</param>
    /// <returns>The state name.</returns>
    public static string FromStateCode(int? stateCode) => stateCode switch
    {
        0 => Draft,
        1 => Activated,
        2 => Suspended,
        _ => Unknown,
    };

    /// <summary>Projects what an activation run just observed.</summary>
    /// <param name="workflows">Every workflow the workspace defines.</param>
    /// <param name="activation">What the service reported for this run.</param>
    /// <returns>One item per workflow, <c>unknown</c> where the service said nothing about it.</returns>
    public static IReadOnlyList<AgentDeploymentWorkflowItem> FromActivation(IReadOnlyList<CopilotWorkflowView> workflows, CopilotWorkflowActivation activation)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(activation);

        var identifying = IdentifyingNames(workflows);

        return [.. workflows.Select(workflow => Item(workflow, Attribute(workflow, activation, identifying)))];
    }

    /// <summary>
    /// Reads the observation the service made of one workflow.
    /// </summary>
    /// <param name="workflow">The workflow being reported on.</param>
    /// <param name="activation">What the service reported for this run.</param>
    /// <param name="identifying">The display names that belong to exactly one workflow.</param>
    /// <returns>The state to report.</returns>
    private static string Attribute(CopilotWorkflowView workflow, CopilotWorkflowActivation activation, HashSet<string> identifying)
    {
        if (activation.ObservedById.TryGetValue(workflow.WorkflowId, out var running))
        {
            return running ? Activated : Draft;
        }

        return identifying.Contains(workflow.DisplayName) && activation.Observed.TryGetValue(workflow.DisplayName, out var named)
            ? named ? Activated : Draft
            : Recorded(workflow);
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
    /// <returns>One item per workflow, falling back to the workspace's own record.</returns>
    public static IReadOnlyList<AgentDeploymentWorkflowItem> FromCache(IReadOnlyList<CopilotWorkflowView> workflows, IReadOnlyDictionary<string, int> observed)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(observed);

        return [.. workflows.Select(workflow => Item(workflow, observed.TryGetValue(workflow.WorkflowId, out var state)
            ? state == WorkflowStateCache.Activated ? Activated : Draft
            : Recorded(workflow)))];
    }

    /// <summary>
    /// Falls back to the state the workspace's own copy of the workflow records.
    /// </summary>
    /// <param name="workflow">The workflow as the workspace describes it.</param>
    /// <returns>The state to report.</returns>
    private static string Recorded(CopilotWorkflowView workflow) => workflow.IsActivatedLocally switch
    {
        true => Activated,
        false => Draft,
        null => Unknown,
    };

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
    /// <returns><see langword="true"/> only when every workflow is observed as activated.</returns>
    public static bool AllActivated(IReadOnlyList<AgentDeploymentWorkflowItem> workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);

        return workflows.All(workflow => workflow.State == Activated);
    }

    private static AgentDeploymentWorkflowItem Item(CopilotWorkflowView workflow, string state) => new()
    {
        WorkflowId = workflow.WorkflowId,
        DisplayName = workflow.DisplayName,
        FilePath = workflow.FilePath,
        ConnectionReferences = workflow.ConnectionReferences,
        State = state,
    };
}
