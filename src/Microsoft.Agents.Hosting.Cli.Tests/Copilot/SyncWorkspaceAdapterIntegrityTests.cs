using System.Collections.Immutable;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncWorkspaceAdapterIntegrityTests : IDisposable
{
    private const string ToolPath = "tools/lookup_order.tool.yml";
    private const string PromptPath = "prompts/summarize/metadata.yml";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ah-integrity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static CopilotWorkspaceInfo Workspace() => new()
    {
        WorkspacePath = Path.Combine(Path.GetTempPath(), "ah-integrity-tests"),
        AgentId = "11111111-2222-3333-4444-555555555555",
        EnvironmentId = "env-1",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static SyncWorkspaceAdapter Create(RecordingWorkspaceSynchronizer synchronizer) => CopilotTestFixtures.WorkspaceAdapter(synchronizer);

    private static WorkspaceValidationException Invalid(params WorkspaceDiagnostic[] diagnostics) =>
        new([.. diagnostics]);

    private static WorkspaceDiagnostic Conflict(string path, int line = 12) =>
        new(path, "The file still contains merge conflict markers.", line, 1, WorkspaceDiagnosticKind.MergeConflict);

    private async Task<RecordingWorkspaceSynchronizer> SyncedAsync(IWorkspaceSynchronizer workspaceValidator, string cloudName, string declaredName)
    {
        AgentScaffolder.Scaffold(_directory, "cli-copilot", "Contoso Helpdesk", "con_contosohelpdesk", "Be helpful.");
        CopilotTestFixtures.DeclareDisplayName(_directory, cloudName);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_directory, ".mcs")).FullName, "botdefinition.json"), CopilotTestFixtures.CloudCacheJson(await CopilotTestFixtures.ReadWorkspaceDefinitionAsync(_directory)));
        CopilotTestFixtures.DeclareDisplayName(_directory, declaredName);

        return new RecordingWorkspaceSynchronizer { DefinitionReturns = await CopilotTestFixtures.ReadWorkspaceDefinitionAsync(_directory), WorkspaceValidator = workspaceValidator };
    }

    [Fact]
    public async Task Push_OfSettingsRenamingTheAgentToANameLongerThanAnAgentCanTake_IsASettingsFileErrorWithSyncsReason_BeforeAnyCloudWrite()
    {
        await using var services = CopilotTestFixtures.CopilotStudioServices();
        var synchronizer = await SyncedAsync(services.GetRequiredService<IWorkspaceSynchronizer>(), "Contoso Helpdesk", CopilotTestFixtures.NameOfFortyThreeCharacters);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(synchronizer).PushAsync(Workspace() with { WorkspacePath = _directory }, overwrite: false, CancellationToken.None));

        Assert.Equal((ErrorCodes.Workspace.CopilotWorkspaceFilesUnreadable, ExitCodes.Usage, CopilotErrorKinds.WorkspaceFilesUnreadable), (failure.ErrorCode, failure.ExitCode, failure.ErrorKind));
        Assert.Equal($"1 workspace file could not be read: {WorkspaceFileNames.Settings}: {CopilotTestFixtures.AgentNameTooLongReason}", failure.Message);
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), synchronizer.Calls);
        Assert.Equal(0, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_WhenTheWorkspaceIsInvalid_NamesTheFileAndPosition()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            ThrowIfWorkspaceInvalidThrows = Invalid(Conflict(ToolPath, line: 12)),
        };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

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
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

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
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: true, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceFilesUnreadable, failure.ErrorKind);
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
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceFilesUnreadable, failure.ErrorKind);
        Assert.Contains(PromptPath, failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, synchronizer.ThrowIfWorkspaceInvalidCalls);
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
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

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
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(failure.Remediation));
    }
}
