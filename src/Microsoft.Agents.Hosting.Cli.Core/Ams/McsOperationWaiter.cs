using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Waits for an MCS operation to finish, polling every two seconds and tolerating brief poll failures.</summary>
public sealed class McsOperationWaiter
{
    /// <summary>The wait before each poll.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>The longest the command waits for one operation.</summary>
    public static readonly TimeSpan MaximumWait = TimeSpan.FromMinutes(45);

    /// <summary>How long a running operation may go without a heartbeat before the wait stops.</summary>
    public static readonly TimeSpan StaleHeartbeat = TimeSpan.FromMinutes(10);

    /// <summary>The poll failures in a row the wait tolerates before it reports the last one.</summary>
    public const int MaximumConsecutivePollFailures = 3;

    private readonly IAmsMcsOperationTransport _operations;
    private readonly IClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly IProgressStatus _progress;

    /// <summary>Creates the waiter.</summary>
    /// <param name="operations">Reads operations.</param>
    /// <param name="clock">Bounds the wait and judges heartbeats.</param>
    /// <param name="delay">Waits between polls, replaced in tests.</param>
    /// <param name="progress">Shows the operation's state on the status line.</param>
    public McsOperationWaiter(IAmsMcsOperationTransport operations, IClock clock, Func<TimeSpan, CancellationToken, Task>? delay = null, IProgressStatus? progress = null)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delay = delay ?? Task.Delay;
        _progress = progress ?? NoProgressStatus.Instance;
    }

    /// <summary>Polls until the operation finishes; Ctrl+C stops the wait, never the operation.</summary>
    /// <param name="target">The data-plane endpoint.</param>
    /// <param name="agentId">The agent the operation belongs to.</param>
    /// <param name="operationId">The operation.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    public async Task<AmsMcsOperationRecord> WaitAsync(AmsServiceTarget target, string agentId, string operationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var deadline = _clock.UtcNow + MaximumWait;
        try
        {
            return await PollAsync(target, agentId, operationId, deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw AmsAgentException.OperationWaitInterrupted(agentId, operationId, exception);
        }
    }

    /// <summary>Returns an operation once it succeeded, waiting for it unless the service answered once it ended or the caller does not wait, and throws the error the caller words for it when it failed.</summary>
    /// <param name="target">The data-plane endpoint.</param>
    /// <param name="agentId">The agent the operation belongs to.</param>
    /// <param name="operationId">The operation.</param>
    /// <param name="finished">The operation the service answered with once it ended, or null when it answered while the operation ran.</param>
    /// <param name="async">Whether the caller returns while the operation runs, which gives null.</param>
    /// <param name="failed">Words the failure of the operation.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    public async Task<AmsMcsOperationRecord?> FinishAsync(AmsServiceTarget target, string agentId, string operationId, AmsMcsOperationRecord? finished, bool async, Func<AmsMcsOperationRecord, AmsAgentException> failed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failed);

        var operation = finished is { IsTerminal: true } ? finished : async ? null : await WaitAsync(target, agentId, operationId, cancellationToken).ConfigureAwait(false);
        return operation is null || operation.Status == AmsMcsOperationStatus.Succeeded ? operation : throw failed(operation);
    }

    private async Task<AmsMcsOperationRecord> PollAsync(AmsServiceTarget target, string agentId, string operationId, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (true)
        {
            var wait = TimeSpan.FromTicks(Math.Clamp((deadline - _clock.UtcNow).Ticks, 0, PollInterval.Ticks));
            if (wait > TimeSpan.Zero)
            {
                await _delay(wait, cancellationToken).ConfigureAwait(false);
            }

            AmsMcsOperationRecord operation;
            try
            {
                operation = await _operations.GetOperationAsync(target, agentId, operationId, cancellationToken).ConfigureAwait(false);
                failures = 0;
            }
            catch (AgentHostingException failure) when (IsTransient(failure) && ++failures < MaximumConsecutivePollFailures && _clock.UtcNow < deadline)
            {
                continue;
            }

            if (operation.IsTerminal)
            {
                return operation;
            }

            TrackRunning(operation, agentId, operationId, deadline);
        }
    }

    private void TrackRunning(AmsMcsOperationRecord operation, string agentId, string operationId, DateTimeOffset deadline)
    {
        _progress.Report($"{operation.Kind} {operation.Status.ToLowerInvariant()}");
        var now = _clock.UtcNow;
        if (now >= deadline)
        {
            throw AmsAgentException.OperationWaitTimedOut(agentId, operationId, $"it is still {operation.Status} after {MaximumWait.TotalMinutes:0} minutes.");
        }

        if (operation.Status == AmsMcsOperationStatus.Running && operation.LastHeartbeatTime is { } heartbeat && now - heartbeat > StaleHeartbeat)
        {
            throw AmsAgentException.OperationWaitTimedOut(agentId, operationId, $"it has reported no progress since {heartbeat:u}.");
        }
    }

    private static bool IsTransient(AgentHostingException failure) =>
        failure.ErrorCode is ErrorCodes.Deployment.AgentDeployTimedOut or ErrorCodes.Deployment.AgentDeployTransportFailed
        || failure is TargetRequestException { Status: 408 or 429 or >= 500 };
}
