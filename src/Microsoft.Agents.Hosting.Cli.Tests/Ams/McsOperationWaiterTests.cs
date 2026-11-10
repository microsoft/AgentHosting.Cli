using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.Evaluation;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class McsOperationWaiterTests
{
    private static readonly AmsServiceTarget Target = new() { Endpoint = new Uri("https://env-1.api.powerplatform.com/"), Audience = EnvironmentFixtures.Audience, RouteForm = AmsRouteForm.EnvironmentHost, TenantId = EnvironmentFixtures.ContosoTenant, EnvironmentId = "env-1" };

    private readonly AdvancingTestClock _clock = new();
    private readonly List<TimeSpan> _delays = [];
    private readonly FakeMcsService _service = new();
    private readonly RecordingProgressStatus _progress = new(shown: true);

    [Fact]
    public async Task Wait_PollsEveryTwoSeconds_UntilTheOperationFinishes()
    {
        for (var poll = 0; poll < 5; poll++)
        {
            _service.Operations.Enqueue(Running());
        }

        _service.Operations.Enqueue(McsRecords.Operation(AmsMcsOperationStatus.Succeeded));

        Assert.Equal(AmsMcsOperationStatus.Succeeded, (await Waiter().WaitAsync(Target, McsRecords.AgentId, "op-deploy", default)).Status);
        Assert.Equal([2, 2, 2, 2, 2, 2], _delays.Select(delay => delay.TotalSeconds));
        Assert.Equal(5, _progress.Reports.Count);
        Assert.Equal("Deploy running", _progress.Reports[0]);
    }

    [Fact]
    public async Task Wait_ReturnsAFailedOperationForTheCallerToReport()
    {
        _service.Operations.Enqueue(McsRecords.Operation(AmsMcsOperationStatus.Failed));

        Assert.Equal("McsSourceValidationFailed", (await Waiter().WaitAsync(Target, McsRecords.AgentId, "op-deploy", default)).Error?.Code);
    }

    [Fact]
    public async Task Wait_StopsWhenARunningOperationStopsReportingHeartbeats()
    {
        _service.Operations.Enqueue(Running() with { LastHeartbeatTime = _clock.UtcNow - TimeSpan.FromMinutes(11) });

        var exception = await Assert.ThrowsAsync<AmsAgentException>(() => Waiter().WaitAsync(Target, McsRecords.AgentId, "op-deploy", default));

        Assert.Equal(ErrorCodes.AmsAgent.OperationWaitTimedOut, exception.ErrorCode);
        Assert.Contains("--operation-id op-deploy", exception.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wait_StopsAfterTheMaximumWait()
    {
        var waiter = new McsOperationWaiter(_service, _clock, (delay, _) =>
        {
            _clock.Advance(TimeSpan.FromMinutes(20));
            return Task.CompletedTask;
        });
        for (var poll = 0; poll < 5; poll++)
        {
            _service.Operations.Enqueue(McsRecords.Operation(AmsMcsOperationStatus.Accepted));
        }

        var exception = await Assert.ThrowsAsync<AmsAgentException>(() => waiter.WaitAsync(Target, McsRecords.AgentId, "op-deploy", default));

        Assert.Equal(ErrorCodes.AmsAgent.OperationWaitTimedOut, exception.ErrorCode);
        Assert.Contains("45 minutes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wait_ToleratesBriefPollFailures_ButReportsTheThirdInARow()
    {
        var failures = 0;

        Assert.Equal(AmsMcsOperationStatus.Succeeded, (await new McsOperationWaiter(new FlakyOperations(() => ++failures <= 2 ? throw PollTimeout() : McsRecords.Operation(AmsMcsOperationStatus.Succeeded)), _clock, Delay).WaitAsync(Target, McsRecords.AgentId, "op-deploy", default)).Status);
        var alwaysFailing = new FlakyOperations(() => throw PollTimeout());
        await Assert.ThrowsAsync<AgentDeployException>(() => new McsOperationWaiter(alwaysFailing, _clock, Delay).WaitAsync(Target, McsRecords.AgentId, "op-deploy", default));
        Assert.Equal(3, alwaysFailing.Polls);
    }

    [Fact]
    public async Task Wait_DoesNotRetryAPermanentPollFailure()
    {
        var polls = new FlakyOperations(() => throw AmsAgentException.Rejected(ErrorCodes.AmsAgent.OperationFailed, "gone", ExitCodes.Failure, "x", "y"));

        await Assert.ThrowsAsync<AmsAgentException>(() => new McsOperationWaiter(polls, _clock, Delay).WaitAsync(Target, McsRecords.AgentId, "op-deploy", default));

        Assert.Equal(1, polls.Polls);
    }

    [Fact]
    public async Task CtrlC_StopsWaitingAndNamesTheOperation()
    {
        using var cancellation = new CancellationTokenSource();
        var waiter = new McsOperationWaiter(_service, _clock, (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        var exception = await Assert.ThrowsAsync<AmsAgentException>(() => waiter.WaitAsync(Target, McsRecords.AgentId, "op-deploy", cancellation.Token));

        Assert.Equal(ErrorCodes.AmsAgent.OperationWaitInterrupted, exception.ErrorCode);
        Assert.Contains("op-deploy", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"msagent agent operation show --agent-id {McsRecords.AgentId} --operation-id op-deploy", exception.Remediation, StringComparison.Ordinal);
        Assert.Empty(_service.Calls);
    }

    [Fact]
    public async Task Wait_KeepsPollingARunningOperationBeyondTheReadBudget_UntilItFinishesWithinTheMaximumWait()
    {
        var started = _clock.UtcNow;

        Assert.Equal(AmsMcsOperationStatus.Succeeded, (await new McsOperationWaiter(new FlakyOperations(() => _clock.UtcNow - started < TimeSpan.FromMinutes(44) ? Running() : McsRecords.Operation(AmsMcsOperationStatus.Succeeded)), _clock, Delay).WaitAsync(Target, McsRecords.AgentId, "op-deploy", default)).Status);
        Assert.InRange(_clock.UtcNow - started, TimeSpan.FromMinutes(44), McsOperationWaiter.MaximumWait);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finish_TakesAnOperationTheServiceAnsweredWithOnceItEnded_WithoutPolling_EvenForACallerThatDoesNotWait(bool async)
    {
        var finished = McsRecords.Operation(AmsMcsOperationStatus.Succeeded);

        Assert.Same(finished, await Waiter().FinishAsync(Target, McsRecords.AgentId, "op-deploy", finished, async, Failed, default));
        Assert.Empty(_service.Calls);
        Assert.Empty(_delays);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finish_PollsAnOperationTheServiceAnsweredWithBeforeItEnded(bool answeredWhileRunning)
    {
        _service.Operations.Enqueue(Running());
        _service.Operations.Enqueue(McsRecords.Operation(AmsMcsOperationStatus.Succeeded));

        var operation = await Waiter().FinishAsync(Target, McsRecords.AgentId, "op-deploy", answeredWhileRunning ? Running() : null, false, Failed, default);

        Assert.Equal(AmsMcsOperationStatus.Succeeded, operation?.Status);
        Assert.Equal(["get-operation op-deploy", "get-operation op-deploy"], _service.Calls);
        Assert.Equal([2, 2], _delays.Select(delay => delay.TotalSeconds));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finish_ForACallerThatDoesNotWait_GivesNothingForAnOperationThatHasNotEnded(bool answeredWhileRunning)
    {
        Assert.Null(await Waiter().FinishAsync(Target, McsRecords.AgentId, "op-deploy", answeredWhileRunning ? Running() : null, true, Failed, default));
        Assert.Empty(_service.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finish_FailsAnOperationThatEndedInFailure_WithTheCallersWordingWhetherPolledOrAnswered(bool async)
    {
        var failed = McsRecords.Operation(AmsMcsOperationStatus.Failed);
        _service.Operations.Enqueue(failed);

        var polled = await Assert.ThrowsAsync<AmsAgentException>(() => Waiter().FinishAsync(Target, McsRecords.AgentId, "op-deploy", null, false, Failed, default));
        var answered = await Assert.ThrowsAsync<AmsAgentException>(() => Waiter().FinishAsync(Target, McsRecords.AgentId, "op-deploy", failed, async, Failed, default));

        Assert.Equal((ErrorCodes.AmsAgent.SourceValidationFailed, polled.ExitCode, polled.ErrorKind, polled.Message, polled.Remediation, polled.HasMultilineGuidance), (answered.ErrorCode, answered.ExitCode, answered.ErrorKind, answered.Message, answered.Remediation, answered.HasMultilineGuidance));
        Assert.Equal(["get-operation op-deploy"], _service.Calls);
    }

    private McsOperationWaiter Waiter() => new(_service, _clock, Delay, _progress);

    private Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        _delays.Add(delay);
        _clock.Advance(delay);
        return Task.CompletedTask;
    }

    private AmsMcsOperationRecord Running() => McsRecords.Operation(AmsMcsOperationStatus.Running) with { LastHeartbeatTime = _clock.UtcNow };

    private static AmsAgentException Failed(AmsMcsOperationRecord operation) => AmsMcsOperationErrorMap.OperationFailed(operation);

    private static AgentDeployException PollTimeout() => AgentDeployException.TimedOut("operation read", "request-1", false, new TaskCanceledException());

    private sealed class FlakyOperations(Func<AmsMcsOperationRecord> poll) : IAmsMcsOperationTransport
    {
        public int Polls { get; private set; }

        public Task<AmsMcsOperationAccepted> DeployAsync(AmsMcsDeployTransportRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AmsMcsOperationAccepted> CaptureAsync(AmsMcsCaptureTransportRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AmsMcsDeploymentRecord> RecordDeployAsync(AmsMcsDeployRecordRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AmsMcsOperationRecord> GetOperationAsync(AmsServiceTarget target, string agentId, string operationId, CancellationToken cancellationToken)
        {
            Polls++;
            return Task.FromResult(poll());
        }
    }
}
