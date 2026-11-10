using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Copilot;

/// <summary>
/// Agent lookup and the publish lifecycle: resolving a selector to an agent, and publishing a draft.
/// </summary>
public sealed class CopilotCatalogService
{
    private static readonly TimeSpan PublishMaximumWait = TimeSpan.FromMinutes(5);

    private readonly ICopilotCatalogPort _catalog;
    private readonly ICopilotPublishPort _publisher;
    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly CopilotWorkspaceLocator _workspaceLocator;

    /// <summary>Creates the service.</summary>
    /// <param name="catalog">The agent catalog transport.</param>
    /// <param name="publisher">Publishes agents and waits for Copilot Studio to finish.</param>
    /// <param name="environmentResolver">Resolves the target environment.</param>
    /// <param name="workspaceLocator">Finds the workspace <c>publish</c> defaults to.</param>
    public CopilotCatalogService(ICopilotCatalogPort catalog, ICopilotPublishPort publisher, CopilotEnvironmentResolver environmentResolver, CopilotWorkspaceLocator workspaceLocator)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _workspaceLocator = workspaceLocator ?? throw new ArgumentNullException(nameof(workspaceLocator));
    }

    /// <summary>Resolves an identifier or schema name, failing when nothing matches.</summary>
    /// <param name="environment">The environment to resolve within.</param>
    /// <param name="botIdOrSchemaName">The value to resolve.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The resolved agent identifier.</returns>
    public async Task<string> ResolveAgentIdAsync(CopilotEnvironmentContext environment, string botIdOrSchemaName, CancellationToken cancellationToken) =>
        await _catalog.ResolveAgentIdAsync(environment, botIdOrSchemaName, cancellationToken).ConfigureAwait(false)
            ?? throw CopilotStudioException.AgentNotFound(botIdOrSchemaName);

    /// <summary>Publishes an agent's draft content and waits until Copilot Studio reports the publish succeeded, but only when the caller confirms.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The published agent, or what would be published when unconfirmed.</returns>
    public async Task<CopilotPublishResult> PublishAsync(CopilotPublishRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workspace = string.IsNullOrWhiteSpace(request.Bot)
            ? await _workspaceLocator.RequireAsync(request.ProjectDirectory, cancellationToken).ConfigureAwait(false)
            : null;

        var environment = await _environmentResolver.ResolveAsync(request.Environment ?? workspace?.EnvironmentId, cancellationToken).ConfigureAwait(false);
        var agentId = workspace?.AgentId ?? await ResolveAgentIdAsync(environment, request.Bot!, cancellationToken).ConfigureAwait(false);
        var agent = await _catalog.GetAgentAsync(environment, agentId, cancellationToken).ConfigureAwait(false);
        var resolvedFrom = workspace is null ? "bot-argument" : "workspace";

        if (!request.Force)
        {
            return PublishResult("confirmation-required", agent, environment, resolvedFrom, null);
        }

        return PublishResult("published", agent, environment, resolvedFrom, await _publisher.PublishAsync(environment, agentId, PublishMaximumWait, cancellationToken).ConfigureAwait(false) ?? await _catalog.GetPublishedOnAsync(environment, agentId, cancellationToken).ConfigureAwait(false));
    }

    private static CopilotPublishResult PublishResult(string status, CopilotAgentSummary agent, CopilotEnvironmentContext environment, string resolvedFrom, DateTimeOffset? publishedOn) => new()
    {
        Status = status,
        AgentId = agent.AgentId,
        SchemaName = agent.SchemaName,
        EnvironmentId = environment.EnvironmentId,
        ResolvedFrom = resolvedFrom,
        PublishedOn = publishedOn,
    };
}
