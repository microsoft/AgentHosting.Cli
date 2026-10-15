using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent deployment connection connectors</c>: reports the connectors an environment serves, narrowed to
/// the rows the request asked for. Changes nothing.
/// </summary>
public sealed class AgentDeploymentConnectorListService
{
    private readonly ConnectionTargetResolver _targets;
    private readonly IConnectorCatalogPort _connectors;
    private readonly ICopilotConnectionPort _connections;

    /// <summary>Creates the service.</summary>
    /// <param name="targets">Resolves the environment the verb reads.</param>
    /// <param name="connectors">Reads the environment's connector catalog.</param>
    /// <param name="connections">Reads the workspace's declared references, to mark the connectors already in use.</param>
    public AgentDeploymentConnectorListService(ConnectionTargetResolver targets, IConnectorCatalogPort connectors, ICopilotConnectionPort connections)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _connectors = connectors ?? throw new ArgumentNullException(nameof(connectors));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>Lists the connectors in scope.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The connectors the request asked for.</returns>
    public async Task<AgentDeploymentConnectorListResult> ListAsync(AgentDeploymentConnectorListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await _targets.ResolveAsync(request, request.EnvironmentId, "msagent deployment connection connectors", cancellationToken).ConfigureAwait(false);
        var catalog = await _connectors.ListConnectorsAsync(target.Environment, cancellationToken).ConfigureAwait(false);
        var diagnostics = new List<string>();
        var declared = await ReadDeclaredConnectorsAsync(target, diagnostics, cancellationToken).ConfigureAwait(false);
        var page = ConnectorCatalogQuery.Query(catalog, request.Filter, request.Connector, request.Limit, declared ?? []);

        return new AgentDeploymentConnectorListResult
        {
            Status = "connectors-listed",
            EnvironmentId = target.Environment.EnvironmentId,
            Limit = page.Limit,
            Truncated = page.Truncated,
            Connectors = [.. page.Connectors.Select(connector => Describe(connector, declared))],
            Diagnostics = diagnostics,
        };
    }

    private async Task<IReadOnlyCollection<string>?> ReadDeclaredConnectorsAsync(ConnectionTarget target, List<string> diagnostics, CancellationToken cancellationToken)
    {
        if (target.WorkspaceDirectory is not { } workspaceDirectory)
        {
            return null;
        }

        try
        {
            return (await _connections.ReadDeclaredConnectorsAsync(target.Environment, workspaceDirectory, cancellationToken).ConfigureAwait(false))
                .Select(ConnectionConsentLink.NormalizeOptionalResourceId)
                .Where(connectorId => connectorId.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            diagnostics.Add($"The workspace's connection references could not be read, so no connector is marked as in use: {failure.Message}");

            return null;
        }
    }

    private static AgentDeploymentConnectorItem Describe(CopilotConnectorInfo connector, IReadOnlyCollection<string>? declared) => new()
    {
        ConnectorId = connector.InternalId,
        DisplayName = connector.DisplayName,
        Publisher = connector.Publisher,
        Tier = connector.Tier,
        InUse = declared?.Contains(ConnectionConsentLink.NormalizeResourceId(connector.InternalId)),
    };
}
