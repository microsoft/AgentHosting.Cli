using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent deployment connection add</c>: declares a new connection reference for a
/// connector and binds it, by the same rules <c>update connection</c> binds an existing one.
/// </summary>
public sealed class AgentDeploymentConnectionReferenceCreateService
{
    private const string Verb = "msagent deployment connection add";

    private readonly AgentDeploymentConnectionResolver _targets;
    private readonly ConnectorResolver _connectorResolver;
    private readonly ICopilotConnectionPort _connections;
    private readonly ConnectionBindingCoordinator _bindings;
    private readonly ManagedAgentPrincipalResolver _principals;

    /// <summary>Creates the service.</summary>
    /// <param name="targets">Resolves the deployment, environment and workspace the verb acts on.</param>
    /// <param name="connectorResolver">Resolves the connector the reference targets, and its connections.</param>
    /// <param name="connections">Declares the reference, reads candidates, and writes the binding.</param>
    /// <param name="bindings">Resolves the connector to a connection when one is not named.</param>
    /// <param name="principals">Names the identity a connection has to belong to before it is bound without asking.</param>
    public AgentDeploymentConnectionReferenceCreateService(AgentDeploymentConnectionResolver targets, ConnectorResolver connectorResolver, ICopilotConnectionPort connections, ConnectionBindingCoordinator bindings, ManagedAgentPrincipalResolver principals)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _connectorResolver = connectorResolver ?? throw new ArgumentNullException(nameof(connectorResolver));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _principals = principals ?? throw new ArgumentNullException(nameof(principals));
    }

    /// <summary>Declares one connection reference and binds it where it can.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The reference that was declared, and the connection it was bound to.</returns>
    /// <exception cref="UsageException">No connector was named, or the environment serves none by that id.</exception>
    public async Task<AgentDeploymentConnectionReferenceCreateResult> CreateAsync(AgentDeploymentConnectionReferenceCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Connector))
        {
            throw new UsageException($"`{Verb}` requires --connector. Run `{ConnectionCommandHints.ListConnectors(request, _targets.FindAgentId(request), environmentId: null)}` to find one.");
        }

        var target = await _targets.ResolveAsync(request, Verb, cancellationToken).ConfigureAwait(false);
        var environment = target.Environment;
        var resolution = await _connectorResolver.RequireAsync(environment, request.Connector, ConnectionCommandHints.ListConnectors(request, target.Agent.AgentId, environmentId: null), cancellationToken).ConfigureAwait(false);
        var connector = resolution.Connector;

        var explicitConnectionId = string.IsNullOrWhiteSpace(request.ConnectionId)
            ? null
            : ConnectionReadinessPlanner.RequireConnected(
                await _connectorResolver.ConnectionsAsync(environment, resolution, cancellationToken).ConfigureAwait(false),
                request.ConnectionId.Trim(),
                $"connector '{connector.InternalId}'").ConnectionId;

        var logicalName = await DeclareAsync(request, target, connector, cancellationToken).ConfigureAwait(false);

        try
        {
            return await BindAsync(request, target, resolution, logicalName, explicitConnectionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw ManagedAgentException.ConnectionReferenceNotBound(logicalName, connector.InternalId, failure, ConnectionCommandHints.BindReference(request, target.Agent.AgentId, logicalName));
        }
    }

    private async Task<string> DeclareAsync(AgentDeploymentConnectionReferenceCreateRequest request, AgentDeploymentConnectionTarget target, CopilotConnectorInfo connector, CancellationToken cancellationToken)
    {
        try
        {
            return await _connections.CreateConnectionReferenceAsync(target.Environment, target.WorkspaceDirectory, connector.InternalId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw ManagedAgentException.ConnectionReferenceNotDeclared(connector.InternalId, target.Environment.EnvironmentId, failure, ConnectionCommandHints.ListConnections(request, target.Agent.AgentId));
        }
    }

    private async Task<AgentDeploymentConnectionReferenceCreateResult> BindAsync(AgentDeploymentConnectionReferenceCreateRequest request, AgentDeploymentConnectionTarget target, ConnectorResolution resolution, string logicalName, string? explicitConnectionId, CancellationToken cancellationToken)
    {
        var environment = target.Environment;
        var connector = resolution.Connector;
        var snapshot = await _connections.ReadConnectionsAsync(environment, target.WorkspaceDirectory, cancellationToken).ConfigureAwait(false);
        var plan = snapshot.References.Select(ConnectionReadinessPlanner.Observe).ToList();
        var index = plan.FindIndex(item => item.View.LogicalName.Equals(logicalName, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            return Describe(request, target, logicalName, connector.InternalId, null, null, ready: false, [$"The reference was declared, but the service did not report it, so nothing was bound. Run `{ConnectionCommandHints.BindReference(request, target.Agent.AgentId, logicalName)}` once it appears."]);
        }

        plan[index] = ConnectionReadinessPlanner.Plan(plan[index].View, await _principals.ResolveUsernameAsync(cancellationToken).ConfigureAwait(false));

        var group = new ConnectorGroup(ConnectionConsentLink.NormalizeOptionalResourceId(plan[index].View.ConnectorId), [index]);
        var diagnostics = new List<string>();

        if (explicitConnectionId is not null)
        {
            ConnectionBindingCoordinator.BindExplicitly(plan, group, explicitConnectionId);
        }
        else
        {
            await ResolveConnectionAsync(request, plan, group, index, target, resolution, diagnostics, cancellationToken).ConfigureAwait(false);
        }

        var item = plan[index];

        if (ConnectionReadinessPlanner.IsBinding(item) && item.ConnectionId is { } connectionId)
        {
            await _connections.BindAsync(environment, target.WorkspaceDirectory, [new CopilotConnectionBinding { LogicalName = logicalName, ConnectionId = connectionId }], cancellationToken).ConfigureAwait(false);
        }

        diagnostics.AddRange(ConnectionReport.UnownedCandidates([item]));

        var reported = ConnectionReadinessPlanner.ToResultItem(item, null, ConnectionSelectionScope.Status(plan, group));

        return Describe(request, target, logicalName, connector.InternalId, reported.ConnectionId, reported.ConnectionOwner, ConnectionReadinessPlanner.IsResolved(item), diagnostics);
    }

    private async Task ResolveConnectionAsync(AgentDeploymentConnectionReferenceCreateRequest request, List<ConnectionPlanItem> plan, ConnectorGroup group, int index, AgentDeploymentConnectionTarget target, ConnectorResolution resolution, List<string> diagnostics, CancellationToken cancellationToken)
    {
        if (ConnectionReadinessPlanner.IsResolved(plan[index]))
        {
            return;
        }

        if (!request.NonInteractive)
        {
            await _bindings.ResolveConnectorAsync(plan, group, target.Environment, resolution, openBrowser: true, diagnostics, cancellationToken).ConfigureAwait(false);

            return;
        }

        if (ConnectionReadinessPlanner.IsProvablyWithoutAConnection(plan[index]))
        {
            await _bindings.CreateForConnectorAsync(plan, [index], target.Environment, resolution, openBrowser: false, diagnostics, cancellationToken).ConfigureAwait(false);

            return;
        }

        if (plan[index].View.CatalogUnavailable)
        {
            diagnostics.Add(ConnectionReport.UnreadableCatalog(plan[index].View.LogicalName, ConnectionCommandHints.BindReference(request, target.Agent.AgentId, plan[index].View.LogicalName)));
        }
    }

    private static AgentDeploymentConnectionReferenceCreateResult Describe(AgentDeploymentConnectionReferenceCreateRequest request, AgentDeploymentConnectionTarget target, string logicalName, string connectorId, string? connectionId, string? owner, bool ready, IReadOnlyList<string> diagnostics) => new()
    {
        Status = ready ? "connection-reference-bound" : "connection-reference-declared",
        EnvironmentId = target.Environment.EnvironmentId,
        ConnectionReference = logicalName,
        ConnectorId = connectorId,
        ConnectionId = connectionId,
        ConnectionOwner = owner,
        Ready = ready,
        BindCommand = ConnectionCommandHints.BindReference(request, target.Agent.AgentId, logicalName),
        Diagnostics = diagnostics,
    };
}
