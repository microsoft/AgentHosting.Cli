using System.Collections.Immutable;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncWorkspaceAdapterIntegrityTests
{
    private const string ToolPath = "tools/lookup_order.tool.yml";
    private const string PromptPath = "prompts/summarize/metadata.yml";

    private static CopilotWorkspaceInfo Workspace() => new()
    {
        WorkspacePath = Path.Combine(Path.GetTempPath(), "ah-integrity-tests"),
        AgentId = "11111111-2222-3333-4444-555555555555",
        EnvironmentId = "env-1",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static SyncWorkspaceAdapter Create(RecordingWorkspaceSynchronizer synchronizer) =>
        new(
            synchronizer,
            synchronizer,
            new RecordingRetargetService(),
            new StubSolutionVersionsClient(),
            new StubComponentCollectionClient(),
            new StubOperationContextProvider(),
            new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider()),
            new CopilotChangeMapper());

    private static WorkspaceValidationException Invalid(params WorkspaceDiagnostic[] diagnostics) =>
        new([.. diagnostics]);

    private static WorkspaceDiagnostic Conflict(string path, int line = 12) =>
        new(path, "The file still contains merge conflict markers.", line, 1, WorkspaceDiagnosticKind.MergeConflict);

    [Fact]
    public async Task Push_ChecksTheWorkspaceBeforeTheFirstCloudWrite()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        var checkedAt = synchronizer.Calls.IndexOf(nameof(IWorkspaceSynchronizer.ThrowIfWorkspaceInvalidAsync));
        var firstWriteAt = synchronizer.Calls.IndexOf(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync));

        Assert.True(checkedAt >= 0);
        Assert.True(checkedAt < firstWriteAt);
    }

    [Fact]
    public async Task Push_ChecksTheWorkspaceAgainstTheDefinitionItRead()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.ThrowIfWorkspaceInvalidCalls);
    }

    [Fact]
    public async Task Push_WithOverwrite_StillChecksTheWorkspace()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: true, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.ThrowIfWorkspaceInvalidCalls);
    }

    [Fact]
    public async Task Push_WhenTheWorkspaceIsInvalid_SendsNothingToTheCloud()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            ThrowIfWorkspaceInvalidThrows = Invalid(Conflict(ToolPath)),
        };

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), synchronizer.Calls);
        Assert.Equal(0, synchronizer.PushLocalChangesCalls);
        Assert.Equal(0, synchronizer.UploadKnowledgeFilesCalls);
    }

    [Fact]
    public async Task Push_WhenTheWorkspaceIsInvalid_NamesTheFileAndPosition()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            ThrowIfWorkspaceInvalidThrows = Invalid(Conflict(ToolPath, line: 12)),
        };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceFilesUnreadable, failure.ErrorKind);
        Assert.Contains(ToolPath, failure.Message, StringComparison.Ordinal);
        Assert.Contains("(12,1)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadableWorkspaceFailures_ReportEveryFileOnOneLine()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            ThrowIfWorkspaceInvalidThrows = Invalid(Conflict(ToolPath), Conflict("settings.mcs.yml", line: 3)),
        };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.DoesNotContain(failure.Message, char.IsControl);
        Assert.Equal(failure.Message, TerminalText.Sanitize(failure.Message));
        Assert.Contains(ToolPath, failure.Message, StringComparison.Ordinal);
        Assert.Contains("settings.mcs.yml", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_WhenTheWorkspaceIsInvalid_OverwriteDoesNotBypassTheCheck()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            ThrowIfWorkspaceInvalidThrows = Invalid(Conflict(ToolPath)),
        };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: true, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceFilesUnreadable, failure.ErrorKind);
        Assert.Equal(0, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_ThatWouldOverwriteASharedEnvironmentVariableTheTargetHolds_RefusesBeforeTheFirstCloudWrite()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            DefinitionReturns = CopilotTestFixtures.AgentDefinition("con_agent"),
            LocalChangeSet = CopilotTestFixtures.Writing("cr9_shared"),
            EnvironmentVariableCheck = CopilotTestFixtures.TargetHolding("cr9_shared"),
        };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: true, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.SharedEnvironmentVariablesRequireOverwrite, failure.ErrorKind);
        Assert.Equal("This deploy would overwrite 1 shared environment variable that env-1 already holds: cr9_shared.", failure.Message);
        Assert.Equal("Re-run `msagent agent deploy` with --overwrite to replace its value in env-1, or discard the local change to environmentvariables/cr9_shared.mcs.yml.", failure.Remediation);
        Assert.True(Assert.Single(synchronizer.EnvironmentVariableChecksComparedWithTheCloudCache));
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), synchronizer.Calls);
        Assert.Equal(0, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_WhenTheTargetHoldsNoneOfTheSharedEnvironmentVariablesItWrites_PushesWithoutApprovingAnOverwrite()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            DefinitionReturns = CopilotTestFixtures.AgentDefinition("con_agent"),
            LocalChangeSet = CopilotTestFixtures.Writing("cr9_shared"),
        };

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Single(synchronizer.EnvironmentVariableChecksComparedWithTheCloudCache);
        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.Empty(synchronizer.LastApprovedEnvironmentVariableOverwrites!.Value);
    }

    [Fact]
    public async Task Push_ThatWritesOnlyTheAgentsOwnEnvironmentVariables_DoesNotAskTheTargetWhichItHolds()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            DefinitionReturns = CopilotTestFixtures.AgentDefinition("con_agent"),
            LocalChangeSet = CopilotTestFixtures.Writing("con_agent.greeting"),
            EnvironmentVariableCheck = CopilotTestFixtures.TargetHolding("con_agent.greeting"),
        };

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Empty(synchronizer.EnvironmentVariableChecksComparedWithTheCloudCache);
        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_WhenOverwritingSharedEnvironmentVariables_DoesNotAskTheTargetWhichItHolds()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            DefinitionReturns = CopilotTestFixtures.AgentDefinition("con_agent"),
            LocalChangeSet = CopilotTestFixtures.Writing("cr9_shared"),
            EnvironmentVariableCheck = CopilotTestFixtures.TargetHolding("cr9_shared"),
        };

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: true, CancellationToken.None);

        Assert.Empty(synchronizer.EnvironmentVariableChecksComparedWithTheCloudCache);
        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_WhenTheTargetLacksAnEnvironmentVariableBesideTheSharedOneItWrites_RefusesTheMissingVariableBeforeTheFirstCloudWrite()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            DefinitionReturns = CopilotTestFixtures.AgentDefinition("con_agent"),
            LocalChangeSet = CopilotTestFixtures.Writing("cr9_shared"),
            EnvironmentVariableCheck = new([new WorkspaceDiagnostic("topics/Greeting.mcs.yml", "Environment variable 'cr9_missing' does not exist in the target environment.", 0, 0, WorkspaceDiagnosticKind.MissingDependency)], []),
        };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceFilesUnreadable, failure.ErrorKind);
        Assert.Contains("topics/Greeting.mcs.yml", failure.Message, StringComparison.Ordinal);
        Assert.Contains("'cr9_missing' does not exist in the target environment", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), synchronizer.Calls);
        Assert.Equal(0, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_WhenTheWorkspaceCannotBeRead_IsTranslated()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            ReadWorkspaceDefinitionThrows = Invalid(new WorkspaceDiagnostic(PromptPath, "Duplicate key 'name'.", 4, 3)),
        };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceFilesUnreadable, failure.ErrorKind);
        Assert.Contains(PromptPath, failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, synchronizer.ThrowIfWorkspaceInvalidCalls);
    }

    [Fact]
    public async Task Push_WhenTheComponentPushRefusesToCreateOneSchemaNameTwice_NamesBothFilesWithoutRetryingOrUploading()
    {
        var cause = Invalid(new WorkspaceDiagnostic("knowledge/files/abcmain2.txt", "The component schema name crf9a_agent.file.abcmain2.txt_0Sh is defined by both knowledge/files/abcmain2.txt.mcs.yml and knowledge/files/abcmain2.txt, so a push would create it twice.", 0, 0));
        var synchronizer = new RecordingWorkspaceSynchronizer { PushLocalChangesThrows = cause };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceFilesUnreadable, failure.ErrorKind);
        Assert.Contains("defined by both knowledge/files/abcmain2.txt.mcs.yml and knowledge/files/abcmain2.txt", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(failure.Message, char.IsControl);
        Assert.Same(cause, failure.InnerException);
        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.Equal(0, synchronizer.UploadKnowledgeFilesCalls);
    }

    [Fact]
    public async Task GetChanges_WhenTheWorkspaceCannotBeRead_IsTranslated()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            ReadWorkspaceDefinitionThrows = Invalid(new WorkspaceDiagnostic(PromptPath, "Duplicate key 'name'.", 4, 3)),
        };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).GetChangesAsync(Workspace(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceFilesUnreadable, failure.ErrorKind);
    }

    [Fact]
    public async Task GetChanges_DoesNotCheckTheWorkspaceBecauseNothingIsSent()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).GetChangesAsync(Workspace(), CancellationToken.None);

        Assert.Equal(0, synchronizer.ThrowIfWorkspaceInvalidCalls);
    }

    [Fact]
    public async Task Pull_AsksTheSynchronizerToReportConflicts()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PullAsync(Workspace(), CancellationToken.None);

        Assert.NotNull(synchronizer.LastPullConflicts);
    }

    [Fact]
    public async Task Pull_WhenTheMergeLeavesConflicts_ReportsTheFileAsAWarning()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            PullConflicts = [Conflict(ToolPath, line: 7)],
        };

        var outcome = await Create(synchronizer).PullAsync(Workspace(), CancellationToken.None);

        var warning = Assert.Single(outcome.Warnings);
        Assert.Contains(ToolPath, warning, StringComparison.Ordinal);
        Assert.Contains("(7,1)", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_WhenTheMergeLeavesConflicts_StillAppliesTheRemoteChanges()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            PullConflicts = [Conflict(ToolPath)],
        };

        var outcome = await Create(synchronizer).PullAsync(Workspace(), CancellationToken.None);

        Assert.Equal(1, synchronizer.DownloadKnowledgeFilesCalls);
        Assert.NotNull(outcome.Changes);
    }

    [Fact]
    public async Task Pull_WhenSeveralFilesConflict_ReportsOneWarningPerFile()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            PullConflicts = [Conflict(ToolPath), Conflict("settings.mcs.yml", line: 3)],
        };

        var outcome = await Create(synchronizer).PullAsync(Workspace(), CancellationToken.None);

        Assert.Equal(2, outcome.Warnings.Count);
        Assert.Contains(outcome.Warnings, warning => warning.Contains(ToolPath, StringComparison.Ordinal));
        Assert.Contains(outcome.Warnings, warning => warning.Contains("settings.mcs.yml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pull_ConflictWarnings_CarryNoLineBreaksForTheTerminalToMangle()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            PullConflicts = [Conflict(ToolPath), Conflict("settings.mcs.yml", line: 3)],
        };

        var outcome = await Create(synchronizer).PullAsync(Workspace(), CancellationToken.None);

        Assert.All(outcome.Warnings, warning => Assert.DoesNotContain(warning, char.IsControl));
        Assert.All(outcome.Warnings, warning => Assert.Equal(warning, TerminalText.Sanitize(warning)));
    }

    [Fact]
    public async Task Pull_WithoutConflicts_ReportsNoWarnings()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        var outcome = await Create(synchronizer).PullAsync(Workspace(), CancellationToken.None);

        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public async Task Pull_WhenTheWorkspaceCannotBeRead_IsTranslated()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            ReadWorkspaceDefinitionThrows = Invalid(new WorkspaceDiagnostic(PromptPath, "Duplicate key 'name'.", 4, 3)),
        };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PullAsync(Workspace(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceFilesUnreadable, failure.ErrorKind);
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.PullExistingChangesAsync), synchronizer.Calls);
    }

    [Fact]
    public async Task UnreadableWorkspaceFailures_CarryTheSyncLibraryFailureAsTheCause()
    {
        var cause = Invalid(Conflict(ToolPath));
        var synchronizer = new RecordingWorkspaceSynchronizer { ThrowIfWorkspaceInvalidThrows = cause };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Same(cause, failure.InnerException);
    }

    [Fact]
    public async Task UnreadableWorkspaceFailures_AreReportedAsUsageErrors()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            ThrowIfWorkspaceInvalidThrows = Invalid(Conflict(ToolPath)),
        };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(failure.Remediation));
    }
}
