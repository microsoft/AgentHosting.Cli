using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsRemoteDeployServiceTests : IDisposable
{
    private const string SavedCommit = "1f0e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c";

    private readonly AmsHarness _ams = new();

    public AmsRemoteDeployServiceTests()
    {
        _ams.LinkToRepository();
        _ams.AddAgent();
        _ams.AddDeployment();
    }

    public void Dispose() => _ams.Dispose();

    [Fact]
    public async Task ACommitGitResolves_IsDeployedInFull_AndConfigRecordsTheDeploy()
    {
        _ams.Git.Commits["9fceb02"] = McsRecords.Commit;

        var result = await Deploy(new McsAmsAgentDeployRequest { CommitId = " 9fceb02 " });

        var deploy = Assert.Single(_ams.Service.Deploys);
        Assert.Equal((McsRecords.AgentId, McsRecords.DevDeploymentId, McsRecords.Commit, false, false, false), (deploy.AgentId, deploy.DeploymentId, deploy.CommitSha, deploy.Publish, deploy.Overwrite, deploy.RespondAsync));
        Assert.Equal(("deployed", McsRecords.Commit, "op-deploy"), (result.Status, result.CommitSha, result.OperationId));
        Assert.Equal(new DateTimeOffset(2026, 8, 2, 10, 15, 0, TimeSpan.Zero), _ams.Deployments().Single().LastDeployedTime);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData(false, "env-1")]
    [InlineData(true, "env-1")]
    [InlineData(true, "ENV-1")]
    public async Task ADevDeployOfTheCommitTheFolderHolds_ConnectsTheAgentsFolderAndItsCollectionFolders_OrRefreshesThem_AndConfigSavesTheDevAgent(bool connected, string connectedEnvironmentId)
    {
        _ams.Workspaces.WorkspaceExists = connected;
        _ams.Workspaces.EnvironmentIdByDirectory[AmsHarness.AgentFolder] = connectedEnvironmentId;
        var collection = _ams.DeclareCollection();

        var result = await Deploy(new McsAmsAgentDeployRequest());

        var connect = Assert.Single(_ams.Connect.Commands);
        Assert.Equal(("env-1", Path.Combine(_ams.Root, AmsHarness.AgentFolder), AmsHarness.SchemaName), (connect.Environment.EnvironmentId, connect.WorkspaceDirectory, connect.SchemaName));
        Assert.Equal([collection], connect.CollectionDirectories);
        Assert.Equal(("deployed", true, (string?)null), (result.Status, result.WorkspaceConnected, result.WorkspaceNotice));
        Assert.Equal((RecordingConnectPort.DevBotId, AmsHarness.SchemaName), (_ams.Agent().McsAgentId, _ams.Agent().McsSchemaName));
        Assert.Equal(new DateTimeOffset(2026, 8, 2, 10, 15, 0, TimeSpan.Zero), _ams.Deployments().Single().LastDeployedTime);
        Assert.Equal(["Deployed Contoso HR to contoso-dev (dev) from commit 9fceb02.", $"  commit       {McsRecords.Commit}", "  operation    op-deploy", "  environment  env-1", "  published    yes \u2014 the content is live"], result.ToTextLines());
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ADevDeployOfTheCommitTheFolderHolds_ComparesTheCommitsIgnoringCase()
    {
        _ams.Service.Operations.Enqueue(McsRecords.Operation(AmsMcsOperationStatus.Succeeded) with { SourceCommitSha = McsRecords.Commit.ToUpperInvariant() });

        var result = await Deploy(new McsAmsAgentDeployRequest());

        Assert.Single(_ams.Connect.Commands);
        Assert.Equal((true, (string?)null), (result.WorkspaceConnected, result.WorkspaceNotice));
    }

    [Theory]
    [InlineData(false, "The agent's folder holds 9fceb02, not the deployed 1f0e2d3, so msagent didn't connect the folder to Dev; `msagent agent pull` works there after a Dev deploy of the commit the folder holds.")]
    [InlineData(true, "The agent's folder holds 9fceb02, not the deployed 1f0e2d3, so msagent left the folder's connection to Dev as it was; `msagent agent pull` brings the deployed changes into it.")]
    public async Task ADevDeployOfACommitTheFolderDoesntHold_LeavesTheFolderAsItWas_AndANoticeSaysWhy(bool connected, string notice)
    {
        _ams.Workspaces.WorkspaceExists = connected;
        _ams.Workspaces.EnvironmentIdByDirectory[AmsHarness.AgentFolder] = "env-1";
        _ams.Git.Commits["1f0e2d3"] = McsRecords.OlderCommit;
        _ams.Service.Operations.Enqueue(McsRecords.Operation(AmsMcsOperationStatus.Succeeded) with { SourceCommitSha = McsRecords.OlderCommit });

        var result = await Deploy(new McsAmsAgentDeployRequest { CommitId = "1f0e2d3" });

        Assert.Empty(_ams.Connect.Commands);
        Assert.Equal(("deployed", false, notice), (result.Status, result.WorkspaceConnected, result.WorkspaceNotice));
        Assert.Empty(result.Warnings);
        Assert.Null(_ams.Agent().McsAgentId);
        Assert.Equal(new DateTimeOffset(2026, 8, 2, 10, 15, 0, TimeSpan.Zero), _ams.Deployments().Single().LastDeployedTime);
    }

    [Theory]
    [InlineData($"{AmsHarness.AgentFolder}/topics/Greeting.mcs.yml")]
    [InlineData("Shared Tools/topics/Lookup.mcs.yml")]
    public async Task UncommittedChangesInTheAgentsFolderOrACollectionFolder_LeaveTheFolderUnconnected_AndANoticeSaysWhy(string changed)
    {
        _ams.DeclareCollection();
        _ams.Git.Changed.Add(changed);

        var result = await Deploy(new McsAmsAgentDeployRequest());

        Assert.Empty(_ams.Connect.Commands);
        Assert.Equal((false, "The agent's folder, or a collection folder it declares, has uncommitted changes, so msagent didn't connect the folder to Dev; `msagent agent pull` works there after a Dev deploy of the commit the folder holds."), (result.WorkspaceConnected, result.WorkspaceNotice));
        Assert.Equal(["--remote deploys 9fceb02 from GRS; your local changes aren't in it."], result.Warnings);
    }

    [Fact]
    public async Task AHeadThatIsntPushed_LeavesTheFolderUnconnected_AndANoticeSaysWhy()
    {
        _ams.Git.Commits["HEAD"] = McsRecords.NewCommit;
        _ams.Git.HeadPushed = false;

        var result = await Deploy(new McsAmsAgentDeployRequest());

        Assert.Empty(_ams.Connect.Commands);
        Assert.Equal((false, "The agent's folder holds commits that aren't pushed to GRS, so msagent didn't connect the folder to Dev; `msagent agent pull` works there after a Dev deploy of the commit the folder holds."), (result.WorkspaceConnected, result.WorkspaceNotice));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public async Task AFolderGitNamesNoCommitFor_IsLeftUnconnected_WithOneNoticeWhateverGitsReason(bool gitMissing, bool hasCommit, bool headPushed)
    {
        _ams.Git.Missing = gitMissing;
        _ams.Git.HeadPushed = headPushed;
        _ams.Git.MergeBase = null;
        if (!hasCommit)
        {
            _ams.Git.Commits.Clear();
        }

        var result = await Deploy(new McsAmsAgentDeployRequest());

        Assert.Empty(_ams.Connect.Commands);
        Assert.Equal((false, "Git can't tell which commit the agent's folder holds, so msagent didn't connect the folder to Dev; `msagent agent pull` works there after a Dev deploy of the commit the folder holds."), (result.WorkspaceConnected, result.WorkspaceNotice));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ADeployWhoseCommitAmsDoesntName_LeavesTheFolderUnconnected_AndANoticeSaysWhy()
    {
        _ams.Service.OnDeploy = _ => new AmsMcsOperationAccepted { OperationId = "op-deploy", Status = AmsMcsOperationStatus.Accepted };
        _ams.Service.Operations.Enqueue(McsRecords.Operation(AmsMcsOperationStatus.Succeeded) with { SourceCommitSha = null });

        var result = await Deploy(new McsAmsAgentDeployRequest());

        Assert.Empty(_ams.Connect.Commands);
        Assert.Equal(((string?)null, false, "AMS didn't name the commit it deployed, so msagent didn't connect the folder to Dev; `msagent agent pull` works there after a Dev deploy of the commit the folder holds."), (result.CommitSha, result.WorkspaceConnected, result.WorkspaceNotice));
    }

    [Theory]
    [InlineData(McsRecords.Commit)]
    [InlineData(McsRecords.NewCommit)]
    public async Task ADevDeploy_LeavesAFolderConnectedToAnotherEnvironmentAlone_WithoutAMessage(string head)
    {
        _ams.Workspaces.WorkspaceExists = true;
        _ams.Workspaces.EnvironmentIdByDirectory[AmsHarness.AgentFolder] = "env-2";
        _ams.Git.Commits["HEAD"] = head;

        var result = await Deploy(new McsAmsAgentDeployRequest());

        Assert.Empty(_ams.Connect.Commands);
        Assert.Equal(("deployed", false, (string?)null), (result.Status, result.WorkspaceConnected, result.WorkspaceNotice));
        Assert.Null(_ams.Agent().McsAgentId);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task AConnectThatFails_IsAWarning_WhileTheDeployCountsAndConfigRecordsIt()
    {
        var failure = CopilotStudioException.AgentNotFound(AmsHarness.SchemaName, "env-1");
        _ams.Connect.Failure = failure;

        var result = await Deploy(new McsAmsAgentDeployRequest());

        Assert.Equal(("deployed", false), (result.Status, result.WorkspaceConnected));
        Assert.Equal([$"The deploy finished, but the agent's folder wasn't connected to Dev: {failure.Message} To bring Copilot Studio changes into the repository, run `msagent agent capture`, then `git pull`."], result.Warnings);
        Assert.Equal(new DateTimeOffset(2026, 8, 2, 10, 15, 0, TimeSpan.Zero), _ams.Deployments().Single().LastDeployedTime);
        Assert.Null(_ams.Agent().McsAgentId);
    }

    [Theory]
    [InlineData(McsRecords.DevDeploymentId, "The deploy finished, but the agent's folder wasn't connected to Dev: The settings file is locked. To bring Copilot Studio changes into the repository, run `msagent agent capture`, then `git pull`.")]
    [InlineData(McsRecords.TestDeploymentId, "The deploy finished, but the agent's folder couldn't be read: The settings file is locked.")]
    public async Task AFolderThatCantBeReadOnceTheDeployFinished_IsAWarning_WhileTheDeployCountsAndConfigRecordsIt(string deploymentId, string warning)
    {
        _ams.AddDeployment(deploymentId: McsRecords.TestDeploymentId, name: "contoso-test", stage: DeploymentStages.Test, environmentId: "env-2");
        _ams.WorkspaceIdentities = new FailingWorkspaceIdentityPort(new IOException("The settings file is locked."));

        var result = await _ams.RemoteDeploy().HandleAsync(Request(new McsAmsAgentDeployRequest()) with { DeploymentId = deploymentId }, default);

        Assert.Equal(("deployed", false), (result.Status, result.WorkspaceConnected));
        Assert.Equal([warning], result.Warnings);
        Assert.Empty(_ams.Connect.Commands);
        Assert.NotNull(_ams.Deployments().Single(deployment => deployment.DeploymentId == deploymentId).LastDeployedTime);
        Assert.Equal(AmsHarness.SchemaName, _ams.Agent().McsSchemaName);
    }

    [Theory]
    [InlineData(1, "Dev doesn't hold the collection in Shared Tools yet, so that folder isn't connected; a Dev deploy that includes the collection connects it.")]
    [InlineData(2, "Dev doesn't hold the collections in Shared Tools, Other Tools yet, so those folders aren't connected; a Dev deploy that includes them connects them.")]
    public async Task CollectionsDevDoesntHoldYet_AreNamedInOneWarning_WhileTheAgentsFolderIsConnected(int skipped, string warning)
    {
        _ams.Connect.SkippedCollectionDirectories.AddRange(new[] { Path.Combine(_ams.Root, "Shared Tools"), Path.Combine(_ams.Root, "Other Tools") }.Take(skipped));

        var result = await Deploy(new McsAmsAgentDeployRequest());

        Assert.Equal(("deployed", true), (result.Status, result.WorkspaceConnected));
        Assert.Equal([warning], result.Warnings);
        Assert.Equal(RecordingConnectPort.DevBotId, _ams.Agent().McsAgentId);
    }

    [Fact]
    public async Task AnUnexpectedFailureRecordingTheDeploy_IsAWarning_WhileTheDeployCounts()
    {
        await _ams.Profiles.UpsertAsync(new AuthProfile { AccountKey = " ", Username = "op@contoso.com" }, default);

        var result = await Deploy(new McsAmsAgentDeployRequest());

        Assert.Equal(("deployed", true), (result.Status, result.WorkspaceConnected));
        Assert.StartsWith("The deploy finished, but .config wasn't updated: ", Assert.Single(result.Warnings), StringComparison.Ordinal);
        Assert.Null(_ams.Deployments().Single().LastDeployedTime);
    }

    [Fact]
    public async Task AFolderThatNamesNoSchemaName_IsNotConnected_AndTheWarningSaysWhy()
    {
        _ams.Store.Update(_ams.Root, _ams.Agent() with { McsSchemaName = null });
        File.WriteAllText(Path.Combine(_ams.Root, AmsHarness.AgentFolder, "settings.mcs.yml"), "displayName: Contoso HR\n");

        var result = await Deploy(new McsAmsAgentDeployRequest());

        Assert.Empty(_ams.Connect.Commands);
        Assert.Equal("deployed", result.Status);
        Assert.StartsWith($"The deploy finished, but the agent's folder wasn't connected to Dev: Agent '{McsRecords.AgentId}' has no readable schema name:", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeploymentStages.Test, "env-2")]
    [InlineData(DeploymentStages.Prod, "env-3")]
    public async Task APromotion_ConnectsNothing_SaysNothingAboutTheFolder_AndConfigRecordsTheDeploy(string stage, string environmentId)
    {
        _ams.AddDeployment(deploymentId: McsRecords.TestDeploymentId, name: "contoso-later", stage: stage, environmentId: environmentId);
        _ams.Git.Commits["HEAD"] = McsRecords.NewCommit;

        var result = await _ams.RemoteDeploy().HandleAsync(Request(new McsAmsAgentDeployRequest()) with { DeploymentId = McsRecords.TestDeploymentId }, default);

        Assert.Equal(("deployed", false, (string?)null), (result.Status, result.WorkspaceConnected, result.WorkspaceNotice));
        Assert.Empty(_ams.Connect.Commands);
        Assert.NotNull(_ams.Deployments().Single(deployment => deployment.DeploymentId == McsRecords.TestDeploymentId).LastDeployedTime);
    }

    [Fact]
    public async Task AStandaloneWorkflowsDevDeploy_ConnectsNothing_AndSaysNothingAboutTheFolder()
    {
        using var ams = new AmsHarness();
        ams.AddAgentWorkspace();
        ams.Store.Add(ams.Root, ManagedAgentFixtures.Agent(McsRecords.WorkflowAgentId, "Escalate") with { AgentType = AgentTypes.McsWorkflow, AgentLocation = AmsHarness.AgentFolder, SourceControlMetadata = new AgentSourceControlMetadata(McsRecords.RepositoryId, "main", AmsHarness.AgentFolder) });
        ams.Store.Add(ams.Root, ManagedAgentFixtures.Deployment(McsRecords.DevDeploymentId, McsRecords.WorkflowAgentId, "escalate-dev") with { AgentType = AgentTypes.McsWorkflow });

        var result = await ams.RemoteDeploy().HandleAsync(new McsAmsAgentDeployRequest { Project = ams.Root, AgentId = McsRecords.WorkflowAgentId, DeploymentId = McsRecords.DevDeploymentId }, default);

        Assert.Equal(("deployed", false, (string?)null), (result.Status, result.WorkspaceConnected, result.WorkspaceNotice));
        Assert.Empty(ams.Connect.Commands);
    }

    [Fact]
    public async Task ADevDeployOfAnAgentWhoseFolderIsMissing_ConnectsNothing_AndConfigRecordsTheDeploy()
    {
        Directory.Delete(Path.Combine(_ams.Root, AmsHarness.AgentFolder), recursive: true);

        var result = await Deploy(new McsAmsAgentDeployRequest());

        Assert.Equal(("deployed", false), (result.Status, result.WorkspaceConnected));
        Assert.Empty(_ams.Connect.Commands);
        Assert.Equal(new DateTimeOffset(2026, 8, 2, 10, 15, 0, TimeSpan.Zero), _ams.Deployments().Single().LastDeployedTime);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("release-1")]
    [InlineData("0123abc")]
    public async Task AValueGitCannotResolve_IsSentAsGiven(string commitId)
    {
        await Deploy(new McsAmsAgentDeployRequest { CommitId = commitId });

        Assert.Equal(commitId, Assert.Single(_ams.Service.Deploys).CommitSha);
    }

    [Fact]
    public async Task WithoutACommit_AmsChoosesIt_AndTheResultNamesIt()
    {
        var result = await Deploy(new McsAmsAgentDeployRequest());

        Assert.Null(Assert.Single(_ams.Service.Deploys).CommitSha);
        Assert.Equal(McsRecords.Commit, result.CommitSha);
    }

    [Fact]
    public async Task ADevDeployThatSavedDevsEditsFirst_ReturnsTheCommitItDeployedAndTheSavedCommit_AndPrintsOnlyTheDeployRows()
    {
        _ams.Service.Answered = AmsMcsWire.Parse(WireFixture.Read("mcs-operation.deploy-captured.json"), AmsMcsWire.ReadOperation);

        var result = await Deploy(new McsAmsAgentDeployRequest());

        Assert.Equal((SavedCommit, SavedCommit), (result.CommitSha, result.CapturedCommitSha));
        Assert.NotNull(result.WorkspaceNotice);
        Assert.Equal(["Deployed Contoso HR to contoso-dev (dev) from commit 1f0e2d3.", $"  commit       {SavedCommit}", "  operation    12345678-7777-4777-8777-000000000007", "  environment  env-1", "  published    no \u2014 this deploy's content is a draft; the version published 2026-07-25 19:02:38Z stays live. Re-run with --publish to make it live."], result.ToTextLines());
    }

    [Fact]
    public async Task LocalChanges_AreAWarning_FromTheSameGitCheckALocalDeployRecords()
    {
        _ams.Git.Changed.Add($"{AmsHarness.AgentFolder}/topics/Greeting.mcs.yml");

        var result = await Deploy(new McsAmsAgentDeployRequest());

        Assert.Equal(["--remote deploys 9fceb02 from GRS; your local changes aren't in it."], result.Warnings);
    }

    [Fact]
    public async Task Async_ReturnsOnceAmsAcceptsTheDeploy_AndLeavesConfigAlone()
    {
        var before = File.ReadAllText(Path.Combine(_ams.Root, ".config", "agent.config.json"));

        var result = await Deploy(new McsAmsAgentDeployRequest { Async = true });

        Assert.Equal(("deploying", true), (result.Status, Assert.Single(_ams.Service.Deploys).RespondAsync));
        Assert.DoesNotContain(_ams.Service.Calls, call => call.StartsWith("get-operation", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllText(Path.Combine(_ams.Root, ".config", "agent.config.json")));
        Assert.Equal((false, (string?)null), (result.WorkspaceConnected, result.WorkspaceNotice));
        Assert.Empty(_ams.Connect.Commands);
    }

    [Fact]
    public async Task AFailedOperation_FailsWithAmssError()
    {
        _ams.Service.Operations.Enqueue(McsRecords.Operation(AmsMcsOperationStatus.Failed));

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => Deploy(new McsAmsAgentDeployRequest()));

        Assert.Equal(ErrorCodes.AmsAgent.SourceValidationFailed, failure.ErrorCode);
    }

    [Fact]
    public async Task ADeployAmsRefuses_IsShownAsAmsWordsIt()
    {
        var refusal = McsRecords.Rejected(ErrorCodes.AmsAgent.CommitNotAccepted, "mcs-commit-not-accepted", ExitCodes.Usage);
        _ams.Service.OnDeploy = _ => throw refusal;

        Assert.Same(refusal, await Assert.ThrowsAsync<AmsAgentException>(() => Deploy(new McsAmsAgentDeployRequest { CommitId = McsRecords.Commit })));
    }

    [Fact]
    public async Task ABusyService_IsAskedAgain_AfterTheWaitItNames()
    {
        var delays = new List<TimeSpan>();
        var attempts = 0;
        _ams.Service.OnDeploy = _ => ++attempts == 1 ? throw AmsAgentException.Rejected(ErrorCodes.AmsAgent.OperationCapacityExceeded, "busy", ExitCodes.Failure, "mcs-operation-capacity-exceeded", "Try again.", retryAfter: TimeSpan.FromSeconds(5)) : new AmsMcsOperationAccepted { OperationId = "op-deploy", Status = AmsMcsOperationStatus.Accepted, SourceCommitSha = McsRecords.Commit };

        await _ams.RemoteDeploy(delays).HandleAsync(Request(new McsAmsAgentDeployRequest()), default);

        Assert.Equal([TimeSpan.FromSeconds(5)], delays);
        Assert.Equal(2, _ams.Service.Deploys.Count);
    }

    [Fact]
    public async Task AnUnregisteredAgent_IsRefusedBeforeAnyCall()
    {
        using var ams = new AmsHarness();
        ams.AddAgent(registered: false);
        ams.AddDeployment();

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => ams.RemoteDeploy().HandleAsync(new McsAmsAgentDeployRequest { Project = ams.Root, AgentId = McsRecords.AgentId, DeploymentId = McsRecords.DevDeploymentId }, default));

        Assert.Equal((ErrorCodes.AmsAgent.AgentNotRegistered, ExitCodes.Usage), (failure.ErrorCode, failure.ExitCode));
        Assert.Empty(ams.Service.Calls);
        Assert.Empty(ams.Git.Calls);
    }

    private Task<McsAmsAgentDeployResult> Deploy(McsAmsAgentDeployRequest request) => _ams.RemoteDeploy().HandleAsync(Request(request), default);

    private McsAmsAgentDeployRequest Request(McsAmsAgentDeployRequest request) => request with { Project = _ams.Root, AgentId = McsRecords.AgentId, DeploymentId = McsRecords.DevDeploymentId };
}
