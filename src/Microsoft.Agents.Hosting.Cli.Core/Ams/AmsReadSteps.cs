using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>The AMS steps of <c>agent show</c>, <c>deployment list</c> and <c>deployment show</c>: main's result, plus the git url, the last commit and the state AMS holds for the registered agents, from one repository read.</summary>
public sealed class AmsReadSteps : ICommandHandler<ManagedAgentShowRequest, ManagedAgentShowResult>, ICommandHandler<AgentDeploymentListRequest, AgentDeploymentListResult>, ICommandHandler<AgentDeploymentShowRequest, AgentDeploymentShowResult>
{
    private readonly IAgentHostingOperations _operations;
    private readonly ManagedAgentResolver _resolver;
    private readonly AmsTargets _targets;
    private readonly IAmsMcsAgentReadTransport _reads;

    /// <summary>Creates the steps.</summary>
    /// <param name="operations">Runs main's commands.</param>
    /// <param name="resolver">Reads the project's agents.</param>
    /// <param name="targets">Resolves the home environment's endpoints.</param>
    /// <param name="reads">Reads the repository with its agents' deployments.</param>
    public AmsReadSteps(IAgentHostingOperations operations, ManagedAgentResolver resolver, AmsTargets targets, IAmsMcsAgentReadTransport reads)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
    }

    /// <inheritdoc />
    public async Task<ManagedAgentShowResult> HandleAsync(ManagedAgentShowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _operations.ShowManagedAgentAsync(request, cancellationToken).ConfigureAwait(false);
        var registered = result.Agents.Where(agent => agent.SourceControlMetadata is not null).ToArray();
        var (repository, warning) = await ReadAsync(registered, cancellationToken).ConfigureAwait(false);
        return result with
        {
            Ams = [.. registered.Select(agent => new AmsAgentDetails(agent.AgentId, agent.SourceControlMetadata!.RepositoryId, agent.SourceControlMetadata.Directory, repository?.GitUrl, [.. DeploymentsOf(repository, agent.AgentId).Select(deployment => new AmsDeploymentState(deployment.DeploymentId, deployment.LastDeployedCommitSha, deployment.State))]))],
            Warnings = warning is null ? null : [warning],
        };
    }

    /// <inheritdoc />
    public async Task<AgentDeploymentListResult> HandleAsync(AgentDeploymentListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _operations.ListAgentDeploymentsAsync(request, cancellationToken).ConfigureAwait(false);
        var registered = Registered(request.Project, result.Slots.Select(slot => slot.AgentId));
        var (repository, warning) = await ReadAsync(registered, cancellationToken).ConfigureAwait(false);
        return result with
        {
            Slots = [.. result.Slots.Select(slot => Find(repository, slot.AgentId, slot.DeploymentId) is { } deployment ? slot with { LastDeployedCommitSha = deployment.LastDeployedCommitSha, State = deployment.State } : slot)],
            Warnings = warning is null ? null : [warning],
            ShowsAms = true,
        };
    }

    /// <inheritdoc />
    public async Task<AgentDeploymentShowResult> HandleAsync(AgentDeploymentShowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _operations.ShowAgentDeploymentAsync(request, cancellationToken).ConfigureAwait(false);
        var (repository, warning) = await ReadAsync(Registered(request.Project, [result.AgentId]), cancellationToken).ConfigureAwait(false);
        var deployment = Find(repository, result.AgentId, result.DeploymentId);
        return result with { LastDeployedCommitSha = deployment?.LastDeployedCommitSha, State = deployment?.State, Warnings = warning is null ? null : [warning], ShowsAms = true };
    }

    private static IEnumerable<AmsMcsDeploymentRecord> DeploymentsOf(AmsMcsRepository? repository, string? agentId) => repository?.Agents.FirstOrDefault(agent => string.Equals(agent.AgentId, agentId, StringComparison.OrdinalIgnoreCase))?.Deployments ?? [];

    private static AmsMcsDeploymentRecord? Find(AmsMcsRepository? repository, string? agentId, string deploymentId) => DeploymentsOf(repository, agentId).FirstOrDefault(deployment => string.Equals(deployment.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase));

    private ManagedAgentRecord[] Registered(string? project, IEnumerable<string?> agentIds)
    {
        var ids = agentIds.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. _resolver.ListAgents(_resolver.ResolveProjectDirectory(project)).Where(agent => agent.SourceControlMetadata is not null && ids.Contains(agent.AgentId))];
    }

    private async Task<(AmsMcsRepository? Repository, string? Warning)> ReadAsync(ManagedAgentRecord[] registered, CancellationToken cancellationToken)
    {
        if (registered.Length == 0)
        {
            return (null, null);
        }

        try
        {
            var home = await _targets.HomeAsync(registered[0].EnvironmentId, cancellationToken).ConfigureAwait(false);
            return (await _reads.ReadRepositoryAsync(_targets.DataPlane(home), registered[0].SourceControlMetadata!.RepositoryId, null, includeDeployments: true, cancellationToken).ConfigureAwait(false), null);
        }
        catch (AgentHostingException failure)
        {
            return (null, $"AMS couldn't be read: {failure.Message}");
        }
    }
}
