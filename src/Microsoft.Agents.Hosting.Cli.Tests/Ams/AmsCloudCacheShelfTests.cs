using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsCloudCacheShelfTests : IDisposable
{
    private static readonly string A = CloudCacheRepository.Commit(0xa1);
    private static readonly string B = CloudCacheRepository.Commit(0xb1);
    private static readonly string C = CloudCacheRepository.Commit(0xc1);
    private static readonly string H = CloudCacheRepository.Commit(0xe1);

    private readonly CloudCacheRepository _repository = new();

    public void Dispose() => _repository.Dispose();

    [Fact]
    public async Task Save_CopiesTheCacheButMsagentsWorkflowState_UnderTheFoldersKeyInTheWorkingTreesGitFolder()
    {
        var folder = _repository.Folder("Agents/Contoso HR");
        File.WriteAllText(Path.Combine(folder, ".mcs", "changetoken.txt"), "token-1");
        File.WriteAllText(Path.Combine(folder, ".mcs", "workflow-state.json"), "observed");
        Directory.CreateDirectory(Path.Combine(folder, ".mcs", "files"));
        File.WriteAllText(Path.Combine(folder, ".mcs", "files", "faq.md"), "faq");
        _repository.Git.GitDirectory = Path.Combine(_repository.Root, ".git", "worktrees", "feature");
        _repository.Checkout(A, "feature");

        Assert.Null(await _repository.Shelf.SaveAsync(folder, CloudCacheRepository.Identity, default));

        var key = Path.Combine(_repository.Root, ".git", "worktrees", "feature", "msagent", "mcs", "acfbcdadeb5b09de");
        var copy = Path.Combine(key, A);
        Assert.Equal(["botdefinition.json", "changetoken.txt", "conn.json", Path.Combine("files", "faq.md")], Directory.EnumerateFiles(copy, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(copy, file)).Order(StringComparer.Ordinal));
        Assert.Equal([copy], Directory.GetDirectories(key));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{"folder":"Agents/Contoso HR","identity":"env-1|agent-1","live":"{{A}}","copies":[{"commit":"{{A}}","branch":"feature","savedAt":"2026-08-02T10:15:00+00:00"}]}"""), JsonNode.Parse(File.ReadAllText(Path.Combine(key, "index.json")))));
        Assert.Equal("observed", File.ReadAllText(Path.Combine(folder, ".mcs", "workflow-state.json")));
    }

    [Fact]
    public async Task Save_AtACommitWithACopy_ReplacesThatCopy_WithoutAnythingAHalfWrittenOneLeft()
    {
        var folder = _repository.Folder();
        File.WriteAllText(Path.Combine(folder, ".mcs", "changetoken.txt"), "token-1");
        await _repository.SaveAsync(folder, A, "v1");
        File.Delete(Path.Combine(folder, ".mcs", "changetoken.txt"));
        var key = _repository.KeyDirectory();
        Directory.CreateDirectory(Path.Combine(key, A + ".tmp"));
        File.WriteAllText(Path.Combine(key, A + ".tmp", "stale.txt"), "stale");

        await _repository.SaveAsync(folder, A, "v2");

        Assert.Equal([Path.Combine(key, A)], Directory.GetDirectories(key));
        Assert.Equal(["botdefinition.json", "conn.json"], Directory.EnumerateFiles(Path.Combine(key, A)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("v2", _repository.Saved(A));
        Assert.Equal([A], _repository.SavedCommits());
        Assert.Equal("2026-08-02T10:16:00+00:00", _repository.Index()["copies"]![0]!["savedAt"]!.GetValue<string>());
    }

    [Fact]
    public async Task Save_KeepsTheTwentyNewestCopies_AndAlwaysTheOneItJustSaved()
    {
        var folder = _repository.Folder();
        await _repository.SaveEachAsync(folder, Enumerable.Range(1, 20), "main");

        _repository.Now = _repository.Now.AddDays(-1);
        await _repository.SaveAsync(folder, H, "head");

        Assert.Equal([H, .. CloudCacheRepository.NewestFirst(2, 20)], _repository.SavedCommits());
        Assert.Equal(20, Directory.GetDirectories(_repository.KeyDirectory()).Length);
        Assert.Null(_repository.Saved(CloudCacheRepository.Commit(1)));
        Assert.Equal((H, "head"), (_repository.Index()["live"]!.GetValue<string>(), _repository.Saved(H)));
    }

    [Theory]
    [InlineData("release")]
    [InlineData("nguhoa/skill")]
    public async Task Save_KeepsTheNewestCopyOfEachOtherLocalBranch_ThenTheNewestCopies_TwentyInAll(string branch)
    {
        var folder = _repository.Folder();
        var main = CloudCacheRepository.Commit(0x100);
        var older = CloudCacheRepository.Commit(0x200);
        var newer = CloudCacheRepository.Commit(0x201);
        await _repository.SaveAsync(folder, main, "main");
        await _repository.SaveAsync(folder, older, "older", branch: branch);
        await _repository.SaveAsync(folder, newer, "newer", branch: branch);

        await _repository.SaveEachAsync(folder, Enumerable.Range(1, 21), "develop");

        Assert.Equal([.. CloudCacheRepository.NewestFirst(4, 21), newer, main], _repository.SavedCommits());
        Assert.Equal(20, Directory.GetDirectories(_repository.KeyDirectory()).Length);
        Assert.Equal(("newer", "main"), (_repository.Saved(newer), _repository.Saved(main)));
        Assert.Null(_repository.Saved(older));
    }

    [Fact]
    public async Task Save_GivesABranchDeletedSinceItSyncedNoPlace()
    {
        var folder = _repository.Folder();
        var main = CloudCacheRepository.Commit(0x100);
        var feature = CloudCacheRepository.Commit(0x200);
        await _repository.SaveAsync(folder, main, "main");
        await _repository.SaveAsync(folder, feature, "feature", branch: "feature");
        await _repository.SaveEachAsync(folder, Enumerable.Range(1, 18), "develop");
        _repository.Git.LocalBranches.Remove("feature");

        await _repository.SaveEachAsync(folder, [19], "develop");

        Assert.Equal([.. CloudCacheRepository.NewestFirst(1, 19), main], _repository.SavedCommits());
        Assert.Null(_repository.Saved(feature));
    }

    [Theory]
    [InlineData("develop")]
    [InlineData(null)]
    public async Task Save_GivesACopySavedOnADetachedHeadNoPlace(string? headBranch)
    {
        var folder = _repository.Folder();
        var main = CloudCacheRepository.Commit(0x100);
        var detached = CloudCacheRepository.Commit(0x200);
        await _repository.SaveAsync(folder, main, "main");
        await _repository.SaveAsync(folder, detached, "detached", branch: null);
        await _repository.SaveEachAsync(folder, Enumerable.Range(1, 18), "develop");

        await _repository.SaveAsync(folder, H, "head", branch: headBranch);

        Assert.Equal([H, .. CloudCacheRepository.NewestFirst(1, 18), main], _repository.SavedCommits());
        Assert.Null(_repository.Saved(detached));
    }

    [Fact]
    public async Task Save_WithMoreOtherBranchesThanPlaces_KeepsTheNewestCopyOfTheMostRecentlySavedOnes()
    {
        var folder = _repository.Folder();
        var older = CloudCacheRepository.Commit(0x100);
        var newer = CloudCacheRepository.Commit(0x101);
        foreach (var number in Enumerable.Range(1, 19))
        {
            await _repository.SaveEachAsync(folder, [number], $"topic/{number}");
        }

        await _repository.SaveAsync(folder, older, "older");
        await _repository.SaveAsync(folder, newer, "newer");

        await _repository.SaveAsync(folder, H, "head", branch: "topic/20");

        Assert.Equal([H, newer, .. CloudCacheRepository.NewestFirst(2, 19)], _repository.SavedCommits());
        Assert.Null(_repository.Saved(older));
        Assert.Null(_repository.Saved(CloudCacheRepository.Commit(1)));
    }

    [Fact]
    public async Task Save_GivesTheBranchHeadIsOnNoPlaceBesideTheCopyItSaves()
    {
        var folder = _repository.Folder();
        var main = CloudCacheRepository.Commit(0x100);
        var older = CloudCacheRepository.Commit(0x200);
        await _repository.SaveAsync(folder, main, "main");
        await _repository.SaveAsync(folder, older, "older", branch: "feature");
        await _repository.SaveEachAsync(folder, Enumerable.Range(1, 18), "develop");

        await _repository.SaveAsync(folder, H, "head", branch: "feature");

        Assert.Equal([H, .. CloudCacheRepository.NewestFirst(1, 18), main], _repository.SavedCommits());
        Assert.Null(_repository.Saved(older));
    }

    [Fact]
    public async Task Save_WhenGitCannotListTheBranches_KeepsTheNewestCopiesWithoutAWarning()
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, CloudCacheRepository.Commit(0x100), "main");
        await _repository.SaveEachAsync(folder, Enumerable.Range(1, 19), "develop");
        _repository.Git.LocalBranchesFailure = AmsAgentException.GitCommandFailed("git for-each-ref", _repository.Root, "fatal: bad ref");

        await _repository.SaveEachAsync(folder, [20], "develop");

        Assert.Equal(CloudCacheRepository.NewestFirst(1, 20), _repository.SavedCommits());
        Assert.Contains("for-each-ref refs/heads", _repository.Git.Calls);
    }

    [Fact]
    public async Task Save_CancelledWhileGitListsTheBranches_WritesNothing()
    {
        var folder = _repository.Folder();
        await _repository.SaveEachAsync(folder, Enumerable.Range(1, 20), "main");
        var index = File.ReadAllText(Path.Combine(_repository.KeyDirectory(), "index.json"));
        _repository.Git.LocalBranchesFailure = new OperationCanceledException();
        _repository.Checkout(H);

        await Assert.ThrowsAsync<OperationCanceledException>(() => _repository.Shelf.SaveAsync(folder, CloudCacheRepository.Identity, default));

        Assert.Equal(index, File.ReadAllText(Path.Combine(_repository.KeyDirectory(), "index.json")));
        Assert.Equal(20, Directory.GetDirectories(_repository.KeyDirectory()).Length);
    }

    [Fact]
    public async Task Save_AsksGitForTheBranchesOnlyWhenACopyHasToGo()
    {
        var folder = _repository.Folder();
        await _repository.SaveEachAsync(folder, Enumerable.Range(1, 20), "main");
        await _repository.SaveEachAsync(folder, [20], "main");

        Assert.DoesNotContain("for-each-ref refs/heads", _repository.Git.Calls);

        await _repository.SaveEachAsync(folder, [21], "main");

        Assert.Single(_repository.Git.Calls, call => call == "for-each-ref refs/heads");
    }

    [Fact]
    public async Task Save_ListsTheCopiesItKeepsNewestFirst()
    {
        var folder = _repository.Folder();
        var main = CloudCacheRepository.Commit(0x100);
        var feature = CloudCacheRepository.Commit(0x200);
        await _repository.SaveAsync(folder, main, "main");
        await _repository.SaveEachAsync(folder, Enumerable.Range(1, 5), "develop");
        await _repository.SaveAsync(folder, feature, "feature", branch: "feature");

        await _repository.SaveEachAsync(folder, Enumerable.Range(6, 14), "develop");

        Assert.Equal([.. CloudCacheRepository.NewestFirst(6, 19), feature, .. CloudCacheRepository.NewestFirst(2, 5), main], _repository.SavedCommits());
    }

    [Fact]
    public async Task Save_ForAFolderNowConnectedToAnotherAgent_DropsTheOtherAgentsCopies()
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, A, "v-a");

        await _repository.SaveAsync(folder, B, "other", "env-1|agent-2");

        Assert.Equal([B], _repository.SavedCommits());
        Assert.False(Directory.Exists(Path.Combine(_repository.KeyDirectory(), A)));
        Assert.Equal("env-1|agent-2", _repository.Index()["identity"]!.GetValue<string>());
    }

    [Fact]
    public async Task AWorkflowRoot_IsTrackedWithoutAnAgentToCheck()
    {
        var root = _repository.Folder("ExpenseApprovals", identity: null);
        await _repository.SaveAsync(root, A, "v-a", identity: null);
        await _repository.SaveAsync(root, B, "v-b", identity: null);
        _repository.Checkout(A);

        Assert.Null(await _repository.Shelf.PrepareAsync(root, null, default));

        Assert.Equal("v-a", CloudCacheRepository.Cache(root));
        Assert.Equal([B, A], _repository.SavedCommits("ExpenseApprovals"));
        Assert.Null(_repository.Index("ExpenseApprovals")["identity"]);
    }

    [Fact]
    public async Task Prepare_AtACommitWithItsOwnCopy_PutsThatCopyInPlace_WithoutAskingGitAboutAncestry()
    {
        var folder = _repository.Folder();
        File.WriteAllText(Path.Combine(folder, ".mcs", "workflow-state.json"), "observed");
        await _repository.SaveAsync(folder, A, "v-a");
        File.WriteAllText(Path.Combine(folder, ".mcs", "changetoken.txt"), "token-b");
        await _repository.SaveAsync(folder, B, "v-b");
        _repository.Checkout(A);
        _repository.Git.Calls.Clear();

        Assert.Null(await _repository.Shelf.PrepareAsync(folder, CloudCacheRepository.Identity, default));

        Assert.Equal("v-a", CloudCacheRepository.Cache(folder));
        Assert.False(File.Exists(Path.Combine(folder, ".mcs", "changetoken.txt")));
        Assert.Equal("observed", File.ReadAllText(Path.Combine(folder, ".mcs", "workflow-state.json")));
        Assert.Equal(A, _repository.Index()["live"]!.GetValue<string>());
        Assert.DoesNotContain(_repository.Git.Calls, call => call.StartsWith("merge-base", StringComparison.Ordinal) || call.StartsWith("status", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prepare_AtTheCommitWhoseCopyIsLive_KeepsTheLiveCache()
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, A, "v-a");
        CloudCacheRepository.Write(folder, "v-a and a sync since");

        Assert.Null(await _repository.Shelf.PrepareAsync(folder, CloudCacheRepository.Identity, default));

        Assert.Equal("v-a and a sync since", CloudCacheRepository.Cache(folder));
    }

    [Fact]
    public async Task Prepare_AtACommitWithoutACopy_PutsTheNearestAncestorsCopyInPlace()
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, B, "v-b");
        await _repository.SaveAsync(folder, A, "v-a");
        await _repository.SaveAsync(folder, C, "v-c");
        _repository.Git.Ancestry.UnionWith([(A, H), (B, H), (A, B)]);
        _repository.Checkout(H);

        Assert.Null(await _repository.Shelf.PrepareAsync(folder, CloudCacheRepository.Identity, default));

        Assert.Equal("v-b", CloudCacheRepository.Cache(folder));
        Assert.Equal(B, _repository.Index()["live"]!.GetValue<string>());
    }

    [Fact]
    public async Task Prepare_AtACommitThatNoCopyContains_LeavesTheLiveCache_AndFindsTheNewestCopyThatContainsIt()
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, A, "v-a");
        await _repository.SaveAsync(folder, B, "v-b");
        await _repository.SaveAsync(folder, C, "v-c");
        _repository.Git.Ancestry.UnionWith([(H, A), (H, B)]);
        _repository.Checkout(H, "nguhoa/skill");

        var gap = await _repository.Shelf.PrepareAsync(folder, CloudCacheRepository.Identity, default);

        Assert.NotNull(gap);
        Assert.Equal((CloudCacheRepository.Agent, H, "nguhoa/skill", true, B, C), (gap.Place.Folder, gap.Place.Head, gap.Place.Branch, gap.Clean, gap.Behind?.Commit, gap.Newest.Commit));
        Assert.Equal("v-c", CloudCacheRepository.Cache(folder));
    }

    [Fact]
    public async Task Prepare_AtACommitThatDivergedFromEveryCopy_FindsNoCopyThatContainsIt()
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, A, "v-a", branch: "nguhoa/skill");
        _repository.Checkout(H, null);

        var gap = await _repository.Shelf.PrepareAsync(folder, CloudCacheRepository.Identity, default);

        Assert.NotNull(gap);
        Assert.Null(gap.Behind);
        Assert.Null(gap.Place.Branch);
        Assert.Equal((A, "nguhoa/skill"), (gap.Newest.Commit, gap.Newest.Branch));
    }

    [Fact]
    public async Task Prepare_TellsUncommittedChangesInTheFolder_FromChangesBesideIt()
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, A, "v-a");
        _repository.Checkout(H);
        _repository.Git.Changed.Add("ContosoHRBackup/topics/Greeting.mcs.yml");

        var clean = await _repository.Shelf.PrepareAsync(folder, CloudCacheRepository.Identity, default);
        _repository.Git.Changed.Add("ContosoHR/topics/Greeting.mcs.yml");
        var dirty = await _repository.Shelf.PrepareAsync(folder, CloudCacheRepository.Identity, default);

        Assert.True(clean?.Clean);
        Assert.False(dirty?.Clean);
        Assert.Contains("status ContosoHR", _repository.Git.Calls);
    }

    [Fact]
    public async Task Prepare_IgnoresACopyWhoseFolderIsGone()
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, A, "v-a");
        await _repository.SaveAsync(folder, B, "v-b");
        Directory.Delete(Path.Combine(_repository.KeyDirectory(), A), recursive: true);
        _repository.Checkout(A);

        var gap = await _repository.Shelf.PrepareAsync(folder, CloudCacheRepository.Identity, default);

        Assert.Equal(B, gap?.Newest.Commit);
        Assert.Equal("v-b", CloudCacheRepository.Cache(folder));
    }

    [Theory]
    [InlineData("no working tree")]
    [InlineData("no commits")]
    [InlineData("unresolvable HEAD")]
    [InlineData("git failed")]
    public async Task WhereGitCannotPlaceTheFolder_NothingIsSwappedOrSaved(string scenario)
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, A, "v-a");
        await _repository.SaveAsync(folder, B, "v-b");
        _repository.Checkout(A);
        switch (scenario)
        {
            case "no working tree":
                _repository.Git.Tree = null;
                break;
            case "no commits":
                _repository.Git.Tree = _repository.Git.Tree! with { HasCommits = false };
                break;
            case "unresolvable HEAD":
                _repository.Git.Commits.Remove("HEAD");
                break;
            default:
                _repository.Git.FindFailure = AmsAgentException.GitCommandFailed("git rev-parse", _repository.Root, "fatal: bad object HEAD");
                break;
        }

        Assert.Null(await _repository.Shelf.PrepareAsync(folder, CloudCacheRepository.Identity, default));
        Assert.Null(await _repository.Shelf.SaveAsync(folder, CloudCacheRepository.Identity, default));

        Assert.Equal("v-b", CloudCacheRepository.Cache(folder));
        Assert.Equal([B, A], _repository.SavedCommits());
    }

    [Theory]
    [InlineData("another agent")]
    [InlineData("unreadable index")]
    [InlineData("index naming a path")]
    public async Task Prepare_LeavesTheLiveCache_WhenItCannotTrustTheCopies(string scenario)
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, A, "v-a");
        await _repository.SaveAsync(folder, B, "v-b");
        _repository.Checkout(A);
        var index = Path.Combine(_repository.KeyDirectory(), "index.json");
        switch (scenario)
        {
            case "unreadable index":
                File.WriteAllText(index, "{");
                break;
            case "index naming a path":
                File.WriteAllText(index, $$"""{"folder":"ContosoHR","identity":"env-1|agent-1","live":"{{B}}","copies":[{"commit":"..","branch":null,"savedAt":"2026-08-02T10:15:00+00:00"},{"commit":"{{A}}","branch":"main","savedAt":"2026-08-02T10:15:00+00:00"}]}""");
                break;
        }

        Assert.Null(await _repository.Shelf.PrepareAsync(folder, scenario == "another agent" ? "env-1|agent-2" : CloudCacheRepository.Identity, default));

        Assert.Equal("v-b", CloudCacheRepository.Cache(folder));
    }

    [Fact]
    public async Task Save_OverAnIndexItCannotTrust_StartsOver()
    {
        var folder = _repository.Folder();
        await _repository.SaveAsync(folder, A, "v-a");
        File.WriteAllText(Path.Combine(_repository.KeyDirectory(), "index.json"), "{");

        await _repository.SaveAsync(folder, B, "v-b");

        Assert.Equal([B], _repository.SavedCommits());
        Assert.False(Directory.Exists(Path.Combine(_repository.KeyDirectory(), A)));
    }

    [Fact]
    public async Task AFolderConnectedBeforeTracking_IsLeftAlone_UntilItsNextSave()
    {
        var folder = _repository.Folder();
        _repository.Checkout(A);

        Assert.Null(await _repository.Shelf.PrepareAsync(folder, CloudCacheRepository.Identity, default));
        Assert.False(Directory.Exists(_repository.Shelves));
        Assert.Null(await _repository.Shelf.SaveAsync(folder, CloudCacheRepository.Identity, default));

        Assert.Equal([A], _repository.SavedCommits());
    }

    [Fact]
    public async Task AFolderWithoutACloudCache_AsksGitNothing_AndSavesNothing()
    {
        var folder = Path.Combine(_repository.Root, "Unconnected");
        Directory.CreateDirectory(folder);
        _repository.Checkout(A);

        Assert.Null(await _repository.Shelf.PrepareAsync(folder, null, default));
        Assert.Null(await _repository.Shelf.SaveAsync(folder, null, default));

        Assert.Empty(_repository.Git.Calls);
        Assert.False(Directory.Exists(_repository.Shelves));
    }

    [Fact]
    public async Task Save_ThatCannotWrite_GivesTheReason_AndLeavesTheLiveCache()
    {
        var folder = _repository.Folder();
        Directory.CreateDirectory(Path.Combine(_repository.Root, ".git"));
        File.WriteAllText(Path.Combine(_repository.Root, ".git", "msagent"), "not a folder");
        _repository.Checkout(A);

        var warning = await _repository.Shelf.SaveAsync(folder, CloudCacheRepository.Identity, default);

        Assert.StartsWith("msagent couldn't save a copy of ContosoHR's cloud cache: ", warning, StringComparison.Ordinal);
        Assert.Equal("v1", CloudCacheRepository.Cache(folder));
    }

    [Fact]
    public async Task Cancellation_IsNeverMistakenForAGitFailure()
    {
        var folder = _repository.Folder();
        _repository.Git.FindFailure = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => _repository.Shelf.PrepareAsync(folder, CloudCacheRepository.Identity, default));
        await Assert.ThrowsAsync<OperationCanceledException>(() => _repository.Shelf.SaveAsync(folder, CloudCacheRepository.Identity, default));
        await Assert.ThrowsAsync<OperationCanceledException>(() => _repository.Shelf.ForgetAsync(folder, default));
    }

    [Fact]
    public async Task Forget_DropsEveryCopyOfTheFolder_EvenOnceTheFolderIsGone()
    {
        var tools = _repository.Folder("Shared Tools");
        var folder = _repository.Folder();
        await _repository.SaveAsync(tools, A, "tools");
        await _repository.SaveAsync(folder, A, "v-a");
        Directory.Delete(folder, recursive: true);

        await _repository.Shelf.ForgetAsync(folder, default);

        Assert.Equal([_repository.KeyDirectory("Shared Tools")], Directory.GetDirectories(_repository.Shelves));
    }
}
