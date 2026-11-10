using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent deployment connection add</c>: creates one connection for a connector through the
/// browser consent experience, and binds nothing.
/// </summary>
public sealed class AgentDeploymentConnectionCreateService
{
    private const string Verb = "msagent deployment connection add";

    private readonly ConnectionTargetResolver _targets;
    private readonly ConnectorResolver _connectorResolver;
    private readonly IConnectionBindingRules _rules;
    private readonly ConnectionCreationFlow _creationFlow;
    private readonly ManagedAgentPrincipalResolver _principals;

    /// <summary>Creates the service.</summary>
    /// <param name="targets">Resolves the environment the connection is created in.</param>
    /// <param name="connectorResolver">Resolves the connector the connection is created for, and its connections.</param>
    /// <param name="rules">Tells which of the connector's connections can satisfy a reference.</param>
    /// <param name="creationFlow">Waits for the connector, then runs the consent experience.</param>
    /// <param name="principals">Names the signed-in identity, to report the connections already owned.</param>
    public AgentDeploymentConnectionCreateService(ConnectionTargetResolver targets, ConnectorResolver connectorResolver, IConnectionBindingRules rules, ConnectionCreationFlow creationFlow, ManagedAgentPrincipalResolver principals)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _connectorResolver = connectorResolver ?? throw new ArgumentNullException(nameof(connectorResolver));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _creationFlow = creationFlow ?? throw new ArgumentNullException(nameof(creationFlow));
        _principals = principals ?? throw new ArgumentNullException(nameof(principals));
    }

    /// <summary>Creates one connection.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The connection that was created.</returns>
    /// <exception cref="UsageException">No connector was named, or the environment serves none by that id.</exception>
    /// <exception cref="ManagedAgentException">The consent experience produced no connection.</exception>
    public async Task<AgentDeploymentConnectionCreateResult> CreateAsync(AgentDeploymentConnectionCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Connector))
        {
            throw new UsageException($"`{Verb}` requires --connector. Run `{ConnectionCommandHints.ListConnectors(request, request.EnvironmentId)}` to find one.");
        }

        var target = await _targets.ResolveAsync(request, request.EnvironmentId, Verb, cancellationToken).ConfigureAwait(false);
        var environment = target.Environment;
        var resolution = await _connectorResolver.RequireAsync(environment, request.Connector, ConnectionCommandHints.ListConnectors(request, request.EnvironmentId), cancellationToken).ConfigureAwait(false);
        var connector = resolution.Connector;
        var diagnostics = await DescribeExistingAsync(environment, resolution, cancellationToken).ConfigureAwait(false);

        var outcome = await _creationFlow.CreateAsync(environment, resolution, openBrowser: !request.NonInteractive, cancellationToken).ConfigureAwait(false);

        if (outcome.Status != ConnectionConsentStatus.Created || string.IsNullOrWhiteSpace(outcome.ConnectionId))
        {
            throw Refused(request, environment.EnvironmentId, connector.InternalId, outcome.ErrorMessage ?? DescribeOutcome(outcome.Status));
        }

        return new AgentDeploymentConnectionCreateResult
        {
            Status = "connection-created",
            EnvironmentId = environment.EnvironmentId,
            ConnectorId = connector.InternalId,
            ConnectorName = connector.DisplayName,
            ConnectionId = outcome.ConnectionId,
            DisplayName = string.IsNullOrWhiteSpace(outcome.DisplayName) ? null : outcome.DisplayName,
            BindCommand = ConnectionCommandHints.BindConnector(request, connector.InternalId, outcome.ConnectionId),
            Diagnostics = diagnostics,
        };
    }

    private static ManagedAgentException Refused(AgentDeploymentConnectionCreateRequest request, string environmentId, string connectorInternalId, string reason) =>
        ManagedAgentException.ConnectionNotCreated(
            connectorInternalId,
            environmentId,
            reason,
            ConnectionCommandHints.BindConnector(request, connectorInternalId, "<id>"));

    private async Task<IReadOnlyList<string>> DescribeExistingAsync(CopilotEnvironmentContext environment, ConnectorResolution resolution, CancellationToken cancellationToken)
    {
        var principal = await _principals.ResolveUsernameAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(principal))
        {
            return [];
        }

        try
        {
            string[] owned = [.. _rules.Connected(await _connectorResolver.ConnectionsAsync(environment, resolution, cancellationToken).ConfigureAwait(false)).Where(connection => ConnectionReadinessPlanner.IsOwnedBy(connection, principal)).Select(connection => connection.ConnectionId).OrderBy(connectionId => connectionId, StringComparer.Ordinal)];

            return owned.Length == 0
                ? []
                : [$"You already own {owned.Length} connected connection(s) for {resolution.Connector.InternalId}: {string.Join(", ", owned)}. A new one was created anyway."];
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return [];
        }
    }

    private static string DescribeOutcome(ConnectionConsentStatus status) => status switch
    {
        ConnectionConsentStatus.Cancelled => "the consent experience was abandoned or timed out.",
        _ => "the consent experience reported no connection.",
    };
}
