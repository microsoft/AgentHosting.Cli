using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Restates the sync library's workflow failures as the CLI's own.</summary>
internal static class WorkflowFailureTranslation
{
    /// <summary>Translates one failure.</summary>
    /// <param name="failure">What the library reported.</param>
    /// <returns>The CLI failure naming the same cause.</returns>
    public static ManagedAgentException Translate(StandaloneWorkflowException failure) => failure.Failure switch
    {
        StandaloneWorkflowFailure.NotFound => ManagedAgentException.WorkflowNotFound(failure.Subject ?? string.Empty),
        StandaloneWorkflowFailure.Ambiguous => ManagedAgentException.WorkflowAmbiguous(failure.FolderNames),
        StandaloneWorkflowFailure.MetadataUnreadable => ManagedAgentException.WorkflowMetadataUnreadable(failure.Subject ?? string.Empty, failure.Reason ?? string.Empty),
        StandaloneWorkflowFailure.SourceNotFound => ManagedAgentException.WorkflowSourceNotFound(failure.Subject ?? string.Empty, failure.EnvironmentId ?? string.Empty),
        StandaloneWorkflowFailure.ConnectionsNotReady => ManagedAgentException.WorkflowConnectionsNotReady(failure.Subject ?? string.Empty, failure.EnvironmentId ?? string.Empty, failure.ConnectionReference ?? string.Empty),
        StandaloneWorkflowFailure.ReplaceLostTheCloudCopy => ManagedAgentException.WorkflowReplaceLostTheCloudCopy(failure.Subject ?? string.Empty, failure.EnvironmentId ?? string.Empty, failure.Reason ?? string.Empty, failure.RestoreFailure ?? string.Empty),
        StandaloneWorkflowFailure.ProjectHoldsUnpushedChanges => ManagedAgentException.WorkflowProjectHoldsUnpushedChanges(failure.Subject ?? string.Empty),
        StandaloneWorkflowFailure.WorkspaceHoldsUnpushedChanges => ManagedAgentException.WorkflowLocalChangesWouldBeLost(failure.Subject ?? string.Empty),
        StandaloneWorkflowFailure.EnvironmentHoldsUnpulledChanges => ManagedAgentException.WorkflowCloudChangesWouldBeLost(failure.Subject ?? string.Empty),
        StandaloneWorkflowFailure.EnvironmentHoldsUnpublishedDraft => ManagedAgentException.WorkflowUnpublishedDraft(failure.Subject ?? string.Empty),
        StandaloneWorkflowFailure.InvalidName => ManagedAgentException.WorkflowNameInvalid(failure.Message),
        StandaloneWorkflowFailure.Managed => ManagedAgentException.WorkflowManaged(failure.Message),
        _ => ManagedAgentException.WorkflowPushFailed(failure.Subject ?? string.Empty, failure.EnvironmentId ?? string.Empty, failure.Reason ?? failure.Message),
    };

    /// <summary>Runs an operation, restating any workflow failure it raises.</summary>
    /// <typeparam name="T">What the operation produces.</typeparam>
    /// <param name="operation">The operation to run.</param>
    /// <returns>What the operation produced.</returns>
    public static async Task<T> TranslateAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (StandaloneWorkflowException failure)
        {
            throw Translate(failure);
        }
    }
}
