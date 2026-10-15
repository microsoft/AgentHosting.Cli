using Microsoft.Agents.Hosting.Cli.Core.Environments;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Builds the browser consent link that creates a connection for a connector.
/// </summary>
public static class ConnectionConsentLink
{
    /// <summary>
    /// How long any consent flow waits for the operator before giving up and removing what it
    /// created. Matches the lifetime the service stamps on a consent link, so the wait ends at
    /// roughly the moment the link stops being usable.
    /// </summary>
    public static readonly TimeSpan DefaultConsentTimeout = TimeSpan.FromMinutes(8);

    /// <summary>Tells an operator how long a flow will wait, and how to stop it early.</summary>
    /// <param name="timeout">How long the flow will wait.</param>
    /// <param name="canInterrupt">Whether the operator can end the wait from the keyboard.</param>
    /// <returns>The notice both consent flows announce, so neither can word it differently.</returns>
    public static string WaitNotice(TimeSpan timeout, bool canInterrupt) =>
        canInterrupt
            ? $"Waiting up to {timeout.TotalMinutes:0} minutes, or press Esc to stop waiting."
            : $"Waiting up to {timeout.TotalMinutes:0} minutes.";

    /// <summary>The player host serving the connection consent experience.</summary>
    /// <param name="category">The cluster category the environment belongs to.</param>
    /// <returns>The host serving the connection consent experience.</returns>
    public static string HostFor(CoreServicesClusterCategory category) =>
        CoreServicesCluster.PlayerHost(category);

    /// <summary>Builds a consent link.</summary>
    /// <param name="category">The cluster category the environment belongs to.</param>
    /// <param name="environmentId">The environment the connection is created in.</param>
    /// <param name="connectorId">The connector, as an internal id or a full provider path.</param>
    /// <param name="callbackUrl">Where the browser reports the result, or <see langword="null"/> when nothing is listening.</param>
    /// <param name="nonce">Correlates the callback with this request.</param>
    /// <returns>The consent link.</returns>
    public static Uri Build(CoreServicesClusterCategory category, string environmentId, string connectorId, Uri? callbackUrl, string? nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        var managedAppsPlayer = CoreServicesCluster.ManagedAppsPlayerHost(category);
        var query = new List<string>();

        if (managedAppsPlayer is not null)
        {
            query.Add($"ms_envId={Uri.EscapeDataString(environmentId)}");
        }

        query.Add($"connector={Uri.EscapeDataString(NormalizeResourceId(connectorId))}");

        if (callbackUrl is not null)
        {
            query.Add($"callbackUrl={Uri.EscapeDataString(callbackUrl.ToString())}");
        }

        if (!string.IsNullOrWhiteSpace(nonce))
        {
            query.Add($"nonce={Uri.EscapeDataString(nonce)}");
        }

        query.Add("v=1");

        return managedAppsPlayer is null
            ? new Uri($"https://{HostFor(category)}/appframework/e/{Uri.EscapeDataString(environmentId)}/connections/new?{string.Join("&", query)}")
            : new Uri($"https://{managedAppsPlayer}/connection/new?{string.Join("&", query)}");
    }

    /// <summary>The maker portal host for each cluster category.</summary>
    /// <param name="category">The cluster category the environment belongs to.</param>
    /// <returns>The host serving the maker portal.</returns>
    public static string MakerHostFor(CoreServicesClusterCategory category) =>
        CoreServicesCluster.MakerHost(category);

    /// <summary>
    /// Builds the maker portal link that creates a connection by hand.
    /// </summary>
    /// <param name="category">The cluster category the environment belongs to.</param>
    /// <param name="environmentId">The environment the connection is created in.</param>
    /// <param name="connectorId">The connector, as an internal id or a full provider path.</param>
    /// <returns>The maker portal link.</returns>
    public static Uri BuildMakerLink(CoreServicesClusterCategory category, string environmentId, string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        return new Uri($"https://{MakerHostFor(category)}/environments/{Uri.EscapeDataString(environmentId)}/connections/available?apiName={Uri.EscapeDataString(NormalizeResourceId(connectorId))}");
    }

    /// <summary>
    /// Builds the maker portal link that lists an environment's connections.
    /// </summary>
    /// <param name="category">The cluster category the environment belongs to.</param>
    /// <param name="environmentId">The environment whose connections are listed.</param>
    /// <returns>The maker portal connections list.</returns>
    public static Uri BuildConnectionsListLink(CoreServicesClusterCategory category, string environmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);

        return new Uri($"https://{MakerHostFor(category)}/environments/{Uri.EscapeDataString(environmentId)}/connections");
    }

    /// <summary>The Power Apps API host for each cluster category.</summary>
    /// <param name="category">The cluster category the environment belongs to.</param>
    /// <returns>The host serving the Power Apps connections API.</returns>
    public static string ApiHostFor(CoreServicesClusterCategory category) =>
        CoreServicesCluster.ApiHost(category);

    /// <summary>Reduces a Power Apps resource path to its internal id.</summary>
    /// <param name="resourceId">The connector or connection, in either form.</param>
    /// <returns>The resource's internal id.</returns>
    public static string NormalizeResourceId(string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);

        var trimmed = resourceId.Trim().TrimEnd('/');
        var lastSeparator = trimmed.LastIndexOf('/');

        return lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;
    }

    /// <summary>
    /// Reduces a resource path to its internal id, or to nothing when the service named none.
    ///
    /// Reporting and grouping have to survive a reference the service describes without a connector,
    /// which an undeclared one can be. Only the paths that must name a connector to do their work —
    /// building a consent link, say — use the strict form.
    /// </summary>
    /// <param name="resourceId">The connector or connection, in either form, possibly absent.</param>
    /// <returns>The resource's internal id, or an empty string.</returns>
    public static string NormalizeOptionalResourceId(string? resourceId) =>
        string.IsNullOrWhiteSpace(resourceId) ? string.Empty : NormalizeResourceId(resourceId);
}
