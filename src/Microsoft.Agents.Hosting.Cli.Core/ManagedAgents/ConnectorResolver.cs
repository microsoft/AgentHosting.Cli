using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>One resolved connector, with the connections already read for it.</summary>
/// <param name="Connector">The connector the request named.</param>
/// <param name="Connections">
/// The connector's connections where resolving them already read them, and <see langword="null"/>
/// where the catalog answered without a lookup.
/// </param>
public sealed record ConnectorResolution(CopilotConnectorInfo Connector, IReadOnlyList<CopilotConnectionCandidate>? Connections);

/// <summary>
/// Resolves the connector a verb acts on.
/// </summary>
public sealed class ConnectorResolver
{
    private readonly IConnectorCatalogPort _connectors;

    /// <summary>Creates the resolver.</summary>
    /// <param name="connectors">Reads the environment's catalog, and one connector's connections.</param>
    public ConnectorResolver(IConnectorCatalogPort connectors)
    {
        _connectors = connectors ?? throw new ArgumentNullException(nameof(connectors));
    }

    /// <summary>Requires the environment to serve the connector a request named.</summary>
    /// <param name="environment">The environment whose connectors are read.</param>
    /// <param name="connectorId">The connector, as an internal id or a full provider path.</param>
    /// <param name="listCommand">The command that lists the connectors on offer.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>The connector, with any connections the resolution already read.</returns>
    /// <exception cref="UsageException">The service reports no such connector.</exception>
    public async Task<ConnectorResolution> RequireAsync(CopilotEnvironmentContext environment, string connectorId, string listCommand, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        var catalog = await _connectors.ListConnectorsAsync(environment, cancellationToken).ConfigureAwait(false);

        if (ConnectorCatalogQuery.Find(catalog, connectorId) is { } listed)
        {
            return new ConnectorResolution(listed, null);
        }

        var internalId = ConnectionConsentLink.NormalizeResourceId(connectorId);

        try
        {
            var connections = await _connectors.ListConnectionsAsync(environment, internalId, cancellationToken).ConfigureAwait(false);

            return new ConnectorResolution(new CopilotConnectorInfo { InternalId = internalId, DisplayName = internalId }, connections);
        }
        catch (ConnectorNotFoundException)
        {
            throw ConnectorCatalogQuery.NotServed(catalog, connectorId, environment.EnvironmentId, listCommand);
        }
    }

    /// <summary>The connector's connections, read once per run.</summary>
    /// <param name="environment">The environment whose connections are read.</param>
    /// <param name="resolution">The connector, and any connections resolving it already read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One record per connection, whatever its status.</returns>
    public async Task<IReadOnlyList<CopilotConnectionCandidate>> ConnectionsAsync(CopilotEnvironmentContext environment, ConnectorResolution resolution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(resolution);

        return resolution.Connections ?? await _connectors.ListConnectionsAsync(environment, resolution.Connector.InternalId, cancellationToken).ConfigureAwait(false);
    }
}
