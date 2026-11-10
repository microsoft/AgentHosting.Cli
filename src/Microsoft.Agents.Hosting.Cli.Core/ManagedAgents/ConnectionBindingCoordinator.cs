using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Points a connector's connection references at one connection, whether the operator names it,
/// chooses it from the catalog, or has a new one created.
/// </summary>
public sealed class ConnectionBindingCoordinator
{
    private readonly IConnectionSelectionPrompt _selectionPrompt;
    private readonly ConnectionCreationFlow _creationFlow;

    /// <summary>Creates the coordinator.</summary>
    /// <param name="selectionPrompt">Asks which connection a connector's references should use.</param>
    /// <param name="creationFlow">Creates a missing connection through the browser.</param>
    public ConnectionBindingCoordinator(IConnectionSelectionPrompt selectionPrompt, ConnectionCreationFlow creationFlow)
    {
        _selectionPrompt = selectionPrompt ?? throw new ArgumentNullException(nameof(selectionPrompt));
        _creationFlow = creationFlow ?? throw new ArgumentNullException(nameof(creationFlow));
    }

    /// <summary>The references on a connector that can still be bound.</summary>
    /// <param name="plan">Every reference the workspace has.</param>
    /// <param name="group">The connector's references.</param>
    /// <returns>Indexes into the plan, in plan order.</returns>
    public static int[] Actionable(IReadOnlyList<ConnectionPlanItem> plan, ConnectorGroup group)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(group);

