using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>One page of an environment's connector catalog.</summary>
/// <param name="Connectors">The rows the request asked for, already ordered and limited.</param>
/// <param name="Limit">The limit that was applied.</param>
/// <param name="Truncated">Whether more rows matched than were returned.</param>
public sealed record ConnectorCatalogPage(IReadOnlyList<CopilotConnectorInfo> Connectors, int Limit, bool Truncated);

/// <summary>
/// Narrows an environment's connector catalog to the rows a request asked for, and finds the one
/// connector an internal id names.
/// </summary>
public static class ConnectorCatalogQuery
{
    /// <summary>The rows returned when a request names no limit.</summary>
    public const int DefaultLimit = 50;

    private const int SuggestionCount = 5;

    private const int TruncationProbe = 1;

    /// <summary>Filters, orders and limits the catalog.</summary>
    /// <param name="connectors">Every connector the environment serves.</param>
    /// <param name="filter">A substring matched against display name, internal id and publisher.</param>
    /// <param name="connector">An internal id matched exactly.</param>
    /// <param name="limit">The most rows to return, or <see langword="null"/> for the default.</param>
    /// <param name="inUse">The connectors a declared reference already targets, which sort first.</param>
    /// <returns>The page the request asked for.</returns>
    /// <exception cref="UsageException">The limit is not a positive integer.</exception>
    public static ConnectorCatalogPage Query(IReadOnlyList<CopilotConnectorInfo> connectors, string? filter, string? connector, int? limit, IReadOnlyCollection<string> inUse)
    {
        ArgumentNullException.ThrowIfNull(connectors);
        ArgumentNullException.ThrowIfNull(inUse);

        if (limit is <= 0)
        {
            throw new UsageException("--limit must be a positive integer.");
        }

        var applied = limit ?? DefaultLimit;

        var matched = connectors
            .Where(candidate => MatchesConnector(candidate, connector))
            .Where(candidate => MatchesFilter(candidate, filter))
            .OrderByDescending(candidate => inUse.Contains(ConnectionConsentLink.NormalizeResourceId(candidate.InternalId)))
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.InternalId, StringComparer.Ordinal)
            .Take(Probe(applied))
            .ToArray();

        return new ConnectorCatalogPage([.. matched.Take(applied)], applied, matched.Length > applied);
    }

    private static int Probe(int limit) => limit > int.MaxValue - TruncationProbe ? int.MaxValue : limit + TruncationProbe;

    /// <summary>Finds the one connector an internal id names.</summary>
    /// <param name="connectors">Every connector the environment serves.</param>
    /// <param name="connectorId">The connector, as an internal id or a full provider path.</param>
    /// <returns>The connector, or <see langword="null"/> when the environment serves none by that id.</returns>
    public static CopilotConnectorInfo? Find(IReadOnlyList<CopilotConnectorInfo> connectors, string connectorId)
    {
        ArgumentNullException.ThrowIfNull(connectors);

        return connectors.FirstOrDefault(candidate => Identifies(candidate, connectorId));
    }

    /// <summary>Reports that an environment serves no connector by the id a request named.</summary>
    /// <param name="connectors">Every connector the environment serves, for the suggestions.</param>
    /// <param name="connectorId">The connector the request named.</param>
    /// <param name="environmentId">The environment that was read.</param>
    /// <param name="listCommand">The command that lists the connectors on offer.</param>
    /// <returns>The rejection, naming the closest alternatives when there are any.</returns>
    public static UsageException NotServed(IReadOnlyList<CopilotConnectorInfo> connectors, string connectorId, string environmentId, string listCommand)
    {
        ArgumentNullException.ThrowIfNull(connectors);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        return new UsageException($"Environment '{environmentId}' serves no connector '{ConnectionConsentLink.NormalizeResourceId(connectorId)}'.{Suggestions(connectors, connectorId, listCommand)}");
    }

    private static string Suggestions(IReadOnlyList<CopilotConnectorInfo> connectors, string connectorId, string listCommand)
    {
        var target = ConnectionConsentLink.NormalizeResourceId(connectorId);

        string[] closest =
        [
            .. connectors
                .Where(candidate => Contains(candidate.InternalId, target) || Contains(candidate.DisplayName, target))
                .OrderBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Take(SuggestionCount)
                .Select(candidate => candidate.InternalId),
        ];

        return closest.Length == 0
            ? $" Run `{listCommand}` to find it."
            : $" Did you mean: {string.Join(", ", closest)}?";
    }

    private static bool Identifies(CopilotConnectorInfo candidate, string connectorId) =>
        !string.IsNullOrWhiteSpace(connectorId)
        && ConnectionConsentLink.NormalizeResourceId(candidate.InternalId).Equals(ConnectionConsentLink.NormalizeResourceId(connectorId), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesConnector(CopilotConnectorInfo candidate, string? connector) =>
        string.IsNullOrWhiteSpace(connector) || Identifies(candidate, connector);

    private static bool MatchesFilter(CopilotConnectorInfo candidate, string? filter) =>
        string.IsNullOrWhiteSpace(filter)
        || Contains(candidate.DisplayName, filter.Trim())
        || Contains(candidate.InternalId, filter.Trim())
        || Contains(candidate.Publisher, filter.Trim());

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrEmpty(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);
}
