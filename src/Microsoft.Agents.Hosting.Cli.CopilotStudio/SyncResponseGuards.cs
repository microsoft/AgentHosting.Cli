using System.Collections.Immutable;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Describes the workflows and AI prompts a push did not leave running.</summary>
internal static class SyncResponseGuards
{
    /// <summary>Describes every workflow the service refused or left disabled.</summary>
    /// <param name="responses">The per-workflow outcomes.</param>
    /// <returns>One warning per workflow that will not run.</returns>
    public static IReadOnlyList<string> DescribeRefusedWorkflows(ImmutableArray<WorkflowResponse> responses) =>
        [.. responses
            .Where(response => !string.IsNullOrEmpty(response.ErrorMessage))
            .Select(response => DeployWarningText.WorkflowNotActivated(NameOf(response.WorkflowName), response.ErrorMessage))
            .Concat(DescribeDisabledWorkflows(responses))];

    /// <summary>Describes every workflow the service left disabled without reporting an error.</summary>
    /// <param name="responses">The per-workflow outcomes.</param>
    /// <returns>One warning per disabled workflow.</returns>
    private static IEnumerable<string> DescribeDisabledWorkflows(ImmutableArray<WorkflowResponse> responses) =>
        responses
            .Where(response => string.IsNullOrEmpty(response.ErrorMessage) && response.IsDisabled)
            .Select(response => DeployWarningText.WorkflowNotRunning(NameOf(response.WorkflowName)));

    /// <summary>Describes every AI prompt the service refused.</summary>
    /// <param name="responses">The per-prompt outcomes.</param>
    /// <returns>One warning per refused prompt.</returns>
    public static IReadOnlyList<string> DescribeRefusedPrompts(ImmutableArray<SyncDataverseClient.AIPromptResponse> responses) =>
        [.. responses
            .Where(response => !string.IsNullOrEmpty(response.ErrorMessage))
            .Select(response => $"AI prompt '{NameOf(response.PromptName)}' was not saved: {response.ErrorMessage}")];

    private static string NameOf(string? name) => string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;
}
