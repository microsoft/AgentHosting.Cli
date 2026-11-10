using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsRecordMirrorTests : IDisposable
{
    private readonly AmsHarness _ams = new();

    public void Dispose() => _ams.Dispose();

    [Fact]
    public async Task ACreatedDeployment_GoesToAmsFirst_ThenToConfig()
    {
        var agent = _ams.AddAgent();
        var deployment = Test();
        var order = new List<string>();

        var mirrored = await _ams.Mirror.ApplyAsync(_ams.Root, new DeploymentCreated(agent, deployment), () =>
        {
            order.Add($"config after {_ams.Service.Calls.Count} AMS calls");
            return deployment;
        }, default);

        Assert.Equal(["config after 1 AMS calls"], order);
        var created = Assert.Single(_ams.Service.DeploymentCreates);
        Assert.Equal((McsRecords.AgentId, McsRecords.TestDeploymentId, "Test", "contoso-test", "env-2"), (created.AgentId, created.DeploymentId, created.DeploymentType, created.DeploymentName, created.TargetEnvironmentId));
        Assert.Equal((true, (string?)null), (mirrored.Mirrored, mirrored.Warning));
    }

    [Fact]
    public async Task AConfigThatCannotBeWritten_UndoesTheCreate()
    {
        var agent = _ams.AddAgent();
        var failure = new AgentHostingStateException("The configuration could not be written.");

        Assert.Same(failure, await Assert.ThrowsAsync<AgentHostingStateException>(() => _ams.Mirror.ApplyAsync<AgentDeploymentRecord>(_ams.Root, new DeploymentCreated(agent, Test()), () => throw failure, default)));
        Assert.Equal([(McsRecords.AgentId, McsRecords.TestDeploymentId, AgentTypes.McsAgent)], _ams.Service.DeploymentDeletes);
    }

    [Fact]
    public async Task AConfigThatCannotBeWritten_UndoesTheRename()
    {
        var agent = _ams.AddAgent();
        var deployment = _ams.AddDeployment();

        await Assert.ThrowsAsync<AgentHostingStateException>(() => _ams.Mirror.ApplyAsync<AgentDeploymentRecord>(_ams.Root, new DeploymentRenamed(agent, deployment, "contoso-dev-2"), () => throw new AgentHostingStateException("locked"), default));

        Assert.Equal([$"rename-deployment {McsRecords.DevDeploymentId} contoso-dev-2", $"rename-deployment {McsRecords.DevDeploymentId} contoso-dev"], _ams.Service.Calls);
    }

    [Fact]
    public async Task AStageAmsRefuses_IsShown_AndConfigIsNotWritten()
    {
        var refusal = McsRecords.Rejected(ErrorCodes.AmsAgent.DeploymentStageEnvironmentInvalid, "mcs-deployment-stage-environment-invalid", ExitCodes.Usage);
        _ams.Service.DeploymentCreateFailure = refusal;
        var written = false;

        Assert.Same(refusal, await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Mirror.ApplyAsync(_ams.Root, new DeploymentCreated(_ams.AddAgent(), Test()), () => written = true, default)));
        Assert.False(written);
    }

    [Theory]
    [InlineData(204)]
    [InlineData(404)]
    public async Task ADeletedDeployment_IsDeletedFromAms_WhereAMissingOneCountsAsDeleted(int status)
    {
        _ams.Service.DeploymentDeleteFailure = status == 404 ? McsRecords.Status(404) : null;

        var mirrored = await _ams.Mirror.ApplyAsync(_ams.Root, new DeploymentDeleted(_ams.AddAgent(), _ams.AddDeployment()), () => "removed", default);

        Assert.Equal(("removed", true), (mirrored.Value, mirrored.Mirrored));
        Assert.Equal([(McsRecords.AgentId, McsRecords.DevDeploymentId, AgentTypes.McsAgent)], _ams.Service.DeploymentDeletes);
    }

    [Fact]
    public async Task ADeletedAgent_IsDeletedWithTheETagAmsHolds()
    {
        var mirrored = await _ams.Mirror.ApplyAsync(_ams.Root, new AgentDeleted(_ams.AddAgent()), () => 1, default);

        Assert.True(mirrored.Mirrored);
        Assert.Equal([$"get-agent {McsRecords.AgentId}", $"delete-agent {McsRecords.AgentId} {McsRecords.ETag}"], _ams.Service.Calls);
    }

    [Theory]
    [InlineData(AgentTypes.McsAgent, new[] { "Other" })]
    [InlineData(AgentTypes.McsWorkflow, new[] { "Other", "Shared Tools" })]
    public async Task ADeletedAgent_ForgetsTheCloudCacheCopiesOfItsFolders(string agentType, string[] kept)
    {
        _ams.LinkToRepository();
        var agent = _ams.AddAgent() with { AgentType = agentType };
        foreach (var folder in new[] { Path.Combine(_ams.Root, AmsHarness.AgentFolder), _ams.DeclareCollection(), _ams.AddAgentWorkspace("Other", "cr1a2_other") })
        {
            Directory.CreateDirectory(Path.Combine(folder, ".mcs"));
            Assert.Null(await _ams.Shelf.SaveAsync(folder, null, default));
        }

        await _ams.Mirror.ApplyAsync(_ams.Root, new AgentDeleted(agent), () => 1, default);

        Assert.Equal(kept, Directory.GetDirectories(Path.Combine(_ams.Root, ".git", "msagent", "mcs")).Select(key => JsonNode.Parse(File.ReadAllText(Path.Combine(key, "index.json")))!["folder"]!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ADeletedAgent_WhoseCopiesCannotBeForgotten_IsStillDeleted()
    {
        _ams.LinkToRepository();

        var mirrored = await _ams.Mirror.ApplyAsync(_ams.Root, new AgentDeleted(_ams.AddAgent() with { AgentLocation = "Contoso\0HR" }), () => 1, default);

        Assert.True(mirrored.Mirrored);
        Assert.Equal([$"get-agent {McsRecords.AgentId}", $"delete-agent {McsRecords.AgentId} {McsRecords.ETag}"], _ams.Service.Calls);
    }

    [Fact]
    public async Task AnAgentAmsChangedMeanwhile_IsNotDeleted_AndConfigKeepsIt()
    {
        _ams.Service.AgentDeleteFailure = McsRecords.Status(412);
        var written = false;

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => _ams.Mirror.ApplyAsync(_ams.Root, new AgentDeleted(_ams.AddAgent()), () => written = true, default));

        Assert.Equal((ErrorCodes.AmsAgent.AgentRecordChanged, "Run the same command again."), (failure.ErrorCode, failure.Remediation));
        Assert.False(written);
    }

    [Fact]
    public async Task AnUnregisteredAgent_ChangesOnlyConfig_WarningOnlyForCreatesAndRenames()
    {
        var agent = _ams.AddAgent(registered: false);

        var created = await _ams.Mirror.ApplyAsync(_ams.Root, new DeploymentCreated(agent, Test()), () => 1, default);
        var deleted = await _ams.Mirror.ApplyAsync(_ams.Root, new DeploymentDeleted(agent, Test()), () => 2, default);

        Assert.Equal("AMS wasn't updated: agent Contoso HR isn't registered. Run `msagent agent init` to register it.", created.Warning);
        Assert.Null(deleted.Warning);
        Assert.Empty(_ams.Service.Calls);
    }

    private static AgentDeploymentRecord Test() => ManagedAgentFixtures.Deployment(McsRecords.TestDeploymentId, McsRecords.AgentId, "contoso-test", DeploymentStages.Test, "env-2", null);
}
