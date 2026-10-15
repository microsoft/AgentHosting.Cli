using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Copilot;

/// <summary>
/// Agent lookup and the publish lifecycle: resolving a selector to an agent, and publishing a draft.
/// </summary>
public sealed class CopilotCatalogService
{
    private static readonly TimeSpan DefaultPublishPollingInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan DefaultPublishMaximumWait = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan PublishRetryDelay = TimeSpan.FromSeconds(5);

    private readonly ICopilotCatalogPort _catalog;
    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly CopilotWorkspaceLocator _workspaceLocator;
    private readonly IClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates the service.</summary>
    /// <param name="catalog">The agent catalog transport.</param>
    /// <param name="environmentResolver">Resolves the target environment.</param>
    /// <param name="workspaceLocator">Finds the workspace <c>publish</c> defaults to.</param>
    /// <param name="clock">Supplies the current time for polling.</param>
    /// <param name="delay">Waits between polls. Replaced in tests so polling is instant.</param>
    public CopilotCatalogService(ICopilotCatalogPort catalog, CopilotEnvironmentResolver environmentResolver, CopilotWorkspaceLocator workspaceLocator, IClock clock, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _workspaceLocator = workspaceLocator ?? throw new ArgumentNullException(nameof(workspaceLocator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delay = delay ?? ((interval, cancellationToken) => Task.Delay(interval, cancellationToken));
    }

    /// <summary>Resolves an identifier or schema name, failing when nothing matches.</summary>
    /// <param name="environment">The environment to resolve within.</param>
    /// <param name="botIdOrSchemaName">The value to resolve.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The resolved agent identifier.</returns>
    public async Task<string> ResolveAgentIdAsync(CopilotEnvironmentContext environment, string botIdOrSchemaName, CancellationToken cancellationToken) =>
        await _catalog.ResolveAgentIdAsync(environment, botIdOrSchemaName, cancellationToken).ConfigureAwait(false)
            ?? throw CopilotStudioException.AgentNotFound(botIdOrSchemaName);

    /// <summary>Publishes an agent's draft content, but only when the caller confirms.</summary>
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
            return PublishResult("confirmation-required", agent, environment, resolvedFrom, null, null, TimeSpan.Zero);
        }

        var previousPublishedOn = await _catalog.GetPublishedOnAsync(environment, agentId, cancellationToken).ConfigureAwait(false);
        await PublishOnceRetryingAConflictAsync(environment, agentId, cancellationToken).ConfigureAwait(false);

        var maximumWait = request.MaximumWait ?? DefaultPublishMaximumWait;

        var (publishedOn, waited, timedOut) = await PollAsync(
            () => _catalog.GetPublishedOnAsync(environment, agentId, cancellationToken),
            observed => observed != previousPublishedOn,
            request.PollingInterval ?? DefaultPublishPollingInterval,
            maximumWait,
            cancellationToken).ConfigureAwait(false);

        return timedOut
            ? throw CopilotStudioException.PublishTimeout(agentId, maximumWait)
            : PublishResult("published", agent, environment, resolvedFrom, previousPublishedOn, publishedOn, waited);
    }

    /// <summary>
    /// Publishes, retrying once when the service reports it is still busy with the content just pushed.
    ///
    /// A first publish straight after a push is the common case for a new agent, and the service answers
    /// it with a conflict while it settles. Publishing is idempotent, so one delayed retry costs nothing
    /// when it was already live and saves the operator re-running the whole deploy when it was not.
    /// </summary>
    private async Task PublishOnceRetryingAConflictAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken)
    {
        try
        {
            await _catalog.PublishAgentAsync(environment, agentId, cancellationToken).ConfigureAwait(false);

            return;
        }
        catch (CopilotStudioException failure) when (failure.ErrorKind == CopilotErrorKinds.ServiceError)
        {
            await _delay(PublishRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        await _catalog.PublishAgentAsync(environment, agentId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(T Observed, TimeSpan Waited, bool TimedOut)> PollAsync<T>(Func<Task<T>> read, Func<T, bool> isComplete, TimeSpan pollingInterval, TimeSpan maximumWait, CancellationToken cancellationToken)
    {
        var startedAt = _clock.UtcNow;

        while (true)
        {
            var observed = await read().ConfigureAwait(false);
            var waited = _clock.UtcNow - startedAt;

            if (isComplete(observed))
            {
                return (observed, waited, false);
            }

            if (waited >= maximumWait)
            {
                return (observed, waited, true);
            }

            await _delay(pollingInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static CopilotPublishResult PublishResult(string status, CopilotAgentSummary agent, CopilotEnvironmentContext environment, string resolvedFrom, DateTimeOffset? previousPublishedOn, DateTimeOffset? publishedOn, TimeSpan waited) => new()
    {
        Status = status,
        AgentId = agent.AgentId,
        SchemaName = agent.SchemaName,
        EnvironmentId = environment.EnvironmentId,
        ResolvedFrom = resolvedFrom,
        PreviousPublishedOn = previousPublishedOn,
        PublishedOn = publishedOn,
        Waited = waited,
    };
}
