using System.Collections.Immutable;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

internal static class SyncResponseGuards
{
    /// <summary>Describes every workflow the service refused or left disabled, redacting any raw Dataverse response a refusal quotes.</summary>
    /// <param name="responses">The per-workflow outcomes.</param>
    /// <returns>One warning per workflow that will not run.</returns>
    public static IReadOnlyList<string> DescribeRefusedWorkflows(ImmutableArray<WorkflowResponse> responses) => [.. responses.Where(response => !string.IsNullOrEmpty(response.ErrorMessage)).Select(response => DeployWarningText.WorkflowNotActivated(NameOf(response.WorkflowName), DataverseFailureText.Redact(response.ErrorMessage))).Concat(DescribeDisabledWorkflows(responses))];

    /// <summary>Describes every workflow the service left disabled without reporting an error.</summary>
    /// <param name="responses">The per-workflow outcomes.</param>
    /// <returns>One warning per disabled workflow.</returns>
    private static IEnumerable<string> DescribeDisabledWorkflows(ImmutableArray<WorkflowResponse> responses) =>
        responses
            .Where(response => string.IsNullOrEmpty(response.ErrorMessage) && response.IsDisabled)
            .Select(response => DeployWarningText.WorkflowNotRunning(NameOf(response.WorkflowName)));

    /// <summary>Describes every AI prompt the service refused, redacting any raw Dataverse response a refusal quotes.</summary>
    /// <param name="responses">The per-prompt outcomes.</param>
    /// <returns>One warning per refused prompt.</returns>
    public static IReadOnlyList<string> DescribeRefusedPrompts(ImmutableArray<SyncDataverseClient.AIPromptResponse> responses) => [.. responses.Where(response => !string.IsNullOrEmpty(response.ErrorMessage)).Select(response => DeployWarningText.PromptNotSaved(NameOf(response.PromptName), DataverseFailureText.Redact(response.ErrorMessage)))];

    /// <summary>Describes one finding of a workspace push as the warning the CLI reports for it.</summary>
    /// <param name="diagnostic">The finding the sync library reported.</param>
    /// <returns>The warning text.</returns>
    public static string Describe(WorkspacePushDiagnostic diagnostic) => DeployWarningText.PushFinding(diagnostic.Code, diagnostic.Subject, diagnostic.Detail, diagnostic.Message);

    private static string NameOf(string? name) => string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;
}
