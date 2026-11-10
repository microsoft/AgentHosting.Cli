using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsAgentCreationTests : IDisposable
{
    private const string Holding = $"Copilot Studio agent 'Contoso HR' is already in AMS as agent 'Contoso HR', in repository {McsRecords.RepositoryId} (folder {AmsHarness.AgentFolder})";

    private const string PayrollId = "aaaaaaaa-1111-4111-8111-000000000021";

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
        Assert.Equal("shelf", Assert.Throws<ArgumentNullException>(() => new AmsAgentCreation(_ams.Git, _ams.Repositories, _ams.Link, _ams.Registrar, _ams.Committer, _ams.DeployRecords, _ams.Targets, _ams.Service, _ams.Store, _ams.Clock, null!, _ams.Workspaces, _ams.JoinOver(_ams.Git, _ams.Connect))).ParamName);
        Assert.Equal("workspaces", Assert.Throws<ArgumentNullException>(() => new AmsAgentCreation(_ams.Git, _ams.Repositories, _ams.Link, _ams.Registrar, _ams.Committer, _ams.DeployRecords, _ams.Targets, _ams.Service, _ams.Store, _ams.Clock, _ams.Shelf, null!, _ams.JoinOver(_ams.Git, _ams.Connect))).ParamName);
        Assert.Equal("join", Assert.Throws<ArgumentNullException>(() => new AmsAgentCreation(_ams.Git, _ams.Repositories, _ams.Link, _ams.Registrar, _ams.Committer, _ams.DeployRecords, _ams.Targets, _ams.Service, _ams.Store, _ams.Clock, _ams.Shelf, _ams.Workspaces, null!)).ParamName);
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
        var refusal = McsRecords.Rejected(ErrorCodes.AmsAgent.RequestRejected, "ams-request-rejected", ExitCodes.Usage);
        _ams.Service.OnRegister = _ => throw refusal;
        var ticket = await _ams.Creation.PrepareAsync(Plan(repositoryId: McsRecords.RepositoryId, botId: McsRecords.BotId), default);
        var stored = _ams.AddAgent(registered: false);

        Assert.Same(refusal, await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Creation.CompleteAsync(ticket!, stored, _ams.AddDeployment(), [], default)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_ams.Root));
        Assert.DoesNotContain(_ams.Git.Calls, call => call.StartsWith("commit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARegisterThatFindsTheBotClaimedAfterTheHoldersRead_SaysTheSameCommandJoinsIt_AndUndoesTheFolderAndTheRecords()
    {
        var refusal = AmsAgentException.Rejected(ErrorCodes.AmsAgent.BotClaimed, "Another AMS agent already holds this Copilot Studio agent or flow. Service detail: claimed.", ExitCodes.Usage, "mcs-bot-claimed", "Clone that agent's repository and run `msagent agent init` there.", TimeSpan.FromSeconds(3));
        _ams.Service.OnRegister = _ => throw refusal;
        var ticket = await _ams.Creation.PrepareAsync(Plan(repositoryId: McsRecords.RepositoryId, botId: McsRecords.BotId), default);
        var stored = _ams.AddAgent(registered: false);

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Creation.CompleteAsync(ticket!, stored, _ams.AddDeployment(), [], default));

        Assert.Equal((ErrorCodes.AmsAgent.BotClaimed, refusal.Message, ExitCodes.Usage, "mcs-bot-claimed", "Run the same command again: it joins that agent.", TimeSpan.FromSeconds(3)), (failure.ErrorCode, failure.Message, failure.ExitCode, failure.ErrorKind, failure.Remediation, failure.RetryAfter));
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

    [Fact]
    public async Task AScaffoldOrACopy_ClaimsNothing_SoItReadsNoHolderAndJoinsNothing()
    {
        Assert.Null(await _ams.Creation.JoinAsync(Plan(McsRecords.RepositoryId) with { SourceName = "Contoso HR" }, default));

        Assert.Empty(_ams.Service.Calls);
        Assert.Empty(_ams.Git.Calls);
    }

    [Fact]
    public async Task ACloneNoAgentHolds_JoinsNothing_AndTodaysQuestionAndRegisterFollow_ResolvingItsHomeOnce()
    {
        var client = new FakeEnvironmentClient(McsRecords.Home(), McsRecords.Home("env-2", "Contoso Test"));
        _ams.Targets = new AmsTargets(new AmsEndpointProfile(), McsRecords.Environments(client, "env-2"), PowerPlatformApiTargetConfig.Public);
        var listed = client.ListCalls;
        _ams.Git.HoldGrsInitialCommit();
        var creation = _ams.Creation;
        var plan = Clone();

        Assert.Null(await creation.JoinAsync(plan, default));
        var ticket = await creation.PrepareAsync(plan, default);
        var resolved = client.ListCalls - listed;
        var registration = await creation.CompleteAsync(ticket!, _ams.AddAgent(registered: false, mcsAgentId: McsRecords.BotId), _ams.AddDeployment(), [], default);

        Assert.Equal(1, resolved);
        Assert.Equal($"find-holders {McsRecords.BotId} -", _ams.Service.Calls[0]);
        Assert.Equal([$"Create a GRS repository in environment 'Contoso Dev' for {_ams.Root} and push agent 'Contoso HR' to it? [Y/n]"], _ams.Prompt.Questions);
        Assert.Equal(McsRecords.BotId, Assert.Single(Assert.Single(_ams.Service.Registrations).Deployments).BotId);
        Assert.True(registration!.Registered);
    }

    [Fact]
    public async Task AJoinIntoAnEmptyFolder_ClonesTheRepository_RestoresTheHolderAndTheAgentsTheCloneHolds_AndRegistersCommitsAndPushesNothing()
    {
        HoldInTheRemote(Payroll(), McsRecords.ListItem() with { AgentId = "aaaaaaaa-1111-4111-8111-000000000031", DisplayName = "Gone", AgentFolder = "Gone" });
        _ams.Git.RemoteFiles["Payroll/settings.mcs.yml"] = "displayName: Payroll\nschemaName: cr1a2_payroll\n";

        var join = await _ams.Creation.JoinAsync(Clone(), default);

        Assert.Equal([$"{Holding}. Clone that repository into {_ams.Root} and connect Contoso HR there? [Y/n]"], _ams.Prompt.Questions);
        Assert.Equal([$"find-holders {McsRecords.BotId} -", $"read-repository {McsRecords.RepositoryId} {AmsHarness.AgentFolder} with-deployments"], _ams.Service.Calls);
        Assert.Equal(["require", "find", $"clone {McsRecords.GitUrl}", $"helper {McsRecords.GitUrl}", $"ls-files {AmsHarness.AgentFolder} Payroll", $"rev-parse {McsRecords.Commit}", $"merge-base {McsRecords.Commit} HEAD", $"status {AmsHarness.AgentFolder}", $"read-files {McsRecords.Commit} {AmsHarness.AgentFolder}"], _ams.Git.Calls);
        Assert.Equal([(McsRecords.AgentId, AmsHarness.AgentFolder), (PayrollId, "Payroll")], _ams.Store.List(_ams.Root).Select(agent => (agent.AgentId, agent.AgentLocation)));
        Assert.Equal((McsRecords.AgentId, McsRecords.BotId, new AgentSourceControlMetadata(McsRecords.RepositoryId, "main", AmsHarness.AgentFolder)), (join!.Agent!.AgentId, join.Agent.McsAgentId, join.Agent.SourceControlMetadata));
        Assert.Equal((McsRecords.DevDeploymentId, McsRecords.AgentId, DeploymentStages.Dev), (join.Deployment!.DeploymentId, join.Deployment.AgentId, join.Deployment.DeploymentType));
        Assert.Equal((true, false, McsRecords.RepositoryId, "contoso-agents", McsRecords.GitUrl, AmsHarness.AgentFolder, (string?)null, false), (join.Ams!.Joined, join.Ams.Registered, join.Ams.RepositoryId, join.Ams.RepositoryName, join.Ams.GitUrl, join.Ams.Folder, join.Ams.CommitSha, join.Ams.Pushed));
        Assert.Equal([new AmsRestoredAgent(McsRecords.AgentId, "Contoso HR", AmsHarness.AgentFolder), new AmsRestoredAgent(PayrollId, "Payroll", "Payroll")], join.Ams.Restored!);
        Assert.Equal(((bool?)true, (IReadOnlyList<string>?)null, (IReadOnlyList<string>?)null, (IReadOnlyList<string>?)null), (join.WorkspaceConnected, join.Ams.MergedFiles, join.Ams.ConflictedFiles, join.Ams.Warnings));
        Assert.Empty(_ams.Service.Registrations);
        Assert.Empty(_ams.Service.DeployRecords);
        Assert.Empty(_ams.Git.CommitRequests);
        Assert.False(File.Exists(Path.Combine(_ams.Root, ".gitignore")));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("contoso hr", false)]
    [InlineData("Contoso HR copy", true)]
    public async Task AJoinInACloneOfTheHoldersRepository_RestoresTheHolderWithoutCloning_AndIgnoresAnotherAgentNameWithAWarning(string? requestedName, bool warned)
    {
        HoldInAClone();

        var join = await _ams.Creation.JoinAsync(Clone(requestedName), default);

        Assert.Equal([$"{Holding}. Restore Contoso HR in {_ams.Root} and connect Contoso HR there? [Y/n]"], _ams.Prompt.Questions);
        Assert.DoesNotContain(_ams.Git.Calls, call => call.StartsWith("clone", StringComparison.Ordinal));
        Assert.Equal(("Contoso HR", AmsHarness.AgentFolder), (join!.Agent!.DisplayName, join.Agent.AgentLocation));
        Assert.Equal(warned ? new[] { $"--agent-name 'Contoso HR copy' is ignored: Copilot Studio agent 'Contoso HR' is already in AMS as 'Contoso HR', in folder {AmsHarness.AgentFolder}." } : null, join.Ams!.Warnings?.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AJoinInAFolderThatHoldsOnlyTheProject_OrInAGitRepositoryWithoutARemote_LinksItToTheHoldersRepository_AndLeavesUnknownFoldersAndUnregisteredAgentsAsTheyAre(bool gitRepository)
    {
        _ams.AddAgent(PayrollId, "Payroll", registered: false, displayName: "Payroll");
        if (gitRepository)
        {
            WorkingTree(hasCommits: false);
        }

        HoldInTheRemote();
        _ams.Git.RemoteFiles["Notes/settings.mcs.yml"] = "displayName: Notes\nschemaName: cr1a2_notes\n";

        var join = await _ams.Creation.JoinAsync(Clone(), default);

        Assert.Equal([$"{Holding}. Link {_ams.Root} to that repository and connect Contoso HR there? [Y/n]"], _ams.Prompt.Questions);
        Assert.Contains("checkout main origin", _ams.Git.Calls);
        Assert.Equal([(PayrollId, (AgentSourceControlMetadata?)null), (McsRecords.AgentId, new AgentSourceControlMetadata(McsRecords.RepositoryId, "main", AmsHarness.AgentFolder))], _ams.Store.List(_ams.Root).Select(agent => (agent.AgentId, agent.SourceControlMetadata)));
        Assert.True(File.Exists(Path.Combine(_ams.Root, "Notes", "settings.mcs.yml")));
        Assert.Equal([McsRecords.AgentId], join!.Ams!.Restored!.Select(agent => agent.AgentId));
        Assert.Empty(_ams.Service.Registrations);
    }

    [Fact]
    public async Task AJoinUnderGlobalNonInteractive_AsksNothing_AndJoins()
    {
        HoldInAClone();

        var join = await _ams.Creation.JoinAsync(Clone() with { NonInteractive = true }, default);

        Assert.Empty(_ams.Prompt.Questions);
        Assert.True(join!.Ams!.Joined);
    }

    [Fact]
    public async Task ADeclinedJoin_IsReportedAsDeclined_AndLeavesTheFolderAsItWas()
    {
        var project = Path.Combine(_ams.Root, "work", "new");
        Directory.CreateDirectory(project);
        _ams.Service.Holders.Add(Held());
        _ams.Service.Repository = McsRecords.Repository(McsRecords.ListItem("env-1", BaseDev())) with { AgentFolderExists = true };
        _ams.Prompt.Answer = false;

        var join = await _ams.Creation.JoinAsync(Clone() with { ProjectDirectory = project, ProjectDirectoryCreated = true }, default);

        Assert.Same(AgentCreationJoin.Declined, join);
        Assert.Equal((null, null, null, null), (join!.Agent, join.Deployment, join.Ams, join.WorkspaceConnected));
        Assert.False(Directory.Exists(project));
        Assert.DoesNotContain(_ams.Git.Calls, call => call.StartsWith("clone", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("the repository's files")]
    [InlineData("the same text, which the repository holds in other line endings and with a byte order mark")]
    [InlineData("another definition")]
    public async Task AJoinOfAStandaloneWorkflow_AsksToUpdateIt_RestoresIt_AndTakesDevsVersion_KeepingTheRepositorysBytesWhereTheTextIsTheSame(string dev)
    {
        var folder = await HoldAWorkflowAsync();
        switch (dev)
        {
            case "another definition":
                _ams.Flows.Cloud[Guid.Parse(McsRecords.WorkflowId)].ClientData = "{\"properties\":{\"definition\":{\"triggers\":{}}}}";
                break;
            case "the repository's files":
                break;
            default:
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    File.WriteAllText(file, File.ReadAllText(file).ReplaceLineEndings("\r\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                }

                break;
        }

        var before = Bytes(folder);

        var join = await _ams.Creation.JoinAsync(WorkflowClone(), default);

        Assert.Equal([$"Copilot Studio workflow 'Escalate' is already in AMS as workflow 'Escalate', in repository {McsRecords.RepositoryId} (folder Escalate). Restore Escalate in {_ams.Root} and update it from Copilot Studio? [Y/n]"], _ams.Prompt.Questions);
        Assert.Equal($"find-holders - {McsRecords.WorkflowId}", _ams.Service.Calls[0]);
        Assert.Equal((McsRecords.WorkflowAgentId, AgentTypes.McsWorkflow, (bool?)null), (join!.Agent!.AgentId, join.Agent.AgentType, join.WorkspaceConnected));
        Assert.Empty(_ams.Connect.MergeCommands);
        Assert.Equal([$"get:{McsRecords.WorkflowId}", $"get:{McsRecords.WorkflowId}"], _ams.Flows.Calls);
        Assert.True(Directory.Exists(Path.Combine(Assert.Single(Directory.GetDirectories(Path.Combine(_ams.Root, ".git", "msagent", "mcs"))), McsRecords.Commit)));
        if (dev == "another definition")
        {
            Assert.NotEqual(before, Bytes(folder));
            Assert.Equal(["Copilot Studio's version of workflow 'Escalate' replaced the repository's in folder Escalate, uncommitted: `git status` lists it, and `git restore \"Escalate\"` undoes it."], join.Ams!.Warnings);
        }
        else
        {
            Assert.Equal(before, Bytes(folder));
            Assert.Null(join.Ams!.Warnings);
        }
    }

    [Fact]
    public async Task AJoinOfAStandaloneWorkflowThatDevCannotGive_KeepsTheJoinAndTheRepositorysVersion_WithANoticeThatSaysWhy()
    {
        var folder = await HoldAWorkflowAsync();
        _ams.Flows.Cloud.Clear();
        var before = Bytes(folder);

        var join = await _ams.Creation.JoinAsync(WorkflowClone(), default);

        Assert.Equal(before, Bytes(folder));
        Assert.Equal(McsRecords.WorkflowAgentId, _ams.Agent(McsRecords.WorkflowAgentId).AgentId);
        var notice = Assert.Single(join!.Ams!.Warnings!);
        Assert.StartsWith("Pulling workflow 'Escalate' from Dev failed (", notice, StringComparison.Ordinal);
        Assert.EndsWith("), so folder Escalate holds the repository's version; `msagent agent pull --overwrite` there takes Copilot Studio's.", notice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Escalate/notes.txt", false)]
    [InlineData("README.md", true)]
    public async Task AJoinOfAStandaloneWorkflowWhoseFolderHoldsUncommittedChanges_KeepsThemAndTakesNothingFromDev_WhileChangesElsewhereDontStopThePull(string changed, bool pulled)
    {
        var folder = await HoldAWorkflowAsync();
        _ams.Flows.Cloud[Guid.Parse(McsRecords.WorkflowId)].ClientData = "{\"properties\":{\"definition\":{\"triggers\":{}}}}";
        _ams.Git.Changed.Add(changed);
        var before = Bytes(folder);
        var reads = _ams.Flows.Calls.Count;

        var join = await _ams.Creation.JoinAsync(WorkflowClone(), default);

        Assert.Equal((McsRecords.WorkflowAgentId, true), (join!.Agent!.AgentId, join.Ams!.Joined));
        Assert.Equal(McsRecords.WorkflowAgentId, _ams.Agent(McsRecords.WorkflowAgentId).AgentId);
        Assert.Contains("status Escalate", _ams.Git.Calls);
        Assert.Equal(pulled ? 1 : 0, _ams.Flows.Calls.Count - reads);
        Assert.Equal(pulled, !before.SequenceEqual(Bytes(folder)));
        Assert.Equal([pulled ? "Copilot Studio's version of workflow 'Escalate' replaced the repository's in folder Escalate, uncommitted: `git status` lists it, and `git restore \"Escalate\"` undoes it." : "Workflow 'Escalate' in folder Escalate has uncommitted changes, so msagent didn't take Copilot Studio's version; commit or stash them, then run `msagent agent pull --overwrite` there."], join.Ams.Warnings);
    }

    [Theory]
    [InlineData("homed elsewhere", "agent aaaaaaaa-1111-4111-8111-000000000021, homed in environment 14141414-1414-4141-8141-141414141414.")]
    [InlineData("homed in another environment", $"agent 'Contoso HR' ({McsRecords.AgentId}), in repository {McsRecords.RepositoryId} (folder ContosoHR).")]
    [InlineData("held through a Test deployment", $"agent 'Contoso HR' ({McsRecords.AgentId}), in repository {McsRecords.RepositoryId} (folder ContosoHR).")]
    [InlineData("held without a deployment", $"agent 'Contoso HR' ({McsRecords.AgentId}), in repository {McsRecords.RepositoryId} (folder ContosoHR).")]
    [InlineData("held without a deployment by an agent without a home deployment", $"agent 'Contoso HR' ({McsRecords.AgentId}), in repository {McsRecords.RepositoryId} (folder ContosoHR).")]
    [InlineData("still being registered", $"agent 'Contoso HR' ({McsRecords.AgentId}), in repository {McsRecords.RepositoryId} (folder ContosoHR). It is still being registered.")]
    [InlineData("being deleted", $"agent 'Contoso HR' ({McsRecords.AgentId}), in repository {McsRecords.RepositoryId} (folder ContosoHR). It is being deleted.")]
    [InlineData("in an unknown state", $"agent 'Contoso HR' ({McsRecords.AgentId}), in repository {McsRecords.RepositoryId} (folder ContosoHR).")]
    [InlineData("without a repository", $"agent 'Contoso HR' ({McsRecords.AgentId}) (folder ContosoHR).")]
    [InlineData("without a folder", $"agent 'Contoso HR' ({McsRecords.AgentId}), in repository {McsRecords.RepositoryId}.")]
    public async Task AHolderTheJoinCannotTake_Is19013BeforeTheQuestion_NamingTheHolder_AndChangesNothing(string holder, string named)
    {
        _ams.AddAgent(PayrollId, "Payroll", registered: false, displayName: "Payroll");
        _ams.Service.Holders.Add(holder switch
        {
            "homed elsewhere" => new AmsMcsHolder { AgentId = "AAAAAAAA-1111-4111-8111-000000000021", HomeEnvironmentId = "14141414-1414-4141-8141-141414141414", DeploymentId = "EEEEEEEE-5555-4555-8555-000000000025", State = AmsMcsHolderState.Active },
            "homed in another environment" => Held() with { HomeEnvironmentId = "env-2" },
            "held through a Test deployment" => Held() with { DeploymentId = McsRecords.TestDeploymentId.ToUpperInvariant() },
            "held without a deployment" => Held() with { DeploymentId = null },
            "held without a deployment by an agent without a home deployment" => Held() with { DeploymentId = null, Agent = McsRecords.ListItem() with { HomeDeploymentId = null } },
            "still being registered" => Held() with { State = AmsMcsHolderState.Pending },
            "being deleted" => Held() with { State = AmsMcsHolderState.Releasing },
            "in an unknown state" => Held() with { State = "Frozen" },
            "without a repository" => Held() with { Agent = McsRecords.ListItem() with { RepositoryId = null } },
            _ => Held() with { Agent = McsRecords.ListItem() with { AgentFolder = null } },
        });
        var before = Contents();

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Creation.JoinAsync(Clone(), default));

        Assert.Equal((ErrorCodes.AmsAgent.BotClaimed, ExitCodes.Usage, "mcs-bot-claimed", $"Another AMS agent already holds this Copilot Studio agent or flow: {named}", holder is "still being registered" or "being deleted" ? "Run the same command again once that finishes." : "Clone that agent's repository and run `msagent agent init` there."), (failure.ErrorCode, failure.ExitCode, failure.ErrorKind, failure.Message, failure.Remediation));
        Assert.Equal([$"find-holders {McsRecords.BotId} -"], _ams.Service.Calls);
        Assert.Empty(_ams.Git.Calls);
        Assert.Empty(_ams.Prompt.Questions);
        Assert.Equal(before, Contents());
    }

    [Fact]
    public async Task AHolderWhoseFolderIsNotOnMainYet_Is19013BeforeTheQuestion_SayingItsOwnerHasToPushIt()
    {
        Hold();
        _ams.Service.Repository = _ams.Service.Repository with { AgentFolderExists = false };

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Creation.JoinAsync(Clone(), default));

        Assert.Equal((ErrorCodes.AmsAgent.BotClaimed, $"Another AMS agent already holds this Copilot Studio agent or flow: agent 'Contoso HR' ({McsRecords.AgentId}), in repository {McsRecords.RepositoryId} (folder ContosoHR). Its folder isn't on main yet, so its owner has to push it first."), (failure.ErrorCode, failure.Message));
        Assert.Empty(_ams.Prompt.Questions);
        Assert.Empty(Contents());
    }

    [Theory]
    [InlineData("a clone of another repository", false)]
    [InlineData("--repository-id", false)]
    [InlineData("agents of another repository", false)]
    [InlineData("a clone without the holder's folder", true)]
    public async Task AFolderThatCannotJoinTheHolder_Is19077BeforeTheQuestion_AndChangesNothing(string folder, bool pullFirst)
    {
        Hold();
        var repositoryId = folder == "--repository-id" ? McsRecords.OtherRepositoryId.ToUpperInvariant() : null;
        switch (folder)
        {
            case "a clone of another repository":
                _ams.LinkToRepository(McsRecords.OtherGitUrl);
                break;
            case "agents of another repository":
                _ams.Store.Add(_ams.Root, ManagedAgentFixtures.Agent(PayrollId, "Payroll") with { AgentLocation = "Payroll", SourceControlMetadata = new AgentSourceControlMetadata(McsRecords.OtherRepositoryId, "main", "Payroll") });
                break;
            case "a clone without the holder's folder":
                _ams.LinkToRepository();
                break;
        }

        var before = Contents();

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Creation.JoinAsync(Clone(repositoryId: repositoryId), default));

        var reason = folder switch
        {
            "a clone of another repository" => $"{_ams.Root} is a clone of repository {McsRecords.OtherRepositoryId}",
            "--repository-id" => $"--repository-id names repository {McsRecords.OtherRepositoryId}",
            "agents of another repository" => $"this project's agents are in repository {McsRecords.OtherRepositoryId}",
            _ => $"this clone of it lacks folder {AmsHarness.AgentFolder}",
        };
        Assert.Equal((ErrorCodes.AmsAgent.CannotJoinHolder, ExitCodes.Usage, "cannot-join-holder", $"{Holding}, and {reason}."), (failure.ErrorCode, failure.ExitCode, failure.ErrorKind, failure.Message));
        Assert.Equal(pullFirst ? "Run `git pull`, then the same command again." : "Run the same command with --project <an empty folder>, without --repository-id.", failure.Remediation);
        Assert.Equal(pullFirst ? 2 : 1, _ams.Service.Calls.Count);
        Assert.Empty(_ams.Prompt.Questions);
        Assert.Equal(before, Contents());
    }

    [Theory]
    [InlineData("the holder")]
    [InlineData("another agent with the holder's name")]
    [InlineData("another agent in the holder's folder")]
    public async Task AConfigThatHoldsTheHolderItsNameOrItsFolder_IsRefusedBeforeTheQuestion_AndChangesNothing(string held)
    {
        Hold();
        var agent = held switch
        {
            "the holder" => _ams.AddAgent(),
            "another agent with the holder's name" => _ams.AddAgent(PayrollId, "Payroll", registered: false, displayName: "contoso hr"),
            _ => _ams.AddAgent(PayrollId, registered: false, displayName: "Payroll"),
        };
        var before = Contents();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => _ams.Creation.JoinAsync(Clone(), default));

        Assert.Equal(held == "another agent in the holder's folder" ? (ErrorCodes.Workspace.WorkspaceNotEmpty, $"'{Path.Combine(_ams.Root, AmsHarness.AgentFolder)}' already holds agent 'Payroll'.") : (ErrorCodes.Workspace.AlreadyRegistered, $"This project is already registered as {agent.DisplayName} ({agent.AgentId}) in '{_ams.Locator.ConfigFilePath(_ams.Root)}'."), (failure.ErrorCode, failure.Message));
        Assert.Empty(_ams.Prompt.Questions);
        Assert.Equal(before, Contents());
    }

    [Fact]
    public async Task AJoinIntoAFolderWithFilesOfItsOwn_ReachesInitsRefusal_BeforeTheQuestion()
    {
        Hold();
        File.WriteAllText(Path.Combine(_ams.Root, "notes.txt"), "mine");

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Creation.JoinAsync(Clone(), default));

        Assert.Equal(ErrorCodes.AmsAgent.ProjectFolderNotDedicated, failure.ErrorCode);
        Assert.Empty(_ams.Prompt.Questions);
    }

    [Fact]
    public async Task AJoinWhoseLinkWouldOverwriteTheProjectsFiles_ReachesInitsRefusal_AndTakesTheLinkBack()
    {
        _ams.AddAgent(PayrollId, "Payroll", registered: false, displayName: "Payroll");
        HoldInTheRemote();
        _ams.Git.RemoteFiles["Payroll/settings.mcs.yml"] = "displayName: Payroll\nschemaName: cr1a2_payroll\n";
        var before = Contents();

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Creation.JoinAsync(Clone(), default));

        Assert.Equal(ErrorCodes.AmsAgent.RepositoryWouldOverwrite, failure.ErrorCode);
        Assert.Single(_ams.Prompt.Questions);
        Assert.Equal(before, Contents());
    }

    [Fact]
    public async Task AFailedRepositoryRead_StopsTheJoinBeforeTheQuestion_WithNothingToTakeBack()
    {
        Hold();
        _ams.Service.RepositoryFailure = McsRecords.Status(503);

        Assert.Equal(503, (await Assert.ThrowsAsync<TargetRequestException>(() => _ams.Creation.JoinAsync(Clone(), default))).Status);
        Assert.Empty(_ams.Prompt.Questions);
        Assert.Empty(Contents());
    }

    [Fact]
    public async Task AFailedClone_TakesTheJoinBack()
    {
        HoldInTheRemote();
        var failure = McsRecords.Rejected(ErrorCodes.AmsAgent.GitCommandFailed, "git-command-failed");
        _ams.Git.CloneFailure = failure;

        Assert.Same(failure, await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Creation.JoinAsync(Clone(), default)));
        Assert.Empty(Contents());
    }

    [Fact]
    public async Task AFailedRestore_TakesBackTheCloneAndTheRecords()
    {
        HoldInTheRemote(Payroll());
        _ams.Git.RemoteFiles["Payroll/settings.mcs.yml"] = "displayName: Payroll\nschemaName: cr1a2_payroll\n";
        _ams.Service.Repository = McsRecords.Repository(Payroll(), McsRecords.ListItem("env-1", BaseDev() with { DeploymentType = "Staging" })) with { AgentFolderExists = true };

        await Assert.ThrowsAsync<FormatException>(() => _ams.Creation.JoinAsync(Clone(), default));

        Assert.Empty(Contents());
        Assert.Empty(_ams.Store.List(_ams.Root));
    }

    [Fact]
    public async Task AHolderWhoseFolderTheCloneLacks_TakesBackTheCloneAndTheRecords_And19013SaysItIsNotOnMainYet()
    {
        Hold(Payroll());
        _ams.Git.RemoteHasMain = true;
        _ams.Git.RemoteFiles["Payroll/settings.mcs.yml"] = "displayName: Payroll\nschemaName: cr1a2_payroll\n";

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Creation.JoinAsync(Clone(), default));

        Assert.Equal(ErrorCodes.AmsAgent.BotClaimed, failure.ErrorCode);
        Assert.EndsWith("Its folder isn't on main yet, so its owner has to push it first.", failure.Message, StringComparison.Ordinal);
        Assert.Empty(Contents());
    }

    [Fact]
    public async Task AJoin_MergesFromTheFilesOfTheHoldersFolderAndItsCollectionsAtTheCloudBase_WithOneSyncCall()
    {
        HoldInAClone();
        _ams.Service.Repository = McsRecords.Repository(McsRecords.ListItem("env-1", BaseDev()) with { SchemaName = "cr1a2_listed" }) with { AgentFolderExists = true };
        var collection = _ams.DeclareCollection();
        _ams.Git.BaseFiles[$"{AmsHarness.AgentFolder}/topics/Greeting.mcs.yml"] = "kind: AdaptiveDialog\n";
        _ams.Git.BaseFiles["Shared Tools/collection.mcs.yml"] = "schemaName: cr1a2_sharedTools\n";
        _ams.Git.BaseFiles["Other/settings.mcs.yml"] = "schemaName: cr1a2_other\n";

        var join = await _ams.Creation.JoinAsync(Clone(), default);

        Assert.Contains($"read-files {McsRecords.Commit} {AmsHarness.AgentFolder} Shared Tools", _ams.Git.Calls);
        Assert.DoesNotContain("fetch origin main", _ams.Git.Calls);
        var merge = Assert.Single(_ams.Connect.MergeCommands);
        Assert.Empty(_ams.Connect.Commands);
        Assert.Equal((Path.Combine(_ams.Root, AmsHarness.AgentFolder), "cr1a2_listed", "env-1"), (merge.Connect.WorkspaceDirectory, merge.Connect.SchemaName, merge.Connect.Environment.EnvironmentId));
        Assert.Equal([collection], merge.Connect.CollectionDirectories);
        Assert.Equal([$"{AmsHarness.AgentFolder}/topics/Greeting.mcs.yml", "Shared Tools/collection.mcs.yml"], merge.BaseFiles.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("kind: AdaptiveDialog\n", Encoding.UTF8.GetString(merge.BaseFiles[$"{AmsHarness.AgentFolder}/topics/Greeting.mcs.yml"].Span));
        Assert.True(join!.WorkspaceConnected);
    }

    [Fact]
    public async Task AHolderAmsListsWithoutASchemaName_IsConnectedUnderTheSchemaNameItsFolderDeclares()
    {
        HoldInAClone();
        _ams.Service.Repository = McsRecords.Repository(McsRecords.ListItem("env-1", BaseDev()) with { SchemaName = null }) with { AgentFolderExists = true };

        await _ams.Creation.JoinAsync(Clone(), default);

        Assert.Equal(AmsHarness.SchemaName, Assert.Single(_ams.Connect.MergeCommands).Connect.SchemaName);
    }

    [Fact]
    public async Task AJoin_KeepsACopyOfEachConnectedFoldersCloudCacheForHead_AndNoneForASkippedCollection()
    {
        HoldInAClone();
        var collection = _ams.DeclareCollection();
        _ams.Connect.SkippedCollectionDirectories.Add(collection);
        _ams.Connect.OnMerge = () =>
        {
            Cloned(AmsHarness.AgentFolder);
            Cloned("Shared Tools");
        };

        var join = await _ams.CreationOver(_ams.Git, new AmsCloudCacheAttachPort(new SyncedFolders(), _ams.Connect, _ams.Shelf, _ams.Workspaces)).JoinAsync(Clone(), default);

        Assert.Equal([$"ContosoHR env-1|agent-1 live={McsRecords.Commit} {McsRecords.Commit}=cloud"], Copies());
        Assert.Equal(["Contoso HR in Dev doesn't link the collection in Shared Tools, so that folder isn't connected."], join!.Ams!.Warnings);
        Assert.True(join.WorkspaceConnected);
    }

    [Fact]
    public async Task AJoin_WhoseAgentInDevLinksNoneOfSeveralDeclaredCollections_SaysSoForAllOfThem()
    {
        HoldInAClone();
        _ams.Connect.SkippedCollectionDirectories.AddRange([Path.Combine(_ams.Root, "Shared Tools"), Path.Combine(_ams.Root, "Archived Tools")]);

        var join = await _ams.Creation.JoinAsync(Clone(), default);

        Assert.Equal(["Contoso HR in Dev doesn't link the collections in Shared Tools, Archived Tools, so those folders aren't connected."], join!.Ams!.Warnings);
    }

    [Theory]
    [InlineData(1, "the new collection folder Policies")]
    [InlineData(6, "the new collection folders Policies 1, Policies 2, Policies 3, Policies 4, Policies 5 and 1 more")]
    public async Task TheMergeWarning_NamesTheCollectionFoldersTheMergeAddedBesideTheAgent(int count, string folders)
    {
        HoldInAClone();
        string[] added = [.. Enumerable.Range(1, count).Select(number => Path.Combine(_ams.Root, count == 1 ? "Policies" : $"Policies {number}"))];
        _ams.Connect.Merged = new CopilotConnectMergeOutcome { Connected = new CopilotConnectOutcome { AgentId = RecordingConnectPort.DevBotId }, AddedCollectionDirectories = added, AddedPaths = [.. added.Select(folder => $"{Path.GetFileName(folder)}/collection.mcs.yml")] };

        var join = await _ams.Creation.JoinAsync(Clone(), default);

        Assert.Equal([$"Copilot Studio holds edits to Contoso HR that main lacks. They are merged into ContosoHR and {folders} as uncommitted changes, which `git status` lists."], join!.Ams!.Warnings);
    }

    [Theory]
    [InlineData("an unknown base", "AMS doesn't know which commit Dev holds for ContosoHR")]
    [InlineData("a base missing after a fetch", "Dev holds ContosoHR as of commit 9fceb02, which the repository lacks even after a fetch")]
    [InlineData("a base that is not an ancestor of head", "Dev holds ContosoHR as of commit 9fceb02, which isn't HEAD or an ancestor of it")]
    [InlineData("a failed connect", "Merging Copilot Studio's edits into ContosoHR failed (Dataverse refused the read)")]
    public async Task AJoinThatCannotMergeAndConnect_KeepsTheJoin_WithANoticeThatSaysWhy(string cause, string reason)
    {
        HoldInAClone();
        switch (cause)
        {
            case "an unknown base":
                _ams.Service.Repository = McsRecords.Repository(McsRecords.ListItem("env-1", McsRecords.Deployment())) with { AgentFolderExists = true };
                break;
            case "a base missing after a fetch":
                _ams.Git.Commits.Remove(McsRecords.Commit);
                break;
            case "a base that is not an ancestor of head":
                _ams.Git.MergeBase = McsRecords.OlderCommit;
                break;
            default:
                _ams.Connect.Failure = new InvalidOperationException("Dataverse refused the read.");
                break;
        }

        var join = await _ams.Creation.JoinAsync(Clone(), default);

        Assert.Equal([$"{reason}, so msagent didn't connect the folder to Dev; `msagent agent pull` works there after a Dev deploy of the commit the folder holds."], join!.Ams!.Warnings);
        Assert.False(join.WorkspaceConnected);
        Assert.Equal(cause == "a failed connect" ? 1 : 0, _ams.Connect.MergeCommands.Count);
        Assert.Equal(cause == "a base missing after a fetch" ? 1 : 0, _ams.Git.Calls.Count(call => call == "fetch origin main"));
        Assert.Equal(McsRecords.AgentId, _ams.Agent().AgentId);
    }

    [Theory]
    [InlineData("ContosoHR/topics/Greeting.mcs.yml", false)]
    [InlineData("Shared Tools/topics/Lookup.mcs.yml", false)]
    [InlineData("ContosoHR/topics/New.mcs.yml", false)]
    [InlineData("Notes.md", true)]
    public async Task OverRealGit_AMergeLeavesTheAgentsFolderAndItsCollectionFoldersAlone_WhileEitherHoldsAnUncommittedOrUntrackedFile_ButNotForAChangeOutsideThem(string changed, bool merges)
    {
        await RealGit.InitAsync(_ams.Root);
        var agent = _ams.AddAgent();
        _ams.DeclareCollection();
        RealGit.Write(_ams.Root, "ContosoHR/topics/Greeting.mcs.yml", "kind: AdaptiveDialog\n");
        RealGit.Write(_ams.Root, "Shared Tools/topics/Lookup.mcs.yml", "kind: AdaptiveDialog\n");
        RealGit.Write(_ams.Root, "Notes.md", "Notes\n");
        await RealGit.RunAsync(_ams.Root, "add", "--all");
        await RealGit.RunAsync(_ams.Root, "commit", "--quiet", "-m", "Seed");
        var seed = Assert.Single(await RealGit.RunAsync(_ams.Root, "rev-parse", "HEAD"));
        RealGit.Write(_ams.Root, changed, "edited\n");

        var connection = await _ams.JoinOver(RealGit.Adapter(), _ams.Connect).ConnectAsync(_ams.Root, [new AmsRestore(agent, null, BaseDev() with { CloudBaseCommitSha = seed })], default);

        Assert.Equal((merges, merges ? 1 : 0), (connection.Connected, _ams.Connect.MergeCommands.Count));
        Assert.Equal(merges ? [] : ["ContosoHR, or a collection folder it declares, has uncommitted changes, so msagent didn't connect the folder to Dev; `msagent agent pull` works there after a Dev deploy of the commit the folder holds."], connection.Warnings);
    }

    [Fact]
    public async Task ABaseTheCloneLacks_IsFetchedOnce_ThenMergedAndConnected()
    {
        HoldInAClone();
        _ams.Git.Commits.Remove(McsRecords.Commit);
        _ams.Git.Fetched[McsRecords.Commit] = McsRecords.Commit;

        var join = await _ams.Creation.JoinAsync(Clone(), default);

        Assert.Equal([$"rev-parse {McsRecords.Commit}", "fetch origin main", $"rev-parse {McsRecords.Commit}", $"merge-base {McsRecords.Commit} HEAD", $"status {AmsHarness.AgentFolder}", $"read-files {McsRecords.Commit} {AmsHarness.AgentFolder}"], _ams.Git.Calls.SkipWhile(call => !call.StartsWith("rev-parse", StringComparison.Ordinal)));
        Assert.True(join!.WorkspaceConnected);
    }

    [Fact]
    public async Task AJoinThatMergesCopilotStudiosEdits_ListsEveryChangedFile_AndWarnsAboutTheConflictedAndReplacedOnes()
    {
        HoldInAClone();
        _ams.Connect.Merged = new CopilotConnectMergeOutcome
        {
            Connected = new CopilotConnectOutcome { AgentId = RecordingConnectPort.DevBotId },
            MergedPaths = ["ContosoHR/topics/Merged.mcs.yml"],
            AddedPaths = ["ContosoHR/topics/Added.mcs.yml"],
            UpdatedPaths = ["ContosoHR/topics/Updated.mcs.yml"],
            DeletedPaths = ["ContosoHR/topics/Deleted.mcs.yml"],
            ConflictedPaths = ["ContosoHR/topics/Conflicted.mcs.yml"],
            ReplacedPaths = ["ContosoHR/icon.png", "ContosoHR/logo.png"],
        };

        var join = await _ams.Creation.JoinAsync(Clone(), default);

        Assert.Equal(["ContosoHR/topics/Merged.mcs.yml", "ContosoHR/topics/Added.mcs.yml", "ContosoHR/topics/Updated.mcs.yml", "ContosoHR/topics/Deleted.mcs.yml", "ContosoHR/topics/Conflicted.mcs.yml", "ContosoHR/icon.png", "ContosoHR/logo.png"], join!.Ams!.MergedFiles!);
        Assert.Equal(["ContosoHR/topics/Conflicted.mcs.yml"], join.Ams.ConflictedFiles!);
        Assert.Equal(
            [
                $"  repository   {McsRecords.RepositoryId} (contoso-agents), joined",
                $"  git url      {McsRecords.GitUrl}",
                $"  restored     Contoso HR ({McsRecords.AgentId}) in ContosoHR",
                "  warning    Copilot Studio holds edits to Contoso HR that main lacks. They are merged into ContosoHR as uncommitted changes, which `git status` lists. 1 file has a conflict to resolve: ContosoHR/topics/Conflicted.mcs.yml. 2 files are Copilot Studio's version, since a text merge can't settle them: ContosoHR/icon.png, ContosoHR/logo.png.",
            ],
            join.Ams.ToTextLines());
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, " 2 files have conflicts to resolve: ContosoHR/a.mcs.yml, ContosoHR/b.mcs.yml. 1 file is Copilot Studio's version, since a text merge can't settle it: ContosoHR/icon.png.")]
    public async Task TheMergeWarning_CountsTheConflictedAndReplacedFiles(bool conflicts, string tail)
    {
        HoldInAClone();
        _ams.Connect.Merged = new CopilotConnectMergeOutcome
        {
            Connected = new CopilotConnectOutcome { AgentId = RecordingConnectPort.DevBotId },
            UpdatedPaths = ["ContosoHR/topics/Updated.mcs.yml"],
            ConflictedPaths = conflicts ? ["ContosoHR/a.mcs.yml", "ContosoHR/b.mcs.yml"] : [],
            ReplacedPaths = conflicts ? ["ContosoHR/icon.png"] : [],
        };

        var join = await _ams.Creation.JoinAsync(Clone(), default);

        Assert.Equal([$"Copilot Studio holds edits to Contoso HR that main lacks. They are merged into ContosoHR as uncommitted changes, which `git status` lists.{tail}"], join!.Ams!.Warnings);
        Assert.Equal(conflicts ? 2 : null, join.Ams.ConflictedFiles?.Count);
    }

    private static AmsMcsAgentListItem Payroll() => McsRecords.ListItem() with { AgentId = PayrollId, DisplayName = "Payroll", SchemaName = "cr1a2_payroll", AgentFolder = "Payroll" };

    private static AmsMcsDeploymentRecord BaseDev() => McsRecords.Deployment() with { CloudBaseCommitSha = McsRecords.Commit };

    private static AmsMcsHolder Held() => new() { AgentId = McsRecords.AgentId.ToUpperInvariant(), HomeEnvironmentId = "ENV-1", DeploymentId = McsRecords.DevDeploymentId.ToUpperInvariant(), State = AmsMcsHolderState.Active, Agent = McsRecords.ListItem() };

    private AgentCreationPlan Clone(string? requestedName = null, string? repositoryId = null) => Plan(repositoryId, McsRecords.BotId) with { SourceName = "Contoso HR", RequestedName = requestedName };

    private void Hold(params AmsMcsAgentListItem[] others)
    {
        _ams.Git.Changed.Clear();
        _ams.Service.Holders.Add(Held());
        _ams.Service.Repository = McsRecords.Repository([McsRecords.ListItem("env-1", BaseDev()), .. others]) with { AgentFolderExists = true };
        _ams.Git.Commits[McsRecords.Commit] = McsRecords.Commit;
    }

    private void HoldInAClone()
    {
        _ams.LinkToRepository();
        _ams.AddAgentWorkspace();
        Hold();
    }

    private async Task<string> HoldAWorkflowAsync()
    {
        var workflow = McsRecords.WorkflowListItem();
        _ams.LinkToRepository();
        var folder = await _ams.AddWorkflowFolderAsync();
        _ams.Service.Holders.Add(Held() with { AgentId = McsRecords.WorkflowAgentId, Agent = workflow });
        _ams.Service.Repository = McsRecords.Repository(workflow) with { AgentFolderExists = true };
        return folder;
    }

    private AgentCreationPlan WorkflowClone() => Plan() with { AgentType = AgentTypes.McsWorkflow, AgentLocation = "Escalate", DisplayName = "Escalate", ClaimedWorkflowId = McsRecords.WorkflowId, SourceName = "Escalate" };

    private static string[] Bytes(string folder) => [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(folder, path)).Where(path => !path.StartsWith(".mcs", StringComparison.Ordinal)).Select(path => $"{path}={Convert.ToHexString(File.ReadAllBytes(Path.Combine(folder, path)))}").Order(StringComparer.Ordinal)];

    private void HoldInTheRemote(params AmsMcsAgentListItem[] others)
    {
        _ams.Git.RemoteHasMain = true;
        _ams.Git.RemoteFiles[$"{AmsHarness.AgentFolder}/settings.mcs.yml"] = $"displayName: Contoso HR\nschemaName: {AmsHarness.SchemaName}\n";
        Hold(others);
    }

    private string[] Contents() => [.. Directory.EnumerateFileSystemEntries(_ams.Root, "*", SearchOption.AllDirectories).Select(path => File.Exists(path) ? $"{Path.GetRelativePath(_ams.Root, path)}={File.ReadAllText(path)}" : $"{Path.GetRelativePath(_ams.Root, path)}/").Order(StringComparer.Ordinal)];

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
