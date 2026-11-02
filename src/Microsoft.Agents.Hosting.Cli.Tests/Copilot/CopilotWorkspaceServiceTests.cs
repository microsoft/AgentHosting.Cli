using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class CopilotWorkspaceServiceTests
{
    private static CopilotWorkspaceService Create(FakeCopilotWorkspacePort workspaces, FakeCopilotCatalogPort? catalog = null, FakeCopilotFileSystem? fileSystem = null, string? instanceUrl = CopilotTestFixtures.DataverseOrganization)
    {
        var files = fileSystem ?? new FakeCopilotFileSystem();

        return new CopilotWorkspaceService(workspaces, CopilotTestFixtures.Locator(workspaces, files));
    }

    [Fact]
    public async Task Push_WithNoLocalChanges_ReportsNoLocalChangesAndDoesNotPush()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        var result = await Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None);

        Assert.Equal("no-local-changes", result.Status);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public async Task Push_WithLocalChangesOnly_Pushes()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("Greeting"));

        var result = await Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None);

        Assert.Equal("pushed", result.Status);
        Assert.Equal(1, workspaces.PushCalls);
        Assert.Equal(1, result.ChangeCount);
    }

    [Fact]
    public async Task Push_WithOverlappingLocalAndRemoteChanges_IsRejectedAndDoesNotPush()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("Greeting"));
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("Escalate"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("Greeting"));

        var error = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.PushConflictsRequirePull, error.ErrorKind);
        Assert.Equal(ExitCodes.Usage, error.ExitCode);
        Assert.Contains("Greeting", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Escalate", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, workspaces.PushCalls);
    }

    [Fact]
    public async Task Push_WithOverwrite_PushesTheConflictingComponents()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("Greeting"));
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("Escalate"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("Greeting"));

        var result = await Create(workspaces).PushAsync(new CopilotPushRequest { Overwrite = true }, CancellationToken.None);

        Assert.Equal("pushed", result.Status);
        Assert.Equal(1, workspaces.PushCalls);
        Assert.True(workspaces.LastPushOverwrite);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Push_PassesTheOverwriteDecisionToThePort(bool overwrite)
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("Greeting"));

        await Create(workspaces).PushAsync(new CopilotPushRequest { Overwrite = overwrite }, CancellationToken.None);

        Assert.Equal(overwrite, workspaces.LastPushOverwrite);
    }

    [Fact]
    public async Task Push_WithoutOverwrite_SurfacesARemoteChangeAsAFailure()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("Greeting"));
        workspaces.PushThrowsSequence.Enqueue(CopilotStudioException.RemoteChangedDuringPush(new InvalidOperationException("stale token")));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.RemoteChangedDuringPush, failure.ErrorKind);
        Assert.Equal(1, workspaces.PushCalls);
    }

    [Fact]
    public async Task Push_WithOverwriteAndNoConflict_Pushes()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("Greeting"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("Goodbye"));

        var result = await Create(workspaces).PushAsync(new CopilotPushRequest { Overwrite = true }, CancellationToken.None);

        Assert.Equal("pushed", result.Status);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Push_WithOverwriteAndNoLocalChanges_StillPushesNothing()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("Greeting"));

        var result = await Create(workspaces).PushAsync(new CopilotPushRequest { Overwrite = true }, CancellationToken.None);

        Assert.Equal("no-local-changes", result.Status);
    }

    [Fact]
    public async Task Push_WithOnlyAuxiliaryChanges_IsNotReportedAsNothingToDo()
    {
        var workspaces = new FakeCopilotWorkspacePort { AuxiliaryChangeCountReturns = 1 };

        var result = await Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None);

        Assert.Equal("pushed", result.Status);
        Assert.Equal(1, workspaces.PushCalls);
    }

    [Fact]
    public async Task Push_WithAManifestOnlyChange_IsNotReportedAsNothingToDo()
    {
        var workspaces = new FakeCopilotWorkspacePort { UploadedFileNamesReturns = ["SKILL.md"] };

        var result = await Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None);

        Assert.Equal("pushed", result.Status);
        Assert.Equal(1, result.UploadedFileCount);
    }

    [Fact]
    public async Task Push_CountsUploadedFilesApartFromComponentChanges()
    {
        var workspaces = new FakeCopilotWorkspacePort { UploadedFileNamesReturns = ["SKILL.md", "File 1.txt"] };
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("Greeting"));

        var result = await Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None);

        Assert.Equal(1, result.ChangeCount);
        Assert.Equal(2, result.UploadedFileCount);
    }

    [Fact]
    public async Task Push_WithNothingSentAtAll_ReportsNoLocalChanges()
    {
        var workspaces = new FakeCopilotWorkspacePort();

        var result = await Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None);

        Assert.Equal("no-local-changes", result.Status);
        Assert.Equal(0, result.UploadedFileCount);
    }

    [Fact]
    public async Task Push_WithNoComponentChanges_StillAsksThePortSoAPromptEditIsNotLost()
    {
        var workspaces = new FakeCopilotWorkspacePort();

        await Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None);

        Assert.Equal(1, workspaces.PushCalls);
    }

    [Fact]
    public async Task Push_WithDisjointLocalChangeAndCloudAddition_Proceeds()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("Greeting"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("Goodbye", kind: CopilotChangeKinds.Created));

        var result = await Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None);

        Assert.Equal("pushed", result.Status);
        Assert.Equal(1, workspaces.PushCalls);
    }

    [Fact]
    public async Task Push_WhenTheCloudUpdatedATrackedComponent_IsRejectedAndDoesNotPush()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("Greeting", kind: CopilotChangeKinds.Updated));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.PushConflictsRequirePull, failure.ErrorKind);
        Assert.Contains("Greeting", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, workspaces.PushCalls);
    }

    [Fact]
    public async Task Push_WhenTheCloudDeletedATrackedComponent_IsRejectedAndDoesNotPush()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("Escalate"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("Greeting", kind: CopilotChangeKinds.Deleted));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.PushConflictsRequirePull, failure.ErrorKind);
        Assert.Equal(0, workspaces.PushCalls);
    }

    [Fact]
    public async Task Push_WhenTheCloudOnlyAddedAComponent_Proceeds()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("Greeting"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("cat-new", kind: CopilotChangeKinds.Created));

        var result = await Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None);

        Assert.Equal("pushed", result.Status);
        Assert.Equal(1, workspaces.PushCalls);
    }

    [Fact]
    public async Task Push_WhenBothSidesAddedTheSameComponent_IsRejected()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("cat-new", kind: CopilotChangeKinds.Created));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("cat-new", kind: CopilotChangeKinds.Created));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.PushConflictsRequirePull, failure.ErrorKind);
        Assert.Equal(0, workspaces.PushCalls);
    }

    [Fact]
    public async Task Push_WhenTheCloudUpdatedATrackedComponentWithOverwrite_Pushes()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("Greeting", kind: CopilotChangeKinds.Updated));
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("Escalate"));

        var result = await Create(workspaces).PushAsync(new CopilotPushRequest { Overwrite = true }, CancellationToken.None);

        Assert.Equal("pushed", result.Status);
        Assert.Equal(1, workspaces.PushCalls);
        Assert.True(workspaces.LastPushOverwrite);
    }

    [Fact]
    public async Task Push_ReportsEachConflictingComponentOnce()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("Greeting"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("Greeting", kind: CopilotChangeKinds.Updated));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("greeting", kind: CopilotChangeKinds.Deleted));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None));

        Assert.Contains("1 remote change", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("2 remote changes", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_MatchesConflictsWithoutRegardToCase()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("greeting"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("GREETING"));

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None));

        Assert.Equal(0, workspaces.PushCalls);
    }

    [Fact]
    public async Task Push_OutsideAWorkspace_FailsWithWorkspaceNotFound()
    {
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };

        var error = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(workspaces).PushAsync(new CopilotPushRequest(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceNotFound, error.ErrorKind);
        Assert.Equal(0, workspaces.PushCalls);
    }

    [Fact]
    public async Task Pull_WithNoRemoteChanges_ReportsUpToDate()
    {
        var workspaces = new FakeCopilotWorkspacePort();

        var result = await Create(workspaces).PullAsync(new CopilotPullRequest(), CancellationToken.None);

        Assert.Equal("up-to-date", result.Status);
        Assert.Equal(0, result.ChangeCount);
    }

    [Fact]
    public async Task Pull_WithRemoteChanges_ReportsThemApplied()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("Greeting"));

        var result = await Create(workspaces).PullAsync(new CopilotPullRequest(), CancellationToken.None);

        Assert.Equal("pulled", result.Status);
        Assert.Equal(1, result.ChangeCount);
        Assert.Equal(1, workspaces.PullCalls);
    }

    [Fact]
    public async Task Pull_WhenTheMergeLeavesConflicts_CarriesThemAsWarnings()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("Greeting"));
        workspaces.PullWarnings.Add("1 workspace file has unresolved merge conflicts.");

        var result = await Create(workspaces).PullAsync(new CopilotPullRequest(), CancellationToken.None);

        Assert.Equal("pulled", result.Status);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Pull_WithoutConflicts_CarriesNoWarnings()
    {
        var workspaces = new FakeCopilotWorkspacePort();

        var result = await Create(workspaces).PullAsync(new CopilotPullRequest(), CancellationToken.None);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Pull_OutsideAWorkspace_FailsWithWorkspaceNotFound()
    {
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };

        var error = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(workspaces).PullAsync(new CopilotPullRequest(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceNotFound, error.ErrorKind);
        Assert.Equal(0, workspaces.PullCalls);
    }

    [Fact]
    public async Task ResolvingAnEnvironmentWithoutADataverseOrganization_IsRejected()
    {
        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => CopilotTestFixtures.Resolver(instanceUrl: null).ResolveAsync(null, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.EnvironmentMissingInstanceUrl, failure.ErrorKind);
    }

    [Fact]
    public async Task Pull_TargetsTheDataverseOrganizationBeforeTheSyncLibraryReadsIt()
    {
        var workspaces = new AsyncLocalWorkspacePort();
        var files = new FakeCopilotFileSystem();
        var service = new CopilotWorkspaceService(workspaces, CopilotTestFixtures.Locator(workspaces, files));

        var result = await service.PullAsync(new CopilotPullRequest(), CancellationToken.None);

        Assert.Equal("up-to-date", result.Status);
    }

    [Fact]
    public async Task Push_TargetsTheDataverseOrganizationBeforeTheSyncLibraryReadsIt()
    {
        var workspaces = new AsyncLocalWorkspacePort();
        var files = new FakeCopilotFileSystem();
        var service = new CopilotWorkspaceService(workspaces, CopilotTestFixtures.Locator(workspaces, files));

        var result = await service.PushAsync(new CopilotPushRequest(), CancellationToken.None);

        Assert.Equal("no-local-changes", result.Status);
    }
}
