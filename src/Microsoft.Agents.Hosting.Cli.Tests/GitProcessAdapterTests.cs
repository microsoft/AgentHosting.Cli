using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Playground;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.Ams;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class GitProcessAdapterTests
{
    private const string Url = "https://22222222222222222222222222222222.22.environment.api.powerplatform.com/appframework/git/repositories/cccccccc-3333-4333-8333-000000000003";
    private const string HelperKey = "credential.https://22222222222222222222222222222222.22.environment.api.powerplatform.com.helper";
    private const string Helper = "!AH_CLI_FEATURES=AmsMcsAgents msagent auth git-credential";

    private static readonly string GitDirectory = Path.Combine(Path.GetTempPath(), "git-adapter-tests", "cmd");
    private static readonly string Root = Path.GetFullPath(Path.GetTempPath());

    private readonly Dictionary<string, PlaygroundProcessResult> _answers = new(StringComparer.Ordinal);
    private readonly PlaygroundToolLocatorTests.RecordingProcessRunner _runner;

    public GitProcessAdapterTests() => _runner = new PlaygroundToolLocatorTests.RecordingProcessRunner(request => _answers.TryGetValue(string.Join(' ', request.Arguments), out var answer) ? answer : Ok());

    private static string GitPath => Path.GetFullPath(Path.Combine(GitDirectory, "git.exe"));

    [Fact]
    public async Task FindWorkingTree_ReadsTheRootRemotesBranchAndHistory_WithoutEverPrompting()
    {
        _answers["rev-parse --show-toplevel"] = Ok(Root + "\n");
        _answers["remote -v"] = Ok($"origin\t{Url} (fetch)\norigin\t{Url} (push)\nfork\thttps://example.com/x.git (fetch)\n");
        _answers["symbolic-ref --quiet --short HEAD"] = Ok("main\n");

        var tree = await Adapter().FindWorkingTreeAsync(Root, default);

        Assert.Equal(Root, tree!.Root);
        Assert.Equal([new GitRemote("origin", Url), new GitRemote("fork", "https://example.com/x.git")], tree.Remotes);
        Assert.Equal(("main", true), (tree.Branch, tree.HasCommits));
        Assert.All(_runner.Requests, request => Assert.Equal((GitPath, "0", GitProcessAdapter.LocalTimeout), (request.ExecutablePath, request.EnvironmentOverrides!["GIT_TERMINAL_PROMPT"], request.Timeout)));
    }

    [Fact]
    public async Task FindWorkingTree_OutsideARepository_IsNull()
    {
        _answers["rev-parse --show-toplevel"] = Failed(128, "fatal: not a git repository (or any of the parent directories): .git");

        Assert.Null(await Adapter().FindWorkingTreeAsync(Root, default));
        Assert.Single(_runner.Requests);
    }

    [Fact]
    public async Task FindWorkingTree_OfAMissingFolder_RunsNothing()
    {
        Assert.Null(await Adapter().FindWorkingTreeAsync(Path.Combine(Root, Guid.NewGuid().ToString("N")), default));
        Assert.Empty(_runner.Requests);
    }

    [Fact]
    public async Task Clone_SignsInWithMsagentsHelperForTheRepositorysHostOnly()
    {
        var target = Path.Combine(Root, "contoso");

        await Adapter().CloneAsync(Url, target, default);

        var clone = _runner.Requests[0];
        Assert.Equal(["-c", $"{HelperKey}=", "-c", $"{HelperKey}={Helper}", "clone", Url, target], clone.Arguments);
        Assert.Equal(GitProcessAdapter.NetworkTimeout, clone.Timeout);
        Assert.Equal(2, _runner.Requests.Count);
    }

    [Fact]
    public async Task Clone_OfAnEmptyRepository_PointsHeadAtMain()
    {
        var target = Path.Combine(Root, "contoso");
        _answers["rev-parse --verify --quiet HEAD"] = Failed(1, string.Empty);

        await Adapter().CloneAsync(Url, target, default);

        Assert.Equal(["symbolic-ref", "HEAD", "refs/heads/main"], _runner.Requests[^1].Arguments);
        Assert.Equal(target, _runner.Requests[^1].WorkingDirectory);
    }

    [Fact]
    public async Task InitAndRemotes_MakeMainTheFirstBranch_AndAddOrRemoveTheRemote()
    {
        await Adapter().InitAsync(Root, default);
        await Adapter().AddRemoteAsync(Root, "origin", Url, default);
        await Adapter().RemoveRemoteAsync(Root, "origin", default);

        Assert.Equal([["init", "--initial-branch=main"], ["remote", "add", "origin", Url], ["remote", "remove", "origin"]], _runner.Requests.Select(request => request.Arguments.ToArray()));
    }

    [Theory]
    [InlineData(0, "", true)]
    [InlineData(128, "fatal: couldn't find remote ref main", false)]
    public async Task Fetch_SignsInForTheRemotesUrl_AndAnEmptyRemoteHasNoMain(int exitCode, string standardError, bool fetched)
    {
        _answers["remote get-url origin"] = Ok(Url + "\n");
        _answers[$"-c {HelperKey}= -c {HelperKey}={Helper} fetch origin main"] = new PlaygroundProcessResult(exitCode, string.Empty, standardError);

        Assert.Equal(fetched, await Adapter().FetchBranchAsync(Root, "origin", "main", default));
        Assert.Equal(GitProcessAdapter.NetworkTimeout, _runner.Requests[^1].Timeout);
    }

    [Fact]
    public async Task Listings_ReadGitsNulSeparatedPaths_WithSpacesQuotesAndNonAsciiIntact()
    {
        _answers["ls-tree -r -z --name-only origin/main"] = Ok("Contoso HR/topics/Greeting.mcs.yml\0\"quoted\".txt\0Café/a.yml\0");
        _answers["ls-files -z -- Contoso HR"] = Ok("Contoso HR/.mcs/botdefinition.json\0");
        _answers["status --porcelain=v1 -z --untracked-files=all -- Contoso HR Café"] = Ok(" M Contoso HR/a.yml\0?? Café/b.yml\0R  Contoso HR/new name.yml\0Contoso HR/old name.yml\0");
        var adapter = Adapter();

        Assert.Equal(["Contoso HR/topics/Greeting.mcs.yml", "\"quoted\".txt", "Café/a.yml"], await adapter.ListTreeAsync(Root, "origin/main", default));
        Assert.Equal(["Contoso HR/.mcs/botdefinition.json"], await adapter.ListTrackedAsync(Root, ["Contoso HR"], default));
        Assert.Equal(["Contoso HR/a.yml", "Café/b.yml", "Contoso HR/new name.yml"], await adapter.StatusAsync(Root, ["Contoso HR", "Café"], default));
        Assert.Empty(await adapter.StatusAsync(Root, [], default));
        Assert.Equal(3, _runner.Requests.Count);
    }

    [Fact]
    public async Task Commit_AddsAndCommitsOnlyTheNamedPaths_LeavingOutEveryMcsFolder_AndGivesTheNewHead()
    {
        _answers["rev-parse --verify --quiet HEAD^{commit}"] = Ok("2fd4e1c67a2d28fced849ee1bb76e7391b93eb12\n");

        var committed = await Adapter().CommitAsync(Root, ["ContosoHR", ".gitignore"], "Add agent Contoso HR", default);

        Assert.Equal(new GitCommitResult("2fd4e1c67a2d28fced849ee1bb76e7391b93eb12", null), committed);
        Assert.Equal([["add", "--", "ContosoHR", ".gitignore", ":(exclude,icase).mcs/*", ":(exclude,icase)*/.mcs/*"], ["commit", "-m", "Add agent Contoso HR", "--", "ContosoHR", ".gitignore", ":(exclude,icase).mcs/*", ":(exclude,icase)*/.mcs/*"], ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"]], _runner.Requests.Select(request => request.Arguments.ToArray()));
    }

    [Fact]
    public async Task Commit_OfARealRepository_LeavesOutEveryMcsFileAtAnyDepthAndInAnyCase_TrackedOrNot()
    {
        using var ams = new AmsHarness();
        await RealGit.InitAsync(ams.Root);
        RealGit.Write(ams.Root, "Contoso HR/settings.mcs.yml", "displayName: Contoso HR\n");
        RealGit.Write(ams.Root, "Contoso HR/.mcs/conn.json", "{}");
        await RealGit.RunAsync(ams.Root, "add", "--all");
        await RealGit.RunAsync(ams.Root, "commit", "--quiet", "-m", "Seed");
        RealGit.Write(ams.Root, "Contoso HR/settings.mcs.yml", "displayName: Contoso Help\n");
        RealGit.Write(ams.Root, "Contoso HR/.mcs/conn.json", "{\"agentId\":\"x\"}");
        RealGit.Write(ams.Root, "Contoso HR/agents/Café/.MCS/state.json", "{}");

        var committed = await RealGit.Adapter().CommitAsync(ams.Root, ["Contoso HR"], "Rename Contoso HR", default);

        Assert.Equal(committed.CommitSha, Assert.Single(await RealGit.RunAsync(ams.Root, "rev-parse", "HEAD")));
        Assert.Equal(["Contoso HR/settings.mcs.yml"], await RealGit.RunAsync(ams.Root, "show", "--name-only", "--format=", "HEAD"));
        Assert.Equal(["M \"Contoso HR/.mcs/conn.json\"", "?? \"Contoso HR/agents/\""], await RealGit.RunAsync(ams.Root, "status", "--porcelain=v1"));
    }

    [Fact]
    public async Task ListCommittedPaths_ListsWhatTheCommitsAddOrChange_UnderThePathspecs_FromGitsNulSeparatedNames()
    {
        _answers["log --format= --name-only -z --diff-filter=AM HEAD --not --remotes=origin -- :(icase).mcs/* :(icase)*/.mcs/*"] = Ok("Contoso HR/.mcs/conn.json\0Café/.MCS/state.json\0Contoso HR/.mcs/conn.json\0");

        var paths = await Adapter().ListCommittedPathsAsync(Root, ["HEAD", "--not", "--remotes=origin"], SyncStatePathspecs.Included, default);

        Assert.Equal(["Contoso HR/.mcs/conn.json", "Café/.MCS/state.json", "Contoso HR/.mcs/conn.json"], paths);
        Assert.Equal(["log", "--format=", "--name-only", "-z", "--diff-filter=AM", "HEAD", "--not", "--remotes=origin", "--", ":(icase).mcs/*", ":(icase)*/.mcs/*"], _runner.Requests.Single().Arguments);
        Assert.Equal(GitProcessAdapter.LocalTimeout, _runner.Requests.Single().Timeout);
    }

    [Fact]
    public async Task CommitAndPush_ThatGitRefuses_GiveGitsReasonOnOneLine()
    {
        _answers["commit -m Add -- ContosoHR :(exclude,icase).mcs/* :(exclude,icase)*/.mcs/*"] = Failed(128, "Author identity unknown\n\n*** Please tell me who you are.\n");
        _answers["remote get-url origin"] = Ok(Url + "\n");
        _answers[$"-c {HelperKey}= -c {HelperKey}={Helper} push -u origin main"] = Failed(1, " ! [rejected]        main -> main (fetch first)\n");
        var adapter = Adapter();

        Assert.Equal(new GitCommitResult(null, "Author identity unknown *** Please tell me who you are."), await adapter.CommitAsync(Root, ["ContosoHR"], "Add", default));
        Assert.Equal(new GitPushResult(false, "! [rejected] main -> main (fetch first)"), await adapter.PushAsync(Root, "origin", "main", null, default));
    }

    [Fact]
    public async Task Push_ThatReplacesACommit_IsLeasedOnExactlyThatCommit_AndSignsIn()
    {
        _answers["remote get-url origin"] = Ok(Url + "\n");

        var pushed = await Adapter().PushAsync(Root, "origin", "main", "01cc0e77a1b2c3d4e5f60718293a4b5c6d7e8f90", default);

        Assert.Equal(new GitPushResult(true, null), pushed);
        Assert.Equal(["-c", $"{HelperKey}=", "-c", $"{HelperKey}={Helper}", "push", "--force-with-lease=main:01cc0e77a1b2c3d4e5f60718293a4b5c6d7e8f90", "-u", "origin", "main"], _runner.Requests[^1].Arguments);
        Assert.Equal(GitProcessAdapter.NetworkTimeout, _runner.Requests[^1].Timeout);
    }

    [Fact]
    public async Task ResolveCommitAndMergeBase_OfUnknownRevisions_AreNull()
    {
        _answers["rev-parse --verify --quiet nope^{commit}"] = Failed(1, string.Empty);
        _answers["merge-base HEAD origin/main"] = Failed(1, string.Empty);
        var adapter = Adapter();

        Assert.Null(await adapter.ResolveCommitAsync(Root, "nope", default));
        Assert.Null(await adapter.MergeBaseAsync(Root, "HEAD", "origin/main", default));
    }

    [Fact]
    public async Task ListLocalBranches_NamesEachBranchAsSymbolicRefNamesTheCheckedOutOne()
    {
        _answers["-c core.warnAmbiguousRefs=false for-each-ref --format=%(refname:short) refs/heads/"] = Ok("main\r\nnguhoa/skill\nheads/v1\n");

        Assert.Equal(["main", "nguhoa/skill", "heads/v1"], await Adapter().ListLocalBranchesAsync(Root, default));
        Assert.Equal((GitProcessAdapter.LocalTimeout, Root), (_runner.Requests.Single().Timeout, _runner.Requests.Single().WorkingDirectory));
    }

    [Fact]
    public async Task ListLocalBranches_ThatGitRefuses_IsGitCommandFailed()
    {
        _answers["-c core.warnAmbiguousRefs=false for-each-ref --format=%(refname:short) refs/heads/"] = Failed(128, "fatal: not a git repository\n");

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => Adapter().ListLocalBranchesAsync(Root, default));

        Assert.Equal(ErrorCodes.AmsAgent.GitCommandFailed, failure.ErrorCode);
        Assert.Contains("git for-each-ref", failure.Message, StringComparison.Ordinal);
        Assert.Contains("fatal: not a git repository", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListLocalBranches_ThatCannotStart_NamesGitForEachRef()
    {
        var adapter = Adapter(new PlaygroundToolLocatorTests.RecordingProcessRunner(_ => throw new TimeoutException("The process exceeded the configured timeout.")));

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => adapter.ListLocalBranchesAsync(Root, default));

        Assert.Equal(ErrorCodes.AmsAgent.GitCommandFailed, failure.ErrorCode);
        Assert.Contains("git for-each-ref", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("  origin/main\n", true)]
    [InlineData("", false)]
    public async Task IsOnRemoteBranch_AsksForTheRemotesBranchesThatContainTheCommit(string output, bool pushed)
    {
        _answers["branch -r --contains 9fceb02 --list origin/*"] = Ok(output);

        Assert.Equal(pushed, await Adapter().IsOnRemoteBranchAsync(Root, "9fceb02", "origin", default));
    }

    [Fact]
    public async Task GitDirectory_IsTheWorkingTreesOwnAbsoluteGitFolder()
    {
        _answers["rev-parse --absolute-git-dir"] = Ok(Path.Combine(Root, "repo", ".git", "worktrees", "feature").Replace('\\', '/') + "\n");

        Assert.Equal(Path.Combine(Root, "repo", ".git", "worktrees", "feature"), await Adapter().GitDirectoryAsync(Root, default));
        Assert.Equal(GitProcessAdapter.LocalTimeout, _runner.Requests.Single().Timeout);
    }

    [Fact]
    public async Task Restore_RestoresOnlyTheWorkingTreeOfTheNamedPaths_FromTheCommit()
    {
        await Adapter().RestoreAsync(Root, "9fceb02d0ae598e95dc970b74767f19372d61af8", ["Contoso HR", "Café"], default);

        Assert.Equal(["restore", "--source=9fceb02d0ae598e95dc970b74767f19372d61af8", "--worktree", "--", "Contoso HR", "Café"], _runner.Requests.Single().Arguments);
        Assert.Equal(GitProcessAdapter.LocalTimeout, _runner.Requests.Single().Timeout);
    }

    [Fact]
    public async Task GitDirectoryAndRestore_ThatGitRefuses_AreGitCommandFailed()
    {
        _answers["rev-parse --absolute-git-dir"] = Failed(128, "fatal: not a git repository\n");
        _answers["restore --source=9fceb02 --worktree -- ContosoHR"] = Failed(1, "error: pathspec 'ContosoHR' did not match any file(s) known to git\n");
        var adapter = Adapter();

        var gitDirectory = await Assert.ThrowsAsync<AmsAgentException>(() => adapter.GitDirectoryAsync(Root, default));
        var restore = await Assert.ThrowsAsync<AmsAgentException>(() => adapter.RestoreAsync(Root, "9fceb02", ["ContosoHR"], default));

        Assert.Equal((ErrorCodes.AmsAgent.GitCommandFailed, ErrorCodes.AmsAgent.GitCommandFailed), (gitDirectory.ErrorCode, restore.ErrorCode));
        Assert.Contains("git restore", restore.Message, StringComparison.Ordinal);
        Assert.Contains("did not match any file(s) known to git", restore.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetRepositoryHelper_ReplacesTheRepositorysHelpersForItsHostWithMsagents()
    {
        await Adapter().SetRepositoryHelperAsync(Root, Url, default);

        Assert.Equal([["config", "--local", "--replace-all", HelperKey, string.Empty], ["config", "--local", "--add", HelperKey, Helper]], _runner.Requests.Select(request => request.Arguments.ToArray()));
    }

    [Fact]
    public async Task AFailingCommand_IsGitCommandFailed_WithGitsReasonOnOneLine()
    {
        _answers[$"remote add origin {Url}"] = Failed(3, "error: remote origin\n already exists.\n");

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => Adapter().AddRemoteAsync(Root, "origin", Url, default));

        Assert.Equal((ErrorCodes.AmsAgent.GitCommandFailed, ExitCodes.Failure), (failure.ErrorCode, failure.ExitCode));
        Assert.Contains("git remote", failure.Message, StringComparison.Ordinal);
        Assert.Contains("error: remote origin already exists.", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProcessThatCannotStart_IsGitCommandFailed()
    {
        var adapter = Adapter(new PlaygroundToolLocatorTests.RecordingProcessRunner(_ => throw new TimeoutException("The process exceeded the configured timeout.")));

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => adapter.InitAsync(Root, default));

        Assert.Equal(ErrorCodes.AmsAgent.GitCommandFailed, failure.ErrorCode);
        Assert.Contains("The process exceeded the configured timeout.", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutGitOnPath_EveryCommandIsGitNotFound_AndRunsNothing()
    {
        var adapter = Adapter(environment: new PlaygroundToolLocatorTests.FakePlaygroundEnvironment(isWindows: true, pathValue: GitDirectory, existingPaths: []));

        var required = Assert.Throws<AuthenticationException>(adapter.RequireGit);
        var init = await Assert.ThrowsAsync<AuthenticationException>(() => adapter.InitAsync(Root, default));

        Assert.Equal((ErrorCodes.Authentication.GitNotFound, "git was not found on PATH, and AMS mode needs it."), (required.ErrorCode, required.Message));
        Assert.Equal(required.ErrorCode, init.ErrorCode);
        Assert.Empty(_runner.Requests);
    }

    private GitProcessAdapter Adapter(PlaygroundToolLocatorTests.RecordingProcessRunner? runner = null, PlaygroundToolLocatorTests.FakePlaygroundEnvironment? environment = null)
    {
        var processes = runner ?? _runner;
        var tools = new PlaygroundToolLocator(processes, environment ?? new PlaygroundToolLocatorTests.FakePlaygroundEnvironment(isWindows: true, pathValue: GitDirectory, existingPaths: [GitPath]));
        return new GitProcessAdapter(processes, tools, new GitCredentialHelper(tools, new GitHostTrust(PowerPlatformApiTargetConfig.Public, new AmsEndpointProfile())));
    }

    private static PlaygroundProcessResult Ok(string output = "") => new(0, output, string.Empty);

    private static PlaygroundProcessResult Failed(int exitCode, string standardError) => new(exitCode, string.Empty, standardError);
}
