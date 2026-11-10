using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

/// <summary>
/// Pins which deploy warnings the activation pass's counts replace. Only a state claim is restated by
/// a count; a service error is not, so it has to survive.
/// </summary>
public sealed class DeployWarningTextTests
{
    [Fact]
    public void AStateClaimIsRestatedByTheCounts()
    {
        Assert.True(DeployWarningText.IsRestatedByWorkflowCounts(DeployWarningText.WorkflowNotRunning("Escalate")));
    }

    [Fact]
    public void AServiceErrorIsNotRestatedByTheCounts()
    {
        Assert.False(DeployWarningText.IsRestatedByWorkflowCounts(
            DeployWarningText.WorkflowNotActivated("Escalate", "Dataverse request failed (400).")));
    }

    [Fact]
    public void AWarningAboutSomethingElseIsNotRestatedByTheCounts()
    {
        Assert.False(DeployWarningText.IsRestatedByWorkflowCounts("AI prompt 'Summarize' was not saved: nope."));
    }

    [Fact]
    public void ANullWarningIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => DeployWarningText.IsRestatedByWorkflowCounts(null!));
    }

    [Fact]
    public void AConnectionReferenceCollisionIsNotRestatedByTheCounts()
    {
        Assert.False(DeployWarningText.IsRestatedByWorkflowCounts(
            "A connection reference this push declares already exists in env-1."));
    }

    [Theory]
    [InlineData(WorkspacePushDiagnosticCodes.WorkflowRefused, "Escalate", "The flow definition is invalid.", "The environment refused the workflow 'Escalate': The flow definition is invalid.", "Workflow 'Escalate' was not activated: The flow definition is invalid.")]
    [InlineData(WorkspacePushDiagnosticCodes.WorkflowRefused, "(unnamed)", "The flow definition is invalid.", "The environment refused the workflow '(unnamed)': The flow definition is invalid.", "Workflow '(unnamed)' was not activated: The flow definition is invalid.")]
    [InlineData(WorkspacePushDiagnosticCodes.WorkflowNotRunning, "Notify", null, "The environment left the workflow 'Notify' disabled.", "Workflow 'Notify' is not running: the service left it disabled.")]
    [InlineData(WorkspacePushDiagnosticCodes.PromptRefused, "HR summary prompt", "The prompt model is unavailable.", "The environment refused the AI prompt 'HR summary prompt': The prompt model is unavailable.", "AI prompt 'HR summary prompt' was not saved: The prompt model is unavailable.")]
    [InlineData(WorkspacePushDiagnosticCodes.ConnectorRefused, "Weather", null, "Failed to push custom connector 'Weather': denied", "Failed to push custom connector 'Weather': denied")]
    [InlineData(AgentSyncDiagnosticCodes.UnpublishedDraftOverwritten, null, null, "The workflow 'Expense approval' (0a1b2c3d-1111-4111-8111-00000000000a) held a draft saved in the designer but not published in env-test; the push overwrote it.", "The workflow 'Expense approval' (0a1b2c3d-1111-4111-8111-00000000000a) held a draft saved in the designer but not published in env-test; the push overwrote it.")]
    [InlineData(AgentSyncDiagnosticCodes.ActivationRefused, null, null, "Workflow 'Escalate' was not activated: its trigger is invalid.", "Workflow 'Escalate' was not activated: its trigger is invalid.")]
    public void APushFinding_IsWordedTheSameInLocalAndAmsMode(string code, string? subject, string? detail, string message, string expected)
    {
        var notice = new AmsMcsOperationNotice { Kind = AmsMcsOperationNoticeKind.PushWarning, Target = subject, Code = code, Detail = detail, Message = message };

        Assert.Equal(expected, SyncResponseGuards.Describe(new WorkspacePushDiagnostic(code, subject, detail, message)));
        Assert.Equal(expected, Assert.Single(McsAmsViews.Notices([notice])).Message);
    }
}
