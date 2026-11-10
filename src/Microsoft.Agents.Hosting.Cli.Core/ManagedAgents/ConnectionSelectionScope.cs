using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>One connector and the references in this run that target it.</summary>
/// <param name="ConnectorId">The connector's internal id.</param>
/// <param name="References">Indexes into the plan, in plan order.</param>
public sealed record ConnectorGroup(string ConnectorId, IReadOnlyList<int> References);

/// <summary>
/// Narrows a plan to the references a run acts on, and groups them by connector.
/// </summary>
public static class ConnectionSelectionScope
{
    /// <summary>Shown for a connector whose references are all bound to a connection.</summary>
    public const string Connected = "connected";

    /// <summary>Shown for a connector with a reference no connection is bound to.</summary>
    public const string NotConnected = "not connected";

    /// <summary>Chooses which plan entries a request addresses.</summary>
    /// <param name="plan">Every reference the workspace has.</param>
    /// <param name="request">The command input.</param>
    /// <param name="workspaceDirectory">The workspace whose references are selected.</param>
    /// <returns>Indexes into <paramref name="plan"/>, in order.</returns>
    /// <exception cref="ManagedAgentException">The named connection reference is not declared.</exception>
    /// <exception cref="UsageException">No selected reference targets the named connector.</exception>
    public static IReadOnlyList<int> Apply(IReadOnlyList<ConnectionPlanItem> plan, AgentDeploymentConnectionRequest request, string workspaceDirectory)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);

        var selected = Enumerable.Range(0, plan.Count).ToList();

        if (!string.IsNullOrWhiteSpace(request.ConnectionReference))
        {
            selected = [.. selected.Where(index => plan[index].View.LogicalName.Equals(request.ConnectionReference.Trim(), StringComparison.OrdinalIgnoreCase))];

            if (selected.Count == 0)
            {
                throw ManagedAgentException.ConnectionReferenceNotDeclared(
                    request.ConnectionReference.Trim(),
                    workspaceDirectory,
                    [.. plan.Select(item => item.View.LogicalName)],
                    ConnectionCommandHints.ListConnections(request));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Connector))
        {
            var connector = ConnectionConsentLink.NormalizeResourceId(request.Connector.Trim());
            selected = [.. selected.Where(index => Connector(plan[index]).Equals(connector, StringComparison.OrdinalIgnoreCase))];

            if (selected.Count == 0)
            {
                throw new UsageException($"No connection reference targets connector '{connector}'.");
            }
        }

        return selected;
    }

    /// <summary>Requires the selectors to have narrowed to exactly one connector.</summary>
    /// <param name="plan">Every reference the workspace has.</param>
    /// <param name="selected">The selected plan indexes.</param>
    /// <param name="request">The command input.</param>
    /// <returns>The single selected connector, with every reference it serves.</returns>
    /// <exception cref="UsageException">The selectors matched no connector or several.</exception>
    public static ConnectorGroup RequireExactlyOneConnector(IReadOnlyList<ConnectionPlanItem> plan, IReadOnlyList<int> selected, AgentDeploymentConnectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(request);

        var connectors = Group(plan, selected);

        if (connectors.Count == 1)
        {
            return connectors[0];
        }

        throw new UsageException(connectors.Count == 0
            ? "--connection-id needs exactly one connector, and the selectors matched none."
            : $"--connection-id needs exactly one connector, and the selectors matched {connectors.Count}: {string.Join(", ", connectors.Select(connector => connector.ConnectorId))}. Add --connector to choose one.");
    }

    /// <summary>Groups the run's references by the connector they target.</summary>
    /// <param name="plan">Every reference the workspace has.</param>
    /// <param name="selected">The references this run was scoped to.</param>
    /// <param name="byReference">Whether each reference forms its own group instead of sharing its connector's.</param>
    /// <returns>One group per connector, connectors needing a decision first.</returns>
    public static IReadOnlyList<ConnectorGroup> Group(IReadOnlyList<ConnectionPlanItem> plan, IReadOnlyList<int> selected, bool byReference = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selected);

        return [.. Partition(plan, selected, byReference)
            .OrderBy(group => Rank(plan, group))
            .ThenBy(group => group.ConnectorId, StringComparer.Ordinal)
            .ThenBy(group => Name(plan, group), StringComparer.Ordinal)];
    }

    private static IEnumerable<ConnectorGroup> Partition(IReadOnlyList<ConnectionPlanItem> plan, IReadOnlyList<int> selected, bool byReference) =>
        byReference
            ? selected.Select(index => new ConnectorGroup(Connector(plan[index]), [index]))
            : selected
                .GroupBy(index => Connector(plan[index]), StringComparer.OrdinalIgnoreCase)
                .Select(group => new ConnectorGroup(group.Key, [.. group]));

    private static string Name(IReadOnlyList<ConnectionPlanItem> plan, ConnectorGroup group) =>
        group.References.Count == 0 ? string.Empty : plan[group.References[0]].View.LogicalName;

    /// <summary>Whether a connector still needs the operator to decide something.</summary>
    /// <param name="plan">Every reference the workspace has.</param>
    /// <param name="group">The connector's references.</param>
    /// <returns><see langword="true"/> when at least one reference is unresolved.</returns>
    public static bool NeedsAttention(IReadOnlyList<ConnectionPlanItem> plan, ConnectorGroup group)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(group);

        return group.References.Any(index => NeedsDecision(plan[index]));
    }

    /// <summary>Whether the operator can act on a connector at all.</summary>
    /// <param name="plan">Every reference the workspace has.</param>
    /// <param name="group">The connector's references.</param>
    /// <returns><see langword="true"/> when at least one reference can be bound.</returns>
    public static bool IsSelectable(IReadOnlyList<ConnectionPlanItem> plan, ConnectorGroup group)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(group);

        return group.References.Any(index => ConnectionReadinessPlanner.IsActionable(plan[index]));
    }

    /// <summary>Builds the menu the operator picks a connector from.</summary>
    /// <param name="plan">Every reference the workspace has.</param>
    /// <param name="groups">The connector groups, already ordered.</param>
    /// <returns>The menu.</returns>
    public static ConnectorMenu Menu(IReadOnlyList<ConnectionPlanItem> plan, IReadOnlyList<ConnectorGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(groups);

        return new ConnectorMenu
        {
            Connectors = [.. groups.Select((group, position) => new ConnectorChoice
            {
                Number = position + 1,
                ConnectorId = group.ConnectorId,
                Status = Status(plan, group),
                References = [.. group.References.Select(index => plan[index].View.LogicalName)],
                Connections = BoundConnections(plan, group),
                NeedsAttention = NeedsAttention(plan, group),
                Selectable = IsSelectable(plan, group),
            })],
        };
    }

    /// <summary>Describes what a connector is waiting on.</summary>
    /// <param name="plan">Every reference the workspace has.</param>
    /// <param name="group">The connector's references.</param>
    /// <returns>The status shown beside the connector.</returns>
    public static string Status(IReadOnlyList<ConnectionPlanItem> plan, ConnectorGroup group)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(group);

        var bound = group.References.Count(index => ConnectionReadinessPlanner.IsResolved(plan[index]));
        var open = group.References.Count - bound;

        if (open == 0)
        {
            return Connected;
        }

        return bound == 0 ? NotConnected : $"{Connected} ({bound}), {NotConnected} ({open})";
    }

    /// <summary>
    /// Describes one reference's state in the same words a connector's status uses.
    /// </summary>
    /// <param name="state">The state the planner recorded.</param>
    /// <returns>The status shown beside the reference.</returns>
    public static string DescribeState(string state) => state switch
    {
        ConnectionReadinessPlanner.Bound or ConnectionReadinessPlanner.AlreadyBound or ConnectionReadinessPlanner.Created => Connected,
        _ => NotConnected,
    };

    /// <summary>The connections that can satisfy every reference on a connector.</summary>
    /// <param name="plan">Every reference the workspace has.</param>
    /// <param name="group">The connector's references.</param>
    /// <returns>The candidates, which are identical for every reference on the connector.</returns>
    public static IReadOnlyList<CopilotConnectionCandidate> Candidates(IReadOnlyList<ConnectionPlanItem> plan, ConnectorGroup group)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(group);

        var actionable = group.References.FirstOrDefault(index => ConnectionReadinessPlanner.IsActionable(plan[index]), -1);

        return actionable < 0 ? [] : plan[actionable].ConnectedCandidates;
    }

    /// <summary>
    /// The connections a connector's references are bound to, as this run has decided them.
    /// </summary>
    /// <param name="plan">Every reference the workspace has.</param>
    /// <param name="group">The connector's references.</param>
    /// <returns>The distinct connections in use, in a stable order.</returns>
    public static IReadOnlyList<string> BoundConnections(IReadOnlyList<ConnectionPlanItem> plan, ConnectorGroup group)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(group);

        return DistinctConnections(plan, group.References);
    }

    private static IReadOnlyList<string> ConnectorConnections(IReadOnlyList<ConnectionPlanItem> plan, ConnectorGroup group) =>
        DistinctConnections(
            plan,
            Enumerable.Range(0, plan.Count).Where(index => Connector(plan[index]).Equals(group.ConnectorId, StringComparison.OrdinalIgnoreCase)));

    private static IReadOnlyList<string> DistinctConnections(IReadOnlyList<ConnectionPlanItem> plan, IEnumerable<int> references) =>
        [.. references
            .Select(index => plan[index].ConnectionId)
            .Where(connectionId => !string.IsNullOrWhiteSpace(connectionId))
            .Select(connectionId => connectionId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(connectionId => connectionId, StringComparer.Ordinal)];

    /// <summary>Numbers the connections the operator can point a connector's references at.</summary>
    /// <param name="plan">Every reference the workspace has.</param>
    /// <param name="group">The connector's references.</param>
    /// <returns>The connections on offer, the ones already in use first.</returns>
    public static IReadOnlyList<ConnectionSelectionOption> Options(IReadOnlyList<ConnectionPlanItem> plan, ConnectorGroup group)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(group);

        var inUse = BoundConnections(plan, group).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. Offered(Candidates(plan, group), AllCandidates(plan, group), ConnectorConnections(plan, group))
            .OrderByDescending(candidate => inUse.Contains(candidate.ConnectionId))
            .ThenBy(candidate => candidate.ConnectionId, StringComparer.Ordinal)
            .Select((candidate, position) => new ConnectionSelectionOption
            {
                Number = position + 1,
                ConnectionId = candidate.ConnectionId,
                Owner = candidate.Owner,
                IsCurrent = inUse.Contains(candidate.ConnectionId),
            })];
    }

    /// <summary>
    /// The connections on offer, including any this run put into use that is not offered as connected.
    ///
    /// A connection the catalog has not yet reported as connected — one just created, most often — is
    /// still what the references use, so it has to appear or the operator is invited to create a
    /// second one. Its record is taken from the catalog where there is one, so the owner shown is the
    /// service's and not a placeholder.
    /// </summary>
    /// <param name="connected">The candidates the catalog reports as connected.</param>
    /// <param name="known">Every candidate the catalog reports, whatever its status.</param>
    /// <param name="inUse">The connections the run has put into use.</param>
    /// <returns>Every connection the operator may choose between.</returns>
    private static IEnumerable<CopilotConnectionCandidate> Offered(IReadOnlyList<CopilotConnectionCandidate> connected, IReadOnlyList<CopilotConnectionCandidate> known, IReadOnlyCollection<string> inUse) =>
        connected.Concat(inUse
            .Where(connectionId => !connected.Any(candidate => candidate.ConnectionId.Equals(connectionId, StringComparison.OrdinalIgnoreCase)))
            .Select(connectionId => known.FirstOrDefault(candidate => candidate.ConnectionId.Equals(connectionId, StringComparison.OrdinalIgnoreCase))
                ?? new CopilotConnectionCandidate { ConnectionId = connectionId, DisplayName = connectionId, Status = "Connected" }));

    /// <summary>Every connection the catalog reports for a connector, whatever its status.</summary>
    /// <param name="plan">Every reference the workspace has.</param>
    /// <param name="group">The connector's references.</param>
    /// <returns>The candidates as the catalog returned them.</returns>
    private static IReadOnlyList<CopilotConnectionCandidate> AllCandidates(IReadOnlyList<ConnectionPlanItem> plan, ConnectorGroup group)
    {
        var actionable = group.References.FirstOrDefault(index => ConnectionReadinessPlanner.IsActionable(plan[index]), -1);

        return actionable < 0 ? [] : plan[actionable].View.Candidates;
    }

    /// <summary>Whether a reference still needs the operator to decide something.</summary>
    /// <param name="item">The decision.</param>
    /// <returns><see langword="true"/> when it is unresolved.</returns>
    public static bool NeedsDecision(ConnectionPlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.State is ConnectionReadinessPlanner.NoCandidate or ConnectionReadinessPlanner.Undeclared or ConnectionReadinessPlanner.Unbound;
    }

    private static string Connector(ConnectionPlanItem item) => ConnectionConsentLink.NormalizeOptionalResourceId(item.View.ConnectorId);

    private static int Rank(IReadOnlyList<ConnectionPlanItem> plan, ConnectorGroup group) =>
        !NeedsAttention(plan, group) ? 2 : Candidates(plan, group).Count > 0 ? 0 : 1;
}
