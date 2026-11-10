using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>The <c>msagent agent capture</c> application service: commits the Copilot Studio content of the agent's Dev deployment to GRS on the caller's behalf and waits for it.</summary>
public sealed class McsAmsAgentCaptureService : ICommandHandler<McsAmsAgentCaptureRequest, McsAmsAgentCaptureResult>
{
    private const string Command = "msagent agent capture";

    private readonly AmsAgentSelection _selection;
    private readonly AmsTargets _targets;
    private readonly IAmsMcsOperationTransport _operations;
    private readonly McsOperationWaiter _waiter;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates the service.</summary>
    /// <param name="selection">Selects the agent and its Dev deployment.</param>
    /// <param name="targets">Resolves the data-plane and on-behalf-of endpoints.</param>
    /// <param name="operations">Starts the capture.</param>
    /// <param name="waiter">Waits for the capture operation.</param>
    /// <param name="delay">Waits before a capacity retry, replaced in tests.</param>
    public McsAmsAgentCaptureService(AmsAgentSelection selection, AmsTargets targets, IAmsMcsOperationTransport operations, McsOperationWaiter waiter, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _waiter = waiter ?? throw new ArgumentNullException(nameof(waiter));
        _delay = delay ?? Task.Delay;
    }

    /// <inheritdoc />
    public async Task<McsAmsAgentCaptureResult> HandleAsync(McsAmsAgentCaptureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agent = await _selection.DevDeploymentAsync(request.Project, request.AgentId, request.AgentName, request.EnvironmentId, Command, cancellationToken).ConfigureAwait(false);
        var deploymentId = agent.DevDeploymentId!;
        var capture = new AmsMcsCaptureTransportRequest { Target = _targets.OnBehalfOf(agent.Home, Command), AgentId = agent.AgentId, DeploymentId = deploymentId, IdempotencyKey = Guid.NewGuid().ToString("D"), Message = request.Message, Resolve = request.Resolve, RespondAsync = request.Async };
        var accepted = await McsCapacityRetry.StartAsync(token => _operations.CaptureAsync(capture, token), _delay, "capture", DeploymentCommandHints.DeploymentShow(agent.AgentId, deploymentId), cancellationToken).ConfigureAwait(false);
        var result = new McsAmsAgentCaptureResult { Status = "capturing", AgentId = agent.AgentId, DeploymentId = deploymentId, OperationId = accepted.OperationId, BaseCommitSha = accepted.SourceCommitSha };
        return await _waiter.FinishAsync(_targets.DataPlane(agent.Home), agent.AgentId, accepted.OperationId, accepted.Finished, request.Async, operation => AmsMcsOperationErrorMap.OperationFailed(operation, agentType: agent.AgentType), cancellationToken).ConfigureAwait(false) is { } operation
            ? result with { Status = operation.ResultCommitSha is null ? "unchanged" : "captured", BaseCommitSha = operation.SourceCommitSha ?? result.BaseCommitSha, CommitSha = operation.ResultCommitSha, MergedCommitSha = operation.MergedCommitSha, Notices = McsAmsViews.Notices(operation.Notices) }
            : result;
    }
}
