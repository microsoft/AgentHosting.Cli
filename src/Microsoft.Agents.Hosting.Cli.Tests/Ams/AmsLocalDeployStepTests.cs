using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsLocalDeployStepTests : IDisposable
{
    private static readonly DateTimeOffset DeployedOn = new(2026, 8, 2, 9, 30, 0, TimeSpan.Zero);

    private readonly AmsHarness _ams = new();

    public AmsLocalDeployStepTests() => _ams.LinkToRepository();

    public void Dispose() => _ams.Dispose();

    [Fact]
    public async Task ADeploy_IsRecordedInAms_WithTheCommitTheFolderHolds()
    {
        _ams.AddAgent();

        var result = await Deploy();

        var record = Assert.Single(_ams.Service.DeployRecords);
        Assert.Equal((McsRecords.AgentId, McsRecords.DevDeploymentId, McsRecords.BotId, (string?)null, McsRecords.Commit, true, true, DeployedOn), (record.AgentId, record.DeploymentId, record.BotId, record.WorkflowId, record.CommitSha, record.Exact, record.Published, record.DeployedTime));
        Assert.Equal(["first"], result.Warnings);
    }

    [Fact]
    public async Task ADeployWithLocalEdits_IsRecordedAsInexact()
    {
        _ams.AddAgent();
        _ams.Git.Changed.Add($"{AmsHarness.AgentFolder}/topics/Greeting.mcs.yml");

        await Deploy();

        Assert.Equal((McsRecords.Commit, false), (_ams.Service.DeployRecords[0].CommitSha, _ams.Service.DeployRecords[0].Exact));
    }

    [Fact]
    public async Task ADeployFromAFolderWithoutACommit_IsRecordedWithoutOne_AndSaysWhy()
    {
        _ams.AddAgent();
        _ams.Git.Commits.Clear();

        var result = await Deploy();

        Assert.Null(Assert.Single(_ams.Service.DeployRecords).CommitSha);
        Assert.Equal(["first", $"{_ams.Root} holds no git commit, so AMS doesn't know which commit was deployed."], result.Warnings);
    }

    [Fact]
    public async Task AWorkflowDeploy_IsRecordedWithItsWorkflow()
    {
        _ams.AddAgentWorkspace();
        _ams.Store.Add(_ams.Root, ManagedAgentFixtures.Agent(McsRecords.AgentId, "Escalate") with { AgentType = AgentTypes.McsWorkflow, AgentLocation = AmsHarness.AgentFolder, SourceControlMetadata = new AgentSourceControlMetadata(McsRecords.RepositoryId, "main", AmsHarness.AgentFolder) });

        await Deploy(result => result with { McsWorkflowId = McsRecords.WorkflowId });

        Assert.Equal(((string?)null, McsRecords.WorkflowId), (_ams.Service.DeployRecords[0].BotId, _ams.Service.DeployRecords[0].WorkflowId));
    }

    [Fact]
    public async Task ARecordAmsRefuses_IsAWarning_AndTheDeployStands()
    {
        _ams.AddAgent();
        _ams.Service.DeployRecordFailure = McsRecords.Status(500);

        var result = await Deploy();

        Assert.Equal((McsRecords.AgentId, true), (result.AgentId, result.Published));
        Assert.Equal(["first", "AMS doesn't know about this deploy: The Agent Management Service answered 500."], result.Warnings);
    }

    [Fact]
    public async Task AnUnregisteredAgent_IsAWarning_AndAmsIsNotCalled()
    {
        _ams.AddAgent(registered: false);

        var result = await Deploy();

        Assert.Equal(["first", "AMS doesn't know agent Contoso HR, so it wasn't told about this deploy. Run `msagent agent init` to register it."], result.Warnings);
        Assert.Empty(_ams.Service.Calls);
    }

    private Task<ManagedAgentDeployResult> Deploy(Func<ManagedAgentDeployResult, ManagedAgentDeployResult>? change = null)
    {
        var operations = new FakeOperations { OnManagedAgentDeploy = request => (change ?? (result => result))(Local(request)) };
        return _ams.LocalDeployStep(operations).HandleAsync(new ManagedAgentDeployRequest { Project = _ams.Root }, default);
    }

    private static ManagedAgentDeployResult Local(ManagedAgentDeployRequest request) => new()
    {
        AgentId = McsRecords.AgentId,
        DisplayName = "Contoso HR",
        DeploymentId = McsRecords.DevDeploymentId,
        DeploymentName = "contoso-dev",
        DeploymentType = DeploymentStages.Dev,
        Action = "update",
        TargetEnvironmentId = "env-1",
        TargetMcsAgentId = McsRecords.BotId,
        Published = true,
        DeployedOn = DeployedOn,
        Warnings = ["first"],
    };
}
