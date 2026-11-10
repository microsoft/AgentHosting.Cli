using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsAgentCreationTests : IDisposable
{
    private readonly AmsHarness _ams = new();

    public AmsAgentCreationTests() => _ams.Git.Changed.Add($"{AmsHarness.AgentFolder}/settings.mcs.yml");

    public void Dispose() => _ams.Dispose();

    [Fact]
    public async Task AnAgentFromTheTemplate_JoinsANewRepository_ThatItIsCommittedAndPushedTo()
    {
        _ams.Git.HoldGrsInitialCommit();
        var ticket = await _ams.Creation.PrepareAsync(Plan(), default);
        var stored = _ams.AddAgent(registered: false);

        var registration = await _ams.Creation.CompleteAsync(ticket!, stored, null, [], default);

        Assert.Equal([$"Create a GRS repository in environment 'Contoso Dev' for {_ams.Root} and push agent 'Contoso HR' to it? [Y/n]"], _ams.Prompt.Questions);
        var registered = Assert.Single(_ams.Service.Registrations);
        Assert.Equal((null, "main", AmsHarness.AgentFolder, AmsHarness.SchemaName), (registered.RepositoryId, registered.Branch, registered.AgentFolder, registered.SchemaName));
        Assert.Empty(registered.Deployments);
        Assert.Equal(new AgentSourceControlMetadata(McsRecords.RepositoryId, "main", AmsHarness.AgentFolder), _ams.Agent().SourceControlMetadata);
        Assert.Equal(["require", "find", .. AttachedOnTheInitialCommit(made: true), $"status {AmsHarness.AgentFolder} .gitignore", "commit msagent: add MCSAgent Contoso HR", "push origin main"], _ams.Git.Calls);
        var commit = Assert.Single(_ams.Git.CommitRequests);
        Assert.Equal([AmsHarness.AgentFolder, ".gitignore"], commit.Paths);
        Assert.Equal("msagent: add MCSAgent Contoso HR", commit.Message);
        Assert.Equal($"{AmsProjectLink.ConfigIgnoreLine}\n{AmsProjectLink.McsIgnoreLine}\n", File.ReadAllText(Path.Combine(_ams.Root, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(_ams.Root, "README.md")));
        Assert.Equal((true, McsRecords.RepositoryId, true, McsRecords.GitUrl, AmsHarness.AgentFolder, McsRecords.NewCommit, true, "main"), (registration!.Registered, registration.RepositoryId, registration.RepositoryCreated, registration.GitUrl, registration.Folder, registration.CommitSha, registration.Pushed, registration.Branch));
        Assert.Null(registration.Warnings);
        Assert.Empty(_ams.Service.DeployRecords);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ANewRepository_IsAttachedInPlace_InAMissingFolder_OrOneThatHoldsOnlyTheProject(bool missing)
    {
        var project = missing ? Path.Combine(_ams.Root, "work", "support") : _ams.Root;
        if (!missing)
        {
            _ams.AddAgent("aaaaaaaa-1111-4111-8111-000000000021", "Payroll", registered: false, displayName: "Payroll");
        }

        _ams.Git.HoldGrsInitialCommit();
        var ticket = await _ams.Creation.PrepareAsync(Plan() with { ProjectDirectory = project, ProjectDirectoryCreated = missing }, default);

        var registration = await _ams.Creation.CompleteAsync(ticket!, _ams.AddAgent(registered: false, project: project), null, [], default);

        Assert.Equal([.. AttachedOnTheInitialCommit(made: true), $"status {AmsHarness.AgentFolder} .gitignore", "commit msagent: add MCSAgent Contoso HR", "push origin main"], _ams.Git.Calls.Skip(2));
        Assert.True(File.Exists(Path.Combine(project, "README.md")));
        Assert.Equal((McsRecords.NewCommit, true, "main"), (registration!.CommitSha, registration.Pushed, registration.Branch));
        Assert.Null(registration.Warnings);
    }

    [Fact]
    public async Task AWorkingTreeWithoutCommits_IsAttachedInPlace_OnMain_EvenFromAnotherUnbornBranch()
    {
        WorkingTree(hasCommits: false, branch: "master");
        _ams.Git.HoldGrsInitialCommit();
        var ticket = await _ams.Creation.PrepareAsync(Plan(), default);

        var registration = await _ams.Creation.CompleteAsync(ticket!, _ams.AddAgent(registered: false), null, [], default);

        Assert.Equal([.. AttachedOnTheInitialCommit(made: false), $"status {AmsHarness.AgentFolder} .gitignore", "commit msagent: add MCSAgent Contoso HR", "push origin main"], _ams.Git.Calls.Skip(2));
        Assert.Equal(("main", true), (registration!.Branch, registration.Pushed));
        Assert.Null(registration.Warnings);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWorkingTreeWithHistory_KeepsIt_AndItsLeasedPushReplacesOnlyGrsInitialCommit(bool repositoryHasMain)
    {
        WorkingTree(hasCommits: true);
        if (repositoryHasMain)
        {
            _ams.Git.HoldGrsInitialCommit();
        }

        var ticket = await _ams.Creation.PrepareAsync(Plan(), default);

        var registration = await _ams.Creation.CompleteAsync(ticket!, _ams.AddAgent(registered: false), null, [], default);

        IReadOnlyList<string> push = repositoryHasMain ? ["rev-parse origin/main", $"push --force-with-lease=main:{McsRecords.InitialCommit} origin main"] : ["push origin main"];
        Assert.Equal([FakeGitPort.Log("HEAD"), $"helper {McsRecords.GitUrl}", $"remote add origin {McsRecords.GitUrl}", "ls-files ", "find", $"status {AmsHarness.AgentFolder} .gitignore", "commit msagent: add MCSAgent Contoso HR", "fetch origin main", .. push], _ams.Git.Calls.Skip(2));
        Assert.False(File.Exists(Path.Combine(_ams.Root, "README.md")));
        Assert.Equal((McsRecords.NewCommit, true, "main"), (registration!.CommitSha, registration.Pushed, registration.Branch));
        Assert.Null(registration.Warnings);
    }

    [Fact]
    public async Task AWorkingTreeWithHistoryOnAnotherBranch_PushesThatBranch_WithoutALease()
    {
        WorkingTree(hasCommits: true, branch: "master");
        _ams.Git.HoldGrsInitialCommit();
        var ticket = await _ams.Creation.PrepareAsync(Plan(), default);

        var registration = await _ams.Creation.CompleteAsync(ticket!, _ams.AddAgent(registered: false), null, [], default);

        Assert.Equal(["commit msagent: add MCSAgent Contoso HR", "push origin master"], _ams.Git.Calls.TakeLast(2));
        Assert.DoesNotContain(_ams.Git.Calls, call => call.StartsWith("fetch", StringComparison.Ordinal));
        Assert.Equal(("master", true), (registration!.Branch, registration.Pushed));
        Assert.Equal(["Remote deploys take main by default; this folder is on master."], registration.Warnings);
    }

    [Fact]
    public async Task ALeasedPushThatMainMovedUnder_IsAWarning_ThatNeverOverwritesWhatMovedIt()
    {
        WorkingTree(hasCommits: true);
        _ams.Git.HoldGrsInitialCommit();
        _ams.Git.PushResult = new GitPushResult(false, "! [rejected] main -> main (stale info)");
        var ticket = await _ams.Creation.PrepareAsync(Plan(), default);

        var registration = await _ams.Creation.CompleteAsync(ticket!, _ams.AddAgent(registered: false), null, [], default);

        Assert.Equal((McsRecords.NewCommit, false), (registration!.CommitSha, registration.Pushed));
        Assert.Equal([$"git push failed: ! [rejected] main -> main (stale info) Run `git push --force-with-lease=main:{McsRecords.InitialCommit} -u origin main` in {_ams.Root}, or, when main has moved since, `git pull --allow-unrelated-histories origin main` and then `git push -u origin main`."], registration.Warnings);
        Assert.DoesNotContain(_ams.Git.Calls, call => call == "push origin main");
    }

    [Fact]
    public async Task AWorkingTreeWithHistory_WhoseNewMainCannotBeFetched_IsAWarning_WithTheLeasedPushToRun()
    {
        WorkingTree(hasCommits: true);
        var failure = AmsAgentException.GitCommandFailed("git fetch", _ams.Root, "unable to access the repository");
        _ams.Git.FetchFailure = failure;
        var ticket = await _ams.Creation.PrepareAsync(Plan(), default);

        var registration = await _ams.Creation.CompleteAsync(ticket!, _ams.AddAgent(registered: false), null, [], default);

        Assert.Equal((McsRecords.NewCommit, false), (registration!.CommitSha, registration.Pushed));
        Assert.Equal([$"git couldn't read main of the new repository: {failure.Message} Run `git fetch origin main` and `git push --force-with-lease=main -u origin main` in {_ams.Root}."], registration.Warnings);
        Assert.DoesNotContain(_ams.Git.Calls, call => call.StartsWith("push", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFolderTheNewRepositoryCannotBeFetchedInto_IsLeftUnlinked_AndNothingIsCommitted()
    {
        _ams.Git.HoldGrsInitialCommit();
        var failure = AmsAgentException.GitCommandFailed("git fetch", _ams.Root, "unable to access the repository");
        _ams.Git.FetchFailure = failure;
        var ticket = await _ams.Creation.PrepareAsync(Plan(), default);

        var registration = await _ams.Creation.CompleteAsync(ticket!, _ams.AddAgent(registered: false), null, [], default);

        Assert.Equal([$"git couldn't link {_ams.Root} to the new repository: {failure.Message} Run `git init -b main`, `git remote add origin {McsRecords.GitUrl}`, `msagent auth setup-git`, `git fetch origin main` and `git checkout -b main --track origin/main` there.", NothingCommitted()], registration!.Warnings);
        Assert.Equal((null, false, "main"), (registration.CommitSha, registration.Pushed, registration.Branch));
        Assert.False(Directory.Exists(Path.Combine(_ams.Root, ".git")));
        Assert.Empty(_ams.Git.CommitRequests);
        Assert.NotNull(_ams.Agent().SourceControlMetadata);
    }

    [Fact]
    public async Task AWorkingTreeWithoutCommits_HoldingAFileOfTheNewRepository_IsLeftWithNothingHalfLinked()
    {
        WorkingTree(hasCommits: false);
        _ams.Git.HoldGrsInitialCommit();
        File.WriteAllText(Path.Combine(_ams.Root, "README.md"), "local");
        var ticket = await _ams.Creation.PrepareAsync(Plan(), default);

        var registration = await _ams.Creation.CompleteAsync(ticket!, _ams.AddAgent(registered: false), null, [], default);

        Assert.Equal([$"git couldn't link {_ams.Root} to the new repository: Linking repository {McsRecords.RepositoryId} would overwrite README.md. Move those files out of the folder, then run `git remote add origin {McsRecords.GitUrl}`, `git fetch origin main` and `git checkout -b main --track origin/main` there.", NothingCommitted()], registration!.Warnings);
        Assert.Equal([$"remote add origin {McsRecords.GitUrl}", $"helper {McsRecords.GitUrl}", "fetch origin main", "ls-tree origin/main", "remote remove origin"], _ams.Git.Calls.Skip(2));
        Assert.Empty(_ams.Git.Tree!.Remotes);
        Assert.Equal("local", File.ReadAllText(Path.Combine(_ams.Root, "README.md")));
        Assert.Equal((null, false), (registration.CommitSha, registration.Pushed));
    }

    [Fact]
    public async Task ACloneIntoANewRepository_ClaimsTheBot_AndRecordsTheDeployOnceThePushLanded()
    {
        _ams.Git.HoldGrsInitialCommit();
        var ticket = await _ams.Creation.PrepareAsync(Plan(botId: McsRecords.BotId), default);
        var stored = _ams.AddAgent(registered: false, mcsAgentId: McsRecords.BotId);

        var registration = await _ams.Creation.CompleteAsync(ticket!, stored, _ams.AddDeployment(), [], default);

        Assert.Equal(McsRecords.BotId, Assert.Single(Assert.Single(_ams.Service.Registrations).Deployments).BotId);
        var record = Assert.Single(_ams.Service.DeployRecords);
        Assert.Equal((McsRecords.DevDeploymentId, McsRecords.BotId, McsRecords.NewCommit, false), (record.DeploymentId, record.BotId, record.CommitSha, record.Published));
        Assert.True(_ams.Git.Calls.IndexOf("push origin main") < _ams.Git.Calls.LastIndexOf("rev-parse HEAD"));
        Assert.True(registration!.Pushed);
    }

    [Fact]
    public async Task ACloneInItsHomeEnvironment_JoinsTheRepository_ClaimsTheBot_AndRecordsTheDeployOfThePushedCommit()
    {
        var ticket = await _ams.Creation.PrepareAsync(Plan(repositoryId: McsRecords.RepositoryId, botId: McsRecords.BotId), default);
        var stored = _ams.AddAgent(registered: false, mcsAgentId: McsRecords.BotId);
        var home = _ams.AddDeployment();

        var registration = await _ams.Creation.CompleteAsync(ticket!, stored, home, [], default);

        Assert.Equal([$"Clone repository 'contoso-agents' ({McsRecords.RepositoryId}) into {_ams.Root} and add agent 'Contoso HR' to it? [Y/n]"], _ams.Prompt.Questions);
        Assert.Equal($"read-repository {McsRecords.RepositoryId} {AmsHarness.AgentFolder} agents", _ams.Service.Calls[0]);
        Assert.Contains($"clone {McsRecords.GitUrl}", _ams.Git.Calls);
        var deployment = Assert.Single(Assert.Single(_ams.Service.Registrations).Deployments);
        Assert.Equal((McsRecords.DevDeploymentId, "Dev", McsRecords.BotId, (string?)null), (deployment.DeploymentId, deployment.DeploymentType, deployment.BotId, deployment.WorkflowId));
        var record = Assert.Single(_ams.Service.DeployRecords);
        Assert.Equal((McsRecords.DevDeploymentId, McsRecords.BotId, McsRecords.NewCommit, false, _ams.Clock.UtcNow), (record.DeploymentId, record.BotId, record.CommitSha, record.Published, record.DeployedTime));
        Assert.False(registration!.RepositoryCreated);
    }

    [Fact]
    public async Task ACloneFromAnotherEnvironment_ClaimsNoBot_AndRecordsNoDeploy()
    {
        var ticket = await _ams.Creation.PrepareAsync(Plan(repositoryId: McsRecords.RepositoryId), default);
        var stored = _ams.AddAgent(registered: false);

        await _ams.Creation.CompleteAsync(ticket!, stored, _ams.AddDeployment(), [], default);

        Assert.Null(Assert.Single(Assert.Single(_ams.Service.Registrations).Deployments).BotId);
        Assert.Empty(_ams.Service.DeployRecords);
    }

    [Fact]
    public async Task AClone_KeepsACopyOfEachClonedFolder_ForTheCommitThatAddsIt()
    {
        _ams.Git.HoldGrsInitialCommit();
        var ticket = await _ams.Creation.PrepareAsync(Plan(repositoryId: McsRecords.RepositoryId, botId: McsRecords.BotId), default);
        var stored = _ams.AddAgent(registered: false, mcsAgentId: McsRecords.BotId);
        _ams.DeclareCollection();

        var registration = await _ams.Creation.CompleteAsync(ticket!, stored, _ams.AddDeployment(), [Cloned(AmsHarness.AgentFolder), Cloned("Shared Tools")], default);

        Assert.Equal((McsRecords.NewCommit, null), (registration!.CommitSha, registration.Warnings));
        Assert.Equal([$"ContosoHR env-1|agent-1 live={McsRecords.NewCommit} {McsRecords.NewCommit}=cloud", $"Shared Tools env-1|agent-1 live={McsRecords.NewCommit} {McsRecords.NewCommit}=cloud"], Copies());
    }

    [Fact]
    public async Task AClonedWorkflow_JoiningANewRepository_KeepsACopyWithoutAnAgentToCheck_ForTheCommitThatAddsIt()
    {
        _ams.Git.HoldGrsInitialCommit();
        var ticket = await _ams.Creation.PrepareAsync(Plan() with { AgentType = AgentTypes.McsWorkflow, DisplayName = "Escalate" }, default);
        var stored = _ams.Store.Add(_ams.Root, ManagedAgentFixtures.Agent(McsRecords.WorkflowAgentId, "Escalate") with { AgentType = AgentTypes.McsWorkflow, AgentLocation = AmsHarness.AgentFolder });

        await _ams.Creation.CompleteAsync(ticket!, stored, null, [Cloned(AmsHarness.AgentFolder)], default);

        Assert.Equal([$"ContosoHR  live={McsRecords.NewCommit} {McsRecords.NewCommit}=cloud"], Copies());
    }

    [Theory]
    [InlineData("git could not commit")]
    [InlineData("nothing to commit")]
    [InlineData("nothing to commit on an unborn branch")]
    public async Task AClone_WithoutTheCommitThatAddsIt_KeepsNoCopy(string scenario)
    {
        _ams.Git.HoldGrsInitialCommit();
        if (scenario.StartsWith("nothing to commit", StringComparison.Ordinal))
        {
            _ams.Git.Changed.Clear();
        }
        else
        {
            _ams.Git.CommitResult = new GitCommitResult(null, "Author identity unknown");
        }

        if (scenario.EndsWith("unborn branch", StringComparison.Ordinal))
        {
            _ams.Git.Commits.Remove("HEAD");
            _ams.Git.HeadPushed = false;
        }

        var ticket = await _ams.Creation.PrepareAsync(Plan(repositoryId: McsRecords.RepositoryId, botId: McsRecords.BotId), default);

        var registration = await _ams.Creation.CompleteAsync(ticket!, _ams.AddAgent(registered: false, mcsAgentId: McsRecords.BotId), _ams.AddDeployment(), [Cloned(AmsHarness.AgentFolder)], default);

        Assert.Null(registration!.CommitSha);
        Assert.Empty(Copies());
        Assert.DoesNotContain("git-dir", _ams.Git.Calls);
        Assert.DoesNotContain(_ams.Git.Calls, call => call.StartsWith("push", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AClone_WhoseCopyCannotBeSaved_SaysWhy_AndKeepsItsCommitAndPush()
    {
        _ams.Git.HoldGrsInitialCommit();
        var ticket = await _ams.Creation.PrepareAsync(Plan(repositoryId: McsRecords.RepositoryId, botId: McsRecords.BotId), default);
        File.WriteAllText(Path.Combine(_ams.Root, ".git", "msagent"), "not a folder");

        var registration = await _ams.Creation.CompleteAsync(ticket!, _ams.AddAgent(registered: false, mcsAgentId: McsRecords.BotId), _ams.AddDeployment(), [Cloned(AmsHarness.AgentFolder)], default);

        Assert.Equal((McsRecords.NewCommit, true), (registration!.CommitSha, registration.Pushed));
        Assert.StartsWith("msagent couldn't save a copy of ContosoHR's cloud cache: ", Assert.Single(registration.Warnings!), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStep_NeedsTheShelfAndTheWorkspaces()
    {
        Assert.Equal("shelf", Assert.Throws<ArgumentNullException>(() => new AmsAgentCreation(_ams.Git, _ams.Repositories, _ams.Link, _ams.Registrar, _ams.Committer, _ams.DeployRecords, _ams.Targets, _ams.Service, _ams.Store, _ams.Clock, null!, _ams.Workspaces)).ParamName);
        Assert.Equal("workspaces", Assert.Throws<ArgumentNullException>(() => new AmsAgentCreation(_ams.Git, _ams.Repositories, _ams.Link, _ams.Registrar, _ams.Committer, _ams.DeployRecords, _ams.Targets, _ams.Service, _ams.Store, _ams.Clock, _ams.Shelf, null!)).ParamName);
    }

    [Fact]
    public async Task AWorkflow_IsAskedAboutAsAWorkflow_SendsNoSchemaNameOrAuthoringShape_AndJoinsTheNewRepositoryLikeAnAgent()
    {
        _ams.Git.HoldGrsInitialCommit();
        var ticket = await _ams.Creation.PrepareAsync(Plan() with { AgentType = AgentTypes.McsWorkflow, DisplayName = "Escalate" }, default);
        var stored = _ams.Store.Add(_ams.Root, ManagedAgentFixtures.Agent(McsRecords.WorkflowAgentId, "Escalate") with { AgentType = AgentTypes.McsWorkflow, AgentLocation = AmsHarness.AgentFolder });

        var registration = await _ams.Creation.CompleteAsync(ticket!, stored, null, [], default);

        Assert.StartsWith($"Create a GRS repository in environment 'Contoso Dev' for {_ams.Root} and push workflow 'Escalate'", Assert.Single(_ams.Prompt.Questions), StringComparison.Ordinal);
        Assert.Equal((null, null), (Assert.Single(_ams.Service.Registrations).SchemaName, _ams.Service.Registrations[0].AuthoringShape));
        Assert.Equal([.. AttachedOnTheInitialCommit(made: true), $"status {AmsHarness.AgentFolder} .gitignore", "commit msagent: add MCSWorkflow Escalate", "push origin main"], _ams.Git.Calls.Skip(2));
        Assert.Equal((McsRecords.NewCommit, true), (registration!.CommitSha, registration.Pushed));
    }

    [Fact]
    public async Task ANo_CancelsTheCreation_BeforeAnythingIsWritten()
    {
        _ams.Prompt.Answer = false;

        Assert.Null(await _ams.Creation.PrepareAsync(Plan(), default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_ams.Root));
        Assert.Equal(["require", "find"], _ams.Git.Calls);
        Assert.Empty(_ams.Service.Calls);
    }

    [Fact]
    public async Task AFolderTheRepositoryAlreadyHolds_IsRefusedBeforeTheQuestion()
    {
        _ams.Service.Repository = McsRecords.Repository() with { AgentFolderExists = true };

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Creation.PrepareAsync(Plan(repositoryId: McsRecords.RepositoryId), default));

        Assert.Equal((ErrorCodes.AmsAgent.FolderExistsInRepository, "Folder ContosoHR already exists on main of repository contoso-agents."), (failure.ErrorCode, failure.Message));
        Assert.Empty(_ams.Prompt.Questions);
    }

    [Fact]
    public async Task AnUnpushedCommitThatAddsMcs_IsRefusedBeforeTheQuestion_AndNothingIsRegisteredLinkedOrCommitted()
    {
        _ams.LinkToRepository();
        _ams.Git.Committed.Add("Payroll/.mcs/workflow-state.json");

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Creation.PrepareAsync(Plan(), default));

        Assert.Equal((ErrorCodes.AmsAgent.McsFolderInRepository, ExitCodes.Usage, "Commits msagent would push hold a .mcs folder inside Payroll, a cache that must come from the cloud, so nothing was changed."), (failure.ErrorCode, failure.ExitCode, failure.Message));
        Assert.Equal("Remove it from those commits, for example with `git filter-repo --invert-paths --path-glob \"*.mcs/*\"` where git filter-repo is installed, or squash them into one commit without it. Then run the command again.", failure.Remediation);
        Assert.Equal(["require", "find", FakeGitPort.Log("HEAD", "--not", "--remotes=origin")], _ams.Git.Calls);
        Assert.Empty(_ams.Prompt.Questions);
        Assert.Empty(_ams.Service.Calls);
        Assert.Equal([".git"], Directory.EnumerateFileSystemEntries(_ams.Root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AFolderWhoseOwnHistoryOnceAddedMcs_IsRefusedOverAllOfHead_NamingARootMcsByTheProjectFolder()
    {
        WorkingTree(hasCommits: true);
        _ams.Git.Committed.AddRange(["Payroll/.MCS/conn.json", ".mcs/workflow-state.json", "payroll/.mcs/botdefinition.json"]);

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Creation.PrepareAsync(Plan(), default));

        Assert.Equal($"Commits msagent would push hold a .mcs folder inside {_ams.Root}, Payroll, a cache that must come from the cloud, so nothing was changed.", failure.Message);
        Assert.Equal(["require", "find", FakeGitPort.Log("HEAD")], _ams.Git.Calls);
        Assert.Empty(_ams.Prompt.Questions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFolderWithoutAWorkingTreeOrWithoutCommits_ReadsNoHistory(bool workingTree)
    {
        if (workingTree)
        {
            WorkingTree(hasCommits: false);
        }

        _ams.Git.Committed.Add("Payroll/.mcs/conn.json");

        Assert.NotNull(await _ams.Creation.PrepareAsync(Plan(), default));
        Assert.DoesNotContain(_ams.Git.Calls, call => call.StartsWith("log", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverRealGit_AnUnpushedCommitThatOnlyDeletesMcs_Passes_AndOneThatAddsOrChangesItIsRefused(bool addsOrChanges)
    {
        await RealGit.InitAsync(_ams.Root);
        await RealGit.RunAsync(_ams.Root, "remote", "add", "origin", McsRecords.GitUrl);
        RealGit.Write(_ams.Root, "Payroll/settings.mcs.yml", "displayName: Payroll\n");
        RealGit.Write(_ams.Root, "Payroll/.mcs/workflow-state.json", "{}");
        await RealGit.RunAsync(_ams.Root, "add", "--all");
        await RealGit.RunAsync(_ams.Root, "commit", "--quiet", "-m", "Pushed");
        await RealGit.RunAsync(_ams.Root, "update-ref", "refs/remotes/origin/main", "HEAD");
        if (addsOrChanges)
        {
            RealGit.Write(_ams.Root, "Payroll/.mcs/workflow-state.json", "{\"state\":1}");
            RealGit.Write(_ams.Root, "Contoso HR/agents/Café/.MCS/conn.json", "{}");
            await RealGit.RunAsync(_ams.Root, "add", "--all");
        }
        else
        {
            await RealGit.RunAsync(_ams.Root, "rm", "--quiet", "-r", "Payroll/.mcs");
        }

        await RealGit.RunAsync(_ams.Root, "commit", "--quiet", "-m", "Unpushed");
        var plan = Plan() with { ProjectDirectory = Path.GetFullPath(Assert.Single(await RealGit.RunAsync(_ams.Root, "rev-parse", "--show-toplevel"))) };
        var creation = _ams.CreationOver(RealGit.Adapter());

        if (addsOrChanges)
        {
            Assert.Equal("Commits msagent would push hold a .mcs folder inside Contoso HR, Payroll, a cache that must come from the cloud, so nothing was changed.", (await Assert.ThrowsAsync<AmsAgentException>(() => creation.PrepareAsync(plan, default))).Message);
        }
        else
        {
            Assert.NotNull(await creation.PrepareAsync(plan, default));
        }

        Assert.Empty(_ams.Prompt.Questions);
    }

    [Fact]
    public async Task OverRealGit_AFolderCommittedOnAStaleMainThenMergedByAPull_IsPushedAsThatMerge()
    {
        var origin = Path.Combine(_ams.Root, "origin.git");
        var project = Path.Combine(_ams.Root, "project");
        Directory.CreateDirectory(project);
        await RealGit.RunAsync(_ams.Root, "init", "--quiet", "--bare", "--initial-branch=main", origin);
        await RealGit.InitAsync(project);
        await RealGit.RunAsync(project, "remote", "add", "origin", origin);
        RealGit.Write(project, "Payroll/settings.mcs.yml", "displayName: Payroll\n");
        await RealGit.RunAsync(project, "add", "--all");
        await RealGit.RunAsync(project, "commit", "--quiet", "-m", "msagent: add MCSAgent Payroll");
        RealGit.Write(project, "Payroll/settings.mcs.yml", "displayName: Payroll captured\n");
        await RealGit.RunAsync(project, "commit", "--quiet", "--all", "-m", "msagent: capture");
        await RealGit.RunAsync(project, "push", "--quiet", "origin", "main");
        await RealGit.RunAsync(project, "reset", "--quiet", "--hard", "HEAD~1");
        RealGit.Write(project, $"{AmsHarness.AgentFolder}/settings.mcs.yml", "displayName: Contoso HR\n");
        await RealGit.RunAsync(project, "add", "--all");
        await RealGit.RunAsync(project, "commit", "--quiet", "-m", "msagent: add MCSAgent Contoso HR");
        await RealGit.RunAsync(project, "merge", "--quiet", "--no-edit", "origin/main");
        var merge = Assert.Single(await RealGit.RunAsync(project, "rev-parse", "HEAD"));

        var outcome = await new AmsCommitter(RealGit.Adapter(), _ams.Collections).CommitAndPushAsync(project, [AmsHarness.AgentFolder], "msagent: add MCSAgent Contoso HR", null, default);

        Assert.Equal((merge, true, AmsProjectLink.Branch), (outcome.CommitSha, outcome.Pushed, outcome.Branch));
        Assert.Empty(outcome.Warnings);
        Assert.Equal([merge], await RealGit.RunAsync(_ams.Root, $"--git-dir={origin}", "rev-parse", "main"));
    }

    [Fact]
    public async Task ARefusedRegister_IsShownAsAmsWordsIt_AndUndoesTheFolderAndTheRecords()
    {
        var refusal = McsRecords.Rejected(ErrorCodes.AmsAgent.BotClaimed, "mcs-bot-claimed", ExitCodes.Usage);
        _ams.Service.OnRegister = _ => throw refusal;
        var ticket = await _ams.Creation.PrepareAsync(Plan(repositoryId: McsRecords.RepositoryId, botId: McsRecords.BotId), default);
        var stored = _ams.AddAgent(registered: false);

        Assert.Same(refusal, await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Creation.CompleteAsync(ticket!, stored, _ams.AddDeployment(), [], default)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_ams.Root));
        Assert.DoesNotContain(_ams.Git.Calls, call => call.StartsWith("commit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedRegister_KeepsWhatTheProjectHeldBefore()
    {
        var other = _ams.AddAgent("aaaaaaaa-1111-4111-8111-000000000021", "Payroll", displayName: "Payroll");
        _ams.LinkToRepository();
        _ams.Service.OnRegister = _ => throw McsRecords.Status(500);
        var ticket = await _ams.Creation.PrepareAsync(Plan(), default);
        var stored = _ams.AddAgent(registered: false);

        await Assert.ThrowsAsync<TargetRequestException>(() => _ams.Creation.CompleteAsync(ticket!, stored, null, [], default));

        Assert.Equal([other.AgentId], _ams.Store.List(_ams.Root).Select(agent => agent.AgentId));
        Assert.False(Directory.Exists(Path.Combine(_ams.Root, AmsHarness.AgentFolder)));
        Assert.True(Directory.Exists(Path.Combine(_ams.Root, "Payroll")));
    }

    [Fact]
    public async Task AFailedPush_IsAWarning_AndTheAgentStaysRegistered()
    {
        _ams.Git.HoldGrsInitialCommit();
        _ams.Git.PushResult = new GitPushResult(false, "unable to access the repository: Could not resolve host");
        var ticket = await _ams.Creation.PrepareAsync(Plan(), default);

        var registration = await _ams.Creation.CompleteAsync(ticket!, _ams.AddAgent(registered: false), null, [], default);

        Assert.Equal((McsRecords.NewCommit, false), (registration!.CommitSha, registration.Pushed));
        Assert.Equal([$"git push failed: unable to access the repository: Could not resolve host Run `git push -u origin main` in {_ams.Root}."], registration.Warnings);
        Assert.NotNull(_ams.Agent().SourceControlMetadata);
    }

    [Theory]
    [InlineData("Author identity unknown *** Please tell me who you are.", "Set user.name and user.email with `git config --global`")]
    [InlineData("fatal: empty ident name (for <hoa@contoso.com>) not allowed", "Set user.name and user.email with `git config --global`")]
    [InlineData("error: pathspec 'Contoso HR' did not match any file(s) known to git", "Fix what git reports")]
    public async Task AFailedCommit_IsAWarning_WhoseRemedyFitsGitsReason_AndNothingIsPushed(string failure, string remedy)
    {
        _ams.Git.HoldGrsInitialCommit();
        _ams.Git.CommitResult = new GitCommitResult(null, failure);
        var ticket = await _ams.Creation.PrepareAsync(Plan(), default);

        var registration = await _ams.Creation.CompleteAsync(ticket!, _ams.AddAgent(registered: false), null, [], default);

        Assert.Null(registration!.CommitSha);
        Assert.False(registration.Pushed);
        Assert.Equal([$"git could not commit: {failure} {remedy}, then run `git commit` and `git push -u origin main` in {_ams.Root}."], registration.Warnings);
        Assert.DoesNotContain(_ams.Git.Calls, call => call.StartsWith("push", StringComparison.Ordinal));
        Assert.NotNull(_ams.Agent().SourceControlMetadata);
    }

    [Fact]
    public async Task AFailedDeployRecordOfAClone_IsAWarning()
    {
        _ams.Service.DeployRecordFailure = McsRecords.Status(503);
        var ticket = await _ams.Creation.PrepareAsync(Plan(repositoryId: McsRecords.RepositoryId, botId: McsRecords.BotId), default);

        var registration = await _ams.Creation.CompleteAsync(ticket!, _ams.AddAgent(registered: false), _ams.AddDeployment(), [], default);

        Assert.Equal(["AMS doesn't know about this deploy: The Agent Management Service answered 503."], registration!.Warnings);
        Assert.True(registration.Pushed);
    }

    [Fact]
    public async Task AWorkingTreeOnAnotherBranch_IsAWarning()
    {
        _ams.LinkToRepository(branch: "feature");
        var ticket = await _ams.Creation.PrepareAsync(Plan(), default);

        var registration = await _ams.Creation.CompleteAsync(ticket!, _ams.AddAgent(registered: false), null, [], default);

        Assert.Empty(_ams.Prompt.Questions);
        Assert.Equal(McsRecords.RepositoryId, Assert.Single(_ams.Service.Registrations).RepositoryId);
        Assert.Equal("feature", registration!.Branch);
        Assert.Equal(["Remote deploys take main by default; this folder is on feature."], registration.Warnings);
        Assert.Equal("push origin feature", _ams.Git.Calls[^1]);
    }

    [Fact]
    public async Task AConfigThatCannotRecordTheFolder_DeletesTheRegistrationAgain()
    {
        var unstored = ManagedAgentFixtures.Agent(McsRecords.AgentId, "Contoso HR") with { AgentLocation = AmsHarness.AgentFolder, McsSchemaName = AmsHarness.SchemaName };
        _ams.AddAgentWorkspace();

        await Assert.ThrowsAnyAsync<AgentHostingException>(() => _ams.Registrar.RegisterAsync(_ams.Root, unstored, [], null, null, null, McsRecords.Home(), "msagent agent create", default));

        Assert.Equal([(McsRecords.AgentId, McsRecords.ETag)], _ams.Service.AgentDeletes);
    }

    private AgentCreationPlan Plan(string? repositoryId = null, string? botId = null) =>
        new(_ams.Root, false, AmsHarness.AgentFolder, "Contoso HR", AgentTypes.McsAgent, "env-1", repositoryId, false, botId, null);

    private static string[] AttachedOnTheInitialCommit(bool made) =>
        [.. made ? ["init"] : Array.Empty<string>(), $"remote add origin {McsRecords.GitUrl}", $"helper {McsRecords.GitUrl}", "fetch origin main", "ls-tree origin/main", "checkout main origin", "ls-files ", "find"];

    private string NothingCommitted() => $"Nothing was committed. Once {_ams.Root} is linked, run `git add -- {AmsHarness.AgentFolder} .gitignore`, `git commit` and `git push -u origin main` there.";

    private void WorkingTree(bool hasCommits, string branch = "main")
    {
        Directory.CreateDirectory(Path.Combine(_ams.Root, ".git"));
        _ams.Git.Tree = new GitWorkingTree(_ams.Root, [], branch, hasCommits);
    }

    private string Cloned(string folder)
    {
        var path = Path.Combine(_ams.Root, folder);
        Directory.CreateDirectory(Path.Combine(path, ".mcs"));
        File.WriteAllText(Path.Combine(path, ".mcs", "botdefinition.json"), "cloud");
        return path;
    }

    private string[] Copies()
    {
        var shelves = Path.Combine(_ams.Root, ".git", "msagent", "mcs");
        List<string> copies = [];
        foreach (var keyDirectory in Directory.Exists(shelves) ? Directory.GetDirectories(shelves) : [])
        {
            var index = JsonNode.Parse(File.ReadAllText(Path.Combine(keyDirectory, "index.json")))!;
            copies.AddRange(Directory.GetDirectories(keyDirectory).Select(copy => $"{index["folder"]} {index["identity"]} live={index["live"]} {Path.GetFileName(copy)}={File.ReadAllText(Path.Combine(copy, "botdefinition.json"))}"));
        }

        return [.. copies.Order(StringComparer.Ordinal)];
    }
}
