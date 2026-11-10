using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Platform.Content.Exceptions;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncWorkspaceAdapterPushRetryTests
{
    private const string UnreadableReplyMessage = "Improper response, not implemented";

    private static CopilotWorkspaceInfo Workspace() => new()
    {
        WorkspacePath = Path.Combine(Path.GetTempPath(), "ah-push-retry-tests"),
        AgentId = "11111111-2222-3333-4444-555555555555",
        EnvironmentId = "env-1",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static SyncWorkspaceAdapter Create(RecordingWorkspaceSynchronizer synchronizer, RecordingRetargetService retarget, ISyncDataverseClient? dataverseClient = null) => CopilotTestFixtures.WorkspaceAdapter(synchronizer, retarget, dataverseClient);

    private static RecordingWorkspaceSynchronizer RefusingOnce() => CopilotTestFixtures.FailingOnce(CopilotTestFixtures.RemoteChanged());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Push_PassesTheOverwriteDecisionToTheLibrary(bool overwrite)
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite, CancellationToken.None);

        Assert.Equal(overwrite, synchronizer.LastOverwriteRemoteChanges);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Push_WhenTheLibraryReportsARemoteChange_FailsWithoutRefreshingOrResending(bool overwrite)
    {
        var synchronizer = RefusingOnce();
        var retarget = new RecordingRetargetService();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.RemoteChangedDuringPush, failure.ErrorKind);
        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.DoesNotContain(nameof(RecordingRetargetService.SyncWorkspaceAsync), retarget.Calls);
    }

    [Fact]
    public async Task Push_WithAMissingParentAgent_IsNeverRetried()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer { PushLocalChangesThrows = CopilotTestFixtures.ConnectedAgentMissing() };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: true, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.ConnectedAgentNotInTarget, failure.ErrorKind);
        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_WhenTheReplyCannotBeReadEveryTime_GivesUpAfterFourAttempts()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer { PushLocalChangesThrows = new ArgumentException(UnreadableReplyMessage) };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: true, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.PushResponseUnreadable, failure.ErrorKind);
        Assert.Equal(4, synchronizer.PushLocalChangesCalls);
        Assert.Contains("could not be read after 4 attempt(s)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_WhenTheSyncLibraryStopsForAReasonTheCliDoesNotName_PassesItOnUnchanged()
    {
        var stopped = new WorkspacePushException("the push stopped");

        var failure = await Assert.ThrowsAsync<WorkspacePushException>(
            () => Create(new RecordingWorkspaceSynchronizer { PushLocalChangesThrows = stopped }, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Same(stopped, failure);
    }

    [Fact]
    public async Task Push_WhileCopilotStudioIsStillProvisioningTheAgent_PushesNothingAndSaysToRunItAgain()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, new RecordingRetargetService(), dataverseClient: new StubSolutionVersionsClient { ProvisioningStatus = "Provisioning" }).PushAsync(Workspace(), overwrite: true, CancellationToken.None));

        Assert.Equal((Core.Errors.ErrorCodes.CopilotStudio.AgentNotProvisioned, ExitCodes.Failure, CopilotErrorKinds.AgentNotProvisioned), (failure.ErrorCode, failure.ExitCode, failure.ErrorKind));
        Assert.Equal("Copilot Studio was still provisioning the classic bot '11111111-2222-3333-4444-555555555555' after 00:05:00, so nothing was pushed into it. The bot is kept, and a later deploy pushes into the same bot once Copilot Studio finishes provisioning it.", failure.Message);
        Assert.Equal("Wait until Copilot Studio finishes provisioning the agent, then run the same command again; it reuses the same agent.", failure.Remediation);
        Assert.Equal(WorkspacePushErrorCodes.AgentNotProvisioned, Assert.IsType<WorkspacePushException>(failure.InnerException).Code);
        Assert.Equal(0, synchronizer.PushLocalChangesCalls);
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), synchronizer.Calls);
    }

    private static RecordingWorkspaceSynchronizer AlwaysFailing(Exception failure) =>
        new() { PushLocalChangesThrows = failure };

    private static int RefreshCount(RecordingRetargetService retarget) =>
        retarget.Calls.Count(call => call == nameof(RecordingRetargetService.SyncWorkspaceAsync));

    [Fact]
    public async Task Push_WhenAConnectionReferenceIsStillMissingAfterTheRefresh_FailsWithMissingConnectionReference()
    {
        var synchronizer = AlwaysFailing(CopilotTestFixtures.MissingEntity());
        var retarget = new RecordingRetargetService();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.MissingConnectionReference, failure.ErrorKind);
        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
        Assert.Equal(1, RefreshCount(retarget));
        Assert.IsType<DataverseNotFoundException>(failure.InnerException);
    }

    [Fact]
    public async Task Push_WhenAMissingConnectionReferencePersistsAfterARefusedWorkflow_ReportsTheMissingReference()
    {
        var synchronizer = AlwaysFailing(CopilotTestFixtures.MissingEntity());
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", ErrorMessage = "the flow definition is invalid" });

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.MissingConnectionReference, failure.ErrorKind);
        Assert.Contains("1 component", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_WhenCancelled_IsNeverRetried()
    {
        var synchronizer = AlwaysFailing(new OperationCanceledException());
        var retarget = new RecordingRetargetService();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.Equal(0, RefreshCount(retarget));
    }

    [Fact]
    public async Task Push_WhenADuplicateIsFollowedByAMissingConnectionReference_RefreshesOnlyOnce()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.PushLocalChangesFailures.Enqueue(new InvalidOperationException(CopilotTestFixtures.DuplicateConnectionReferenceMessage));
        synchronizer.PushLocalChangesFailures.Enqueue(CopilotTestFixtures.MissingEntity());
        var retarget = new RecordingRetargetService();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.MissingConnectionReference, failure.ErrorKind);
        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
        Assert.Equal(1, RefreshCount(retarget));
    }

    [Fact]
    public async Task Push_WhenAMissingConnectionReferenceIsFollowedByADuplicate_RefreshesOnlyOnce()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.PushLocalChangesFailures.Enqueue(CopilotTestFixtures.MissingEntity());
        synchronizer.PushLocalChangesFailures.Enqueue(new InvalidOperationException(CopilotTestFixtures.DuplicateConnectionReferenceMessage));
        var retarget = new RecordingRetargetService();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.DuplicateConnectionReference, failure.ErrorKind);
        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
        Assert.Equal(1, RefreshCount(retarget));
    }
}