        return [.. group.References.Where(index => ConnectionReadinessPlanner.IsActionable(plan[index]))];
    }

    /// <summary>
    /// Offers one connector's connections and points every reference it serves at the answer.
    /// </summary>
    /// <param name="plan">Every reference the workspace has.</param>
    /// <param name="group">The connector being resolved.</param>
    /// <param name="environment">The environment a connection would be created in.</param>
    /// <param name="resolution">The connector already resolved for this group, when one was, so a proven-servable connector is not waited for again.</param>
    /// <param name="openBrowser">Whether creating one may launch a browser.</param>
    /// <param name="diagnostics">Receives an explanation for a connection that was not created.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>A task that completes when the connector is resolved or the operator goes back.</returns>
    public async Task ResolveConnectorAsync(List<ConnectionPlanItem> plan, ConnectorGroup group, CopilotEnvironmentContext environment, ConnectorResolution? resolution, bool openBrowser, List<string> diagnostics, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var targets = Actionable(plan, group);

        if (targets.Length == 0)
        {
            return;
        }

        var choice = await _selectionPrompt.SelectAsync(
            new ConnectionSelectionRequest
            {
                ConnectorId = group.ConnectorId,
                References = [.. targets.Select(index => new ConnectionSelectionReference
                {
                    LogicalName = plan[index].View.LogicalName,
                    Usages = [.. plan[index].View.Usages.Select(usage => usage.FilePath)],
                })],
                Options = ConnectionSelectionScope.Options(plan, group),
            },
            cancellationToken).ConfigureAwait(false);

        if (choice.Kind == ConnectionSelectionKind.CreateNew)
        {
            await CreateForConnectorAsync(plan, targets, environment, resolution, openBrowser, diagnostics, cancellationToken).ConfigureAwait(false);
        }
        else if (choice.Kind == ConnectionSelectionKind.Existing && !string.IsNullOrWhiteSpace(choice.ConnectionId))
        {
            BindGroup(plan, targets, ConnectionReadinessPlanner.Bound, choice.ConnectionId);
        }
    }

    /// <summary>Walks the operator through a run's connectors, one at a time, until they stop, settling after each decision.</summary>
    /// <param name="plan">Every reference the run knows.</param>
    /// <param name="groups">The run's connectors, already ordered.</param>
    /// <param name="environment">The environment a connection would be created in.</param>
    /// <param name="openBrowser">Whether creating one may launch a browser.</param>
    /// <param name="diagnostics">Receives an explanation for a connection that was not created.</param>
    /// <param name="settle">Applies what the operator has decided so far.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>A task that completes when the operator stops.</returns>
    public async Task ResolveConnectorsAsync(List<ConnectionPlanItem> plan, IReadOnlyList<ConnectorGroup> groups, CopilotEnvironmentContext environment, bool openBrowser, List<string> diagnostics, Func<Task> settle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(settle);

        if (!groups.Any(group => ConnectionSelectionScope.IsSelectable(plan, group)))
        {
            return;
        }

        if (groups.Count == 1)
        {
            await ResolveConnectorAsync(plan, groups[0], environment, null, openBrowser, diagnostics, cancellationToken).ConfigureAwait(false);
            await settle().ConfigureAwait(false);

            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var chosen = await _selectionPrompt.ChooseConnectorsAsync(ConnectionSelectionScope.Menu(plan, groups), cancellationToken).ConfigureAwait(false);

            if (chosen.Count == 0)
            {
                return;
            }

            foreach (var position in chosen.Where(position => position >= 0 && position < groups.Count))
            {
                await ResolveConnectorAsync(plan, groups[position], environment, null, openBrowser, diagnostics, cancellationToken).ConfigureAwait(false);
                await settle().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Creates one connection and binds every reference on that connector to it.</summary>
    /// <param name="plan">Every reference the workspace has.</param>
    /// <param name="targets">The references the created connection would serve.</param>
    /// <param name="environment">The environment the connection is created in.</param>
    /// <param name="resolution">The connector already resolved for these references, when one was, so a proven-servable connector is not waited for again.</param>
    /// <param name="openBrowser">Whether to launch a browser, or only print the link.</param>
    /// <param name="diagnostics">Receives an explanation for a connection that was not created.</param>
    /// <param name="cancellationToken">Cancels the creation.</param>
    /// <returns>A task that completes when the connection is created or refused.</returns>
    public async Task CreateForConnectorAsync(List<ConnectionPlanItem> plan, IReadOnlyList<int> targets, CopilotEnvironmentContext environment, ConnectorResolution? resolution, bool openBrowser, List<string> diagnostics, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (targets.Count == 0)
        {
            return;
        }

        var item = plan[targets[0]];
        var outcome = await CreateAsync(environment, item.View.ConnectorId, resolution, openBrowser, cancellationToken).ConfigureAwait(false);

        if (outcome.Status == ConnectionConsentStatus.Created && !string.IsNullOrWhiteSpace(outcome.ConnectionId))
        {
            BindGroup(plan, targets, ConnectionReadinessPlanner.Created, outcome.ConnectionId);

            return;
        }

        diagnostics.Add($"{item.View.LogicalName}: {outcome.ErrorMessage ?? "no connection was created."}");
    }

    private Task<ConnectionConsentResult> CreateAsync(CopilotEnvironmentContext environment, string connectorId, ConnectorResolution? resolution, bool openBrowser, CancellationToken cancellationToken) =>
        Describes(resolution, connectorId)
            ? _creationFlow.CreateAsync(environment, resolution!, openBrowser, cancellationToken)
            : _creationFlow.CreateAsync(environment, connectorId, openBrowser, cancellationToken);

    private static bool Describes(ConnectorResolution? resolution, string connectorId) =>
        resolution is not null
        && ConnectionConsentLink.NormalizeOptionalResourceId(resolution.Connector.InternalId).Equals(ConnectionConsentLink.NormalizeOptionalResourceId(connectorId), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Binds every reference a connector serves to the connection the operator named.
    ///
    /// The connector is the unit here for the same reason it is at the prompt: the references on one
    /// share a candidate list, so naming a connection for one of them names it for all.
    /// </summary>
    /// <param name="plan">Every reference the workspace has.</param>
    /// <param name="group">The connector whose references are bound.</param>
    /// <param name="connectionId">The connection the operator named.</param>
    /// <exception cref="UsageException">No reference on the connector can be bound.</exception>
    public static void BindExplicitly(List<ConnectionPlanItem> plan, ConnectorGroup group, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(group);

        var targets = Actionable(plan, group);

        if (targets.Length == 0)
        {
            throw new UsageException($"No reference on '{group.ConnectorId}' can be bound: the service named no connector for it.");
        }

        BindGroup(plan, targets, ConnectionReadinessPlanner.Bound, ConnectionReadinessPlanner.Select(plan[targets[0]], connectionId).ConnectionId!);
    }

    /// <summary>Points every reference on a connector at the one connection chosen for it.</summary>
    /// <param name="plan">Every reference the workspace has.</param>
    /// <param name="targets">The references to point.</param>
    /// <param name="state">The state the references reach.</param>
    /// <param name="connectionId">The connection they should use.</param>
    public static void BindGroup(List<ConnectionPlanItem> plan, IReadOnlyList<int> targets, string state, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(targets);

        foreach (var index in targets)
        {
            plan[index] = ConnectionReadinessPlanner.Rebind(plan[index], state, connectionId);
        }
    }
}
