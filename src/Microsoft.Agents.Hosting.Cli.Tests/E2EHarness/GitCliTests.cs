using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class GitCliTests : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly Transcript _transcript;
    private readonly GitCli _git;

    public GitCliTests()
    {
        _transcript = new Transcript(_scratch.Folder("run"));
        _git = AmsMcsTestKit.Git(_scratch, _transcript);
    }

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public async Task ACommitIsPushedAndTheRemoteHeadNamesIt()
    {
        var (seed, first) = await SeedAsync();

        Assert.Matches("^[0-9a-f]{40}$", first);
        Assert.Equal(first, await _git.RemoteHeadAsync(seed, "main", CancellationToken.None));
        Assert.Null(await _git.RemoteHeadAsync(seed, "no-such-branch", CancellationToken.None));
        Assert.Contains("$ git push --quiet origin main", File.ReadAllText(Path.Combine(_scratch.Path, "run", "transcript.log")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACloneFollowsTheRemoteWithAFastForwardPull()
    {
        var (seed, first) = await SeedAsync();
        var clone = Path.Combine(_scratch.Path, "clone");
        await _git.CloneAsync(Path.Combine(_scratch.Path, "origin.git"), clone, CancellationToken.None);
        File.WriteAllText(Path.Combine(seed, "notes.txt"), "second");
        var second = await _git.CommitAsync(seed, "second", ["notes.txt"], CancellationToken.None);
        await _git.PushAsync(seed, "main", CancellationToken.None);

        await _git.PullAsync(clone, "main", CancellationToken.None);

        Assert.Equal(second, await _git.RevParseAsync(clone, "HEAD", CancellationToken.None));
        Assert.Null(await _git.RevParseAsync(clone, "no-such-ref", CancellationToken.None));
        Assert.Equal([$"{second} second", $"{first} first"], await _git.LogAsync(clone, "HEAD", "%H %s", CancellationToken.None));
        Assert.True(await _git.ExistsAsync(clone, first, CancellationToken.None));
        Assert.False(await _git.ExistsAsync(clone, new string('0', 40), CancellationToken.None));
    }

    [Fact]
    public async Task AChangeShowsInTheStatusAndARestorePutsItBack()
    {
        var (seed, _) = await SeedAsync();
        File.WriteAllText(Path.Combine(seed, "notes.txt"), "changed");

        Assert.Equal([" M notes.txt"], await _git.StatusAsync(seed, ["notes.txt"], CancellationToken.None));

        await _git.RestoreAsync(seed, ["notes.txt"], CancellationToken.None);

        Assert.Empty(await _git.StatusAsync(seed, ["notes.txt"], CancellationToken.None));
        Assert.Equal("first", File.ReadAllText(Path.Combine(seed, "notes.txt")));
    }

    [Fact]
    public async Task ABranchIsCreatedAndLeft()
    {
        var (seed, first) = await SeedAsync();

        await _git.SwitchAsync(seed, "feature", create: true, CancellationToken.None);
        var onFeature = (await _git.RequireAsync(seed, ["branch", "--show-current"], CancellationToken.None)).StdOut.Trim();
        await _git.SwitchAsync(seed, "main", create: false, CancellationToken.None);

        Assert.Equal("feature", onFeature);
        Assert.Equal("main", (await _git.RequireAsync(seed, ["branch", "--show-current"], CancellationToken.None)).StdOut.Trim());
        Assert.Equal(first, await _git.RevParseAsync(seed, "refs/heads/feature", CancellationToken.None));
    }

    [Fact]
    public async Task AConfigValueIsReadAndAMissingKeyReadsAsNone()
    {
        var (seed, _) = await SeedAsync();

        Assert.Equal(["E2E Harness"], await _git.ConfigAsync(seed, "user.name", CancellationToken.None));
        Assert.Empty(await _git.ConfigAsync(seed, "no.such", CancellationToken.None));
    }

    [Fact]
    public async Task AFailingCommandStopsWithItsRedactedError()
    {
        var (seed, _) = await SeedAsync();

        var failure = await Assert.ThrowsAsync<E2EException>(() => _git.RequireAsync(seed, ["checkout", "password=hunter2"], CancellationToken.None));

        Assert.StartsWith($"git checkout password=hunter2 exited 1 in {seed}: ", failure.Message, StringComparison.Ordinal);
        Assert.Contains("password=***", failure.Message[failure.Message.IndexOf(": ", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", File.ReadAllText(Path.Combine(_scratch.Path, "run", "transcript.log")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureWithoutAFolderNamesTheCurrentOne()
    {
        var failure = await Assert.ThrowsAsync<E2EException>(() => _git.RequireAsync(null, ["no-such-command"], CancellationToken.None));

        Assert.Contains($" in {Environment.CurrentDirectory}: ", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTipNamesHeadMainAndWhatIsUncommitted()
    {
        var (seed, first) = await SeedAsync();

        var clean = await _git.TipAsync(seed, "main", CancellationToken.None);
        File.WriteAllText(Path.Combine(seed, "notes.txt"), "changed");
        var dirty = await _git.TipAsync(seed, "main", CancellationToken.None);

        Assert.Equal((first, first), (clean.Head, clean.Remote));
        Assert.Empty(clean.Status);
        Assert.True(clean.HoldsRemote);
        Assert.Equal([" M notes.txt"], dirty.Status);
        Assert.False(dirty.HoldsRemote);
    }

    [Fact]
    public async Task AFetchBringsTheRemoteMainWithoutTouchingTheFolder()
    {
        var (seed, first) = await SeedAsync();
        var clone = Path.Combine(_scratch.Path, "clone");
        await _git.CloneAsync(Path.Combine(_scratch.Path, "origin.git"), clone, CancellationToken.None);
        File.WriteAllText(Path.Combine(seed, "later.txt"), "later");
        var second = await _git.CommitAsync(seed, "second", ["later.txt"], CancellationToken.None);
        await _git.PushAsync(seed, "main", CancellationToken.None);

        await _git.FetchAsync(clone, CancellationToken.None);

        Assert.Equal(second, await _git.RevParseAsync(clone, "origin/main", CancellationToken.None));
        Assert.Equal(first, await _git.RevParseAsync(clone, "HEAD", CancellationToken.None));
        Assert.True(await _git.ExistsAsync(clone, "origin/main:later.txt", CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(clone, "later.txt")));
    }

    [Fact]
    public async Task ACloneShapedAsGrsMakesItIsInspectedFactByFact()
    {
        var origin = _scratch.Folder("grs.git");
        var project = Path.Combine(_scratch.Path, "project");
        var helper = _scratch.Place(Path.Combine("bin", "msagent.cmd"));
        await _git.RequireAsync(origin, ["init", "--bare", "--quiet", "-b", "main"], CancellationToken.None);
        await _git.InitAsync(project, CancellationToken.None);
        await _git.RequireAsync(project, ["remote", "add", "origin", origin], CancellationToken.None);
        File.WriteAllText(Path.Combine(project, "README.md"), "# repository");
        await _git.RequireAsync(project, ["add", "README.md"], CancellationToken.None);
        await _git.RequireAsync(project, ["-c", "user.name=GRS", "-c", "user.email=grs@powerplatform.com", "commit", "--quiet", "-m", "Initial commit"], CancellationToken.None);
        var initial = (await _git.RevParseAsync(project, "HEAD", CancellationToken.None))!;
        File.WriteAllText(Path.Combine(project, ".gitignore"), "/.config/agent.config.json\n.mcs/\n");
        Directory.CreateDirectory(Path.Combine(project, "Agent A", ".mcs"));
        File.WriteAllText(Path.Combine(project, "Agent A", "settings.mcs.yml"), "displayName: Agent A\n");
        File.WriteAllText(Path.Combine(project, "Agent A", ".mcs", "conn.json"), "{}");
        Directory.CreateDirectory(Path.Combine(project, ".config"));
        File.WriteAllText(Path.Combine(project, ".config", "agent.config.json"), "{}");
        var added = await _git.CommitAsync(project, "msagent: add MCSAgent Agent A", [".gitignore", "Agent A"], CancellationToken.None);
        await _git.PushAsync(project, "main", CancellationToken.None);
        await _git.RequireAsync(project, ["config", "--local", "--add", $"credential.https://{AmsMcsTestKit.GitHost}.helper", string.Empty], CancellationToken.None);
        await _git.RequireAsync(project, ["config", "--local", "--add", $"credential.https://{AmsMcsTestKit.GitHost}.helper", $"!AH_CLI_FEATURES=AmsMcsAgents MSAGENT_GIT_HOST={AmsMcsTestKit.GitHost} '{helper}' auth git-credential"], CancellationToken.None);

        var clone = await _git.InspectAsync(project, AmsMcsTestKit.GitHost, CancellationToken.None);

        Assert.True(clone.IsRootOf(project));
        Assert.Equal(origin, clone.Origin);
        Assert.Equal((added, "harness@example.invalid", "msagent: add MCSAgent Agent A"), (clone.Head!.Sha, clone.Head.AuthorEmail, clone.Head.Subject));
        Assert.Equal([initial], clone.Head.Parents);
        Assert.Equal((initial, "grs@powerplatform.com", 0, "Initial commit"), (clone.Parent!.Sha, clone.Parent.AuthorEmail, clone.Parent.Parents.Count, clone.Parent.Subject));
        Assert.Equal(["README.md"], clone.ParentTree);
        Assert.Equal([".gitignore", "Agent A/settings.mcs.yml"], clone.Changed);
        Assert.Empty(clone.Status!);
        Assert.Equal(["/.config/agent.config.json", ".mcs/"], clone.Ignore);
        Assert.Empty(clone.TrackedConfig!);
        Assert.Empty(clone.TrackedCache!);
        Assert.Equal(added, clone.RemoteMain);
        Assert.True(clone.ParentIsGrsInitialCommit);
        Assert.True(clone.HasMsagentHelper(AmsMcsTestKit.GitHost));
    }

    [Fact]
    public async Task AFirstCommitWithoutAnOriginHasNoParentRemoteOrHelpers()
    {
        var project = Path.Combine(_scratch.Path, "local");
        await _git.InitAsync(project, CancellationToken.None);
        File.WriteAllText(Path.Combine(project, "notes.txt"), "first");
        var first = await _git.CommitAsync(project, "first", ["notes.txt"], CancellationToken.None);
        File.WriteAllText(Path.Combine(project, "stray.txt"), "stray");

        var clone = await _git.InspectAsync(project, AmsMcsTestKit.GitHost, CancellationToken.None);

        Assert.Equal(first, clone.Head!.Sha);
        Assert.Empty(clone.Head.Parents);
        Assert.Null(clone.Parent);
        Assert.Null(clone.ParentTree);
        Assert.Equal(["notes.txt"], clone.Changed);
        Assert.Equal(["?? stray.txt"], clone.Status);
        Assert.Empty(clone.Ignore);
        Assert.Null(clone.Origin);
        Assert.Null(clone.RemoteMain);
        Assert.Null(clone.Helpers);
    }

    [Fact]
    public async Task AFolderOutsideAnyRepositoryHasNoFacts()
    {
        var git = new GitCli(_transcript, EnvironmentOverlay.Empty.Set("GIT_CEILING_DIRECTORIES", _scratch.Path).Set("GIT_CONFIG_NOSYSTEM", "1"));

        var clone = await git.InspectAsync(_scratch.Folder("plain"), AmsMcsTestKit.GitHost, CancellationToken.None);

        Assert.All(new object?[] { clone.TopLevel, clone.Origin, clone.Head, clone.Parent, clone.RemoteMain, clone.ParentTree, clone.Changed, clone.Status, clone.TrackedConfig, clone.TrackedCache, clone.Helpers }, fact => Assert.Null(fact));
        Assert.Empty(clone.Ignore);
    }

    [Fact]
    public void AProjectWithoutCopiesHasNoShelves()
    {
        Assert.Empty(GitCli.ReadShelves(_scratch.Folder("project")));
    }

    [Fact]
    public void EachIndexGivesItsFolderIdentityLiveCommitAndCopies()
    {
        var key = Path.Combine("project", ".git", "msagent", "mcs", "b-key");
        _scratch.Place(Path.Combine(key, "index.json"), """{"folder":"Agents/Contoso HR","identity":"env-1|agent-1","live":"bbbb","copies":[{"commit":"bbbb","branch":"main","savedAt":"2026-08-02T10:16:00+00:00"},{"commit":"aaaa","branch":null,"savedAt":"2026-08-02T10:15:00+00:00"}]}""");
        _scratch.Place(Path.Combine(key, "bbbb", "botdefinition.json"), "{}");
        _scratch.Place(Path.Combine(key, "aaaa", "conn.json"), "{}");
        _scratch.Folder(Path.Combine(key, "cccc"));
        _scratch.Folder(Path.Combine("project", ".git", "msagent", "mcs", "a-key"));

        var shelves = GitCli.ReadShelves(Path.Combine(_scratch.Path, "project"));

        Assert.Equal(["a-key", "b-key"], shelves.Select(shelf => shelf.Key));
        var shelf = shelves[1];
        Assert.Equal(Path.Combine(_scratch.Path, key), shelf.KeyDirectory);
        Assert.Equal("Agents/Contoso HR", shelf.Folder);
        Assert.Equal("env-1|agent-1", shelf.Identity);
        Assert.Equal("bbbb", shelf.Live);
        Assert.Equal([new McsShelfCopy("bbbb", "main", DateTimeOffset.Parse("2026-08-02T10:16:00+00:00", System.Globalization.CultureInfo.InvariantCulture)), new McsShelfCopy("aaaa", null, DateTimeOffset.Parse("2026-08-02T10:15:00+00:00", System.Globalization.CultureInfo.InvariantCulture))], shelf.Copies);
        Assert.Equal(["aaaa", "bbbb"], shelf.Stored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("null")]
    public void AMissingOrUnreadableIndexMeansNoCopies(string? index)
    {
        var key = Path.Combine("project", ".git", "msagent", "mcs", "key");
        _scratch.Place(Path.Combine(key, "aaaa", "conn.json"), "{}");

        if (index is not null)
        {
            _scratch.Place(Path.Combine(key, "index.json"), index);
        }

        var shelf = Assert.Single(GitCli.ReadShelves(Path.Combine(_scratch.Path, "project")));

        Assert.Null(shelf.Folder);
        Assert.Null(shelf.Identity);
        Assert.Null(shelf.Live);
        Assert.Empty(shelf.Copies);
        Assert.Equal(["aaaa"], shelf.Stored);
    }

    [Theory]
    [InlineData("Agents/Contoso HR", "Agents/Contoso HR", true)]
    [InlineData("Agents/Contoso HR", "agents\\contoso hr\\", true)]
    [InlineData("Agents/Contoso HR", "Contoso HR", true)]
    [InlineData("Agents/Contoso HR", "HR", false)]
    [InlineData("Agents/Contoso HR", "Other", false)]
    [InlineData(null, "Contoso HR", false)]
    [InlineData("", "Contoso HR", false)]
    public void AShelfNamesItsFolderWithoutCaseOrSeparatorDifferences(string? folder, string candidate, bool named)
    {
        Assert.Equal(named, new McsShelf("key", "directory", folder, null, null, [], []).Names(candidate));
    }

    [Fact]
    public async Task ACloneWithAHelperUsesItForThatCommandOnly()
    {
        var (_, first) = await SeedAsync();
        var clone = Path.Combine(_scratch.Path, "mate");

        await _git.CloneWithHelperAsync(Path.Combine(_scratch.Path, "origin.git"), clone, AmsMcsTestKit.GitHost, "store", CancellationToken.None);

        Assert.Equal(first, await _git.RevParseAsync(clone, "HEAD", CancellationToken.None));
        Assert.Null(await _git.HelpersAsync(clone, AmsMcsTestKit.GitHost, CancellationToken.None));
        Assert.Contains($"git -c credential.https://{AmsMcsTestKit.GitHost}.helper= -c credential.https://{AmsMcsTestKit.GitHost}.helper=store clone --quiet", File.ReadAllText(Path.Combine(_scratch.Path, "run", "transcript.log")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLastHelperIsTheOneGitUsesAndAFolderWithoutHelpersHasNone()
    {
        var (seed, _) = await SeedAsync();
        var none = await _git.LastHelperAsync(seed, AmsMcsTestKit.GitHost, CancellationToken.None);
        await _git.RequireAsync(seed, ["config", "--local", "--add", $"credential.https://{AmsMcsTestKit.GitHost}.helper", string.Empty], CancellationToken.None);
        await _git.RequireAsync(seed, ["config", "--local", "--add", $"credential.https://{AmsMcsTestKit.GitHost}.helper", "!msagent auth git-credential"], CancellationToken.None);

        Assert.Equal(string.Empty, none);
        Assert.Equal("!msagent auth git-credential", await _git.LastHelperAsync(seed, AmsMcsTestKit.GitHost, CancellationToken.None));
    }

    [Fact]
    public async Task AnIgnoredFileIsAddedOnlyWhenForced()
    {
        var (seed, _) = await SeedAsync();
        File.WriteAllText(Path.Combine(seed, ".gitignore"), "ignored.txt\n");
        File.WriteAllText(Path.Combine(seed, "ignored.txt"), "kept out");

        await Assert.ThrowsAsync<E2EException>(() => _git.AddAsync(seed, ["ignored.txt"], force: false, CancellationToken.None));
        await _git.AddAsync(seed, [".gitignore", "ignored.txt"], force: true, CancellationToken.None);
        var commit = await _git.CommitStagedAsync(seed, "track the ignored file", CancellationToken.None);

        Assert.Equal([".gitignore", "ignored.txt", "notes.txt"], await _git.FilesAsync(seed, commit, CancellationToken.None));
    }

    [Fact]
    public async Task ARemovalIsStagedThenCommitted()
    {
        var (seed, _) = await SeedAsync();
        Directory.CreateDirectory(Path.Combine(seed, "Agent"));
        File.WriteAllText(Path.Combine(seed, "Agent", "settings.mcs.yml"), "displayName: A\n");
        await _git.CommitAsync(seed, "add the agent", ["Agent"], CancellationToken.None);

        await _git.RemoveAsync(seed, ["Agent", "notes.txt"], cached: false, CancellationToken.None);
        var staged = await _git.StatusAsync(seed, [], CancellationToken.None);
        var commit = await _git.CommitStagedAsync(seed, "remove everything", CancellationToken.None);

        Assert.Equal(["D  Agent/settings.mcs.yml", "D  notes.txt"], staged);
        Assert.Empty((await _git.FilesAsync(seed, commit, CancellationToken.None))!);
        Assert.False(Directory.Exists(Path.Combine(seed, "Agent")));
    }

    [Fact]
    public async Task ACachedRemovalUntracksTheFolderAndKeepsItsFilesOnDisk()
    {
        var (seed, _) = await SeedAsync();
        var state = _scratch.Place(Path.Combine("seed", "Agent", ".mcs", "workflow-state.json"), "{}");
        await _git.AddAsync(seed, ["Agent"], force: true, CancellationToken.None);
        await _git.CommitStagedAsync(seed, "track the cache", CancellationToken.None);

        await _git.RemoveAsync(seed, ["Agent/.mcs"], cached: true, CancellationToken.None);
        var staged = await _git.StatusAsync(seed, [], CancellationToken.None);
        var commit = await _git.CommitStagedAsync(seed, "untrack the cache", CancellationToken.None);

        Assert.Equal(["D  Agent/.mcs/workflow-state.json", "?? Agent/"], staged);
        Assert.Equal(["notes.txt"], await _git.FilesAsync(seed, commit, CancellationToken.None));
        Assert.Equal("{}", File.ReadAllText(state));
    }

    [Fact]
    public async Task ASquashLeavesOneCommitWithTheMessageAndTheLastTree()
    {
        var (seed, first) = await SeedAsync();
        File.WriteAllText(Path.Combine(seed, "second.txt"), "second");
        await _git.CommitAsync(seed, "second", ["second.txt"], CancellationToken.None);

        var squashed = await _git.SquashAsync(seed, "squashed", CancellationToken.None);

        Assert.Equal(squashed, await _git.RevParseAsync(seed, "HEAD", CancellationToken.None));
        Assert.NotEqual(first, squashed);
        Assert.Equal([$"{squashed} squashed"], await _git.LogAsync(seed, "HEAD", "%H %s", CancellationToken.None));
        Assert.Equal(["notes.txt", "second.txt"], await _git.FilesAsync(seed, squashed, CancellationToken.None));
    }

    [Fact]
    public async Task TheCacheHistoryListsEachCommitThatTouchedACacheOnAnyRefUntilNoRefHoldsThem()
    {
        var (seed, _) = await SeedAsync();
        _scratch.Place(Path.Combine("seed", ".mcs", "conn.json"), "{}");
        _scratch.Place(Path.Combine("seed", "Agent", ".mcs", "workflow-state.json"), "{}");
        _scratch.Place(Path.Combine("seed", "Agent", "settings.mcs.yml"), "displayName: A\n");
        await _git.AddAsync(seed, [".mcs", "Agent"], force: true, CancellationToken.None);
        await _git.CommitStagedAsync(seed, "track the caches", CancellationToken.None);
        await _git.RemoveAsync(seed, [".mcs"], cached: false, CancellationToken.None);
        await _git.CommitStagedAsync(seed, "drop the root cache", CancellationToken.None);
        await _git.SwitchAsync(seed, "side", create: true, CancellationToken.None);
        await _git.SwitchAsync(seed, "main", create: false, CancellationToken.None);

        var listed = await _git.CacheHistoryAsync(seed, CancellationToken.None);
        await _git.RemoveAsync(seed, ["Agent/.mcs"], cached: true, CancellationToken.None);
        await _git.SquashAsync(seed, "squashed", CancellationToken.None);
        var onSide = await _git.CacheHistoryAsync(seed, CancellationToken.None);
        await _git.RequireAsync(seed, ["branch", "--quiet", "-D", "side"], CancellationToken.None);

        Assert.Equal([".mcs/conn.json", ".mcs/conn.json", "Agent/.mcs/workflow-state.json"], listed);
        Assert.Equal(listed, onSide);
        Assert.Empty(await _git.CacheHistoryAsync(seed, CancellationToken.None));
    }

    [Fact]
    public async Task AConfigValueIsSetInTheRepository()
    {
        var (seed, _) = await SeedAsync();

        await _git.SetConfigAsync(seed, "core.autocrlf", "true", CancellationToken.None);

        Assert.Equal(["true"], await _git.ConfigAsync(seed, "core.autocrlf", CancellationToken.None));
    }

    [Fact]
    public async Task AFileAndItsSizeAreReadAsARevisionHoldsThem()
    {
        var (seed, first) = await SeedAsync();
        File.WriteAllText(Path.Combine(seed, "notes.txt"), "second\nline\n");
        var second = await _git.CommitAsync(seed, "second", ["notes.txt"], CancellationToken.None);

        Assert.Equal("first", await _git.ShowFileAsync(seed, first, "notes.txt", CancellationToken.None));
        Assert.Equal("second\nline", await _git.ShowFileAsync(seed, second, "notes.txt", CancellationToken.None));
        Assert.Null(await _git.ShowFileAsync(seed, second, "missing.txt", CancellationToken.None));
        Assert.Equal(5, await _git.BlobSizeAsync(seed, $"{first}:notes.txt", CancellationToken.None));
        Assert.Null(await _git.BlobSizeAsync(seed, $"{first}:missing.txt", CancellationToken.None));
    }

    [Fact]
    public async Task TheCapturesTipIsTheFetchedBranchOfItsDeployment()
    {
        var (seed, first) = await SeedAsync();
        var clone = Path.Combine(_scratch.Path, "clone");
        await _git.CloneAsync(Path.Combine(_scratch.Path, "origin.git"), clone, CancellationToken.None);
        var none = await _git.CapturesTipAsync(clone, "deployment-1", CancellationToken.None);
        await _git.RequireAsync(seed, ["push", "--quiet", "origin", "HEAD:refs/heads/ams/captures/deployment-1"], CancellationToken.None);
        File.WriteAllText(Path.Combine(seed, "notes.txt"), "other");
        var other = await _git.CommitAsync(seed, "other", ["notes.txt"], CancellationToken.None);
        await _git.RequireAsync(seed, ["push", "--quiet", "origin", "HEAD:refs/heads/ams/captures/deployment-2"], CancellationToken.None);

        Assert.Null(none);
        Assert.Equal(first, await _git.CapturesTipAsync(clone, "DEPLOYMENT-1", CancellationToken.None));
        Assert.Equal(other, await _git.CapturesTipAsync(clone, "deployment-2", CancellationToken.None));
        Assert.Null(await _git.CapturesTipAsync(clone, "deployment-3", CancellationToken.None));
    }

    [Fact]
    public async Task HistoryIsListedNewestFirstAndAncestryIsAnswered()
    {
        var (seed, first) = await SeedAsync();
        File.WriteAllText(Path.Combine(seed, "notes.txt"), "second");
        var second = await _git.CommitAsync(seed, "second", ["notes.txt"], CancellationToken.None);

        Assert.Equal([second, first], await _git.RevListAsync(seed, "HEAD", CancellationToken.None));
        Assert.Empty(await _git.RevListAsync(seed, "no-such-ref", CancellationToken.None));
        Assert.True(await _git.IsAncestorAsync(seed, first, second, CancellationToken.None));
        Assert.False(await _git.IsAncestorAsync(seed, second, first, CancellationToken.None));
    }

    [Fact]
    public async Task AFileIsModifiedWhenItDiffersFromHead()
    {
        var (seed, _) = await SeedAsync();
        var clean = await _git.IsModifiedAsync(seed, "notes.txt", CancellationToken.None);
        File.WriteAllText(Path.Combine(seed, "notes.txt"), "changed");

        Assert.False(clean);
        Assert.True(await _git.IsModifiedAsync(seed, "notes.txt", CancellationToken.None));
    }

    [Fact]
    public async Task TheCurrentBranchIsNamedAndADetachedHeadOrAFolderOutsideGitHasNone()
    {
        var (seed, first) = await SeedAsync();
        var onMain = await _git.CurrentBranchAsync(seed, CancellationToken.None);
        await _git.RequireAsync(seed, ["switch", "--quiet", "--detach", first], CancellationToken.None);
        var outside = new GitCli(_transcript, EnvironmentOverlay.Empty.Set("GIT_CEILING_DIRECTORIES", _scratch.Path).Set("GIT_CONFIG_NOSYSTEM", "1"));

        Assert.Equal("main", onMain);
        Assert.Null(await _git.CurrentBranchAsync(seed, CancellationToken.None));
        Assert.Null(await outside.CurrentBranchAsync(_scratch.Folder("plain"), CancellationToken.None));
    }

    private async Task<(string Seed, string Commit)> SeedAsync()
    {
        var origin = _scratch.Folder("origin.git");
        var seed = Path.Combine(_scratch.Path, "seed");
        await _git.RequireAsync(origin, ["init", "--bare", "--quiet", "-b", "main"], CancellationToken.None);
        await _git.InitAsync(seed, CancellationToken.None);
        await _git.RequireAsync(seed, ["remote", "add", "origin", origin], CancellationToken.None);
        File.WriteAllText(Path.Combine(seed, "notes.txt"), "first");
        var commit = await _git.CommitAsync(seed, "first", ["notes.txt"], CancellationToken.None);
        await _git.PushAsync(seed, "main", CancellationToken.None);
        return (seed, commit);
    }
}
