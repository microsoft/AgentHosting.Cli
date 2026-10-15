namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>The warning texts a deploy produces, declared once so producer and classifier agree.</summary>
public static class DeployWarningText
{
    /// <summary>Opens a warning naming a workflow the service did not leave running.</summary>
    public const string WorkflowPrefix = "Workflow '";

    /// <summary>Marks a workflow warning that only restates a state the activation pass counts.</summary>
    public const string WorkflowNotRunningMarker = "' is not running:";

    /// <summary>The warning for a workflow the service stored but left disabled.</summary>
    /// <param name="workflowName">The workflow the service named.</param>
    /// <returns>The warning text.</returns>
    public static string WorkflowNotRunning(string workflowName) =>
        $"{WorkflowPrefix}{workflowName}{WorkflowNotRunningMarker} the service left it disabled.";

    /// <summary>The warning for a workflow the service refused, carrying its own explanation.</summary>
    /// <param name="workflowName">The workflow the service named.</param>
    /// <param name="detail">The service's description of the refusal.</param>
    /// <returns>The warning text.</returns>
    public static string WorkflowNotActivated(string workflowName, string? detail) =>
        $"{WorkflowPrefix}{workflowName}' was not activated: {detail}";

    /// <summary>
    /// Whether the activation pass's counts already say everything a warning says. Only a state claim
    /// qualifies: a warning carrying a service error is not restated by any count.
    /// </summary>
    /// <param name="warning">The warning to classify.</param>
    /// <returns><see langword="true"/> when the counts supersede it.</returns>
    public static bool IsRestatedByWorkflowCounts(string warning)
    {
        ArgumentNullException.ThrowIfNull(warning);

        return warning.StartsWith(WorkflowPrefix, StringComparison.Ordinal)
            && warning.Contains(WorkflowNotRunningMarker, StringComparison.Ordinal);
    }
}
