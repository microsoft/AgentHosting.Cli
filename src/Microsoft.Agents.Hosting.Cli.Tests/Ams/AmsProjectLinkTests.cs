using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsProjectLinkTests : IDisposable
{
    private const string Subject = "agent 'Contoso HR'";

    private readonly AmsHarness _ams = new();

    public void Dispose() => _ams.Dispose();

    [Theory]
    [InlineData(AmsRepositoryAction.Clone, "Clone repository 'contoso-agents' (" + McsRecords.RepositoryId + ") into {0} and add agent 'Contoso HR' to it? [Y/n]")]
    [InlineData(AmsRepositoryAction.Attach, "Link {0} to repository 'contoso-agents' (" + McsRecords.RepositoryId + ") and add agent 'Contoso HR' to it? [Y/n]")]
    [InlineData(AmsRepositoryAction.InitAndLink, "Create a GRS repository in environment 'Contoso Dev' for {0} and push agent 'Contoso HR' to it? [Y/n]")]
    [InlineData(AmsRepositoryAction.AddOrigin, "Create a GRS repository in environment 'Contoso Dev' for {0} and push agent 'Contoso HR' to it? [Y/n]")]
    public async Task Confirm_AsksOnce_NamingTheFolderAndTheRepository(AmsRepositoryAction action, string question)
    {
        var repository = action is AmsRepositoryAction.Clone or AmsRepositoryAction.Attach ? McsRecords.Repository() : null;

        Assert.True(await _ams.Link.ConfirmAsync(new AmsRepositoryChoice(repository?.RepositoryId, action, null), _ams.Root, Subject, repository, "Contoso Dev", nonInteractive: false, default));
        Assert.Equal([string.Format(System.Globalization.CultureInfo.InvariantCulture, question, _ams.Root)], _ams.Prompt.Questions);
    }

    [Fact]
    public async Task Confirm_GivesTheOperatorsNo()
    {
        _ams.Prompt.Answer = false;

        Assert.False(await _ams.Link.ConfirmAsync(new AmsRepositoryChoice(null, AmsRepositoryAction.InitAndLink, null), _ams.Root, Subject, null, "Contoso Dev", nonInteractive: false, default));
    }

    [Fact]
    public async Task TheGuard_ReadsNoHistory_ForAnAttach_WhichPushesNoLocalHistory()
    {
        _ams.Git.Committed.Add($"{AmsHarness.AgentFolder}/.mcs/conn.json");

        await _ams.Link.RequireNoSyncStateAsync(new AmsRepositoryChoice(McsRecords.RepositoryId, AmsRepositoryAction.Attach, WorkingTree(hasCommits: true)), _ams.Root, [], joined: false, default);

        Assert.Empty(_ams.Git.Calls);
    }

    [Fact]
    public void TheLink_NeedsTheSyncStateRule() =>
        Assert.Equal("syncState", Assert.Throws<ArgumentNullException>(() => new AmsProjectLink(_ams.Git, _ams.Prompt, null!)).ParamName);

    [Theory]
    [InlineData(AmsRepositoryAction.InitAndLink, true)]
    [InlineData(AmsRepositoryAction.None, false)]
    public async Task Confirm_NeverAsks_WhenTheCommandMayNotAsk_OrTheFolderIsAlreadyLinked(AmsRepositoryAction action, bool nonInteractive)
    {
        Assert.True(await _ams.Link.ConfirmAsync(new AmsRepositoryChoice(null, action, null), _ams.Root, Subject, null, "Contoso Dev", nonInteractive, default));
        Assert.Empty(_ams.Prompt.Questions);
    }

    [Fact]
    public async Task Clone_ClonesTheRepository_AndSavesMsagentsHelperInIt()
    {
        var warnings = await _ams.Link.JoinAsync(new AmsRepositoryChoice(McsRecords.RepositoryId, AmsRepositoryAction.Clone, null), _ams.Root, McsRecords.Repository(), default);

        Assert.Empty(warnings);
        Assert.Equal([$"clone {McsRecords.GitUrl}", $"helper {McsRecords.GitUrl}"], _ams.Git.Calls);
    }

    [Fact]
    public async Task AHelperGitCannotSave_IsAWarning()
    {
        var failure = AmsAgentException.GitCommandFailed("git config", _ams.Root, "could not lock config file");
        _ams.Git.HelperFailure = failure;

        var warnings = await _ams.Link.JoinAsync(new AmsRepositoryChoice(McsRecords.RepositoryId, AmsRepositoryAction.Clone, null), _ams.Root, McsRecords.Repository(), default);

        Assert.Equal([$"git couldn't save msagent's credential helper in {_ams.Root}: {failure.Message} Run `msagent auth setup-git` so git signs in to GRS."], warnings);
    }

    [Fact]
    public async Task Attach_ToAnEmptyRepository_LinksTheFolderWithoutCheckingAnythingOut()
    {
        File.WriteAllText(Path.Combine(_ams.Root, "keep.txt"), "x");

        await _ams.Link.JoinAsync(new AmsRepositoryChoice(McsRecords.RepositoryId, AmsRepositoryAction.Attach, null), _ams.Root, McsRecords.Repository(), default);

        Assert.Equal(["init", $"remote add origin {McsRecords.GitUrl}", $"helper {McsRecords.GitUrl}", "fetch origin main"], _ams.Git.Calls);
    }

    [Fact]
    public async Task Attach_ChecksOutMain_AndKeepsTheProjectsOwnGitIgnoreLines()
    {
        _ams.Git.RemoteHasMain = true;
        _ams.Git.RemoteFiles[".gitignore"] = "bin/\n";
        _ams.Git.RemoteFiles["Payroll/settings.mcs.yml"] = "schemaName: cr1a2_payroll\n";
        File.WriteAllText(Path.Combine(_ams.Root, ".gitignore"), "obj/\nbin/\n");

        await _ams.Link.JoinAsync(new AmsRepositoryChoice(McsRecords.RepositoryId, AmsRepositoryAction.Attach, null), _ams.Root, McsRecords.Repository(), default);

        Assert.Equal("bin/\nobj/\n", File.ReadAllText(Path.Combine(_ams.Root, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(_ams.Root, "Payroll", "settings.mcs.yml")));
        Assert.Contains("checkout main origin", _ams.Git.Calls);
    }

    [Fact]
    public async Task Attach_RefusesToOverwriteTheProjectsFiles_NamingTheFirstFive_AndUndoesTheGitItMade()
    {
        _ams.Git.RemoteHasMain = true;
        foreach (var name in new[] { "a", "b", "c", "d", "e", "f", "g" })
        {
            _ams.Git.RemoteFiles[name] = "remote";
            File.WriteAllText(Path.Combine(_ams.Root, name), "local");
        }

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Link.JoinAsync(new AmsRepositoryChoice(McsRecords.RepositoryId, AmsRepositoryAction.Attach, null), _ams.Root, McsRecords.Repository(), default));

        Assert.Equal((ErrorCodes.AmsAgent.RepositoryWouldOverwrite, "Linking repository contoso-agents would overwrite a, b, c, d, e and 2 more."), (failure.ErrorCode, failure.Message));
        Assert.False(Directory.Exists(Path.Combine(_ams.Root, ".git")));
        Assert.Equal(("local", "local"), (File.ReadAllText(Path.Combine(_ams.Root, "a")), File.ReadAllText(Path.Combine(_ams.Root, "g"))));
    }

    [Fact]
    public async Task Attach_RefusesAWorkingTreeWithItsOwnHistory_KeepsIt_AndTakesTheRemoteBack()
    {
        _ams.LinkToRepository();
        var tree = _ams.Git.Tree! with { Remotes = [] };
        _ams.Git.Tree = tree;
        _ams.Git.RemoteHasMain = true;

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Link.JoinAsync(new AmsRepositoryChoice(McsRecords.RepositoryId, AmsRepositoryAction.Attach, tree), _ams.Root, McsRecords.Repository(), default));

        Assert.Equal("Linking repository contoso-agents would overwrite this folder's history: it and the repository's main are unrelated.", failure.Message);
        Assert.True(Directory.Exists(Path.Combine(_ams.Root, ".git")));
        Assert.DoesNotContain("init", _ams.Git.Calls);
        Assert.Equal("remote remove origin", _ams.Git.Calls[^1]);
        Assert.Empty(_ams.Git.Tree!.Remotes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkNew_AttachesAFolderWithoutHistoryInPlace_OnGrsInitialCommit(bool workingTree)
    {
        var tree = workingTree ? WorkingTree(hasCommits: false, branch: "master") : null;
        _ams.Git.HoldGrsInitialCommit();

        var link = await _ams.Link.LinkNewAsync(new AmsRepositoryChoice(null, workingTree ? AmsRepositoryAction.AddOrigin : AmsRepositoryAction.InitAndLink, tree), _ams.Root, McsRecords.RepositoryId, McsRecords.GitUrl, default);

        Assert.Equal((true, false), (link.Linked, link.ReplacesInitialCommit));
        Assert.Empty(link.Warnings);
        Assert.Equal([.. workingTree ? Array.Empty<string>() : ["init"], $"remote add origin {McsRecords.GitUrl}", $"helper {McsRecords.GitUrl}", "fetch origin main", "ls-tree origin/main", "checkout main origin"], _ams.Git.Calls);
        Assert.Equal(("# contoso-agents\n", "main"), (File.ReadAllText(Path.Combine(_ams.Root, "README.md")), _ams.Git.Tree!.Branch));
    }

    [Fact]
    public async Task LinkNew_GivesAWorkingTreeWithHistoryOnlyTheRepositoryAsOrigin_WhoseInitialCommitItsHistoryReplaces()
    {
        var tree = WorkingTree(hasCommits: true);
        _ams.Git.HoldGrsInitialCommit();

        var link = await _ams.Link.LinkNewAsync(new AmsRepositoryChoice(null, AmsRepositoryAction.AddOrigin, tree), _ams.Root, McsRecords.RepositoryId, McsRecords.GitUrl, default);

        Assert.Equal((true, true), (link.Linked, link.ReplacesInitialCommit));
        Assert.Empty(link.Warnings);
        Assert.Equal([$"helper {McsRecords.GitUrl}", $"remote add origin {McsRecords.GitUrl}"], _ams.Git.Calls);
        Assert.False(File.Exists(Path.Combine(_ams.Root, "README.md")));
    }

    [Fact]
    public async Task LinkNew_ThatCannotAddTheOriginOfAWorkingTreeWithHistory_WarnsWithTheCommand()
    {
        var tree = WorkingTree(hasCommits: true);
        var failure = AmsAgentException.GitCommandFailed("git remote", _ams.Root, "could not lock config file .git/config");
        _ams.Git.AddRemoteFailure = failure;

        var link = await _ams.Link.LinkNewAsync(new AmsRepositoryChoice(null, AmsRepositoryAction.AddOrigin, tree), _ams.Root, McsRecords.RepositoryId, McsRecords.GitUrl, default);

        Assert.Equal((true, true), (link.Linked, link.ReplacesInitialCommit));
        Assert.Equal([$"git couldn't link {_ams.Root} to the new repository: {failure.Message} Run `git remote add origin {McsRecords.GitUrl}` there."], link.Warnings);
    }

    [Theory]
    [InlineData("init", false, new[] { "init" })]
    [InlineData("fetch", false, new[] { "init", "remote add origin " + McsRecords.GitUrl, "helper " + McsRecords.GitUrl, "fetch origin main" })]
    [InlineData("remote", true, new[] { "remote add origin " + McsRecords.GitUrl })]
    [InlineData("fetch", true, new[] { "remote add origin " + McsRecords.GitUrl, "helper " + McsRecords.GitUrl, "fetch origin main", "remote remove origin" })]
    public async Task LinkNew_ThatGitCannotFinish_IsTakenBack_AndWarnsWithTheCommandsThatFinishIt(string failingStep, bool workingTree, string[] calls)
    {
        var tree = workingTree ? WorkingTree(hasCommits: false) : null;
        _ams.Git.HoldGrsInitialCommit();
        var failure = AmsAgentException.GitCommandFailed($"git {failingStep}", _ams.Root, "unable to access the repository");
        switch (failingStep)
        {
            case "init":
                _ams.Git.InitFailure = failure;
                break;
            case "remote":
                _ams.Git.AddRemoteFailure = failure;
                break;
            default:
                _ams.Git.FetchFailure = failure;
                break;
        }

        var link = await _ams.Link.LinkNewAsync(new AmsRepositoryChoice(null, workingTree ? AmsRepositoryAction.AddOrigin : AmsRepositoryAction.InitAndLink, tree), _ams.Root, McsRecords.RepositoryId, McsRecords.GitUrl, default);

        var commands = workingTree
            ? $"`git remote add origin {McsRecords.GitUrl}`, `git fetch origin main` and `git checkout -b main --track origin/main`"
            : $"`git init -b main`, `git remote add origin {McsRecords.GitUrl}`, `msagent auth setup-git`, `git fetch origin main` and `git checkout -b main --track origin/main`";
        Assert.Equal((false, false), (link.Linked, link.ReplacesInitialCommit));
        Assert.Equal([$"git couldn't link {_ams.Root} to the new repository: {failure.Message} Run {commands} there."], link.Warnings);
        Assert.Equal(calls, _ams.Git.Calls);
        Assert.Equal(workingTree, Directory.Exists(Path.Combine(_ams.Root, ".git")));
        Assert.False(File.Exists(Path.Combine(_ams.Root, "README.md")));
    }

    [Fact]
    public async Task LinkNew_WhoseTakeBackGitRefuses_StillWarnsAboutWhatStoppedTheLink()
    {
        var tree = WorkingTree(hasCommits: false);
        _ams.Git.HoldGrsInitialCommit();
        File.WriteAllText(Path.Combine(_ams.Root, "README.md"), "local");
        _ams.Git.RemoveRemoteFailure = AmsAgentException.GitCommandFailed("git remote", _ams.Root, "could not lock config file .git/config");

        var link = await _ams.Link.LinkNewAsync(new AmsRepositoryChoice(null, AmsRepositoryAction.AddOrigin, tree), _ams.Root, McsRecords.RepositoryId, McsRecords.GitUrl, default);

        Assert.False(link.Linked);
        Assert.Equal($"git couldn't link {_ams.Root} to the new repository: Linking repository {McsRecords.RepositoryId} would overwrite README.md. Move those files out of the folder, then run `git remote add origin {McsRecords.GitUrl}`, `git fetch origin main` and `git checkout -b main --track origin/main` there.", Assert.Single(link.Warnings));
        Assert.Equal("remote remove origin", _ams.Git.Calls[^1]);
        Assert.Equal("local", File.ReadAllText(Path.Combine(_ams.Root, "README.md")));
    }

    [Theory]
    [InlineData("obj/\r\n")]
    [InlineData(null)]
    public async Task LinkNew_WhoseCheckoutFails_PutsTheProjectsGitIgnoreBack_AndTakesTheLinkBack(string? gitIgnore)
    {
        var path = Path.Combine(_ams.Root, ".gitignore");
        _ams.Git.HoldGrsInitialCommit();
        if (gitIgnore is not null)
        {
            File.WriteAllText(path, gitIgnore);
        }

        _ams.Git.CheckoutFailure = AmsAgentException.GitCommandFailed("git checkout", _ams.Root, "unable to create file README.md: Permission denied");

        var link = await _ams.Link.LinkNewAsync(new AmsRepositoryChoice(null, AmsRepositoryAction.InitAndLink, null), _ams.Root, McsRecords.RepositoryId, McsRecords.GitUrl, default);

        Assert.False(link.Linked);
        Assert.Equal(gitIgnore, File.Exists(path) ? File.ReadAllText(path) : null);
        Assert.False(Directory.Exists(Path.Combine(_ams.Root, ".git")));
    }

    [Theory]
    [InlineData(null, "/.config/agent.config.json\n.mcs/\n")]
    [InlineData("bin/\r\nobj/\r\n", "bin/\r\nobj/\r\n/.config/agent.config.json\r\n.mcs/\r\n")]
    [InlineData("bin/", "bin/\n/.config/agent.config.json\n.mcs/\n")]
    [InlineData("bin/\r\n.mcs/\r\n", "bin/\r\n.mcs/\r\n/.config/agent.config.json\r\n")]
    [InlineData(" /.config/agent.config.json \nobj/", " /.config/agent.config.json \nobj/\n.mcs/\n")]
    public void EnsureGitIgnore_AddsEachLineTheFileLacks_KeepingItsLineEnding(string? before, string after)
    {
        var path = Path.Combine(_ams.Root, ".gitignore");
        if (before is not null)
        {
            File.WriteAllText(path, before);
        }

        AmsProjectLink.EnsureGitIgnore(_ams.Root);
        AmsProjectLink.EnsureGitIgnore(_ams.Root);

        Assert.Equal(after, File.ReadAllText(path));
    }

    [Fact]
    public void Undo_RemovesOnlyTheEntriesTheCommandAdded()
    {
        File.WriteAllText(Path.Combine(_ams.Root, "keep.txt"), "x");
        var snapshot = AmsProjectLink.Snapshot(_ams.Root, projectDirectoryCreated: false);
        Directory.CreateDirectory(Path.Combine(_ams.Root, ".git", "objects"));
        File.WriteAllText(Path.Combine(_ams.Root, ".git", "objects", "pack"), "x");
        File.SetAttributes(Path.Combine(_ams.Root, ".git", "objects", "pack"), FileAttributes.ReadOnly);
        _ams.AddAgentWorkspace();

        AmsProjectLink.Undo(snapshot);

        Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(_ams.Root).Select(Path.GetFileName));
    }

    [Fact]
    public void Undo_RemovesAProjectFolderTheCommandCreated()
    {
        var project = Path.Combine(_ams.Root, "New");
        var snapshot = AmsProjectLink.Snapshot(project, projectDirectoryCreated: true);
        Directory.CreateDirectory(Path.Combine(project, "ContosoHR"));

        AmsProjectLink.Undo(snapshot);

        Assert.False(Directory.Exists(project));
    }

    [Fact]
    public void Undo_TakesBackFilesAddedInsideExistingFolders_AndPutsBackTheGitIgnore()
    {
        Directory.CreateDirectory(Path.Combine(_ams.Root, "Shared"));
        File.WriteAllText(Path.Combine(_ams.Root, "Shared", "local.mcs.yml"), "x");
        File.WriteAllText(Path.Combine(_ams.Root, ".gitignore"), "bin/\r\n");
        var snapshot = AmsProjectLink.Snapshot(_ams.Root, projectDirectoryCreated: false);
        Directory.CreateDirectory(Path.Combine(_ams.Root, "Shared", "Payroll"));
        File.WriteAllText(Path.Combine(_ams.Root, "Shared", "Payroll", "topic.mcs.yml"), "x");
        File.WriteAllText(Path.Combine(_ams.Root, "Shared", "remote.mcs.yml"), "x");
        File.WriteAllText(Path.Combine(_ams.Root, ".gitignore"), "obj/\nbin/\n");

        AmsProjectLink.Undo(snapshot);

        Assert.Equal([".gitignore", "Shared", Path.Combine("Shared", "local.mcs.yml")], Directory.EnumerateFileSystemEntries(_ams.Root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(_ams.Root, path)).Order(StringComparer.Ordinal));
        Assert.Equal("bin/\r\n", File.ReadAllText(Path.Combine(_ams.Root, ".gitignore")));
    }

    private GitWorkingTree WorkingTree(bool hasCommits, string branch = "main")
    {
        Directory.CreateDirectory(Path.Combine(_ams.Root, ".git"));
        return _ams.Git.Tree = new GitWorkingTree(_ams.Root, [], branch, hasCommits);
    }
}
