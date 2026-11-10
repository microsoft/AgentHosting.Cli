using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class McsAmsServiceTests : IDisposable
{
    private readonly AmsHarness _ams = new();

    public void Dispose() => _ams.Dispose();

    [Fact]
    public async Task Capture_OfTheProjectsAgent_CommitsItsDevDeployment_PassingTheMessageAndResolveAsGiven()
    {
        _ams.AddAgent();
        _ams.AddDeployment();
        _ams.Service.Operations.Enqueue(McsRecords.Operation(AmsMcsOperationStatus.Succeeded, "Capture", "op-capture"));

        var result = await Capture().HandleAsync(new McsAmsAgentCaptureRequest { Project = _ams.Root, Message = "Fix greeting", Resolve = "cloud" }, default);

        var capture = Assert.Single(_ams.Service.Captures);
        Assert.Equal((McsRecords.AgentId, McsRecords.DevDeploymentId, "Fix greeting", "cloud", false), (capture.AgentId, capture.DeploymentId, capture.Message, capture.Resolve, capture.RespondAsync));
        Assert.Equal(("captured", McsRecords.Commit, McsRecords.NewCommit), (result.Status, result.BaseCommitSha, result.CommitSha));
    }

    [Fact]
    public async Task Capture_OutsideAProject_TakesTheHomeDeploymentAmsNames()
    {
        var result = await Capture().HandleAsync(new McsAmsAgentCaptureRequest { AgentId = McsRecords.AgentId, EnvironmentId = "env-1", Async = true }, default);

        Assert.Equal([$"get-agent {McsRecords.AgentId}", $"capture {McsRecords.DevDeploymentId}"], _ams.Service.Calls);
        Assert.Equal(("capturing", "op-capture"), (result.Status, result.OperationId));
    }

    [Fact]
    public async Task Capture_OfAnAgentWithoutADevDeployment_IsRefused()
    {
        _ams.Service.Agent = McsRecords.Agent() with { Mcs = new AmsMcsAgentProperties { SchemaName = AmsHarness.SchemaName } };

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => Capture().HandleAsync(new McsAmsAgentCaptureRequest { AgentId = McsRecords.AgentId, EnvironmentId = "env-1" }, default));

        Assert.Equal(ErrorCodes.AmsAgent.AgentNotEligible, failure.ErrorCode);
        Assert.Empty(_ams.Service.Captures);
    }

    [Fact]
    public async Task Capture_ThatAmsCannotBase_ShowsAmssError()
    {
        _ams.AddAgent();
        _ams.AddDeployment();
        var refusal = McsRecords.Rejected(ErrorCodes.AmsAgent.CaptureBaseUnknown, "mcs-capture-base-unknown", ExitCodes.Usage);
        _ams.Service.OnCapture = _ => throw refusal;

        Assert.Same(refusal, await Assert.ThrowsAsync<AmsAgentException>(() => Capture().HandleAsync(new McsAmsAgentCaptureRequest { Project = _ams.Root }, default)));
    }

    [Fact]
    public async Task Capture_OfAnUnregisteredAgent_IsRefusedBeforeAnyCall()
    {
        _ams.AddAgent(registered: false);
        _ams.AddDeployment();

        Assert.Equal(ErrorCodes.AmsAgent.AgentNotRegistered, (await Assert.ThrowsAsync<AmsAgentException>(() => Capture().HandleAsync(new McsAmsAgentCaptureRequest { Project = _ams.Root }, default))).ErrorCode);
        Assert.Empty(_ams.Service.Calls);
    }

    [Fact]
    public async Task OperationShow_ShowsTheOperationsCommits()
    {
        _ams.AddAgent();
        _ams.Service.Operations.Enqueue(McsRecords.Operation(AmsMcsOperationStatus.Succeeded, "Capture", "op-capture"));

        var result = await OperationShow().HandleAsync(new McsAmsOperationShowRequest { Project = _ams.Root, OperationId = "op-capture" }, default);

        Assert.Equal(("Capture", McsRecords.Commit, McsRecords.NewCommit, (string?)null), (result.Kind, result.SourceCommitSha, result.ResultCommitSha, result.Remediation));
    }

    [Fact]
    public async Task OperationShow_OfAFailedOperation_GivesItsErrorAndRemediation()
    {
        _ams.AddAgent();
        _ams.Service.Operations.Enqueue(McsRecords.Operation(AmsMcsOperationStatus.Failed));

        var result = await OperationShow().HandleAsync(new McsAmsOperationShowRequest { Project = _ams.Root, OperationId = "op-deploy" }, default);

        Assert.Equal("McsSourceValidationFailed", result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Remediation));
    }

    [Fact]
    public async Task OperationShow_NeedsAnOperationId()
    {
        await Assert.ThrowsAnyAsync<UsageException>(() => OperationShow().HandleAsync(new McsAmsOperationShowRequest { Project = _ams.Root }, default));
        Assert.Empty(_ams.Service.Calls);
    }

    [Fact]
    public async Task ListViewAms_ReadsTheRecordsOfOneRepository()
    {
        var service = new McsAmsAgentListService(ManagedAgentFixtures.ListService(McsRecords.Environments()), new AmsEndpointProfile(), _ams.Service);

        var result = await service.HandleAsync(new McsAmsAgentListRequest { Options = new ManagedAgentListRequest { RepositoryId = McsRecords.RepositoryId.ToUpperInvariant() } }, default);

        Assert.Equal([$"list-agents 21 {McsRecords.RepositoryId}"], _ams.Service.Calls);
        Assert.Equal((ManagedAgentListViews.Ams, McsRecords.AgentId), (result.View, Assert.Single(result.Agents).AgentId));
    }

    private McsAmsAgentCaptureService Capture() => new(new AmsAgentSelection(_ams.Resolver, _ams.Targets, _ams.Service), _ams.Targets, _ams.Service, new McsOperationWaiter(_ams.Service, _ams.Clock, (_, _) => Task.CompletedTask), (_, _) => Task.CompletedTask);

    private McsAmsOperationShowService OperationShow() => new(new AmsAgentSelection(_ams.Resolver, _ams.Targets, _ams.Service), _ams.Targets, _ams.Service);
}
