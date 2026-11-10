using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsCloudCachePortTests : IDisposable
{
    private static readonly string A = CloudCacheRepository.Commit(0xa1);
    private static readonly string B = CloudCacheRepository.Commit(0xb1);
    private static readonly string H = CloudCacheRepository.Commit(0xe1);

    private readonly CloudCacheRepository _repository = new();
    private readonly SyncedFolders _inner = new();

    public void Dispose() => _repository.Dispose();

    [Fact]
    public async Task ChangesPushAndPull_SyncAgainstTheCommitsCopy_AndSaveWhatTheSyncWrote()
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, A, "v-a");
        await _repository.SaveAsync(folder, B, "v-b");
        _repository.Checkout(A);
        var port = Workspaces();

        await port.GetChangesAsync(SyncedFolders.Info(folder), default);
        _inner.Cloud = "pushed";
        await port.PushAsync(SyncedFolders.Info(folder), overwrite: false, default);
        _repository.Checkout(B);
        _inner.Cloud = "pulled";
        var pulled = await port.PullAsync(SyncedFolders.Info(folder), default);

        Assert.Equal(["changes v-a", "push v-a", "pull v-b"], _inner.Calls);
        Assert.Equal(("pushed", "pulled"), (_repository.Saved(A), _repository.Saved(B)));
        Assert.Equal(["merge left conflicts"], pulled.Warnings);
    }

    [Fact]
    public async Task Pull_AtACleanCommitThatTheSyncedOneContains_BringsTheFolderToTheSyncedCommitFirst()
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, A, "v-a", branch: "nguhoa/skill");
        await _repository.SaveAsync(folder, B, "v-b");
        _repository.Git.Ancestry.Add((H, A));
        _repository.Checkout(H);

        var pulled = await Workspaces().PullAsync(SyncedFolders.Info(folder), default);

        Assert.Equal(["pull v-a"], _inner.Calls);
        Assert.Contains($"restore {A} ContosoHR", _repository.Git.Calls);
        Assert.Equal(("cloud", H), (_repository.Saved(H), _repository.Index()["live"]!.GetValue<string>()));
        Assert.Equal(["merge left conflicts", $"ContosoHR's commit ({H[..7]}) had never synced with Copilot Studio, so msagent brought its files to {A[..7]} (nguhoa/skill), the commit Copilot Studio was last synced from, before pulling; `git diff` shows the result and `git restore \"ContosoHR\"` undoes it."], pulled.Warnings);
    }

    [Fact]
    public async Task Pull_WithUncommittedChangesAtACommitThatNeverSynced_IsRefused_BeforeAnythingChanges()
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, A, "v-a");
        _repository.Git.Ancestry.Add((H, A));
        _repository.Git.Changed.Add("ContosoHR/topics/Greeting.mcs.yml");
        _repository.Checkout(H);

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => Workspaces().PullAsync(SyncedFolders.Info(folder), default));

        Assert.Equal((ErrorCodes.AmsAgent.UnsyncedCommitHasChanges, ExitCodes.Usage, "unsynced-commit-has-changes"), (failure.ErrorCode, failure.ExitCode, failure.ErrorKind));
        Assert.Equal($"ContosoHR has uncommitted changes, and the commit it holds ({H[..7]}) has never synced with Copilot Studio, so a pull can't tell them apart from Copilot Studio's.", failure.Message);
        Assert.Equal("Commit or stash them, then run `msagent agent pull` again.", failure.Remediation);
        Assert.Empty(_inner.Calls);
        Assert.DoesNotContain(_repository.Git.Calls, call => call.StartsWith("restore", StringComparison.Ordinal));
        Assert.Equal("v-a", CloudCacheRepository.Cache(folder));
    }

    [Fact]
    public async Task Pull_AtACommitThatLacksTheSyncedOne_AsksForAMerge_BeforeAnythingChanges()
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, A, "v-a", branch: "nguhoa/skill");
        _repository.Checkout(H);

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => Workspaces().PullAsync(SyncedFolders.Info(folder), default));

        Assert.Equal((ErrorCodes.AmsAgent.SyncedCommitNotMerged, ExitCodes.Usage, "synced-commit-not-merged"), (failure.ErrorCode, failure.ExitCode, failure.ErrorKind));
        Assert.Equal($"ContosoHR holds {H[..7]} (main), which has never synced with Copilot Studio and doesn't contain {A[..7]} (nguhoa/skill), the commit Copilot Studio was last synced from.", failure.Message);
        Assert.Equal($"Merge that commit (`git merge {A[..7]}`), then run `msagent agent pull` again.", failure.Remediation);
        Assert.Empty(_inner.Calls);
    }

    [Fact]
    public async Task Push_AtACommitThatNeverSynced_PushesAgainstTheLiveCache_AndSavesACopyForIt()
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, A, "v-a");
        _repository.Checkout(H);

        await Workspaces().PushAsync(SyncedFolders.Info(folder), overwrite: true, default);

        Assert.Equal(["push v-a"], _inner.Calls);
        Assert.Equal("cloud", _repository.Saved(H));
    }

    [Fact]
    public async Task Pull_ThatCannotSaveACopy_StillPulls_AndSaysWhy()
    {
        var folder = _repository.Folder();
        Directory.CreateDirectory(Path.Combine(_repository.Root, ".git"));
        File.WriteAllText(Path.Combine(_repository.Root, ".git", "msagent"), "not a folder");
        _repository.Checkout(A);

        var pulled = await Workspaces().PullAsync(SyncedFolders.Info(folder), default);

        Assert.Equal(["pull v1"], _inner.Calls);
        Assert.Equal(2, pulled.Warnings.Count);
        Assert.StartsWith("msagent couldn't save a copy of ContosoHR's cloud cache: ", pulled.Warnings[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clone_SavesNoCopy_SinceNoCommitHoldsTheClonedFoldersYet()
    {
        _repository.Folder(cache: "old");
        _repository.Folder("Shared Tools", "old");
        _repository.Checkout(A);

        var cloned = await Workspaces().CloneAsync(new CopilotCloneCommand { Environment = SyncedFolders.Environment, AgentId = "agent-1", WorkspacePath = _repository.Root, ComponentCollectionIds = ["Shared Tools"] }, default);

        Assert.Equal(["clone old", "clone old"], _inner.Calls);
        Assert.Equal(2, cloned.Workspaces.Count);
        Assert.False(Directory.Exists(_repository.Shelves));
        Assert.Empty(_repository.Git.Calls);
    }

    [Fact]
    public async Task CloningAWorkflow_SavesNoCopy_SinceNoCommitHoldsItsRootYet()
    {
        var root = WorkflowRoot();
        _repository.Checkout(A);

        var cloned = await Workflows().CloneAsync(SyncedFolders.Environment, Guid.Empty, root, "Contoso Copy", default);

        Assert.Equal(["clone Contoso Copy v1"], _inner.Calls);
        Assert.Equal((root, "cloud"), (cloned.FolderPath, CloudCacheRepository.Cache(root)));
        Assert.False(Directory.Exists(_repository.Shelves));
        Assert.Empty(_repository.Git.Calls);
    }

    [Fact]
    public async Task TheWorkspacePort_PassesItsReadsThrough()
    {
        var folder = _repository.Folder();
        var port = Workspaces();

        Assert.True(port.IsWorkspace(folder));
        Assert.Equal("Contoso HR", port.ProjectFolderName("Contoso HR"));
        Assert.Equal("agent-1", (await port.ReadWorkspaceInfoAsync(folder, default)).AgentId);

        Assert.Equal(["is-workspace", "folder-name"], _inner.Calls);
        Assert.Empty(_repository.Git.Calls);
    }

    [Fact]
    public async Task Attach_SavesACopyOfTheAttachedFolder()
    {
        var folder = _repository.Folder();
        _repository.Checkout(A);

        var attached = await Attachments().AttachAsync(Attach(folder), default);

        Assert.Equal(["attach notice"], attached.Notices);
        Assert.Equal("cloud", _repository.Saved(A));
    }

    [Fact]
    public async Task Attach_ThatCannotSaveACopy_SaysWhyInANoticeThatDoesNotHoldBackAPublish()
    {
        var folder = _repository.Folder();
        Directory.CreateDirectory(Path.Combine(_repository.Root, ".git"));
        File.WriteAllText(Path.Combine(_repository.Root, ".git", "msagent"), "not a folder");
        _repository.Checkout(A);

        var attached = await Attachments().AttachAsync(Attach(folder), default);

        Assert.Empty(attached.Warnings);
        Assert.Equal("attach notice", attached.Notices[0]);
        Assert.StartsWith("msagent couldn't save a copy of ContosoHR's cloud cache: ", attached.Notices[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_SavesTheAgentAndEveryCollectionItConnected_ButNoSkippedOne()
    {
        var agent = _repository.Folder(cache: "old");
        var tools = _repository.Folder("Shared Tools", "old");
        var archived = _repository.Folder("Archived Tools", "old");
        _inner.Skipped.Add(archived);
        _repository.Checkout(A);

        await Attachments().ConnectAsync(new CopilotConnectCommand { Environment = SyncedFolders.Environment, WorkspaceDirectory = agent, CollectionDirectories = [tools, archived], SchemaName = "cr1a2_contosoHr" }, default);

        Assert.Equal(["connect old", "connect old"], _inner.Calls);
        Assert.Equal(("cloud", "cloud"), (_repository.Saved(A), _repository.Saved(A, "Shared Tools")));
        Assert.Equal(2, Directory.GetDirectories(_repository.Shelves).Length);
    }

    [Fact]
    public async Task Download_AtACleanCommitThatTheSyncedOneContains_ReplacesTheWorkflow_AndSaysSo()
    {
        var root = WorkflowRoot();
        await _repository.SaveAsync(root, A, "v-a", identity: null);
        _repository.Git.Ancestry.Add((H, A));
        _repository.Checkout(H);

        var downloaded = await Workflows().DownloadAsync(SyncedFolders.Environment, Guid.Empty, root, default);

        Assert.Equal(["download overwrite=True v-a"], _inner.Calls);
        Assert.Equal(["renamed", $"ExpenseApprovals's commit ({H[..7]}) had never synced with Copilot Studio, so msagent replaced its workflow with Copilot Studio's version; `git diff` shows the change and `git restore \"ExpenseApprovals\"` undoes it."], downloaded.Warnings);
        Assert.Equal("cloud", _repository.Saved(H, "ExpenseApprovals"));
    }

    [Theory]
    [InlineData("asked to overwrite", true, true)]
    [InlineData("uncommitted changes", false, false)]
    [InlineData("diverged", false, false)]
    [InlineData("nothing changed", false, true)]
    public async Task Download_OtherwiseDownloadsAsAsked_WithoutAWarning(string scenario, bool overwrite, bool overwrites)
    {
        var root = WorkflowRoot();
        await _repository.SaveAsync(root, A, "v-a", identity: null);
        _repository.Git.Ancestry.Add((H, A));
        _repository.Checkout(H);
        switch (scenario)
        {
            case "uncommitted changes":
                _repository.Git.Changed.Add("ExpenseApprovals/workflows/approvals/workflow.json");
                break;
            case "diverged":
                _repository.Git.Ancestry.Clear();
                break;
            case "nothing changed":
                _inner.Changed = false;
                break;
        }

        var downloaded = await Workflows().DownloadAsync(SyncedFolders.Environment, Guid.Empty, root, default, overwrite);

        Assert.Equal([$"download overwrite={overwrites} v-a"], _inner.Calls);
        Assert.Equal(["renamed"], downloaded.Warnings);
    }

    [Fact]
    public async Task Download_ThatCannotSaveACopy_SaysWhy()
    {
        var root = WorkflowRoot();
        Directory.CreateDirectory(Path.Combine(_repository.Root, ".git"));
        File.WriteAllText(Path.Combine(_repository.Root, ".git", "msagent"), "not a folder");
        _repository.Checkout(A);

        var downloaded = await Workflows().DownloadAsync(SyncedFolders.Environment, Guid.Empty, root, default);

        Assert.Equal("renamed", downloaded.Warnings[0]);
        Assert.StartsWith("msagent couldn't save a copy of ExpenseApprovals's cloud cache: ", downloaded.Warnings[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertAndItsUnpulledCheck_RunAgainstTheCommitsCopy_AndTheUpsertSavesWhatItWrote()
    {
        var root = WorkflowRoot();
        await _repository.SaveAsync(root, A, "v-a", identity: null);
        await _repository.SaveAsync(root, B, "v-b", identity: null);
        _repository.Checkout(A);
        var store = Workflows();

        await store.RequireNoUnpulledChangesAsync(SyncedFolders.Environment, root, default);
        await store.UpsertAsync(SyncedFolders.Environment, root, default);

        Assert.Equal(["unpulled v-a", "upsert v-a"], _inner.Calls);
        Assert.Equal("cloud", _repository.Saved(A, "ExpenseApprovals"));
    }

    [Fact]
    public async Task TheWorkflowStore_PassesEverythingElseThrough()
    {
        var root = WorkflowRoot();
        var store = Workflows();

        await store.ReadLocalAsync(root, default);
        await store.GetAsync(SyncedFolders.Environment, Guid.Empty, default);
        await store.ListAsync(SyncedFolders.Environment, null, null, default);
        store.WriteConnectionReferenceDeclaration(root);
        await store.DeleteAsync(SyncedFolders.Environment, Guid.Empty, default);
        await store.GetAgentUseWarningAsync(SyncedFolders.Environment, Guid.Empty, default);

        Assert.Equal(["read-local", "get", "list", "declare", "delete", "agent-use"], _inner.Calls);
        Assert.Empty(_repository.Git.Calls);
    }

    [Fact]
    public async Task RemovingAReference_EditsTheCommitsCopy_AndSavesIt()
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, A, "v-a");
        await _repository.SaveAsync(folder, B, "v-b");
        _repository.Checkout(A);

        var removal = await Connections().RemoveConnectionReferenceAsync(SyncedFolders.Environment, folder, "cr_teams", confirmed: false, default);

        Assert.True(removal.Removed);
        Assert.Equal(["remove-reference v-a"], _inner.Calls);
        Assert.Equal("cloud", _repository.Saved(A));
    }

    [Fact]
    public async Task RemovingAReference_FromAWorkflowRoot_TracksTheRootWithoutAnAgent()
    {
        var root = WorkflowRoot();
        _repository.Checkout(A);

        await Connections().RemoveConnectionReferenceAsync(SyncedFolders.Environment, root, "cr_teams", confirmed: true, default);

        Assert.Equal("cloud", _repository.Saved(A, "ExpenseApprovals"));
        Assert.Null(_repository.Index("ExpenseApprovals")["identity"]);
    }

    [Theory]
    [InlineData("not removed")]
    [InlineData("commit never synced")]
    [InlineData("folder not connected")]
    public async Task RemovingAReference_SavesNothing_UnlessItEditedACopyThatBelongsToTheCommit(string scenario)
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, A, "v-a");
        switch (scenario)
        {
            case "not removed":
                _inner.Removed = false;
                break;
            case "commit never synced":
                _repository.Checkout(H);
                break;
            default:
                File.Delete(Path.Combine(folder, ".mcs", "conn.json"));
                break;
        }

        await Connections().RemoveConnectionReferenceAsync(SyncedFolders.Environment, folder, "cr_teams", confirmed: false, default);

        Assert.Single(_inner.Calls);
        Assert.Equal([A], _repository.SavedCommits());
        Assert.Equal("v-a", _repository.Saved(A));
    }

    [Fact]
    public async Task TheConnectionPort_PassesEverythingElseThrough()
    {
        var folder = _repository.Folder();
        var port = Connections();

        await port.ReadConnectionsAsync(SyncedFolders.Environment, folder, default);
        await port.ReadDeclaredConnectorsAsync(SyncedFolders.Environment, folder, default);
        await port.BindAsync(SyncedFolders.Environment, folder, [], default);
        await port.CreateConnectionReferenceAsync(SyncedFolders.Environment, folder, "shared_teams", default);
        port.ReadWorkflows(folder, []);
        await port.SetWorkflowStatesAsync(SyncedFolders.Environment, folder, [], default);
        await port.ActivateWorkflowsAsync(SyncedFolders.Environment, folder, null, default);

        Assert.Equal(["connections", "connectors", "bind", "create-reference", "workflows", "states", "activate"], _inner.Calls);
        Assert.Empty(_repository.Git.Calls);
    }

    private static CopilotAttachCommand Attach(string folder) => new() { Environment = SyncedFolders.Environment, ProjectDirectory = folder, DisplayName = "Contoso HR", SchemaName = "cr1a2_contosoHr" };

    private string WorkflowRoot()
    {
        var root = _repository.Folder("ExpenseApprovals", identity: null);
        File.WriteAllText(Path.Combine(root, "workflow.sync.yaml"), "version: 1\n");
        return root;
    }

    private AmsCloudCacheWorkspacePort Workspaces() => new(_inner, _repository.Shelf);

    private AmsCloudCacheAttachPort Attachments() => new(_inner, _inner, _repository.Shelf, _inner);

    private AmsCloudCacheWorkflowStore Workflows() => new(_inner, _repository.Shelf);

    private AmsCloudCacheConnectionPort Connections() => new(_inner, _repository.Shelf, _inner, new WorkflowWorkspaceAdapter());
}
