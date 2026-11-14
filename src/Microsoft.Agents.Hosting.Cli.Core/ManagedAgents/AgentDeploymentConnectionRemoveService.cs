using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent deployment connection remove</c>: removes a connection reference from the workspace, deletes
/// a connection from the environment, or both, according to which one the request named.
/// </summary>
public sealed class AgentDeploymentConnectionRemoveService
{
    private const string Verb = "msagent deployment connection remove";

    private readonly ConnectionTargetResolver _targets;
    private readonly ConnectorResolver _connectorResolver;
    private readonly ICopilotConnectionPort _connections;
    private readonly IConnectionLifecyclePort _lifecycle;
    private readonly IConfirmationPrompt _confirmation;

    /// <summary>Creates the service.</summary>
    /// <param name="targets">Resolves the environment, and the workspace when a deployment named one.</param>
    /// <param name="connectorResolver">Resolves the connector a named connection is deleted under.</param>
    /// <param name="connections">Reads the workspace's references, and removes a declaration.</param>
    /// <param name="lifecycle">Deletes the connection.</param>
    /// <param name="confirmation">Asks before removing a reference something still uses.</param>
    public AgentDeploymentConnectionRemoveService(ConnectionTargetResolver targets, ConnectorResolver connectorResolver, ICopilotConnectionPort connections, IConnectionLifecyclePort lifecycle, IConfirmationPrompt confirmation)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _connectorResolver = connectorResolver ?? throw new ArgumentNullException(nameof(connectorResolver));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
    }

    /// <summary>Removes whatever the request named.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What was removed, and what still used it.</returns>
    /// <exception cref="UsageException">Nothing was named, or something still holds what was.</exception>
    public async Task<AgentDeploymentConnectionRemoveResult> RemoveAsync(AgentDeploymentConnectionRemoveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var logicalName = string.IsNullOrWhiteSpace(request.ConnectionReference) ? null : request.ConnectionReference.Trim();
        var connectionId = string.IsNullOrWhiteSpace(request.ConnectionId) ? null : ConnectionConsentLink.NormalizeResourceId(request.ConnectionId.Trim());

        if (logicalName is null && connectionId is null)
        {
            throw new UsageException($"`{Verb}` requires --connection-reference to remove a reference, --connection-id to delete a connection, or both. Run `{ConnectionCommandHints.ListConnections(request, _targets.FindAgentId(request))}` to see them.");
        }

        var target = await _targets.ResolveAsync(request, request.EnvironmentId, Verb, cancellationToken).ConfigureAwait(false);
        var snapshot = await ReadWorkspaceAsync(request, target, connectionId, logicalName, cancellationToken).ConfigureAwait(false);
        var deletion = connectionId is null ? default((string ConnectorId, string ConnectionId)?) : await PlanDeletionAsync(request, target, snapshot, connectionId, logicalName, cancellationToken).ConfigureAwait(false);
        var removal = logicalName is null ? default((IReadOnlyList<string> Usages, bool RecordDeleted)?) : await RemoveReferenceAsync(request, target, logicalName, cancellationToken).ConfigureAwait(false);
        var deleted = deletion is null ? default((string ConnectorId, string ConnectionId)?) : await DeleteConnectionAsync(request, target, deletion.Value, logicalName, removal is not null, cancellationToken).ConfigureAwait(false);

        return new AgentDeploymentConnectionRemoveResult
        {
            Status = deleted is null ? "connection-reference-removed" : "connection-removed",
            EnvironmentId = target.Environment.EnvironmentId,
            ConnectorId = deleted?.ConnectorId,
            ConnectionId = deleted?.ConnectionId,
            ConnectionReference = removal is null ? null : logicalName,
            ConnectionReferenceRecordDeleted = removal?.RecordDeleted ?? false,
            Usages = removal?.Usages ?? [],
        };
    }

    private async Task<CopilotConnectionSnapshot?> ReadWorkspaceAsync(AgentDeploymentConnectionRemoveRequest request, ConnectionTarget target, string? connectionId, string? logicalName, CancellationToken cancellationToken)
    {
        var needed = connectionId is not null && (!request.Force || string.IsNullOrWhiteSpace(request.Connector) || logicalName is not null);

        return needed && target.WorkspaceDirectory is { } workspaceDirectory
            ? await _connections.ReadConnectionsAsync(target.Environment, workspaceDirectory, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private async Task<(IReadOnlyList<string> Usages, bool RecordDeleted)> RemoveReferenceAsync(AgentDeploymentConnectionRemoveRequest request, ConnectionTarget target, string logicalName, CancellationToken cancellationToken)
    {
        if (target.WorkspaceDirectory is not { } workspaceDirectory)
        {
            throw new UsageException("--connection-reference removes a declaration from the agent workspace, so it needs a deployment rather than --environment-id.");
        }

        var attempted = await _connections.RemoveConnectionReferenceAsync(target.Environment, workspaceDirectory, logicalName, confirmed: false, cancellationToken).ConfigureAwait(false);

        if (attempted.Removed)
        {
            return ([], attempted.RecordDeleted);
        }

        if (attempted.Usages.Count == 0)
        {
            throw await NotRemovedAsync(request, target, workspaceDirectory, logicalName, cancellationToken).ConfigureAwait(false);
        }

        await RequireConsentAsync(request, logicalName, attempted.Usages, cancellationToken).ConfigureAwait(false);

        var forced = await _connections.RemoveConnectionReferenceAsync(target.Environment, workspaceDirectory, logicalName, confirmed: true, cancellationToken).ConfigureAwait(false);

        return forced.Removed
            ? ([.. attempted.Usages.Select(usage => usage.DisplayName ?? usage.FilePath)], forced.RecordDeleted)
            : throw ManagedAgentException.ConnectionReferenceNotRemoved(logicalName, workspaceDirectory);
    }

    private async Task<ManagedAgentException> NotRemovedAsync(AgentDeploymentConnectionRemoveRequest request, ConnectionTarget target, string workspaceDirectory, string logicalName, CancellationToken cancellationToken)
    {
        var declared = await DeclaredNamesAsync(target, workspaceDirectory, cancellationToken).ConfigureAwait(false);

        return declared is null || declared.Any(name => name.Equals(logicalName, StringComparison.OrdinalIgnoreCase))
            ? ManagedAgentException.ConnectionReferenceNotRemoved(logicalName, workspaceDirectory)
            : ManagedAgentException.ConnectionReferenceNotDeclared(logicalName, workspaceDirectory, NearMatches(declared, logicalName), ConnectionCommandHints.ListConnections(request, target.AgentId));
    }

    private async Task<IReadOnlyList<string>?> DeclaredNamesAsync(ConnectionTarget target, string workspaceDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _connections.ReadConnectionsAsync(target.Environment, workspaceDirectory, cancellationToken).ConfigureAwait(false);

            return [.. snapshot.References.Select(view => view.LogicalName)];
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> NearMatches(IReadOnlyList<string> declared, string logicalName) =>
        [.. declared
            .Where(name => name.Contains(logicalName, StringComparison.OrdinalIgnoreCase) || logicalName.Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)];

    private async Task RequireConsentAsync(AgentDeploymentConnectionRemoveRequest request, string logicalName, IReadOnlyList<CopilotConnectionUsage> usages, CancellationToken cancellationToken)
    {
        if (request.Force)
        {
            return;
        }

        var outcome = await ConfirmationPolicy.ConfirmAsync(
            _confirmation,
            request.NonInteractive,
            $"Connection reference {logicalName} is still used by {ConnectionUsageReport.Describe(usages)}. Removing it leaves those components naming a reference the workspace no longer declares.",
            logicalName,
            cancellationToken).ConfigureAwait(false);

        if (outcome != ConfirmationOutcome.Confirmed)
        {
            throw new UsageException($"{logicalName} was not removed. Re-run with --force to remove it while {ConnectionUsageReport.Describe(usages)} still uses it.");
        }
    }

    private async Task<(string ConnectorId, string ConnectionId)> PlanDeletionAsync(AgentDeploymentConnectionRemoveRequest request, ConnectionTarget target, CopilotConnectionSnapshot? snapshot, string connectionId, string? removedReference, CancellationToken cancellationToken)
    {
        var holders = Holders(snapshot, connectionId, removedReference);

        if (!request.Force && holders.Length > 0)
        {
            throw new UsageException($"Connection {connectionId} is still used by {holders.Length} connection reference(s): {string.Join(", ", holders)}. Point them elsewhere, remove them, or re-run with --force to delete it anyway.");
        }

        RequireConnectorMatchesReference(request, snapshot, connectionId);

        var resolution = await _connectorResolver.RequireAsync(
            target.Environment,
            ResolveConnector(request, target, snapshot, connectionId),
            ConnectionCommandHints.ListConnectors(request, target.AgentId, request.EnvironmentId),
            cancellationToken).ConfigureAwait(false);

        if (removedReference is not null)
        {
            await RequireConnectorServesConnectionAsync(request, target, resolution, connectionId, cancellationToken).ConfigureAwait(false);
        }

        return (resolution.Connector.InternalId, connectionId);
    }

    private static void RequireConnectorMatchesReference(AgentDeploymentConnectionRemoveRequest request, CopilotConnectionSnapshot? snapshot, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(request.Connector))
        {
            return;
        }

        var recorded = snapshot?.References.FirstOrDefault(view => Holds(view, connectionId))?.ConnectorId;

        if (string.IsNullOrWhiteSpace(recorded))
        {
            return;
        }

        var supplied = ConnectionConsentLink.NormalizeResourceId(request.Connector);
        var expected = ConnectionConsentLink.NormalizeResourceId(recorded);

        if (!supplied.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException($"Connection {connectionId} belongs to connector {expected}, not {supplied}. Re-run with --connector {expected}, or drop --connector to use the one the reference records.");
        }
    }

    private async Task RequireConnectorServesConnectionAsync(AgentDeploymentConnectionRemoveRequest request, ConnectionTarget target, ConnectorResolution resolution, string connectionId, CancellationToken cancellationToken)
    {
        IReadOnlyList<CopilotConnectionCandidate> connections;

        try
        {
            connections = await _connectorResolver.ConnectionsAsync(target.Environment, resolution, cancellationToken).ConfigureAwait(false);
        }
        catch (ConnectorNotFoundException)
        {
            throw Unserved(request, target, resolution, connectionId);
        }

        if (connections.Any(candidate => connectionId.Equals(ConnectionConsentLink.NormalizeOptionalResourceId(candidate.ConnectionId), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw Unserved(request, target, resolution, connectionId);
    }

    private static ManagedAgentException Unserved(AgentDeploymentConnectionRemoveRequest request, ConnectionTarget target, ConnectorResolution resolution, string connectionId) =>
        ManagedAgentException.ConnectionNotServed(resolution.Connector.InternalId, connectionId, target.Environment.EnvironmentId, ConnectionCommandHints.ListConnections(request, target.AgentId));

    private async Task<(string ConnectorId, string ConnectionId)> DeleteConnectionAsync(AgentDeploymentConnectionRemoveRequest request, ConnectionTarget target, (string ConnectorId, string ConnectionId) deletion, string? removedReference, bool referenceRemoved, CancellationToken cancellationToken)
    {
        try
        {
            await _lifecycle.DeleteConnectionAsync(target.Environment, deletion.ConnectorId, deletion.ConnectionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (referenceRemoved && !IsCallerCancellation(failure, cancellationToken))
        {
            throw ManagedAgentException.ConnectionNotDeletedAfterReferenceRemoved(
                deletion.ConnectionId,
                removedReference!,
                failure,
                ConnectionCommandHints.DeleteConnection(request, target.AgentId, request.EnvironmentId, deletion.ConnectorId, deletion.ConnectionId, request.Force));
        }

        return deletion;
    }

    private static bool IsCallerCancellation(Exception failure, CancellationToken cancellationToken) =>
        failure is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private static string[] Holders(CopilotConnectionSnapshot? snapshot, string connectionId, string? removedReference) =>
        snapshot is null
            ? []
            : [.. snapshot.References
                .Where(view => Holds(view, connectionId))
                .Where(view => !view.LogicalName.Equals(removedReference, StringComparison.OrdinalIgnoreCase))
                .Select(view => view.LogicalName)
                .OrderBy(logicalName => logicalName, StringComparer.Ordinal)];

    private static string ResolveConnector(AgentDeploymentConnectionRemoveRequest request, ConnectionTarget target, CopilotConnectionSnapshot? snapshot, string connectionId)
    {
        if (!string.IsNullOrWhiteSpace(request.Connector))
        {
            return request.Connector;
        }

        var holder = snapshot?.References.FirstOrDefault(view => Holds(view, connectionId));

        return string.IsNullOrWhiteSpace(holder?.ConnectorId)
            ? throw new UsageException($"--connector is required to delete connection {connectionId}: no connection reference in this workspace holds it, so its connector is not recorded here. Run `{ConnectionCommandHints.ListConnections(request, target.AgentId)}` to see the connections your references do hold.")
            : ConnectionConsentLink.NormalizeResourceId(holder.ConnectorId);
    }

    private static bool Holds(CopilotConnectionView view, string connectionId) =>
        connectionId.Equals(ConnectionConsentLink.NormalizeOptionalResourceId(view.RecordedConnectionId), StringComparison.OrdinalIgnoreCase);
}
