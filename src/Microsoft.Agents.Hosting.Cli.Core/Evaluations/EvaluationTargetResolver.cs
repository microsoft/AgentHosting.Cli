using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Evaluations;

/// <summary>The environment and agent one evaluation command runs against.</summary>
/// <param name="Environment">The environment holding the agent.</param>
/// <param name="McsAgentId">The Dataverse bot id the evaluation API addresses.</param>
/// <param name="DisplayName">The agent's name, for messages.</param>
public sealed record EvaluationTarget(CopilotEnvironmentContext Environment, string McsAgentId, string DisplayName);

/// <summary>Resolves the agent an evaluation verb addresses.</summary>
public interface IEvaluationTargetResolver
{
    /// <summary>Resolves the agent one evaluation verb addresses.</summary>
    /// <param name="selector">The command's agent and environment selectors.</param>
    /// <param name="verb">The command name, used in failure messages.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    Task<EvaluationTarget> ResolveAsync(EvaluationSelector selector, string verb, CancellationToken cancellationToken);
}

/// <summary>Resolves the agent an evaluation verb addresses, from a project record or an explicit id.</summary>
public sealed class EvaluationTargetResolver : IEvaluationTargetResolver
{
    private const int AmbiguityProbe = 2;

    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly ICopilotCatalogPort _catalog;
    private readonly ManagedAgentResolver _resolver;
    private readonly AgentConfigLocator _locator;
    private readonly CopilotProjectIdentityReader _projectIdentities;

    /// <summary>Creates the resolver.</summary>
    /// <param name="environmentResolver">Resolves the environment to act in.</param>
    /// <param name="catalog">Resolves and lists the agents an environment holds.</param>
    /// <param name="resolver">Resolves records from project selectors.</param>
    /// <param name="locator">Detects whether a project configuration exists.</param>
    /// <param name="projectIdentities">Reads the schema name a workspace declares.</param>
    public EvaluationTargetResolver(CopilotEnvironmentResolver environmentResolver, ICopilotCatalogPort catalog, ManagedAgentResolver resolver, AgentConfigLocator locator, CopilotProjectIdentityReader projectIdentities)
    {
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _projectIdentities = projectIdentities ?? throw new ArgumentNullException(nameof(projectIdentities));
    }

    /// <inheritdoc />
    public async Task<EvaluationTarget> ResolveAsync(EvaluationSelector selector, string verb, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);

