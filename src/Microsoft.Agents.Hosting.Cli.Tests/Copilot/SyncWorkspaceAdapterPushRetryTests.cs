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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Push_PassesTheOverwriteDecisionToTheLibrary(bool overwrite)
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(overwrite, synchronizer.LastOverwriteRemoteChanges);
    }

    [Fact]
    public async Task Push_WithoutOverwritingSharedEnvironmentVariables_ApprovesNoOverwriteEvenWhenItOverwritesCloudDrift()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: true, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.NotNull(synchronizer.LastApprovedEnvironmentVariableOverwrites);
        Assert.Empty(synchronizer.LastApprovedEnvironmentVariableOverwrites.Value);
    }

    [Fact]
    public async Task Push_WhenOverwritingSharedEnvironmentVariables_LeavesTheirOverwritesUnrestricted()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: true, CancellationToken.None);

        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.Null(synchronizer.LastApprovedEnvironmentVariableOverwrites);
    }

    [Fact]
    public async Task Push_WhenTheLibraryRefusesToOverwriteSharedEnvironmentVariables_NamesThemAndTheEnvironmentWithoutRetrying()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            PushLocalChangesThrows = new WorkspaceValidationException(
            [
                new WorkspaceDiagnostic("environmentvariables/cr9_shared.mcs.yml", "Environment variable 'cr9_shared' already exists in the target environment, and overwriting it wasn't approved. Reattach again to approve the overwrite, or discard the change to environmentvariables/cr9_shared.mcs.yml.", 0, 0, WorkspaceDiagnosticKind.UnapprovedOverwrite),
                new WorkspaceDiagnostic("environmentvariables/cr9_region.mcs.yml", "Environment variable 'cr9_region' already exists in the target environment, and overwriting it wasn't approved. Reattach again to approve the overwrite, or discard the change to environmentvariables/cr9_region.mcs.yml.", 0, 0, WorkspaceDiagnosticKind.UnapprovedOverwrite),
            ]),
        };
        var retarget = new RecordingRetargetService();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: true, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.SharedEnvironmentVariablesRequireOverwrite, failure.ErrorKind);
        Assert.Equal(17034, failure.ErrorCode);
        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Equal("This deploy would overwrite 2 shared environment variables that env-1 already holds: cr9_shared, cr9_region.", failure.Message);
        Assert.Equal("Re-run `msagent agent deploy` with --overwrite to replace their values in env-1, or discard the local change to environmentvariables/cr9_shared.mcs.yml, environmentvariables/cr9_region.mcs.yml.", failure.Remediation);
        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.Equal(0, synchronizer.UploadKnowledgeFilesCalls);
        Assert.DoesNotContain(nameof(RecordingRetargetService.SyncWorkspaceAsync), retarget.Calls);
    }

    [Fact]
    public async Task Push_WhenTheLibraryRefusesAManagedEnvironmentVariable_StillReportsTheFileAsUnreadable()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            PushLocalChangesThrows = new WorkspaceValidationException([new WorkspaceDiagnostic("environmentvariables/cr9_managed.mcs.yml", "Environment variable 'cr9_managed' is managed in the target environment, so it can't be changed. Discard the change to environmentvariables/cr9_managed.mcs.yml, then try again.", 0, 0, WorkspaceDiagnosticKind.ManagedDependency)]),
        };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceFilesUnreadable, failure.ErrorKind);
        Assert.Contains("environmentvariables/cr9_managed.mcs.yml", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Push_WhenTheLibraryReportsARemoteChange_FailsWithoutRefreshingOrResending(bool overwrite)
    {
        var synchronizer = RefusingOnce();
        var retarget = new RecordingRetargetService();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.RemoteChangedDuringPush, failure.ErrorKind);
        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.DoesNotContain(nameof(RecordingRetargetService.SyncWorkspaceAsync), retarget.Calls);
    }

    [Fact]
    public async Task Push_WithAMissingParentAgent_IsNeverRetried()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer { PushLocalChangesThrows = new InvalidOperationException(MissingParentMessage) };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: true, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.ConnectedAgentNotInTarget, failure.ErrorKind);
        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_ThatSucceeds_NeverRefreshesTheBaseline()
    {
        var retarget = new RecordingRetargetService();

        await Create(new RecordingWorkspaceSynchronizer(), retarget).PushAsync(Workspace(), overwrite: true, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Empty(retarget.Calls);
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

        await Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: true, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
        Assert.Contains(nameof(RecordingRetargetService.SyncWorkspaceAsync), retarget.Calls);
    }

    [Fact]
    public async Task Push_WhenTheReplyCannotBeRead_RefreshesBeforeEveryResend()
    {
        var synchronizer = UnreadableReply(onlyOnce: false);
        var retarget = new RecordingRetargetService();

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: true, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(4, synchronizer.PushLocalChangesCalls);
        Assert.Equal(3, retarget.Calls.Count(call => call == nameof(RecordingRetargetService.SyncWorkspaceAsync)));
    }

    [Fact]
    public async Task Push_WhenTheReplyCannotBeReadEveryTime_GivesUpAfterFourAttempts()
    {
        var synchronizer = UnreadableReply(onlyOnce: false);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: true, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.PushResponseUnreadable, failure.ErrorKind);
        Assert.Equal(4, synchronizer.PushLocalChangesCalls);
        Assert.Contains("could not be read after 4 attempt(s)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_WhenTheReplyCannotBeRead_WaitsLongerBeforeEachResend()
    {
        var delays = new List<TimeSpan>();

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(UnreadableReply(onlyOnce: false), new RecordingRetargetService(), delays).PushAsync(Workspace(), overwrite: true, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6)], delays);
    }

    [Fact]
    public async Task Push_WithoutOverwrite_StillResendsAnUnreadableReply()
    {
        var synchronizer = UnreadableReply(onlyOnce: true);

        await Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_WithAnUnrelatedArgumentFailure_IsNeverRetried()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer { PushLocalChangesThrows = new ArgumentException("schemaName is required") };

        await Assert.ThrowsAsync<ArgumentException>(
            () => Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: true, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

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

        Assert.Empty((await Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None)).Warnings);
        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
        Assert.Equal(1, RefreshCount(retarget));
    }

    [Fact]
    public async Task Push_WithOverwrite_HealsAMissingConnectionReferenceTheSameWay()
    {
        var synchronizer = CopilotTestFixtures.FailingOnce(CopilotTestFixtures.MissingEntity());
        var retarget = new RecordingRetargetService();

        await Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: true, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
        Assert.Equal(1, RefreshCount(retarget));
    }

    [Fact]
    public async Task Push_WhenAConnectionReferenceIsStillMissingAfterTheRefresh_FailsWithMissingConnectionReference()
    {
        var synchronizer = AlwaysFailing(CopilotTestFixtures.MissingEntity());
        var retarget = new RecordingRetargetService();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

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

        await Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
        Assert.Equal(1, RefreshCount(retarget));
    }

    [Fact]
    public async Task Push_WhenTheEntityNameDiffersInCase_StillHeals()
    {
        var synchronizer = CopilotTestFixtures.FailingOnce(CopilotTestFixtures.MissingEntity("ConnectionReference"));

        await Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_WhenAComponentIsMissing_IsNeverRetried()
    {
        var synchronizer = AlwaysFailing(CopilotTestFixtures.MissingEntity("botcomponent"));
        var retarget = new RecordingRetargetService();

        await Assert.ThrowsAsync<DataverseNotFoundException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.Equal(0, RefreshCount(retarget));
    }

    [Fact]
    public async Task Push_WhenOnlyAnotherExceptionInTheChainNamesTheConnectionReference_IsNeverRetried()
    {
        var synchronizer = AlwaysFailing(new InvalidOperationException("connectionreference save failed", CopilotTestFixtures.MissingEntity("botcomponent")));
        var retarget = new RecordingRetargetService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.Equal(0, RefreshCount(retarget));
    }

    [Fact]
    public async Task Push_WhenAConnectionReferenceFailureIsNotANotFound_IsNeverRetried()
    {
        var synchronizer = AlwaysFailing(new InvalidOperationException("connectionreference does not exist"));
        var retarget = new RecordingRetargetService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.Equal(0, RefreshCount(retarget));
    }

    [Theory]
    [InlineData("[0x80072013:ExportKeyInvalidCreate] violates a database constraint on cre98_AgentC1.cr.WX3p-EQ4")]
    [InlineData("[0x80072013:ExportKeyInvalidCreate] Cannot complete the creation of workflow because it violates a database constraint. The violation happens on the key name: Escalate.")]
    public async Task Push_WhenADuplicateKeyNamesNeitherAConnectionReferenceNorAComponent_KeepsTheServiceFailureWithoutRetrying(string message)
    {
        var synchronizer = AlwaysFailing(new InvalidOperationException(message));
        var retarget = new RecordingRetargetService();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(message, failure.Message);
        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.Equal(0, RefreshCount(retarget));
    }

    [Fact]
    public async Task Push_WhenAMissingConnectionReferenceFollowsARefusedWorkflow_StillHeals()
    {
        var synchronizer = CopilotTestFixtures.FailingOnce(CopilotTestFixtures.MissingEntity());
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", ErrorMessage = "the flow definition is invalid" });

        var outcome = await Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
        Assert.Contains(outcome.Warnings, warning => warning.Contains("Escalate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Push_WhenAMissingConnectionReferencePersistsAfterARefusedWorkflow_ReportsTheMissingReference()
    {
        var synchronizer = AlwaysFailing(CopilotTestFixtures.MissingEntity());
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", ErrorMessage = "the flow definition is invalid" });

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.MissingConnectionReference, failure.ErrorKind);
        Assert.Contains("1 component", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_WhenCancelled_IsNeverRetried()
    {
        var synchronizer = AlwaysFailing(new OperationCanceledException());
        var retarget = new RecordingRetargetService();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

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
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

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
            () => Create(synchronizer, retarget).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.DuplicateConnectionReference, failure.ErrorKind);
        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
        Assert.Equal(1, RefreshCount(retarget));
    }

    [Fact]
    public async Task Push_WhenAMissingConnectionReferenceHeals_RunsEveryOneTimeStepOnce()
    {
        using var workspace = new ClassicWorkspace();
        var synchronizer = CopilotTestFixtures.FailingOnce(CopilotTestFixtures.MissingEntity());

        await Create(synchronizer, new RecordingRetargetService(), dataverseClient: new RecordingConnectionReferenceClient()).PushAsync(workspace.Info, overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.DefinitionOnlyProvisionCount);
        Assert.Equal(0, synchronizer.FolderAwareProvisionCount);
        Assert.Equal(1, synchronizer.UpsertWorkflowCalls);
        Assert.Equal(1, synchronizer.UpsertAIPromptsCalls);
        Assert.Equal(1, synchronizer.Calls.Count(call => call == nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync)));
    }
}
