using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent agent delete</c>: deletes one cloud agent and every record for the managed agent.
/// </summary>
public sealed class ManagedAgentDeleteService
{
    private readonly ManagedAgentResolver _resolver;
    private readonly IManagedAgentRegistry _agents;
    private readonly CopilotWorkspaceLocator _workspaceLocator;
    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly ICopilotCatalogPort _catalog;
    private readonly CopilotProjectIdentityReader _projectIdentities;
    private readonly IConfirmationPrompt _confirmations;
    private readonly IAgentRecordMirror _mirror;

    /// <summary>Creates the service.</summary>
    /// <param name="resolver">Resolves records from selectors.</param>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="workspaceLocator">Reads the directory's cloud baseline, which names the precise target.</param>
    /// <param name="environmentResolver">Resolves the environment the target lives in.</param>
    /// <param name="catalog">Resolves and deletes the cloud agent.</param>
    /// <param name="projectIdentities">Reads the project's declared schema name.</param>
    /// <param name="confirmations">Confirms the delete with the operator.</param>
    /// <param name="mirror">Deletes AMS's records of the agent first, in AMS mode.</param>
    public ManagedAgentDeleteService(ManagedAgentResolver resolver, IManagedAgentRegistry agents, CopilotWorkspaceLocator workspaceLocator, CopilotEnvironmentResolver environmentResolver, ICopilotCatalogPort catalog, CopilotProjectIdentityReader projectIdentities, IConfirmationPrompt confirmations, IAgentRecordMirror? mirror = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _workspaceLocator = workspaceLocator ?? throw new ArgumentNullException(nameof(workspaceLocator));
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _projectIdentities = projectIdentities ?? throw new ArgumentNullException(nameof(projectIdentities));
        _confirmations = confirmations ?? throw new ArgumentNullException(nameof(confirmations));
        _mirror = mirror ?? NoAgentRecordMirror.Instance;
    }