        var environment = await RequireGatewayAsync(
            await _environmentResolver.ResolveAsync(TargetEnvironmentId(selector), cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(selector.AgentId))
        {
            var agentId = selector.AgentId.Trim();

            if (await _catalog.ResolveAgentIdAsync(environment, agentId, cancellationToken).ConfigureAwait(false) is { } directId)
            {
                return new EvaluationTarget(environment, directId, agentId);
            }

            if (ProjectSelector(selector, verb) is not { } fromProject)
            {
                throw CopilotStudioException.AgentNotFound(agentId, environment.EnvironmentId);
            }

            return await RequireAgentAsync(environment, fromProject, cancellationToken).ConfigureAwait(false);
        }

        return ProjectSelector(selector, verb) is { } projectSelector
            ? await RequireAgentAsync(environment, projectSelector, cancellationToken).ConfigureAwait(false)
            : await ResolveFromEnvironmentAsync(environment, selector, verb, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks the environment before resolving it, so the project's own record answers for an agent
    /// it holds and a missing or stale <c>env select</c> cannot redirect or block the verb. An
    /// explicit <c>--environment-id</c> still wins, and the active selection remains the fallback.
    /// </summary>
    private string? TargetEnvironmentId(EvaluationSelector selector) =>
        !string.IsNullOrWhiteSpace(selector.EnvironmentId) ? selector.EnvironmentId
        : _resolver.FindAgent(_resolver.ResolveProjectDirectory(selector.Project), selector.AgentId, selector.AgentName) is { } agent
            ? ManagedAgentResolver.TargetEnvironmentId(agent, null)
            : null;

    private async Task<EvaluationTarget> RequireAgentAsync(CopilotEnvironmentContext environment, ProjectAgent project, CancellationToken cancellationToken) =>
        project.Selector is { } named && await _catalog.ResolveAgentIdAsync(environment, named, cancellationToken).ConfigureAwait(false) is { } agentId
            ? new EvaluationTarget(environment, agentId, project.DisplayName)
            : throw CopilotStudioException.AgentNotFound(project.Selector ?? project.DisplayName, environment.EnvironmentId);

    private async Task<CopilotEnvironmentContext> RequireGatewayAsync(CopilotEnvironmentContext environment, CancellationToken cancellationToken)
    {
        if (environment.AgentManagementUrl is not null)
        {
            return environment;
        }

        var inventory = await _environmentResolver.ListAsync(cancellationToken).ConfigureAwait(false);

        return inventory.Reachable.FirstOrDefault(candidate =>
            candidate.AgentManagementUrl is not null
            && string.Equals(candidate.EnvironmentId, environment.EnvironmentId, StringComparison.OrdinalIgnoreCase))
            ?? throw CopilotStudioException.AgentManagementEndpointMissing(environment.EnvironmentId);
    }

    private ProjectAgent? ProjectSelector(EvaluationSelector selector, string verb)
    {
        var projectDirectory = _resolver.ResolveProjectDirectory(selector.Project);

        if (!string.IsNullOrWhiteSpace(selector.Project) && !Directory.Exists(projectDirectory))
        {
            throw new UsageException($"The directory '{projectDirectory}' does not exist.");
        }

        if (!_locator.ConfigExists(projectDirectory))
        {
            return null;
        }

        var agent = ManagedAgentResolver.RequireMcsAgent(_resolver.ResolveAgent(projectDirectory, selector.AgentId, selector.AgentName), verb);
        var workspaceDirectory = _resolver.FindAgentWorkspaceDirectory(projectDirectory, agent);
        var schemaName = ManagedAgentResolver.McsSchemaNameOf(agent, workspaceDirectory is null ? null : _projectIdentities.Find(workspaceDirectory)?.SchemaName);

        return new ProjectAgent(
            schemaName ?? (string.IsNullOrWhiteSpace(agent.McsAgentId) ? null : agent.McsAgentId.Trim()),
            agent.DisplayName);
    }

    /// <summary>The agent a project names, and the identity the service is addressed by when it has one.</summary>
    /// <param name="Selector">The schema name or cloud id the record carries, absent until a deploy records one.</param>
    /// <param name="DisplayName">The agent's name, for messages.</param>
    private sealed record ProjectAgent(string? Selector, string DisplayName);

    private async Task<EvaluationTarget> ResolveFromEnvironmentAsync(CopilotEnvironmentContext environment, EvaluationSelector selector, string verb, CancellationToken cancellationToken)
    {
        var named = selector.AgentName;
        var agents = await _catalog.ListAgentsAsync(environment, named is null ? AmbiguityProbe : null, cancellationToken).ConfigureAwait(false);

        var matches = named is null
            ? agents
            : [.. agents.Where(agent =>
                string.Equals(agent.DisplayName, named, StringComparison.OrdinalIgnoreCase)
                || string.Equals(agent.SchemaName, named, StringComparison.OrdinalIgnoreCase)
                || string.Equals(agent.AgentId, named, StringComparison.OrdinalIgnoreCase))];

        if (matches.Count == 1)
        {
            return new EvaluationTarget(environment, matches[0].AgentId, matches[0].DisplayName);
        }

        throw matches.Count == 0
            ? CopilotStudioException.AgentNotFound(named ?? verb)
            : new UsageException($"Several agents in environment '{environment.EnvironmentId}' match. Pass --agent-id to choose one.");
    }
}
