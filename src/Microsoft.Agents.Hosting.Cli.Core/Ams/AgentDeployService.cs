using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Puts an existing AMS deployment live as its record says, never creating a deployment.</summary>
public sealed class AgentDeployService
{
    private readonly AmsAddressResolver _addresses;
    private readonly IAmsDeploymentTransport _deployments;

    /// <summary>Creates the service over the AMS deployment transport.</summary>
    public AgentDeployService(AmsAddressResolver addresses, IAmsDeploymentTransport deployments)
    {
        _addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
    }

    /// <summary>Reads the deployment, checks the stage rule, and deploys it with the entity tag of that read.</summary>
    public async Task<AgentDeployResult> DeployAsync(AgentDeployRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var commitId = AmsCommitId.ParseOption(request.CommitId);
        var address = await _addresses.ResolveDeploymentAsync(request, "agent deploy", cancellationToken).ConfigureAwait(false);
        var current = await _addresses.ReadDeploymentAsync(address, cancellationToken).ConfigureAwait(false);
        AmsDeploymentView.RequireNotDeleting(current);
        RequireCommitStage(commitId, current.DeploymentType);
        var deployed = AmsDeploymentView.RequireAddressed(address.Reference, await _deployments.DeployAsync(new AmsDeploymentDeployTransportRequest { ServiceTarget = address.Reference.ServiceTarget, AgentId = address.Reference.AgentId, DeploymentId = address.Reference.DeploymentId, ETag = current.ETag, CommitId = commitId }, cancellationToken).ConfigureAwait(false), "Deployment deploy");
        return new AgentDeployResult
        {
            Status = AgentDeployResult.DeployedStatus,
            Deployment = AmsDeploymentView.ToRecord(deployed),
            Bot = deployed.BotService is null ? AgentDeployResult.BotNone : current.BotService is null ? AgentDeployResult.BotCreated : AgentDeployResult.BotKept,
            PreviousEndpoint = current.RuntimeEndpoint is { } previous && !string.Equals(previous, deployed.RuntimeEndpoint, StringComparison.Ordinal) ? previous : null,
            KindSwitched = current.IsCustomEndpoint != deployed.IsCustomEndpoint,
        };
    }

    private static void RequireCommitStage(string? commitId, string deploymentType)
    {
        if (commitId is not null && string.Equals(deploymentType, "Prod", StringComparison.Ordinal))
        {
            throw new UsageException("--commit-id cannot be used with a Prod deployment: AMS deploys the newest upload to Prod once a Test deployment of the agent runs it. Remove --commit-id.");
        }
    }
}
