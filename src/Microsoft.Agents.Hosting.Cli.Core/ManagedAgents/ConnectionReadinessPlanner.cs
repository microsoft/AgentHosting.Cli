using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>What the planner decided for one connection reference.</summary>
/// <param name="View">The reference the decision is about.</param>
/// <param name="State">One of <c>bound</c>, <c>bound-unverified</c>, <c>already-bound</c>, <c>no-candidate</c>, <c>created</c>, <c>unbound</c>, or <c>undeclared</c>.</param>
/// <param name="ConnectionId">The connection to bind, when one was chosen.</param>
/// <param name="Candidates">The connected candidates that were considered.</param>
public sealed record ConnectionPlanItem(CopilotConnectionView View, string State, string? ConnectionId, IReadOnlyList<CopilotConnectionCandidate> Candidates);

/// <summary>
/// Chooses which connection satisfies each reference. Pure selection: no I/O, so every rule here is
/// provable offline.
/// </summary>
public static class ConnectionReadinessPlanner
{
    /// <summary>The reference was already bound before this command ran.</summary>
    public const string AlreadyBound = "already-bound";

    /// <summary>A connected candidate existed, so one was chosen.</summary>
    public const string Bound = "bound";

    /// <summary>
    /// A connection was chosen for the reference, but the catalog could not be read to confirm it
    /// exists. The binding is still written — an unreadable catalog is not evidence the connection is
    /// absent — but it does not count as resolved, because nothing has confirmed it.
    /// </summary>
    public const string BoundUnverified = "bound-unverified";

    /// <summary>No connected candidate existed, so a connection has to be created.</summary>
    public const string NoCandidate = "no-candidate";

    /// <summary>A connection was created through the browser consent flow, and bound.</summary>
    public const string Created = "created";

    /// <summary>
    /// A component uses the reference but the workspace does not declare it, and nothing can be bound
    /// to fix that here.
    /// </summary>
    public const string Undeclared = "undeclared";

    /// <summary>A connected connection could satisfy the reference, but nothing has bound it yet.</summary>
    public const string Unbound = "unbound";

    private const string ConnectedStatus = "Connected";

    /// <summary>Reports what is true of one reference, proposing nothing.</summary>
    /// <param name="view">The reference to read.</param>
    /// <returns>The reference's observed state.</returns>
    public static ConnectionPlanItem Observe(CopilotConnectionView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var connected = Connected(view);

        if (!view.IsDeclared)
        {
            return connected.Length == 0 || !string.IsNullOrWhiteSpace(view.BoundConnectionId)
                ? new ConnectionPlanItem(view, Undeclared, null, connected)
                : new ConnectionPlanItem(view, Unbound, null, connected);
        }

        if (!string.IsNullOrWhiteSpace(view.BoundConnectionId))
        {
            return new ConnectionPlanItem(view, AlreadyBound, view.BoundConnectionId, []);
        }

        return connected.Length == 0
            ? new ConnectionPlanItem(view, NoCandidate, null, [])
            : new ConnectionPlanItem(view, Unbound, null, connected);
    }

    /// <summary>
    /// Decides what a run that may write should do with one reference.
    ///
    /// Separate from <see cref="Observe"/> because choosing a connection is a proposal, not an
    /// observation: a command that never writes must not report the proposal as though the service
    /// had already accepted it.
    /// </summary>
    /// <param name="view">The reference to plan for, with its candidates.</param>
    /// <param name="principal">The signed-in identity, which a connection has to belong to before it is bound without asking.</param>
    /// <returns>The decision.</returns>
    public static ConnectionPlanItem Plan(CopilotConnectionView view, string? principal)
    {
        var observed = Observe(view);

        if (observed.State != Unbound)
        {
            return observed;
        }

        return Pick(observed.Candidates, principal) is { } chosen
            ? observed with { State = Bound, ConnectionId = chosen }
            : observed;
    }

    /// <summary>
    /// Chooses a connection to bind without asking, and only one the operator already owns.
    ///
    /// A connection runs as the identity that created it, so binding one owned by somebody else
    /// silently decides that the agent runs as them. Being able to see a connection proves it is
    /// visible, not that it was meant for this agent, so anything not provably the operator's own is
    /// left for them to choose. Where several are theirs the first by id is taken, which is
    /// deterministic and keeps the same effective identity either way.
    /// </summary>
    /// <param name="connected">The candidates that could satisfy the reference, in id order.</param>
    /// <param name="principal">The signed-in identity, or <see langword="null"/> when nobody is.</param>
    /// <returns>The connection to bind, or <see langword="null"/> when none is provably the operator's.</returns>
    private static string? Pick(IReadOnlyList<CopilotConnectionCandidate> connected, string? principal) =>
        connected.FirstOrDefault(candidate => IsOwnedBy(candidate, principal))?.ConnectionId;

