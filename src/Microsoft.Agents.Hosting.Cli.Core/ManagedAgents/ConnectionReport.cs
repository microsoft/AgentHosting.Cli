using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Turns a connection plan into the shape both connection verbs report.
/// </summary>
public static class ConnectionReport
{
    /// <summary>
    /// Projects every reference onto the result's connection rows.
    /// </summary>
    /// <param name="plan">The decision for each reference.</param>
    /// <param name="cluster">The cluster category the environment belongs to.</param>
    /// <param name="environmentId">The environment a connection would be created in.</param>
    /// <returns>One row per reference, grouped by connector.</returns>
    public static IReadOnlyList<AgentDeploymentConnectionItem> Connections(IReadOnlyList<ConnectionPlanItem> plan, CoreServicesClusterCategory cluster, string environmentId)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var statuses = ConnectorStatuses(plan);

        return [.. plan
            .Select(item => ConnectionReadinessPlanner.ToResultItem(item, ConsentLinkFor(item, cluster, environmentId), statuses[ConnectionConsentLink.NormalizeOptionalResourceId(item.View.ConnectorId)]))
            .OrderBy(item => item.ConnectorId, StringComparer.Ordinal)
            .ThenBy(item => item.ConnectionReference, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Works out what each connector is waiting on, once, from the same rule the menu asks.
    /// </summary>
    /// <param name="plan">The decision for each reference.</param>
    /// <returns>Connector to the status it is in.</returns>
    private static Dictionary<string, string> ConnectorStatuses(IReadOnlyList<ConnectionPlanItem> plan) =>
        ConnectionSelectionScope.Group(plan, [.. Enumerable.Range(0, plan.Count)])
            .ToDictionary(group => group.ConnectorId, group => ConnectionSelectionScope.Status(plan, group), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names the connections a reference could use where none of them is provably the operator's.
    ///
    /// A connection runs as whoever created it, so one belonging to somebody else is never bound
    /// without being asked for. Naming the ids is what makes that recoverable from a script, where
    /// there is nobody to answer the prompt and <c>--connection-id</c> is the only way through.
    /// </summary>
    /// <param name="plan">The decisions this run acted on.</param>
    /// <returns>One diagnostic per reference left for the operator to decide.</returns>
    public static IReadOnlyList<string> UnownedCandidates(IEnumerable<ConnectionPlanItem> plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return
        [
            .. plan
                .Where(item => item.State == ConnectionReadinessPlanner.Unbound && item.Candidates.Count > 0)
                .Select(item => $"{item.View.LogicalName}: no connection belongs to you, so none was bound. Choose one, or pass --connection-reference {item.View.LogicalName} --connection-id with one of: {string.Join(", ", item.Candidates.Select(candidate => candidate.ConnectionId))}."),
        ];
    }

    /// <summary>
    /// Explains that a connection was left uncreated because the existing ones could not be listed.
    /// </summary>
    /// <param name="logicalName">The reference left unbound.</param>
    /// <param name="bindCommand">The command that binds it to a connection the operator names.</param>
    /// <returns>The diagnostic text.</returns>
    public static string UnreadableCatalog(string logicalName, string bindCommand) =>
        $"{logicalName}: the connections for its connector could not be listed, so none was created — creating one now could duplicate a connection the environment already holds. Retry when the catalog can be read, or run `{bindCommand} --connection-id <id>` to bind one you know.";

    /// <summary>Whether the deployment is ready overall.</summary>
    /// <param name="connectionsReady">Whether every reference is bound.</param>
    /// <param name="workflowsReady">Whether every workflow is observed as running.</param>
    /// <returns><see langword="true"/> only when both halves are ready.</returns>
    public static bool IsReady(bool connectionsReady, bool workflowsReady) => connectionsReady && workflowsReady;

    /// <summary>
    /// Points at the maker portal for any reference no connection is bound to.
    ///
    /// A reference whose existing connections could not be listed is offered the link too: the portal
    /// shows what already exists, so sending the operator there cannot create the duplicate that
    /// binding blind would.
    /// </summary>
    private static Uri? ConsentLinkFor(ConnectionPlanItem item, CoreServicesClusterCategory cluster, string environmentId) =>
        item.State == ConnectionReadinessPlanner.NoCandidate && !string.IsNullOrWhiteSpace(item.View.ConnectorId)
            ? ConnectionConsentLink.BuildMakerLink(cluster, environmentId, item.View.ConnectorId)
            : null;
}
