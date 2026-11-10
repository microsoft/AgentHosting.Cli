using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Decides connection references by the sync library's binding planner, stating each decision in the CLI's terms.</summary>
public sealed class SyncConnectionBindingRules : IConnectionBindingRules
{
    /// <inheritdoc />
    public ConnectionPlanItem Observe(CopilotConnectionView view) => Decide(view, ConnectionBindingPlanner.Observe);

    /// <inheritdoc />
    public ConnectionPlanItem Plan(CopilotConnectionView view, string? principal) => Decide(view, reference => ConnectionBindingPlanner.Plan(reference, principal));

    /// <inheritdoc />
    public IReadOnlyList<CopilotConnectionCandidate> Connected(IReadOnlyList<CopilotConnectionCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return ConnectedOf([.. candidates.Select(Instance)], candidates);
    }

    private static ConnectionPlanItem Decide(CopilotConnectionView view, Func<AgentConnectionView, ConnectionBindingDecision> decide)
    {
        ArgumentNullException.ThrowIfNull(view);

        ConnectionInstance[] instances = [.. view.Candidates.Select(Instance)];
        var decision = decide(new AgentConnectionView { ConnectionReferenceLogicalName = view.LogicalName, ConnectorId = view.ConnectorId, BoundConnectionId = view.BoundConnectionId ?? string.Empty, BoundConnectionExists = view.BoundConnectionExists, CatalogUnavailable = view.CatalogUnavailable, IsDeclared = view.IsDeclared, Candidates = [.. instances] });

        return new ConnectionPlanItem(view, StateOf(decision.State), decision.ConnectionId, Back(decision.Candidates, instances, view.Candidates)) { ConnectedCandidates = ConnectedOf(instances, view.Candidates) };
    }

    private static CopilotConnectionCandidate[] ConnectedOf(ConnectionInstance[] instances, IReadOnlyList<CopilotConnectionCandidate> candidates) => Back(ConnectionBindingPlanner.Observe(new AgentConnectionView { Candidates = [.. instances] }).Candidates, instances, candidates);

    private static CopilotConnectionCandidate[] Back(IReadOnlyList<ConnectionInstance> decided, ConnectionInstance[] instances, IReadOnlyList<CopilotConnectionCandidate> candidates) => [.. decided.Select(instance => candidates[Array.IndexOf(instances, instance)])];

    private static ConnectionInstance Instance(CopilotConnectionCandidate candidate) => new() { Name = candidate.ConnectionId, DisplayName = candidate.DisplayName, Status = candidate.Status, Owner = candidate.Owner ?? string.Empty };

    private static string StateOf(ConnectionBindingState state) => state switch
    {
        ConnectionBindingState.AlreadyBound => ConnectionReadinessPlanner.AlreadyBound,
        ConnectionBindingState.Bound => ConnectionReadinessPlanner.Bound,
        ConnectionBindingState.NoCandidate => ConnectionReadinessPlanner.NoCandidate,
        ConnectionBindingState.Unbound => ConnectionReadinessPlanner.Unbound,
        ConnectionBindingState.Undeclared => ConnectionReadinessPlanner.Undeclared,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "The sync library reported a connection state the CLI does not know."),
    };
}