    /// <summary>
    /// The connections that can satisfy a reference, in a stable order.
    /// </summary>
    /// <param name="view">The reference to read candidates from.</param>
    /// <returns>The connected candidates, ordered by id.</returns>
    public static CopilotConnectionCandidate[] Connected(CopilotConnectionView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return [.. view.Candidates.Where(IsConnected).OrderBy(candidate => candidate.ConnectionId, StringComparer.Ordinal)];
    }

    /// <summary>Whether a connection is in the one state that can satisfy a reference.</summary>
    /// <param name="candidate">The connection the catalog reported.</param>
    /// <returns><see langword="true"/> when the catalog reports it as connected.</returns>
    public static bool IsConnected(CopilotConnectionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return string.Equals(candidate.Status, ConnectedStatus, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a connection provably belongs to an identity.
    /// </summary>
    /// <param name="candidate">The connection the catalog reported.</param>
    /// <param name="principal">The signed-in identity, or <see langword="null"/> when nobody is.</param>
    /// <returns><see langword="true"/> when the catalog names that identity as the owner.</returns>
    public static bool IsOwnedBy(CopilotConnectionCandidate candidate, string? principal)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return !string.IsNullOrWhiteSpace(principal)
            && !string.IsNullOrWhiteSpace(candidate.Owner)
            && candidate.Owner.Equals(principal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Projects a decision onto the command's result shape.</summary>
    /// <param name="item">The decision.</param>
    /// <param name="consentLink">The link that would create a connection, when one is needed.</param>
    /// <param name="connectorStatus">What the connector as a whole is waiting on.</param>
    /// <returns>The result row.</returns>
    public static AgentDeploymentConnectionItem ToResultItem(ConnectionPlanItem item, Uri? consentLink, string connectorStatus) => new()
    {
        ConnectionReference = item.View.LogicalName,
        ConnectorId = ConnectionConsentLink.NormalizeOptionalResourceId(item.View.ConnectorId),
        ConnectorStatus = connectorStatus,
        ConnectionOwner = OwnerOf(item),
        State = item.State,
        ConnectionId = item.ConnectionId,
        Candidates = [.. item.Candidates.Select(candidate => $"{candidate.ConnectionId} ({candidate.DisplayName})")],
        ConsentLink = consentLink?.ToString(),
    };

    /// <summary>
    /// Names the identity the bound connection runs as, so the listing shows whose it is.
    /// </summary>
    /// <param name="item">The decision.</param>
    /// <returns>The owner the catalog reports for the bound connection, when it reports one.</returns>
    private static string? OwnerOf(ConnectionPlanItem item) =>
        string.IsNullOrWhiteSpace(item.ConnectionId)
            ? null
            : item.View.Candidates.FirstOrDefault(candidate => candidate.ConnectionId.Equals(item.ConnectionId, StringComparison.OrdinalIgnoreCase))?.Owner;

    /// <summary>
    /// Whether a decision leaves the reference usable.
    ///
    /// A binding the catalog could not confirm is deliberately excluded: it is written, but readiness
    /// is a claim that the reference will work, and nothing here has established that.
    /// </summary>
    /// <param name="item">The decision.</param>
    /// <returns><see langword="true"/> when the reference ends up bound and that binding was confirmed.</returns>
    public static bool IsResolved(ConnectionPlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.State is Bound or AlreadyBound or Created;
    }

    /// <summary>Whether a decision binds the reference, confirmed or not, and so has to be written.</summary>
    /// <param name="item">The decision.</param>
    /// <returns><see langword="true"/> when a connection was chosen for the reference.</returns>
    public static bool IsBinding(ConnectionPlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.State is Bound or BoundUnverified or Created;
    }

    /// <summary>
    /// Whether a reference's lack of a connection is established well enough to create one unasked.
    /// </summary>
    /// <param name="item">The decision.</param>
    /// <returns><see langword="true"/> when no connection provably exists for the reference.</returns>
    public static bool IsProvablyWithoutAConnection(ConnectionPlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.State == NoCandidate && !item.View.CatalogUnavailable;
    }

    /// <summary>
    /// Whether the operator can still act on a reference.
    ///
    /// A bound reference stays actionable, because it can be pointed at a different connection. The
    /// one closed case is a reference the service named no connector for, which nothing could be
    /// created against and which only a diagnostic can help with.
    /// </summary>
    /// <param name="item">The decision.</param>
    /// <returns><see langword="true"/> when binding it to a connection is safe to offer.</returns>
    public static bool IsActionable(ConnectionPlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return !string.IsNullOrWhiteSpace(item.View.ConnectorId);
    }

    /// <summary>Points a reference at a connection the operator chose.</summary>
    /// <param name="item">The decision to rewrite.</param>
    /// <param name="state">The state the reference reaches.</param>
    /// <param name="connectionId">The connection it should use.</param>
    /// <returns>The rewritten decision, or the original when nothing would change.</returns>
    public static ConnectionPlanItem Rebind(ConnectionPlanItem item, string state, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        return item.State == AlreadyBound && string.Equals(item.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase)
            ? item
            : item with { State = Verified(item.View, state), ConnectionId = connectionId, Candidates = [] };
    }

    /// <summary>
    /// Downgrades a chosen binding to its unverified form when the catalog could not be read. Applied
    /// where the state is written rather than at each caller, so no binding path can claim a
    /// confirmation that was never obtained.
    /// </summary>
    /// <param name="view">The reference being bound.</param>
    /// <param name="state">The state the caller asked for.</param>
    /// <returns>The state the decision actually reaches.</returns>
    private static string Verified(CopilotConnectionView view, string state) =>
        state == Bound && view.CatalogUnavailable ? BoundUnverified : state;

    /// <summary>Whether every reference in a plan ends up bound.</summary>
    /// <param name="items">The decisions.</param>
    /// <returns><see langword="true"/> when nothing is left unbound.</returns>
    public static bool IsReady(IEnumerable<ConnectionPlanItem> items) => items.All(IsResolved);

    /// <summary>
    /// Binds one reference to a connection the operator named.
    ///
    /// An unreadable catalog is not evidence the connection is absent, so the id is taken on trust
    /// rather than checked against a candidate list the read failed to fill. The service rejects a
    /// connection that genuinely does not exist when the binding is written.
    /// </summary>
    /// <param name="item">The decision to override.</param>
    /// <param name="connectionId">The connection the operator chose.</param>
    /// <returns>The reference bound to that connection.</returns>
    /// <exception cref="UsageException">The connection does not satisfy this reference.</exception>
    public static ConnectionPlanItem Select(ConnectionPlanItem item, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        if (item.View.CatalogUnavailable && item.View.Candidates.Count == 0)
        {
            return item with { State = BoundUnverified, ConnectionId = ConnectionConsentLink.NormalizeResourceId(connectionId), Candidates = [] };
        }

        var chosen = RequireConnected(item.View.Candidates, connectionId, $"'{item.View.LogicalName}'");

        return item with { State = Bound, ConnectionId = chosen.ConnectionId, Candidates = [chosen] };
    }

    /// <summary>Requires a connection to exist for a connector and be able to satisfy a reference.</summary>
    /// <param name="candidates">Every connection the connector has.</param>
    /// <param name="connectionId">The connection the request named.</param>
    /// <param name="subject">What the connection has to satisfy, named for the rejection message.</param>
    /// <returns>The connection the id names, carrying the id the service reports for it.</returns>
    /// <exception cref="ManagedAgentException">There is no such connection.</exception>
    /// <exception cref="UsageException">The connection is not connected.</exception>
    public static CopilotConnectionCandidate RequireConnected(IReadOnlyList<CopilotConnectionCandidate> candidates, string connectionId, string subject)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        var chosen = candidates.FirstOrDefault(candidate => candidate.ConnectionId.Equals(connectionId, StringComparison.OrdinalIgnoreCase))
            ?? throw ManagedAgentException.ConnectionNotACandidate(connectionId, subject, [.. candidates.Select(candidate => candidate.ConnectionId)]);

        return IsConnected(chosen)
            ? chosen
            : throw new UsageException($"Connection '{connectionId}' is {chosen.Status}, so it cannot satisfy {subject}. Choose a Connected connection.");
    }
}
