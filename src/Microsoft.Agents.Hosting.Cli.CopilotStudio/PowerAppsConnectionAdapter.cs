using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Deletes connections through the Power Apps connections API, which the sync library does not
/// expose.
/// </summary>
public sealed class PowerAppsConnectionAdapter : IConnectionLifecyclePort, IDisposable
{
    private readonly PowerAppsConnectionClient _client;

    /// <summary>Creates the adapter.</summary>
    /// <param name="tokenProvider">Supplies the Power Apps token the API accepts.</param>
    /// <param name="cluster">The cluster category the environment belongs to.</param>
    /// <param name="handlerFactory">Creates the transport, replaced in tests.</param>
    public PowerAppsConnectionAdapter(ITokenProvider tokenProvider, CoreServicesClusterCategory cluster, Func<HttpMessageHandler>? handlerFactory = null)
    {
        _client = new PowerAppsConnectionClient(tokenProvider, cluster, handlerFactory);
    }

    /// <summary>Disposes the transport shared by every request this adapter sent.</summary>
    public void Dispose() => _client.Dispose();

    /// <inheritdoc />
    public async Task DeleteConnectionAsync(CopilotEnvironmentContext environment, string connectorInternalId, string connectionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorInternalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        var response = await SendAsync(environment, connectorInternalId, connectionId, cancellationToken).ConfigureAwait(false);

        if (PowerAppsConnectionRemoval.ProvesAbsent(response))
        {
            return;
        }

        throw ManagedAgentException.ConnectionNotDeleted(
            ConnectionConsentLink.NormalizeResourceId(connectionId),
            environment.EnvironmentId,
            (int)response.StatusCode,
            response.CorrelationDetail);
    }

    private async Task<PowerAppsConnectionResponse> SendAsync(CopilotEnvironmentContext environment, string connectorInternalId, string connectionId, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.DeleteConnectionAsync(environment, connectorInternalId, connectionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Deleting connection {ConnectionConsentLink.NormalizeResourceId(connectionId)} timed out before the service reported an outcome.", failure);
        }
    }
}
