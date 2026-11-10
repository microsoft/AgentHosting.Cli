using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>The <c>msagent agent operation show</c> application service: reads one deploy or capture operation of an agent AMS registered.</summary>
public sealed class McsAmsOperationShowService : ICommandHandler<McsAmsOperationShowRequest, McsAmsOperationShowResult>
{
    private const string Command = "msagent agent operation show";

    private readonly AmsAgentSelection _selection;
    private readonly AmsTargets _targets;
    private readonly IAmsMcsOperationTransport _operations;

    /// <summary>Creates the service.</summary>
    /// <param name="selection">Selects the agent.</param>
    /// <param name="targets">Resolves the data-plane endpoint.</param>
    /// <param name="operations">Reads the operation.</param>
    public McsAmsOperationShowService(AmsAgentSelection selection, AmsTargets targets, IAmsMcsOperationTransport operations)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    /// <inheritdoc />
    public async Task<McsAmsOperationShowResult> HandleAsync(McsAmsOperationShowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var operationId = DeploymentRequestValidator.Required("--operation-id", request.OperationId);
        var agent = await _selection.AgentAsync(request.Project, request.AgentId, request.AgentName, request.EnvironmentId, Command, cancellationToken).ConfigureAwait(false);
        var operation = await _operations.GetOperationAsync(_targets.DataPlane(agent.Home), agent.AgentId, operationId, cancellationToken).ConfigureAwait(false);
        return new McsAmsOperationShowResult
        {
            OperationId = operation.OperationId,
            Kind = operation.Kind,
            Status = operation.Status,
            AgentId = operation.AgentId,
            DeploymentId = operation.DeploymentId,
            SourceCommitSha = operation.SourceCommitSha,
            ResultCommitSha = operation.ResultCommitSha,
            CapturedCommitSha = operation.CapturedCommitSha,
            MergedCommitSha = operation.MergedCommitSha,
            StartedBy = operation.StartedBy,
            StartedTime = operation.StartedTime,
            LastHeartbeatTime = operation.LastHeartbeatTime,
            CompletedTime = operation.CompletedTime,
            ErrorCode = operation.Error?.Code,
            ErrorMessage = operation.Error?.Message,
            ErrorDetails = [.. operation.Error?.Details.Select(McsAmsViews.DetailText) ?? []],
            Notices = McsAmsViews.Notices(operation.Notices),
            Remediation = operation.Status == AmsMcsOperationStatus.Failed ? AmsMcsOperationErrorMap.OperationRemediation(operation, agent.AgentType) : null,
        };
    }
}
