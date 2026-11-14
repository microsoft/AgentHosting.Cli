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
    private readonly ICopilotAgentResourcePort _resources;
    private readonly IConfirmationPrompt _confirmations;

    /// <summary>Creates the service.</summary>
    /// <param name="resolver">Resolves records from selectors.</param>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="workspaceLocator">Reads the directory's cloud baseline, which names the precise target.</param>
    /// <param name="environmentResolver">Resolves the environment the target lives in.</param>
    /// <param name="catalog">Resolves and deletes the cloud agent.</param>
    /// <param name="projectIdentities">Reads the project's declared schema name.</param>
    /// <param name="resources">Lists, finds, and deletes the agent's resources.</param>
    /// <param name="confirmations">Confirms the delete with the operator.</param>
    public ManagedAgentDeleteService(ManagedAgentResolver resolver, IManagedAgentRegistry agents, CopilotWorkspaceLocator workspaceLocator, CopilotEnvironmentResolver environmentResolver, ICopilotCatalogPort catalog, CopilotProjectIdentityReader projectIdentities, ICopilotAgentResourcePort resources, IConfirmationPrompt confirmations)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _workspaceLocator = workspaceLocator ?? throw new ArgumentNullException(nameof(workspaceLocator));
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _projectIdentities = projectIdentities ?? throw new ArgumentNullException(nameof(projectIdentities));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _confirmations = confirmations ?? throw new ArgumentNullException(nameof(confirmations));
    }

    /// <summary>Deletes the agent's cloud resources and its records, or reports what that would delete.</summary>
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
        var workspaceDirectory = _resolver.FindAgentWorkspaceDirectory(projectDirectory, agent);
        var resources = request.AllResources ? ListResources(workspaceDirectory, agent) : [];
        var workspace = workspaceDirectory is null ? null : await _workspaceLocator.FindAsync(workspaceDirectory, cancellationToken).ConfigureAwait(false);
        var (environment, target) = await ResolveTargetAsync(agent, deployment, workspaceDirectory, workspace, _projectIdentities.FindSchemaName(agent, workspaceDirectory), cancellationToken).ConfigureAwait(false);
        var matchingDeployment = deployment ?? deployments.FirstOrDefault(candidate => string.Equals(ManagedAgentResolver.TargetEnvironmentId(agent, candidate), target?.EnvironmentId, StringComparison.OrdinalIgnoreCase));
        var existing = await FindResourcesAsync(resources, environment, cancellationToken).ConfigureAwait(false);
        var planned = new ManagedAgentDeleteResult
        {
            Status = ManagedAgentDeleteTargets.DeletedStatus,
            AgentId = agent.AgentId,
            DisplayName = agent.DisplayName,
            EnvironmentId = target?.EnvironmentId,
            McsAgentId = target?.AgentId,
            DeploymentId = matchingDeployment?.DeploymentId,
            McsAgentAlreadyAbsent = target is null,
            RemovedAgentDeployments = deployments.Count,
            OrphanedTargets = ManagedAgentDeleteTargets.OrphanedBy(agent, deployments, environment?.EnvironmentId, includeHome: false),
            Resources = [.. existing.Select(AsResource)],
        };

        if (await ManagedAgentDeleteTargets.StopBeforeDeletingAsync(_confirmations, request, planned, cancellationToken).ConfigureAwait(false) is { } stopped)
        {
            return stopped;
        }

        if (target is not null)
        {
            await _catalog.DeleteAgentAsync(target.Environment, target.AgentId, cancellationToken).ConfigureAwait(false);
        }

        await DeleteResourcesAsync(agent.DisplayName, existing, cancellationToken).ConfigureAwait(false);

        return planned with { RemovedAgentDeployments = ManagedAgentDeleteTargets.RemoveRecords(_agents, projectDirectory, agent, target?.EnvironmentId, cancellationToken) };
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
    private async Task<(CopilotEnvironmentContext? Environment, DeleteTarget? Target)> ResolveTargetAsync(ManagedAgentRecord agent, AgentDeploymentRecord? deployment, string? workspaceDirectory, CopilotWorkspaceInfo? workspace, string? schemaName, CancellationToken cancellationToken)
    {
        if (workspaceDirectory is null)
        {
            return (null, null);
        }

        if (deployment is null && workspace is not null)
        {
            var bound = await _environmentResolver.ResolveAsync(workspace.EnvironmentId, cancellationToken).ConfigureAwait(false);
            WorkspaceConnectionVerifier.RequireOriginMatches(workspace, bound);

            return (bound, await FindBoundAgentAsync(bound, workspace, schemaName, workspaceDirectory, agent.AgentId, cancellationToken).ConfigureAwait(false));
        }

        if (schemaName is null)
        {
            return (null, null);
        }

        var environment = await _environmentResolver.ResolveAsync(ManagedAgentResolver.TargetEnvironmentId(agent, deployment), cancellationToken).ConfigureAwait(false);

        return (environment, await FindBySchemaNameAsync(environment, workspace, schemaName, cancellationToken).ConfigureAwait(false));
    }

    private async Task<DeleteTarget?> FindBySchemaNameAsync(CopilotEnvironmentContext environment, CopilotWorkspaceInfo? workspace, string schemaName, CancellationToken cancellationToken)
    {
        var resolved = await _catalog.ResolveAgentIdAsync(environment, schemaName, cancellationToken).ConfigureAwait(false);

        if (resolved is null)
        {
            return null;
        }

        WorkspaceConnectionVerifier.RequireBoundAgent(workspace, environment, resolved);

        return new DeleteTarget(environment, resolved);
    }

    private IReadOnlyList<CopilotCopyLink> ListResources(string? workspaceDirectory, ManagedAgentRecord agent) => workspaceDirectory is null ? throw new UsageException($"--all-resources reads the flows, AI prompts, connection references, and custom connectors of '{agent.DisplayName}' from its workspace, which is no longer on disk. Restore the workspace, or delete without --all-resources.") : ManagedAgentDeleteTargets.Resources(_resources.List(workspaceDirectory));

    private async Task<IReadOnlyList<(CopilotEnvironmentContext Environment, CopilotCopyLink Resource)>> FindResourcesAsync(IReadOnlyList<CopilotCopyLink> resources, CopilotEnvironmentContext? environment, CancellationToken cancellationToken)
    {
        if (environment is null || resources.Count == 0)
        {
            return [];
        }

        return [.. (await _resources.FindExistingAsync(environment, resources, cancellationToken).ConfigureAwait(false)).Select(resource => (environment, resource))];
    }

    private async Task DeleteResourcesAsync(string displayName, IReadOnlyList<(CopilotEnvironmentContext Environment, CopilotCopyLink Resource)> resources, CancellationToken cancellationToken)
    {
        var remaining = new List<string>();

        foreach (var found in resources)
        {
            try
            {
                await _resources.DeleteAsync(found.Environment, found.Resource, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                remaining.Add($"{AsResource(found)} ({ManagedAgentException.DescribeChain(failure)})");
            }
        }

        if (remaining.Count > 0)
        {
            throw ManagedAgentException.ResourceDeleteIncomplete(displayName, resources.Count - remaining.Count, remaining);
        }
    }

    private static ManagedAgentResource AsResource((CopilotEnvironmentContext Environment, CopilotCopyLink Resource) found) => new() { Kind = found.Resource.Kind, Identity = found.Resource.Identity, EnvironmentId = found.Environment.EnvironmentId };

    private async Task<DeleteTarget?> FindBoundAgentAsync(CopilotEnvironmentContext environment, CopilotWorkspaceInfo workspace, string? expectedSchemaName, string workspaceDirectory, string agentId, CancellationToken cancellationToken)
    {
        if (expectedSchemaName is null)
        {
            throw ManagedAgentException.WorkspaceSchemaUnknown(workspaceDirectory, agentId);
        }

        if (await _catalog.FindAgentAsync(environment, workspace.AgentId, cancellationToken).ConfigureAwait(false) is not { } bound)
        {
            return await FindBySchemaNameAsync(environment, workspace, expectedSchemaName, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(bound.SchemaName, expectedSchemaName, StringComparison.OrdinalIgnoreCase))
        {
            throw ManagedAgentException.WorkspaceIdentityMismatch(expectedSchemaName, bound.SchemaName, bound.AgentId, workspaceDirectory);
        }

        return new DeleteTarget(environment, workspace.AgentId);
    }
}
