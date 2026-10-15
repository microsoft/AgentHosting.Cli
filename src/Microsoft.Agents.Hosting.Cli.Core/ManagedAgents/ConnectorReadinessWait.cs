using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Waits for a custom connector to become servable before the consent experience is opened for it.
/// </summary>
public sealed partial class ConnectorReadinessWait
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitialPollDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumPollDelay = TimeSpan.FromSeconds(3);

    private readonly IConnectorCatalogPort _connectors;
    private readonly TimeSpan _timeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates the wait.</summary>
    /// <param name="connectors">Lists the connectors the environment serves.</param>
    /// <param name="timeout">How long to wait for one connector. Defaults to thirty seconds.</param>
    /// <param name="delay">Waits between polls. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public ConnectorReadinessWait(IConnectorCatalogPort connectors, TimeSpan? timeout = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _connectors = connectors ?? throw new ArgumentNullException(nameof(connectors));
        _timeout = timeout ?? DefaultTimeout;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Whether a connector id names a custom connector rather than a shared one.</summary>
    /// <param name="connectorId">The connector, as an internal id or a full provider path.</param>
    /// <returns><see langword="true"/> when the id carries the custom-connector suffix.</returns>
    public static bool IsCustomConnector(string connectorId) =>
        !string.IsNullOrWhiteSpace(connectorId) && CustomConnectorSuffix().IsMatch(ConnectionConsentLink.NormalizeResourceId(connectorId));

    /// <summary>Waits until a custom connector appears in the environment's catalog.</summary>
    /// <param name="environment">The environment whose catalog is polled.</param>
    /// <param name="connectorId">The connector to wait for.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the connector appears, or when the wait times out.</returns>
    public async Task WaitAsync(CopilotEnvironmentContext environment, string connectorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!IsCustomConnector(connectorId))
        {
            return;
        }

        var target = ConnectionConsentLink.NormalizeResourceId(connectorId);
        var deadline = DateTimeOffset.UtcNow + _timeout;
        var nextDelay = InitialPollDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (await IsServableAsync(environment, target, deadline - DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;

            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await _delay(nextDelay < remaining ? nextDelay : remaining, cancellationToken).ConfigureAwait(false);
            nextDelay = nextDelay * 1.5 < MaximumPollDelay ? nextDelay * 1.5 : MaximumPollDelay;
        }
    }

    /// <summary>
    /// Reports whether the catalog already serves the connector, treating a failed read as "not yet".
    ///
    /// The read is given only the time this wait has left, so a call that never answers cannot hold
    /// the consent experience open past the timeout the wait advertises.
    /// </summary>
    private async Task<bool> IsServableAsync(CopilotEnvironmentContext environment, string target, TimeSpan remaining, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);

        try
        {
            var served = await _connectors.ListConnectorInternalIdsAsync(environment, deadline.Token).ConfigureAwait(false);

            return served.Any(id => ConnectionConsentLink.NormalizeResourceId(id).Equals(target, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return false;
        }
    }

    [GeneratedRegex("-5f[0-9a-f]{16}$", RegexOptions.IgnoreCase)]
    private static partial Regex CustomConnectorSuffix();
}
