using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Tries each way of creating a connection in turn, falling back to the next when one does not apply to the connector.</summary>
public sealed class ConnectionConsentLadder : IConnectionConsentFlow, IDisposable
{
    private readonly IReadOnlyList<IConnectionConsentFlow> _tiers;

    /// <summary>Creates the ladder.</summary>
    /// <param name="tiers">The ways to create a connection, most preferred first.</param>
    public ConnectionConsentLadder(IReadOnlyList<IConnectionConsentFlow> tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);

        if (tiers.Count == 0)
        {
            throw new ArgumentException("At least one way of creating a connection is required.", nameof(tiers));
        }

        _tiers = tiers;
    }

    /// <summary>Disposes every tier that holds a transport.</summary>
    public void Dispose()
    {
        foreach (var tier in _tiers.OfType<IDisposable>())
        {
            tier.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<ConnectionConsentResult> CreateConnectionAsync(CopilotEnvironmentContext environment, string connectorId, bool openBrowser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        for (var index = 0; index < _tiers.Count - 1; index++)
        {
            try
            {
                return await _tiers[index].CreateConnectionAsync(environment, connectorId, openBrowser, cancellationToken).ConfigureAwait(false);
            }
            catch (SilentConnectionUnavailableException)
            {
            }
        }

        return await _tiers[^1].CreateConnectionAsync(environment, connectorId, openBrowser, cancellationToken).ConfigureAwait(false);
    }
}
