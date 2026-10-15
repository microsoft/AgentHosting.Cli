using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Creates one connection for a connector: waits for the connector to be servable, then runs the
/// browser consent experience.
/// </summary>
public sealed class ConnectionCreationFlow
{
    private readonly ConnectorReadinessWait _connectorReadiness;
    private readonly IConnectionConsentFlow _consentFlow;

    /// <summary>Creates the flow.</summary>
    /// <param name="connectorReadiness">Waits for a freshly pushed custom connector to become servable.</param>
    /// <param name="consentFlow">Runs the browser consent experience.</param>
    public ConnectionCreationFlow(ConnectorReadinessWait connectorReadiness, IConnectionConsentFlow consentFlow)
    {
        _connectorReadiness = connectorReadiness ?? throw new ArgumentNullException(nameof(connectorReadiness));
        _consentFlow = consentFlow ?? throw new ArgumentNullException(nameof(consentFlow));
    }

    /// <summary>Creates one connection, waiting for the connector only when resolving it did not already prove it servable.</summary>
    /// <param name="environment">The environment the connection is created in.</param>
    /// <param name="resolution">The connector, and any connections resolving it already read.</param>
    /// <param name="openBrowser">Whether to launch a browser, or only print the link to open by hand.</param>
    /// <param name="cancellationToken">Cancels the flow.</param>
    /// <returns>What the consent experience reported.</returns>
    public Task<ConnectionConsentResult> CreateAsync(CopilotEnvironmentContext environment, ConnectorResolution resolution, bool openBrowser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        return CreateCoreAsync(environment, resolution.Connector.InternalId, openBrowser, resolution.Connections is not null, cancellationToken);
    }

    /// <summary>Creates one connection, after waiting for its connector to be servable.</summary>
    /// <param name="environment">The environment the connection is created in.</param>
    /// <param name="connectorId">The connector to create a connection for.</param>
    /// <param name="openBrowser">Whether to launch a browser, or only print the link to open by hand.</param>
    /// <param name="cancellationToken">Cancels the flow.</param>
    /// <returns>What the consent experience reported.</returns>
    public Task<ConnectionConsentResult> CreateAsync(CopilotEnvironmentContext environment, string connectorId, bool openBrowser, CancellationToken cancellationToken) =>
        CreateCoreAsync(environment, connectorId, openBrowser, connectorAlreadyServable: false, cancellationToken);

    private async Task<ConnectionConsentResult> CreateCoreAsync(CopilotEnvironmentContext environment, string connectorId, bool openBrowser, bool connectorAlreadyServable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!connectorAlreadyServable)
        {
            await _connectorReadiness.WaitAsync(environment, connectorId, cancellationToken).ConfigureAwait(false);
        }

        return await _consentFlow.CreateConnectionAsync(environment, connectorId, openBrowser, cancellationToken).ConfigureAwait(false);
    }
}