    /// <summary>Deletes the agent's cloud resources and its records.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What was deleted, and what was left behind.</returns>
    public async Task<ManagedAgentDeleteResult> DeleteAsync(ManagedAgentDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projectDirectory = _resolver.ResolveProjectDirectory(request.Project);
        var agent = ManagedAgentResolver.RequireMcsAgent(_resolver.ResolveAgent(projectDirectory, request.AgentId, request.AgentName), "msagent agent delete");
        var deployment = _resolver.FindDeployment(projectDirectory, agent, request.DeploymentId, request.DeploymentName);
        var deployments = _resolver.ListDeployments(projectDirectory, agent.AgentId);

        var target = await ResolveTargetAsync(projectDirectory, agent, deployment, cancellationToken).ConfigureAwait(false);
        var matchingDeployment = deployment ?? deployments.FirstOrDefault(candidate => string.Equals(ManagedAgentResolver.TargetEnvironmentId(agent, candidate), target?.EnvironmentId, StringComparison.OrdinalIgnoreCase));
        var orphaned = deployments
            .Where(candidate => candidate.DeploymentId != matchingDeployment?.DeploymentId)
            .Select(candidate => new ManagedAgentOrphanedTarget { DeploymentName = candidate.DeploymentName, EnvironmentId = ManagedAgentResolver.TargetEnvironmentId(agent, candidate) })
            .ToArray();

        var outcome = await ConfirmationPolicy.ConfirmAsync(_confirmations, request.NonInteractive, Describe(agent, target, deployments.Count, orphaned), agent.DisplayName, cancellationToken).ConfigureAwait(false);

        if (outcome != ConfirmationOutcome.Confirmed)
        {
            return new ManagedAgentDeleteResult
            {
                Status = outcome == ConfirmationOutcome.Mismatched ? "name-mismatch" : "cancelled",
                AgentId = agent.AgentId,
                DisplayName = agent.DisplayName,
                EnvironmentId = target?.EnvironmentId,
                DeploymentId = matchingDeployment?.DeploymentId,
                OrphanedTargets = orphaned,
            };
        }

        if (target is not null)
        {
            await _catalog.DeleteAgentAsync(target.Environment, target.AgentId, cancellationToken).ConfigureAwait(false);
        }

        AgentRecordMirrored<int> removed;

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            removed = await _mirror.ApplyAsync(projectDirectory, new AgentDeleted(agent), () => _agents.RemoveWithDeployments(projectDirectory, agent.AgentId, agent.EntityTag), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (target is not null && failure is not OperationCanceledException)
        {
            throw ManagedAgentException.RecordsSurvivedCloudDelete(agent.AgentId, target.EnvironmentId, failure.Message);
        }

        return new ManagedAgentDeleteResult
        {
            Status = "agent-deleted",
            AgentId = agent.AgentId,
            DisplayName = agent.DisplayName,
            EnvironmentId = target?.EnvironmentId,
            DeploymentId = matchingDeployment?.DeploymentId,
            McsAgentAlreadyAbsent = target is null,
            RemovedAgentDeployments = removed.Value,
            OrphanedTargets = orphaned,
            Warnings = removed.Warning is null ? [] : [removed.Warning],
            AmsRecordDeleted = removed.Mirrored ? true : null,
            RepositoryId = removed.Mirrored ? agent.SourceControlMetadata?.RepositoryId : null,
        };
    }

    private sealed record DeleteTarget(CopilotEnvironmentContext Environment, string AgentId)
    {
        public string EnvironmentId => Environment.EnvironmentId;
    }

    /// <summary>
    /// Resolves which cloud agent to delete. The agent identifier and its environment always come from
    /// the same source.
    ///
    /// An agent selector chooses the local record only. It never changes which cloud agent is targeted,
    /// because a multi-agent project makes the selector mandatory and a selector that silently switched
    /// resolution strategy would delete a different agent than the same command does in a one-agent
    /// project. A workspace that is no longer on disk leaves nothing to target, so the delete removes the
    /// records alone rather than failing on a directory the operator has already removed.
    /// </summary>
    private async Task<DeleteTarget?> ResolveTargetAsync(string projectDirectory, ManagedAgentRecord agent, AgentDeploymentRecord? deployment, CancellationToken cancellationToken)
    {
        if (_resolver.FindAgentWorkspaceDirectory(projectDirectory, agent) is not { } workspaceDirectory)
        {
            return null;
        }

        var workspace = await _workspaceLocator.FindAsync(workspaceDirectory, cancellationToken).ConfigureAwait(false);
        var expectedSchemaName = ManagedAgentResolver.McsSchemaNameOf(agent, _projectIdentities.Find(workspaceDirectory)?.SchemaName);

        if (deployment is null && workspace is not null)
        {
            var bound = await _environmentResolver.ResolveAsync(workspace.EnvironmentId, cancellationToken).ConfigureAwait(false);
            WorkspaceConnectionVerifier.RequireOriginMatches(workspace, bound);
            await RequireBoundAgentMatchesAsync(bound, workspace.AgentId, expectedSchemaName, workspaceDirectory, agent.AgentId, cancellationToken).ConfigureAwait(false);

            return new DeleteTarget(bound, workspace.AgentId);
        }

        if (expectedSchemaName is not { } schemaName)
        {
            return null;
        }

        var environment = await _environmentResolver.ResolveAsync(ManagedAgentResolver.TargetEnvironmentId(agent, deployment), cancellationToken).ConfigureAwait(false);
        var resolved = await _catalog.ResolveAgentIdAsync(environment, schemaName, cancellationToken).ConfigureAwait(false);

        if (resolved is null)
        {
            return null;
        }

        WorkspaceConnectionVerifier.RequireBoundAgent(workspace, environment, resolved);

        return new DeleteTarget(environment, resolved);
    }

    /// <summary>Rejects a delete whose workspace binding cannot be proven to name the record's own agent.</summary>
    private async Task RequireBoundAgentMatchesAsync(CopilotEnvironmentContext environment, string boundAgentId, string? expectedSchemaName, string workspaceDirectory, string agentId, CancellationToken cancellationToken)
    {
        if (expectedSchemaName is null)
        {
            throw ManagedAgentException.WorkspaceSchemaUnknown(workspaceDirectory, agentId);
        }

        var bound = await _catalog.GetAgentAsync(environment, boundAgentId, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(bound.SchemaName, expectedSchemaName, StringComparison.OrdinalIgnoreCase))
        {
            throw ManagedAgentException.WorkspaceIdentityMismatch(expectedSchemaName, bound.SchemaName, bound.AgentId, workspaceDirectory);
        }
    }

    private static string Describe(ManagedAgentRecord agent, DeleteTarget? target, int deploymentCount, ManagedAgentOrphanedTarget[] orphaned)
    {
        var lines = new List<string>
        {
            "About to delete:",
            $"  agent        {agent.DisplayName} ({agent.AgentId})",
            target is null ? "  cloud agent  none — this agent exists nowhere in Copilot Studio" : $"  cloud agent  {target.AgentId} in {target.EnvironmentId}",
            $"  records      1 ManagedAgent + {deploymentCount} AgentDeployment row(s)",
        };

        if (orphaned.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add("  orphaned by this delete:");
            lines.AddRange(orphaned.Select(orphan => $"    {orphan.EnvironmentId}   (deployment \"{orphan.DeploymentName}\")"));
        }

        lines.Add(string.Empty);
        lines.Add("This deletes remote resources. Local files are not touched.");

        return string.Join(Environment.NewLine, lines);
    }
}
