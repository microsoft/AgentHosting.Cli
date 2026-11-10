using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsAgentInitStepTests : IDisposable
{
    private const string PayrollId = "aaaaaaaa-1111-4111-8111-000000000021";
    private const string PayrollFolder = "Payroll";

    private readonly AmsHarness _ams = new();
    private readonly FakeOperations _operations = new();

    public void Dispose() => _ams.Dispose();

    [Fact]
    public async Task ATeammatesClone_RestoresEveryAgentOfTheRepository_WithItsDeployments_FromOneRead_AndCommitsNothing()
    {
        _ams.LinkToRepository();
        _ams.AddAgentWorkspace();
        _ams.AddAgentWorkspace("Payroll", "cr1a2_payroll", "Payroll");
        _ams.Service.Repository = McsRecords.Repository(
            McsRecords.ListItem("env-1", McsRecords.Deployment(), McsRecords.Deployment(McsRecords.TestDeploymentId, "Test", "contoso-test", "env-2")),
            McsRecords.ListItem() with { AgentId = PayrollId, DisplayName = "Payroll", SchemaName = "cr1a2_payroll", AgentFolder = "Payroll" });

        var result = await Init();

        Assert.Equal([$"read-repository {McsRecords.RepositoryId} - with-deployments"], _ams.Service.Calls);
        Assert.Empty(_ams.Prompt.Questions);
        var restored = _ams.Agent();
        Assert.Equal((McsRecords.BotId, AmsHarness.SchemaName, AmsHarness.AgentFolder, new AgentSourceControlMetadata(McsRecords.RepositoryId, "main", AmsHarness.AgentFolder)), (restored.McsAgentId, restored.McsSchemaName, restored.AgentLocation, restored.SourceControlMetadata));
        Assert.Equal([(McsRecords.DevDeploymentId, DeploymentStages.Dev, "env-1"), (McsRecords.TestDeploymentId, DeploymentStages.Test, "env-2")], _ams.Deployments().Where(deployment => deployment.AgentId == McsRecords.AgentId).Select(deployment => (deployment.DeploymentId, deployment.DeploymentType, deployment.EnvironmentId)));
        Assert.Equal([McsRecords.AgentId, PayrollId], result.Ams!.Restored!.Select(agent => agent.AgentId));
        Assert.Null(result.Ams.CommitSha);
        Assert.Empty(_ams.Git.CommitRequests);
        Assert.Contains("/.config/agent.config.json", File.ReadAllText(Path.Combine(_ams.Root, ".gitignore")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("restores")]
    [InlineData("registers")]
    [InlineData("records")]
    [InlineData("registers as new")]
    public async Task AFolderItCommitsOrRestores_WhoseMcsCacheGitTracks_IsRefusedBeforeTheQuestion_AndNothingIsWritten(string role)
    {
        _ams.LinkToRepository();
        if (role is "registers" or "records")
        {
            _ams.AddAgent(registered: false);
        }
        else
        {
            _ams.AddAgentWorkspace();
        }

        _ams.Service.Repository = role is "restores" or "records" ? McsRecords.Repository(McsRecords.ListItem("env-1", McsRecords.Deployment())) : McsRecords.Repository();
        _ams.Git.Tracked.Add($"{AmsHarness.AgentFolder}/.mcs/botdefinition.json");
        _ams.Git.Tracked.Add($"{AmsHarness.AgentFolder}/topics/Greeting.mcs.yml");
        var before = Contents(_ams.Root);

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => _ams.InitStep(_operations).HandleAsync(new ManagedAgentInitRequest { McsAgentSource = _ams.Root, Name = role == "registers as new" ? "Contoso HR" : null }, default));

        Assert.Equal((ErrorCodes.AmsAgent.McsFolderInRepository, ExitCodes.Usage, $"git tracks a .mcs folder inside {AmsHarness.AgentFolder}, a cache that must come from the cloud, so nothing was changed."), (failure.ErrorCode, failure.ExitCode, failure.Message));
        Assert.Equal($"Run `git rm -r --cached \"{AmsHarness.AgentFolder}/.mcs\"`, then commit and run the command again.", failure.Remediation);
        Assert.Equal([FakeGitPort.Log("HEAD", "--not", "--remotes=origin"), $"ls-files {AmsHarness.AgentFolder}"], _ams.Git.Calls.Where(call => call.StartsWith("ls-files", StringComparison.Ordinal) || call.StartsWith("log", StringComparison.Ordinal)));
        Assert.Empty(_ams.Prompt.Questions);
        Assert.Equal([$"read-repository {McsRecords.RepositoryId} - with-deployments"], _ams.Service.Calls);
        Assert.Equal(before, Contents(_ams.Root));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnInitWhoseCommitsToPushHoldMcs_IsRefusedWithTheHistoryWording_EvenWhereGitStillTracksIt_BeforeTheQuestion(bool linked)
    {
        if (linked)
        {
            _ams.LinkToRepository();
            _ams.Service.Repository = McsRecords.Repository();
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(_ams.Root, ".git"));
            _ams.Git.Tree = new GitWorkingTree(_ams.Root, [], AmsProjectLink.Branch, true);
        }

        _ams.AddAgent(registered: false);
        _ams.Git.Committed.Add($"{AmsHarness.AgentFolder}/.mcs/workflow-state.json");
        _ams.Git.Tracked.Add($"{AmsHarness.AgentFolder}/.mcs/workflow-state.json");
        var before = Contents(_ams.Root);

        var failure = await Assert.ThrowsAsync<AmsAgentException>(Init);

        Assert.Equal((ErrorCodes.AmsAgent.McsFolderInRepository, $"Commits msagent would push hold a .mcs folder inside {AmsHarness.AgentFolder}, a cache that must come from the cloud, so nothing was changed."), (failure.ErrorCode, failure.Message));
        Assert.Equal([linked ? FakeGitPort.Log("HEAD", "--not", "--remotes=origin") : FakeGitPort.Log("HEAD")], _ams.Git.Calls.Where(call => call.StartsWith("ls-files", StringComparison.Ordinal) || call.StartsWith("log", StringComparison.Ordinal)));
        Assert.Empty(_ams.Prompt.Questions);
        Assert.Empty(_ams.Service.Registrations);
        Assert.Equal(before, Contents(_ams.Root));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARepositoryThatBringsATrackedMcsCache_IsRefusedAfterTheCloneOrAttach_AsNothingRestored_AndTakenBack(bool clone)
    {
        if (!clone)
        {
            _ams.AddAgentWorkspace();
        }

        BringPayrollWithTheAttach();
        _ams.Git.Tracked.Add($"{PayrollFolder}/.mcs/botdefinition.json");
        var before = Contents(_ams.Root);

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => _ams.InitStep(MainInit(_ams)).HandleAsync(clone ? CloneRequest(_ams.Root) : AttachRequest(), default));

        Assert.Equal($"git tracks a .mcs folder inside {PayrollFolder}, a cache that must come from the cloud, so nothing was restored.", failure.Message);
        Assert.Equal($"Run `git rm -r \"{PayrollFolder}/.mcs\"`, commit and push, then run `msagent agent init` again; the next deploy to Dev, `msagent agent deploy`, or `msagent agent deploy --remote` when the folder holds exactly the commit it deploys, connects the folder and rebuilds the cache.", failure.Remediation);
        Assert.Contains(clone ? $"clone {McsRecords.GitUrl}" : "checkout main origin", _ams.Git.Calls);
        Assert.DoesNotContain(_ams.Git.Calls, call => call.StartsWith("log", StringComparison.Ordinal));
        Assert.Equal(before, Contents(_ams.Root));
    }

    [Fact]
    public async Task TheAgentsConfigHolds_AreRegisteredWithTheirDeployments_InANewRepository_AttachedOnItsInitialCommit()
    {
        _ams.AddAgent(registered: false);
        _ams.AddDeployment();
        _ams.Git.Changed.Add($"{AmsHarness.AgentFolder}/settings.mcs.yml");
        _ams.Git.HoldGrsInitialCommit();

        var result = await Init();

        Assert.Equal([$"Create a GRS repository in environment 'Contoso Dev' for {_ams.Root} and push agent 'Contoso HR' to it? [Y/n]"], _ams.Prompt.Questions);
        var registered = Assert.Single(_ams.Service.Registrations);
        Assert.Null(registered.RepositoryId);
        Assert.Equal([(McsRecords.DevDeploymentId, "Dev", (string?)null)], registered.Deployments.Select(deployment => (deployment.DeploymentId, deployment.DeploymentType, deployment.BotId)));
        Assert.Equal(new AgentSourceControlMetadata(McsRecords.RepositoryId, "main", AmsHarness.AgentFolder), _ams.Agent().SourceControlMetadata);
        Assert.Equal((McsRecords.AgentId, true, McsRecords.NewCommit, true, "main"), (result.AgentId, result.Ams!.RepositoryCreated, result.Ams.CommitSha, result.Ams.Pushed, result.Ams.Branch));
        Assert.Equal(["init", $"remote add origin {McsRecords.GitUrl}", $"helper {McsRecords.GitUrl}", "fetch origin main", "ls-tree origin/main", "checkout main origin"], _ams.Git.Calls.SkipWhile(call => call != "init").Take(6));
        Assert.Equal("push origin main", _ams.Git.Calls[^1]);
        Assert.True(File.Exists(Path.Combine(_ams.Root, "README.md")));
        Assert.Null(_operations.LastManagedAgentInitRequest);
    }

    [Fact]
    public async Task AWorkingTreeWithoutCommits_IsAttachedOnMain_EvenFromAnotherUnbornBranch_WithNoBranchWarning()
    {
        Directory.CreateDirectory(Path.Combine(_ams.Root, ".git"));
        _ams.Git.Tree = new GitWorkingTree(_ams.Root, [], "master", false);
        _ams.AddAgent(registered: false);
        _ams.Git.Changed.Add($"{AmsHarness.AgentFolder}/settings.mcs.yml");
        _ams.Git.HoldGrsInitialCommit();

        var result = await Init();

        Assert.Equal(("main", true), (result.Ams!.Branch, result.Ams.Pushed));
        Assert.Null(result.Ams.Warnings);
        Assert.Equal("push origin main", _ams.Git.Calls[^1]);
    }

    [Fact]
    public async Task AWorkingTreeWithHistory_RegistersItsAgentsInANewRepository_WhoseInitialCommitTheLeasedPushReplaces()
    {
        Directory.CreateDirectory(Path.Combine(_ams.Root, ".git"));
        _ams.Git.Tree = new GitWorkingTree(_ams.Root, [], AmsProjectLink.Branch, true);
        _ams.AddAgent(registered: false);
        _ams.AddAgent(PayrollId, PayrollFolder, registered: false, displayName: "Payroll");
        _ams.Git.Changed.Add($"{AmsHarness.AgentFolder}/settings.mcs.yml");
        _ams.Git.HoldGrsInitialCommit();

        var result = await Init();

        Assert.Equal([null, McsRecords.RepositoryId], _ams.Service.Registrations.Select(registration => registration.RepositoryId));
        Assert.Equal($"push --force-with-lease=main:{McsRecords.InitialCommit} origin main", _ams.Git.Calls[^1]);
        Assert.DoesNotContain(_ams.Git.Calls, call => call.StartsWith("checkout", StringComparison.Ordinal));
        Assert.Equal((true, "main"), (result.Ams!.Pushed, result.Ams.Branch));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAgentAmsAlreadyHolds_GetsItsFolderRecorded_AndOnlyTheDeploymentsAmsLacks(bool amsAnswersInUppercase)
    {
        _ams.LinkToRepository();
        _ams.AddAgent(registered: false);
        _ams.AddDeployment();
        _ams.AddDeployment(deploymentId: McsRecords.TestDeploymentId, name: "contoso-test", stage: DeploymentStages.Test, environmentId: "env-2");
        var known = McsRecords.ListItem("env-1", McsRecords.Deployment());
        _ams.Service.Repository = amsAnswersInUppercase ? McsRecords.Repository(InUppercase(known)) with { RepositoryId = McsRecords.RepositoryId.ToUpperInvariant() } : McsRecords.Repository(known);

        await Init();

        Assert.Empty(_ams.Service.Registrations);
        Assert.Equal([McsRecords.TestDeploymentId], _ams.Service.DeploymentCreates.Select(created => created.DeploymentId));
        Assert.Equal(new AgentSourceControlMetadata(McsRecords.RepositoryId, "main", AmsHarness.AgentFolder), _ams.Agent().SourceControlMetadata);
    }

    [Fact]
    public async Task ANo_CancelsTheInit_AndWritesNothing()
    {
        _ams.AddAgent(registered: false);
        _ams.Prompt.Answer = false;
        var before = File.ReadAllText(Path.Combine(_ams.Root, ".config", "agent.config.json"));

        var result = await Init();

        Assert.Equal(["Cancelled. Nothing was registered."], result.ToTextLines());
        Assert.Equal(before, File.ReadAllText(Path.Combine(_ams.Root, ".config", "agent.config.json")));
        Assert.Empty(_ams.Service.Calls);
        Assert.False(Directory.Exists(Path.Combine(_ams.Root, ".git")));
    }

    [Fact]
    public async Task AProjectFolderThatIsItselfTheAgentsFolder_IsRefused()
    {
        File.WriteAllText(Path.Combine(_ams.Root, "settings.mcs.yml"), $"displayName: Contoso HR\nschemaName: {AmsHarness.SchemaName}\n");

        Assert.Equal(ErrorCodes.AmsAgent.WorkspaceAtProjectRoot, (await Assert.ThrowsAsync<AmsAgentException>(Init)).ErrorCode);
        Assert.Empty(_ams.Git.Calls);
    }

    [Fact]
    public async Task AnAttach_RestoresTheAgentsTheRepositoryBrings_AndRegistersTheFoldersOwnWorkspace()
    {
        _ams.AddAgentWorkspace();
        BringPayrollWithTheAttach();
        _ams.Git.Changed.Add($"{AmsHarness.AgentFolder}/settings.mcs.yml");

        var result = await _ams.InitStep(MainInit(_ams)).HandleAsync(AttachRequest(), default);

        Assert.Equal([$"Link {_ams.Root} to repository 'contoso-agents' ({McsRecords.RepositoryId}) and add agent 'Contoso HR' to it? [Y/n]"], _ams.Prompt.Questions);
        Assert.Equal([PayrollId], result.Ams!.Restored!.Select(agent => agent.AgentId));
        var registration = Assert.Single(_ams.Service.Registrations);
        Assert.Equal((result.AgentId, McsRecords.RepositoryId, AmsHarness.AgentFolder), (registration.AgentId, registration.RepositoryId, registration.AgentFolder));
        Assert.Equal((AmsHarness.AgentFolder, "msagent: add MCSAgent Contoso HR"), (result.Ams.Folder, Assert.Single(_ams.Git.CommitRequests).Message));
        Assert.Equal([AmsHarness.AgentFolder, PayrollFolder], _ams.Store.List(_ams.Root).Select(agent => agent.AgentLocation).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("main's init")]
    [InlineData("register")]
    [InlineData("tracked .mcs")]
    public async Task AFailureAfterTheAttach_LeavesTheFolderExactlyAsItWas(string failingStep)
    {
        _ams.AddAgentWorkspace();
        File.WriteAllText(Path.Combine(_ams.Root, ".gitignore"), "bin/\r\n");
        BringPayrollWithTheAttach();
        var operations = Failing(failingStep);
        var before = Contents(_ams.Root);

        await Assert.ThrowsAnyAsync<AgentHostingException>(() => _ams.InitStep(operations).HandleAsync(AttachRequest(), default));

        Assert.Contains("checkout main origin", _ams.Git.Calls);
        Assert.Equal(before, Contents(_ams.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEmptyOrMissingFolder_GetsTheRepositoryCloned_AndItsAgentsRestoredWithAmsIds(bool missing)
    {
        var project = missing ? Path.Combine(_ams.Root, "work", "teammate") : _ams.Root;
        _ams.Git.RemoteHasMain = true;
        _ams.Git.RemoteFiles[$"{AmsHarness.AgentFolder}/settings.mcs.yml"] = $"displayName: Contoso HR\nschemaName: {AmsHarness.SchemaName}\n";
        _ams.Git.RemoteFiles[".gitignore"] = $"{AmsProjectLink.ConfigIgnoreLine}\n";
        _ams.Service.Repository = McsRecords.Repository(McsRecords.ListItem("env-1", McsRecords.Deployment(), McsRecords.Deployment(McsRecords.TestDeploymentId, "Test", "contoso-test", "env-2")));

        var result = await _ams.InitStep(_operations).HandleAsync(CloneRequest(project), default);

        Assert.Equal([$"Clone repository 'contoso-agents' ({McsRecords.RepositoryId}) into {project}, restore its agents and connect them? [Y/n]"], _ams.Prompt.Questions);
        Assert.Contains($"clone {McsRecords.GitUrl}", _ams.Git.Calls);
        var restored = Assert.Single(_ams.Store.List(project));
        Assert.Equal((McsRecords.AgentId, McsRecords.BotId, new AgentSourceControlMetadata(McsRecords.RepositoryId, "main", AmsHarness.AgentFolder)), (restored.AgentId, restored.McsAgentId, restored.SourceControlMetadata));
        Assert.Equal([(McsRecords.DevDeploymentId, McsRecords.AgentId), (McsRecords.TestDeploymentId, McsRecords.AgentId)], ((IAgentDeploymentRegistry)_ams.Store).List(project).Select(deployment => (deployment.DeploymentId, deployment.AgentId)));
        Assert.Equal([McsRecords.AgentId], result.Ams!.Restored!.Select(agent => agent.AgentId));
        Assert.Empty(_ams.Git.CommitRequests);
        Assert.Null(_operations.LastManagedAgentInitRequest);
    }

    [Fact]
    public async Task DecliningTheClone_CancelsTheInit_AndCreatesNothing()
    {
        _ams.Service.Repository = McsRecords.Repository(McsRecords.ListItem());
        _ams.Prompt.Answer = false;

        var result = await _ams.InitStep(_operations).HandleAsync(CloneRequest(Path.Combine(_ams.Root, "work", "teammate")), default);

        Assert.Equal("cancelled", result.Status);
        Assert.Equal(["Cancelled. Nothing was registered."], result.ToTextLines());
        Assert.DoesNotContain(_ams.Git.Calls, call => call.StartsWith("clone", StringComparison.Ordinal));
        Assert.Empty(Contents(_ams.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEmptyRepository_FailsWithMainsError_AndLeavesTheFolderAsItWas(bool missing)
    {
        var project = missing ? Path.Combine(_ams.Root, "work", "teammate") : _ams.Root;
        _ams.Service.Repository = McsRecords.Repository();
        var before = Contents(_ams.Root);

        var failure = await Assert.ThrowsAsync<UsageException>(() => _ams.InitStep(MainInit(_ams)).HandleAsync(CloneRequest(project), default));

        Assert.Equal("`msagent agent init` requires --agent-name.", failure.Message);
        Assert.Contains($"clone {McsRecords.GitUrl}", _ams.Git.Calls);
        Assert.Equal(before, Contents(_ams.Root));
    }

    [Fact]
    public async Task ACloneWhoseRootIsAnAgentsFolder_IsRefused_AndTakenBack()
    {
        _ams.Git.RemoteHasMain = true;
        _ams.Git.RemoteFiles["settings.mcs.yml"] = $"displayName: Contoso HR\nschemaName: {AmsHarness.SchemaName}\n";
        _ams.Service.Repository = McsRecords.Repository();

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => _ams.InitStep(MainInit(_ams)).HandleAsync(CloneRequest(_ams.Root), default));

        Assert.Equal(ErrorCodes.AmsAgent.WorkspaceAtProjectRoot, failure.ErrorCode);
        Assert.Empty(Contents(_ams.Root));
    }

    [Fact]
    public async Task UppercaseIdsFromTheRepositoryRead_AreRestoredInMainsForm_WithTheDeploymentsStillTheAgents()
    {
        _ams.LinkToRepository();
        _ams.AddAgentWorkspace();
        _ams.Service.Repository = McsRecords.Repository(InUppercase(McsRecords.ListItem("env-1", McsRecords.Deployment(), McsRecords.Deployment(McsRecords.TestDeploymentId, "Test", "contoso-test", "env-2")))) with { RepositoryId = McsRecords.RepositoryId.ToUpperInvariant() };

        await Init();
        await Init();

        var agent = Assert.Single(_ams.Store.List(_ams.Root));
        Assert.Equal((McsRecords.AgentId, McsRecords.BotId, McsRecords.RepositoryId), (agent.AgentId, agent.McsAgentId, agent.SourceControlMetadata!.RepositoryId));
        var shown = await new ManagedAgentShowService(_ams.Store, _ams.Store, _ams.Locator).ShowAsync(new ManagedAgentShowRequest { Project = _ams.Root }, default);
        Assert.Equal([(McsRecords.DevDeploymentId, McsRecords.AgentId), (McsRecords.TestDeploymentId, McsRecords.AgentId)], shown.Deployments.Select(deployment => (deployment.DeploymentId, deployment.AgentId)));
        Assert.Equal([McsRecords.DevDeploymentId, McsRecords.TestDeploymentId], new AgentDeploymentListService(_ams.Resolver).List(new AgentDeploymentListRequest { Project = _ams.Root, AgentId = McsRecords.AgentId }).Slots.Select(slot => slot.DeploymentId));
    }

    [Fact]
    public async Task ARegisterAnswerWithUppercaseIds_IsRecordedInMainsForm()
    {
        var repositoryId = McsRecords.RepositoryId.ToUpperInvariant();
        _ams.AddAgent(registered: false);
        _ams.AddDeployment();
        _ams.Service.OnRegister = request => McsRecords.Agent() with
        {
            AgentId = request.AgentId.ToUpperInvariant(),
            RepositoryId = repositoryId,
            SourceControl = McsRecords.Agent().SourceControl! with { RepositoryId = repositoryId, RepositoryCreated = true },
            Mcs = McsRecords.Agent().Mcs! with { HomeDeploymentId = McsRecords.DevDeploymentId.ToUpperInvariant() },
        };

        var result = await Init();

        Assert.Equal((McsRecords.RepositoryId, McsRecords.RepositoryId), (_ams.Agent().SourceControlMetadata!.RepositoryId, result.Ams!.RepositoryId));
        Assert.Equal([(McsRecords.DevDeploymentId, McsRecords.AgentId)], new AgentDeploymentListService(_ams.Resolver).List(new AgentDeploymentListRequest { Project = _ams.Root }).Slots.Select(slot => (slot.DeploymentId, slot.AgentId)));
    }

    [Fact]
    public async Task AnAgentConfigHoldsInAnotherFolder_IsNotRestoredAgain_WhenAmsAnswersInUppercase_AndItsAmsFolderIsNamedAsOneConfigDoesNotList()
    {
        _ams.LinkToRepository();
        _ams.AddAgentWorkspace();
        _ams.AddAgentWorkspace("HR");
        _ams.Store.Add(_ams.Root, ManagedAgentFixtures.Agent(McsRecords.AgentId, "Contoso HR") with { AgentLocation = "HR", McsSchemaName = AmsHarness.SchemaName, SourceControlMetadata = new AgentSourceControlMetadata(McsRecords.RepositoryId, "main", AmsHarness.AgentFolder) });
        _ams.AddDeployment();
        _ams.AddDeployment(deploymentId: McsRecords.TestDeploymentId, name: "contoso-test", stage: DeploymentStages.Test, environmentId: "env-2");
        _ams.Service.Repository = McsRecords.Repository(InUppercase(McsRecords.ListItem("env-1", McsRecords.Deployment(), McsRecords.Deployment(McsRecords.TestDeploymentId, "Test", "contoso-test", "env-2")))) with { RepositoryId = McsRecords.RepositoryId.ToUpperInvariant() };
        var before = Contents(_ams.Root);

        var failure = await Assert.ThrowsAsync<UsageException>(Init);

        Assert.Equal((ErrorCodes.General.Usage, $"The project's .config doesn't list the agent in folder {AmsHarness.AgentFolder}, so `msagent agent init` would register it as a new agent. Pass --agent-name to name it."), (failure.ErrorCode, failure.Message));
        Assert.Equal([$"read-repository {McsRecords.RepositoryId} - with-deployments"], _ams.Service.Calls);
        Assert.Equal(before, Contents(_ams.Root));
        Assert.Equal([(McsRecords.AgentId, "HR")], _ams.Store.List(_ams.Root).Select(agent => (agent.AgentId, agent.AgentLocation)));
        Assert.Equal([McsRecords.DevDeploymentId, McsRecords.TestDeploymentId], _ams.Deployments().Select(deployment => deployment.DeploymentId));
        Assert.Null(_operations.LastManagedAgentInitRequest);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFolderOfTheRepositoryThatAmsDoesNotList_FailsNamingIt_AndLeavesTheProjectAsItWas(bool cloned)
    {
        if (cloned)
        {
            _ams.Git.RemoteHasMain = true;
            _ams.Git.RemoteFiles[$"{PayrollFolder}/settings.mcs.yml"] = "displayName: Payroll\nschemaName: cr1a2_payroll\n";
        }
        else
        {
            _ams.LinkToRepository();
            _ams.AddAgentWorkspace(PayrollFolder, "cr1a2_payroll", "Payroll");
        }

        _ams.Service.Repository = McsRecords.Repository();
        var before = Contents(_ams.Root);

        var failure = await Assert.ThrowsAsync<UsageException>(() => _ams.InitStep(MainInit(_ams)).HandleAsync(cloned ? CloneRequest(_ams.Root) : new ManagedAgentInitRequest { McsAgentSource = _ams.Root }, default));

        Assert.Equal((ErrorCodes.General.Usage, $"AMS doesn't list the agent in folder {PayrollFolder} for repository contoso-agents, so `msagent agent init` would register it as a new agent. Pass --agent-name to do that, or remove the folder from the repository if its agent was deleted."), (failure.ErrorCode, failure.Message));
        Assert.Equal(cloned, _ams.Git.Calls.Contains($"clone {McsRecords.GitUrl}"));
        Assert.Equal(before, Contents(_ams.Root));
    }

    [Fact]
    public async Task AWorkflowFolderAmsDoesNotList_InAFolderToLink_FailsNamingItAndTheRepositoryById_BeforeAnythingChanges()
    {
        WorkflowFixtures.Write(Path.Combine(_ams.Root, PayrollFolder), "Payroll");
        _ams.Service.Repository = McsRecords.Repository() with { Name = null };
        var before = Contents(_ams.Root);

        var failure = await Assert.ThrowsAsync<UsageException>(() => _ams.InitStep(MainInit(_ams)).HandleAsync(new ManagedAgentInitRequest { AgentType = AgentTypes.McsWorkflow, McsAgentSource = _ams.Root, RepositoryId = McsRecords.RepositoryId }, default));

        Assert.Equal((ErrorCodes.General.Usage, $"AMS doesn't list the workflow in folder {PayrollFolder} for repository {McsRecords.RepositoryId}, so `msagent agent init` would register it as a new workflow. Pass --agent-name to do that, or remove the folder if its workflow was deleted."), (failure.ErrorCode, failure.Message));
        Assert.Empty(_ams.Prompt.Questions);
        Assert.Equal(before, Contents(_ams.Root));
    }

    [Fact]
    public async Task WithNoRepositoryYet_AFolderConfigDoesNotList_FailsNamingIt_WithoutClaimingAmsDoesNotListIt()
    {
        _ams.AddAgentWorkspace(PayrollFolder, "cr1a2_payroll", "Payroll");
        var before = Contents(_ams.Root);

        var failure = await Assert.ThrowsAsync<UsageException>(Init);

        Assert.Equal((ErrorCodes.General.Usage, $"The project's .config doesn't list the agent in folder {PayrollFolder}, so `msagent agent init` would register it as a new agent. Pass --agent-name to name it."), (failure.ErrorCode, failure.Message));
        Assert.Empty(_ams.Service.Calls);
        Assert.Null(_operations.LastManagedAgentInitRequest);
        Assert.Equal(before, Contents(_ams.Root));
    }

    [Fact]
    public async Task TheAgentsItRestoresThatDevHolds_AreMergedAndConnectedAfterItsCommitAndPush_WithOneSyncCallEach()
    {
        _ams.LinkToRepository();
        _ams.AddAgentWorkspace();
        _ams.AddAgentWorkspace(PayrollFolder, "cr1a2_payroll", "Payroll");
        _ams.AddAgent("aaaaaaaa-1111-4111-8111-000000000041", "Notes", registered: false, displayName: "Notes");
        _ams.Git.Changed.Add("Notes/settings.mcs.yml");
        _ams.Git.Commits[McsRecords.Commit] = McsRecords.Commit;
        _ams.Service.Repository = McsRecords.Repository(McsRecords.ListItem("env-1", BaseDev()), Payroll(BaseDev() with { DeploymentId = "eeeeeeee-5555-4555-8555-000000000035", AgentId = PayrollId }));
        _ams.Connect.Merged = new CopilotConnectMergeOutcome { Connected = new CopilotConnectOutcome { AgentId = RecordingConnectPort.DevBotId }, UpdatedPaths = ["ContosoHR/topics/A.mcs.yml"], ConflictedPaths = ["ContosoHR/topics/C.mcs.yml"] };
        _ams.Connect.OnMerge = () => _ams.Workspaces.WorkspaceExists = true;

        var result = await Init();

        Assert.Equal([$"status {AmsHarness.AgentFolder} {PayrollFolder} Notes .gitignore", "commit msagent: add MCSAgent Notes", "push origin main", $"rev-parse {McsRecords.Commit}", $"merge-base {McsRecords.Commit} HEAD", $"status {AmsHarness.AgentFolder}", $"read-files {McsRecords.Commit} {AmsHarness.AgentFolder}", $"rev-parse {McsRecords.Commit}", $"merge-base {McsRecords.Commit} HEAD", $"status {PayrollFolder}", $"read-files {McsRecords.Commit} {PayrollFolder}"], _ams.Git.Calls.SkipWhile(call => !call.StartsWith("status", StringComparison.Ordinal)));
        Assert.Equal([Path.Combine(_ams.Root, AmsHarness.AgentFolder), Path.Combine(_ams.Root, PayrollFolder)], _ams.Connect.MergeCommands.Select(command => command.Connect.WorkspaceDirectory));
        Assert.Empty(_ams.Connect.Commands);
        Assert.True(result.Connected);
        Assert.Equal(["ContosoHR/topics/A.mcs.yml", "ContosoHR/topics/C.mcs.yml", "ContosoHR/topics/A.mcs.yml", "ContosoHR/topics/C.mcs.yml"], result.Ams!.MergedFiles!);
        Assert.Equal(["ContosoHR/topics/C.mcs.yml", "ContosoHR/topics/C.mcs.yml"], result.Ams.ConflictedFiles!);
        Assert.Equal(
            [
                "Copilot Studio holds edits to Contoso HR that main lacks. They are merged into ContosoHR as uncommitted changes, which `git status` lists. 1 file has a conflict to resolve: ContosoHR/topics/C.mcs.yml.",
                "Copilot Studio holds edits to Payroll that main lacks. They are merged into Payroll as uncommitted changes, which `git status` lists. 1 file has a conflict to resolve: ContosoHR/topics/C.mcs.yml.",
            ],
            result.Ams.Warnings!);
    }

    [Fact]
    public async Task ARestoredAgentDevNeverDeployed_IsLeftUnconnected_WithoutASyncCallOrANotice()
    {
        _ams.LinkToRepository();
        _ams.AddAgentWorkspace();
        _ams.Service.Repository = McsRecords.Repository(McsRecords.ListItem("env-1", McsRecords.Deployment() with { BotId = null, State = "NotDeployed" }));

        var result = await Init();

        Assert.False(result.Connected);
        Assert.Empty(_ams.Connect.MergeCommands);
        Assert.Null(result.Ams!.Warnings);
    }

    [Fact]
    public async Task ARestoredFolderThatCannotBeConnected_ReadsNotConnected_WithTheNoticeThatSaysWhy()
    {
        _ams.LinkToRepository();
        _ams.AddAgentWorkspace();
        _ams.Service.Repository = McsRecords.Repository(McsRecords.ListItem("env-1", McsRecords.Deployment()));

        var result = await Init();

        Assert.False(result.Connected);
        Assert.Equal([$"AMS doesn't know which commit Dev holds for {AmsHarness.AgentFolder}, so msagent didn't connect the folder to Dev; `msagent agent pull` works there after a Dev deploy of the commit the folder holds."], result.Ams!.Warnings);
        Assert.Empty(_ams.Connect.MergeCommands);
        Assert.Equal(McsRecords.BotId, _ams.Agent().McsAgentId);
    }

    [Fact]
    public async Task RestoredAgentsWhoseBaseTheCloneLacks_FetchMainOnce()
    {
        _ams.LinkToRepository();
        _ams.AddAgentWorkspace();
        _ams.AddAgentWorkspace(PayrollFolder, "cr1a2_payroll", "Payroll");
        _ams.Service.Repository = McsRecords.Repository(McsRecords.ListItem("env-1", BaseDev()), Payroll(BaseDev() with { DeploymentId = "eeeeeeee-5555-4555-8555-000000000035", AgentId = PayrollId }));

        var result = await Init();

        Assert.Equal(1, _ams.Git.Calls.Count(call => call == "fetch origin main"));
        Assert.Equal(2, result.Ams!.Warnings!.Count(warning => warning.EndsWith("which the repository lacks even after a fetch, so msagent didn't connect the folder to Dev; `msagent agent pull` works there after a Dev deploy of the commit the folder holds.", StringComparison.Ordinal)));
        Assert.Empty(_ams.Connect.MergeCommands);
    }

    [Fact]
    public async Task ARegisterRefusedWith19013_KeepsTheMapsRemediation_ForAgentInit()
    {
        _ams.AddAgent(registered: false);
        _ams.Git.HoldGrsInitialCommit();
        var refusal = McsRecords.Rejected(ErrorCodes.AmsAgent.BotClaimed, "mcs-bot-claimed", ExitCodes.Usage);
        _ams.Service.OnRegister = _ => throw refusal;

        Assert.Same(refusal, await Assert.ThrowsAsync<AmsAgentException>(Init));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ATeammatesCloneOfAStandaloneWorkflow_RestoresItOnMain_EvenWhenAmsNamesNoBranch_AndTakesDevsVersionAfterItsCommitAndPush_UnlessDevNeverDeployedIt(bool deployed, bool hasDev)
    {
        _ams.LinkToRepository();
        _ams.Git.Changed.Add(".gitignore");
        var folder = await _ams.AddWorkflowFolderAsync();
        _ams.Flows.Cloud[Guid.Parse(McsRecords.WorkflowId)].ClientData = "{\"properties\":{\"definition\":{\"triggers\":{}}}}";
        var before = _ams.Flows.Calls.Count;
        var listed = McsRecords.WorkflowListItem(deployed ? McsRecords.WorkflowId : null) with { Branch = null };
        _ams.Service.Repository = McsRecords.Repository(hasDev ? listed : listed with { Deployments = [] });

        var result = await Init();

        Assert.Equal((McsRecords.WorkflowAgentId, AgentTypes.McsWorkflow, folder, TextGrid.Dash, false), (result.AgentId, result.AgentType, result.WorkflowFolder, result.WorkflowState, result.Connected));
        Assert.Equal(new AgentSourceControlMetadata(McsRecords.RepositoryId, "main", "Escalate"), _ams.Agent(McsRecords.WorkflowAgentId).SourceControlMetadata);
        Assert.Empty(_ams.Connect.MergeCommands);
        Assert.Equal(deployed ? 1 : 0, _ams.Flows.Calls.Count - before);
        Assert.Equal(deployed ? ["Copilot Studio's version of workflow 'Escalate' replaced the repository's in folder Escalate, uncommitted: `git status` lists it, and `git restore \"Escalate\"` undoes it."] : null, result.Ams!.Warnings);
        Assert.Equal(["status Escalate .gitignore", "commit msagent: add MCSWorkflow Escalate", "push origin main", .. deployed ? ["status Escalate", "find", "rev-parse HEAD", "git-dir"] : Array.Empty<string>()], _ams.Git.Calls.SkipWhile(call => !call.StartsWith("status", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ACodeAgentConfigHolds_IsNeitherRegisteredNorRecorded()
    {
        _ams.LinkToRepository();
        _ams.AddAgentWorkspace();
        _ams.Store.Add(_ams.Root, ManagedAgentFixtures.Agent("bbbbbbbb-2222-4222-8222-000000000051", "Helper") with { AgentType = AgentTypes.CodeAgent, AgentLocation = "Helper" });
        _ams.Service.Repository = McsRecords.Repository(McsRecords.ListItem("env-1", McsRecords.Deployment()));

        var result = await Init();

        Assert.Equal([McsRecords.AgentId], result.Ams!.Restored!.Select(agent => agent.AgentId));
        Assert.Empty(_ams.Service.Registrations);
        Assert.Null(_ams.Agent("bbbbbbbb-2222-4222-8222-000000000051").SourceControlMetadata);
    }

    [Fact]
    public async Task TwoFoldersItWouldRegisterAsNew_AreAmbiguous_BeforeAnythingChanges()
    {
        _ams.AddAgentWorkspace();
        _ams.AddAgentWorkspace(PayrollFolder, "cr1a2_payroll", "Payroll");
        var before = Contents(_ams.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => _ams.InitStep(_operations).HandleAsync(new ManagedAgentInitRequest { McsAgentSource = _ams.Root, Name = "Contoso HR" }, default));

        Assert.Equal(ErrorCodes.Workspace.WorkspaceAmbiguous, failure.ErrorCode);
        Assert.Empty(_ams.Prompt.Questions);
        Assert.Equal(before, Contents(_ams.Root));
    }

    [Fact]
    public async Task ACloneThatBringsAnUnreadableConfig_FailsReadingIt_AndIsTakenBack()
    {
        _ams.Git.RemoteHasMain = true;
        _ams.Git.RemoteFiles[$"{AmsHarness.AgentFolder}/settings.mcs.yml"] = $"displayName: Contoso HR\nschemaName: {AmsHarness.SchemaName}\n";
        _ams.Git.RemoteFiles[".config/agent.config.json"] = "{";
        _ams.Service.Repository = McsRecords.Repository(McsRecords.ListItem("env-1", McsRecords.Deployment()));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => _ams.InitStep(_operations).HandleAsync(CloneRequest(_ams.Root), default));

        Assert.Equal(ErrorCodes.Workspace.ConfigInvalid, failure.ErrorCode);
        Assert.Contains($"clone {McsRecords.GitUrl}", _ams.Git.Calls);
        Assert.Empty(Contents(_ams.Root));
    }

    [Fact]
    public void TheStepItsJoinAndTheConnector_NeedEachOfTheirParts()
    {
        var workflows = new WorkflowWorkspaceAdapter();
        var identities = new CopilotProjectIdentityReader(_ams.Locator, _ams.WorkspaceIdentities);
        var locator = CopilotTestFixtures.Locator(_ams.Workspaces, new SystemCopilotFileSystem());
        var pull = _ams.WorkflowPullOver(_ams.Git);
        Func<AmsRepositoryJoin>[] joins =
        [
            () => new(null!, _ams.Store, _ams.Store, workflows, _ams.Link, _ams.Git, _ams.Connector, pull, identities),
            () => new(_ams.Locator, null!, _ams.Store, workflows, _ams.Link, _ams.Git, _ams.Connector, pull, identities),
            () => new(_ams.Locator, _ams.Store, null!, workflows, _ams.Link, _ams.Git, _ams.Connector, pull, identities),
            () => new(_ams.Locator, _ams.Store, _ams.Store, null!, _ams.Link, _ams.Git, _ams.Connector, pull, identities),
            () => new(_ams.Locator, _ams.Store, _ams.Store, workflows, null!, _ams.Git, _ams.Connector, pull, identities),
            () => new(_ams.Locator, _ams.Store, _ams.Store, workflows, _ams.Link, null!, _ams.Connector, pull, identities),
            () => new(_ams.Locator, _ams.Store, _ams.Store, workflows, _ams.Link, _ams.Git, null!, pull, identities),
            () => new(_ams.Locator, _ams.Store, _ams.Store, workflows, _ams.Link, _ams.Git, _ams.Connector, null!, identities),
            () => new(_ams.Locator, _ams.Store, _ams.Store, workflows, _ams.Link, _ams.Git, _ams.Connector, pull, null!),
        ];

        Assert.Equal(["locator", "agents", "deployments", "workflows", "link", "git", "connector", "workflowPull", "identities"], joins.Select(join => Assert.Throws<ArgumentNullException>(join).ParamName));
        Assert.Equal("join", Assert.Throws<ArgumentNullException>(() => new AmsAgentInitStep(_operations, _ams.Git, _ams.Locator, _ams.Store, _ams.Store, _ams.Repositories, _ams.Link, null!, _ams.Registrar, _ams.Mirror, _ams.Committer, _ams.Targets, _ams.Service)).ParamName);
        Assert.Equal("git", Assert.Throws<ArgumentNullException>(() => new AmsWorkspaceConnector(locator, ManagedAgentFixtures.Resolver(), _ams.Collections, _ams.Connect, null!)).ParamName);
    }

    private static AmsMcsDeploymentRecord BaseDev() => McsRecords.Deployment() with { CloudBaseCommitSha = McsRecords.Commit };

    private static AmsMcsAgentListItem Payroll(params AmsMcsDeploymentRecord[] deployments) => McsRecords.ListItem("env-1", deployments) with { AgentId = PayrollId, DisplayName = "Payroll", SchemaName = "cr1a2_payroll", AgentFolder = PayrollFolder };

    private static FakeOperations MainInit(AmsHarness ams)
    {
        var init = new ManagedAgentInitService(ams.Store, ams.Locator, CopilotTestFixtures.Locator(new FakeCopilotWorkspacePort { WorkspaceExists = false }, new SystemCopilotFileSystem()), ManagedAgentFixtures.Resolver(), ManagedAgentFixtures.Identities());
        return new FakeOperations { OnManagedAgentInit = request => init.InitAsync(request, CancellationToken.None).GetAwaiter().GetResult() };
    }

    private static string[] Contents(string root) => [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Select(path => File.Exists(path) ? $"{Path.GetRelativePath(root, path)}={File.ReadAllText(path)}" : $"{Path.GetRelativePath(root, path)}/").Order(StringComparer.Ordinal)];

    private FakeOperations Failing(string step)
    {
        switch (step)
        {
            case "main's init":
                return new FakeOperations { OnManagedAgentInit = _ => throw new UsageException("Main's init refused.") };
            case "register":
                _ams.Service.OnRegister = _ => throw McsRecords.Rejected(ErrorCodes.AmsAgent.BotClaimed, "mcs-bot-claimed", ExitCodes.Usage);
                break;
            default:
                _ams.Git.Tracked.Add($"{PayrollFolder}/.mcs/botdefinition.json");
                break;
        }

        return MainInit(_ams);
    }

    private void BringPayrollWithTheAttach()
    {
        _ams.Git.RemoteHasMain = true;
        _ams.Git.RemoteFiles[$"{PayrollFolder}/settings.mcs.yml"] = "displayName: Payroll\nschemaName: cr1a2_payroll\n";
        _ams.Git.RemoteFiles[".gitignore"] = "obj/\n";
        _ams.Service.Repository = McsRecords.Repository(McsRecords.ListItem() with { AgentId = PayrollId, DisplayName = "Payroll", SchemaName = "cr1a2_payroll", AgentFolder = PayrollFolder });
    }

    private ManagedAgentInitRequest AttachRequest() => new() { McsAgentSource = _ams.Root, Name = "Contoso HR", RepositoryId = McsRecords.RepositoryId };

    private static ManagedAgentInitRequest CloneRequest(string project) => new() { McsAgentSource = project, RepositoryId = McsRecords.RepositoryId };

    private static AmsMcsAgentListItem InUppercase(AmsMcsAgentListItem agent) => agent with
    {
        AgentId = agent.AgentId.ToUpperInvariant(),
        Deployments = [.. agent.Deployments.Select(deployment => deployment with { DeploymentId = deployment.DeploymentId.ToUpperInvariant(), AgentId = deployment.AgentId.ToUpperInvariant(), BotId = deployment.BotId?.ToUpperInvariant() })],
    };

    private Task<ManagedAgentInitResult> Init() => _ams.InitStep(_operations).HandleAsync(new ManagedAgentInitRequest { McsAgentSource = _ams.Root }, default);
}
