using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Platform.Content.Exceptions;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncWorkspaceAdapterPushRetryTests
{
    private const string RemoteChangedMessage = "The agent was changed in the cloud while you were editing it. Get the latest changes before pushing again.";

    private const string MissingParentMessage = "references a parent agent that could not be created";

    private const string UnreadableReplyMessage = "Improper response, not implemented";

    private static CopilotWorkspaceInfo Workspace() => new()
    {
        WorkspacePath = Path.Combine(Path.GetTempPath(), "ah-push-retry-tests"),
        AgentId = "11111111-2222-3333-4444-555555555555",
        EnvironmentId = "env-1",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static SyncWorkspaceAdapter Create(RecordingWorkspaceSynchronizer synchronizer, RecordingRetargetService retarget, List<TimeSpan>? delays = null, ISyncDataverseClient? dataverseClient = null) =>
        new(
            synchronizer,
            retarget,
            dataverseClient ?? new StubSolutionVersionsClient(),
            new StubComponentCollectionClient(),
            new StubOperationContextProvider(),
            new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider()),
            new CopilotChangeMapper(),
            (interval, _) =>
            {
                delays?.Add(interval);
                return Task.CompletedTask;
            });

    private static RecordingWorkspaceSynchronizer RefusingOnce() => CopilotTestFixtures.FailingOnce(new InvalidOperationException(RemoteChangedMessage));

    [Fact]
    public async Task Push_WithOverwrite_RefreshesTheBaselineAndSendsAgain()
    {
        var synchronizer = RefusingOnce();
        var retarget = new RecordingRetargetService();

        await Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: true, CancellationToken.None);

        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
        Assert.Contains(nameof(RecordingRetargetService.SyncWorkspaceAsync), retarget.Calls);
    }

    [Fact]
    public async Task Push_WithOverwrite_DoesNotUpsertWorkflowsOrPromptsASecondTime()
    {
        var synchronizer = RefusingOnce();

        await Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: true, CancellationToken.None);

        Assert.Equal(1, synchronizer.UpsertWorkflowCalls);
        Assert.Equal(1, synchronizer.UpsertAIPromptsCalls);
    }

    [Fact]
    public async Task Push_WithoutOverwrite_ReportsTheRemoteChangeAndNeverRefreshes()
    {
        var synchronizer = RefusingOnce();
        var retarget = new RecordingRetargetService();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.RemoteChangedDuringPush, failure.ErrorKind);
        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.DoesNotContain(nameof(RecordingRetargetService.SyncWorkspaceAsync), retarget.Calls);
    }

    [Fact]
    public async Task Push_WithOverwrite_GivesUpAfterOneRetry()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer { PushLocalChangesThrows = new InvalidOperationException(RemoteChangedMessage) };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: true, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.RemoteChangedDuringPush, failure.ErrorKind);
        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_WithAMissingParentAgent_IsNeverRetried()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer { PushLocalChangesThrows = new InvalidOperationException(MissingParentMessage) };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: true, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.ConnectedAgentNotInTarget, failure.ErrorKind);
        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_ThatSucceeds_NeverRefreshesTheBaseline()
    {
        var retarget = new RecordingRetargetService();

        await Create(new RecordingWorkspaceSynchronizer(), retarget).PushAsync(Workspace(), overwrite: true, CancellationToken.None);

        Assert.Empty(retarget.Calls);
    }

    [Fact]
    public async Task RefreshingTheBaseline_ReadsTheCloudAndLeavesLocalContentAlone()
    {
        var retarget = new RecordingRetargetService();

        await Create(RefusingOnce(), retarget).PushAsync(Workspace(), overwrite: true, CancellationToken.None);

        Assert.False(retarget.LastUpdateWorkspaceDirectory);
        Assert.Null(retarget.LastChangeToken);
    }

    private static RecordingWorkspaceSynchronizer UnreadableReply(bool onlyOnce) => new()
    {
        PushLocalChangesThrows = new ArgumentException(UnreadableReplyMessage),
        PushLocalChangesThrowsOnlyOnce = onlyOnce,
    };

    [Fact]
    public async Task Push_WhenTheReplyCannotBeRead_RefreshesTheBaselineAndSendsAgain()
    {
        var synchronizer = UnreadableReply(onlyOnce: true);
        var retarget = new RecordingRetargetService();

        await Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: true, CancellationToken.None);

        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
        Assert.Contains(nameof(RecordingRetargetService.SyncWorkspaceAsync), retarget.Calls);
    }

    [Fact]
    public async Task Push_WhenTheReplyCannotBeRead_RefreshesBeforeEveryResend()
    {
        var synchronizer = UnreadableReply(onlyOnce: false);
        var retarget = new RecordingRetargetService();

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: true, CancellationToken.None));

        Assert.Equal(4, synchronizer.PushLocalChangesCalls);
        Assert.Equal(3, retarget.Calls.Count(call => call == nameof(RecordingRetargetService.SyncWorkspaceAsync)));
    }

    [Fact]
    public async Task Push_WhenTheReplyCannotBeReadEveryTime_GivesUpAfterFourAttempts()
    {
        var synchronizer = UnreadableReply(onlyOnce: false);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: true, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.PushResponseUnreadable, failure.ErrorKind);
        Assert.Equal(4, synchronizer.PushLocalChangesCalls);
        Assert.Contains("could not be read after 4 attempt(s)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_WhenTheReplyCannotBeRead_WaitsLongerBeforeEachResend()
    {
        var delays = new List<TimeSpan>();

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(UnreadableReply(onlyOnce: false), new RecordingRetargetService(), delays).PushAsync(Workspace(), overwrite: true, CancellationToken.None));

        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6)], delays);
    }

    [Fact]
    public async Task Push_WithoutOverwrite_StillResendsAnUnreadableReply()
    {
        var synchronizer = UnreadableReply(onlyOnce: true);

        await Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_WithAnUnrelatedArgumentFailure_IsNeverRetried()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer { PushLocalChangesThrows = new ArgumentException("schemaName is required") };

        await Assert.ThrowsAsync<ArgumentException>(
            () => Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: true, CancellationToken.None));

        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
    }

    private static RecordingWorkspaceSynchronizer AlwaysFailing(Exception failure) =>
        new() { PushLocalChangesThrows = failure };

    private static int RefreshCount(RecordingRetargetService retarget) =>
        retarget.Calls.Count(call => call == nameof(RecordingRetargetService.SyncWorkspaceAsync));

    [Fact]
    public async Task Push_WhenTheBaselineNamesAMissingConnectionReference_RefreshesTheBaselineAndSendsAgain()
    {
        var synchronizer = CopilotTestFixtures.FailingOnce(CopilotTestFixtures.MissingEntity());
        var retarget = new RecordingRetargetService();

        Assert.Empty((await Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, CancellationToken.None)).Warnings);
        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
        Assert.Equal(1, RefreshCount(retarget));
    }

    [Fact]
    public async Task Push_WithOverwrite_HealsAMissingConnectionReferenceTheSameWay()
    {
        var synchronizer = CopilotTestFixtures.FailingOnce(CopilotTestFixtures.MissingEntity());
        var retarget = new RecordingRetargetService();

        await Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: true, CancellationToken.None);

        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
        Assert.Equal(1, RefreshCount(retarget));
    }

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
    public async Task Push_WhenTheMissingConnectionReferenceIsWrapped_StillHeals()
    {
        var synchronizer = CopilotTestFixtures.FailingOnce(new InvalidOperationException("save failed", CopilotTestFixtures.MissingEntity()));
        var retarget = new RecordingRetargetService();

        await Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
        Assert.Equal(1, RefreshCount(retarget));
    }

    [Fact]
    public async Task Push_WhenTheEntityNameDiffersInCase_StillHeals()
    {
        var synchronizer = CopilotTestFixtures.FailingOnce(CopilotTestFixtures.MissingEntity("ConnectionReference"));

        await Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_WhenAComponentIsMissing_IsNeverRetried()
    {
        var synchronizer = AlwaysFailing(CopilotTestFixtures.MissingEntity("botcomponent"));
        var retarget = new RecordingRetargetService();

        await Assert.ThrowsAsync<DataverseNotFoundException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.Equal(0, RefreshCount(retarget));
    }

    [Fact]
    public async Task Push_WhenOnlyAnotherExceptionInTheChainNamesTheConnectionReference_IsNeverRetried()
    {
        var synchronizer = AlwaysFailing(new InvalidOperationException("connectionreference save failed", CopilotTestFixtures.MissingEntity("botcomponent")));
        var retarget = new RecordingRetargetService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.Equal(0, RefreshCount(retarget));
    }

    [Fact]
    public async Task Push_WhenAConnectionReferenceFailureIsNotANotFound_IsNeverRetried()
    {
        var synchronizer = AlwaysFailing(new InvalidOperationException("connectionreference does not exist"));
        var retarget = new RecordingRetargetService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.Equal(0, RefreshCount(retarget));
    }

    [Fact]
    public async Task Push_WhenAMissingConnectionReferenceFollowsARefusedWorkflow_StillHeals()
    {
        var synchronizer = CopilotTestFixtures.FailingOnce(CopilotTestFixtures.MissingEntity());
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", ErrorMessage = "the flow definition is invalid" });

        var outcome = await Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
        Assert.Contains(outcome.Warnings, warning => warning.Contains("Escalate", StringComparison.Ordinal));
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

    [Fact]
    public async Task Push_WhenAMissingConnectionReferenceHeals_RunsEveryOneTimeStepOnce()
    {
        using var workspace = new ClassicWorkspace();
        var synchronizer = CopilotTestFixtures.FailingOnce(CopilotTestFixtures.MissingEntity());

        await Create(synchronizer, new RecordingRetargetService(), dataverseClient: new RecordingConnectionReferenceClient()).PushAsync(workspace.Info, overwrite: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.DefinitionOnlyProvisionCount);
        Assert.Equal(0, synchronizer.FolderAwareProvisionCount);
        Assert.Equal(1, synchronizer.UpsertWorkflowCalls);
        Assert.Equal(1, synchronizer.UpsertAIPromptsCalls);
        Assert.Equal(1, synchronizer.Calls.Count(call => call == nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync)));
    }
}
