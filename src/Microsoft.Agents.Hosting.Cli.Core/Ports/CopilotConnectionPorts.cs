using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>A connection that could satisfy a reference.</summary>
public sealed record CopilotConnectionCandidate
{
    /// <summary>The connection's identifier.</summary>
    public required string ConnectionId { get; init; }

    /// <summary>The connection's display name, usually the identity that created it.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The connection's status; only <c>Connected</c> can satisfy a reference.</summary>
    public required string Status { get; init; }

    /// <summary>
    /// The identity that owns the connection, when the catalog reports one.
    ///
    /// Shown beside every candidate so an operator choosing between two service accounts is looking
    /// at the identity the agent would run as.
    /// </summary>
    public string? Owner { get; init; }
}

/// <summary>One place a connection reference is used.</summary>
public sealed record CopilotConnectionUsage
{
    /// <summary>The workspace-relative file that uses the reference.</summary>
    public required string FilePath { get; init; }

    /// <summary>The component's display name, when it has one.</summary>
    public string? DisplayName { get; init; }
}

/// <summary>
/// One connection reference an agent declares, with its binding state and the connections that could
/// satisfy it.
/// </summary>
public sealed record CopilotConnectionView
{
    /// <summary>The reference's logical name, unique within the environment.</summary>
    public required string LogicalName { get; init; }

    /// <summary>The connector the reference targets.</summary>
    public required string ConnectorId { get; init; }

    /// <summary>The connector's display name, when the catalog reports one.</summary>
    public string? ConnectorName { get; init; }

    /// <summary>The connection the reference records, whether or not the catalog could confirm it still exists.</summary>
    public string? BoundConnectionId { get; init; }

    /// <summary>Whether the recorded connection still exists, which confirms the binding only when the catalog could be read.</summary>
    public bool BoundConnectionExists { get; init; }

    /// <summary>
    /// Whether the catalog could not be read. Candidates are empty either way, so this is what
    /// separates "no connection exists" from "we could not tell".
    /// </summary>
    public bool CatalogUnavailable { get; init; }

    /// <summary>The connections that could satisfy this reference.</summary>
    public IReadOnlyList<CopilotConnectionCandidate> Candidates { get; init; } = [];

    /// <summary>
    /// Whether the workspace declares this reference.
    ///
    /// Defaults to <see langword="true"/> so that a reference read from a source which does not
    /// distinguish the two is treated as declared, as it was before undeclared references were
    /// surfaced.
    /// </summary>
    public bool IsDeclared { get; init; } = true;

    /// <summary>The components that use this reference.</summary>
    public IReadOnlyList<CopilotConnectionUsage> Usages { get; init; } = [];
}

/// <summary>What removing a connection reference achieved.</summary>
/// <param name="Removed">Whether the declaration was removed.</param>
/// <param name="Usages">The components still using it, when that is what blocked the removal.</param>
public sealed record CopilotConnectionReferenceRemoval(bool Removed, IReadOnlyList<CopilotConnectionUsage> Usages)
{
    /// <summary>Whether the environment's connectionreference record was deleted with the declaration.</summary>
    public bool RecordDeleted { get; init; }
}

/// <summary>A binding to write onto a connection reference.</summary>
public sealed record CopilotConnectionBinding
{
    /// <summary>The reference to bind.</summary>
    public required string LogicalName { get; init; }

    /// <summary>The connection to bind it to.</summary>
    public required string ConnectionId { get; init; }
}

/// <summary>One workflow the agent owns, and the references it needs.</summary>
public sealed record CopilotWorkflowView
{
    /// <summary>The workflow's identifier.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>The workflow's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The workspace-relative file defining the workflow.</summary>
    public string? FilePath { get; init; }

    /// <summary>The connection references the workflow uses.</summary>
    public IReadOnlyList<string> ConnectionReferences { get; init; } = [];

    /// <summary>
    /// Whether every reference this workflow uses is bound, so activating it can succeed.
    ///
    /// Computed by the sync library from the same binding state the service would check. Activation is
    /// only ever attempted where this is <see langword="true"/>, because a refused activation is
    /// reported as a warning and never fails the operation that triggered it.
    /// </summary>
    public bool CanEnable { get; init; }

    /// <summary>
    /// Whether the workspace's own copy of the workflow records it as running.
    /// </summary>
    public bool? IsActivatedLocally { get; init; }
}

/// <summary>A workflow activation state to apply.</summary>
public sealed record CopilotWorkflowStateChange
{
    /// <summary>The workflow to change.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>Whether it should end up running.</summary>
    public required bool Activate { get; init; }
}

/// <summary>
/// One read of an agent's connection references and the workflows that depend on them.
/// </summary>
public sealed record CopilotConnectionSnapshot
{
    /// <summary>Every reference the workspace declares or uses.</summary>
    public IReadOnlyList<CopilotConnectionView> References { get; init; } = [];

    /// <summary>Every workflow the workspace defines.</summary>
    public IReadOnlyList<CopilotWorkflowView> Workflows { get; init; } = [];
}

/// <summary>
/// An agent's connection references and the connections that can satisfy them.
/// </summary>
public interface ICopilotConnectionPort
{
    /// <summary>
    /// Reads the agent's connection references with their binding state and candidates, and the
    /// workflows that depend on them.
    /// </summary>
    /// <param name="environment">The environment holding the agent.</param>
    /// <param name="projectDirectory">The workspace declaring the references.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One view per reference, and one per workflow, from a single read.</returns>
    Task<CopilotConnectionSnapshot> ReadConnectionsAsync(CopilotEnvironmentContext environment, string projectDirectory, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ReadDeclaredConnectorsAsync(CopilotEnvironmentContext environment, string projectDirectory, CancellationToken cancellationToken);

    /// <summary>Writes bindings onto connection references.</summary>
    /// <param name="environment">The environment holding the references.</param>
    /// <param name="projectDirectory">The workspace declaring the references.</param>
    /// <param name="bindings">The bindings to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when every binding is written.</returns>
    Task BindAsync(CopilotEnvironmentContext environment, string projectDirectory, IReadOnlyList<CopilotConnectionBinding> bindings, CancellationToken cancellationToken);

    Task<string> CreateConnectionReferenceAsync(CopilotEnvironmentContext environment, string projectDirectory, string connectorInternalId, CancellationToken cancellationToken);

    Task<CopilotConnectionReferenceRemoval> RemoveConnectionReferenceAsync(CopilotEnvironmentContext environment, string projectDirectory, string logicalName, bool confirmed, CancellationToken cancellationToken);

    /// <summary>Reads the workflows the workspace defines, judging by the sync library's rule whether each can start once the given references are bound.</summary>
    /// <param name="projectDirectory">The workspace defining the workflows.</param>
    /// <param name="boundReferences">The logical names of the references bound to a confirmed connection.</param>
    /// <returns>One view per workflow, ordered by display name.</returns>
    IReadOnlyList<CopilotWorkflowView> ReadWorkflows(string projectDirectory, IReadOnlyCollection<string> boundReferences);

    /// <summary>
    /// Sets workflow activation states directly, without re-uploading the workflows.
    /// </summary>
    /// <param name="environment">The environment holding the workflows.</param>
    /// <param name="projectDirectory">The workspace defining them.</param>
    /// <param name="changes">The states to apply.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The workflows the service activated, and any it declined.</returns>
    Task<CopilotWorkflowActivation> SetWorkflowStatesAsync(CopilotEnvironmentContext environment, string projectDirectory, IReadOnlyList<CopilotWorkflowStateChange> changes, CancellationToken cancellationToken);

    /// <summary>Runs the activation pass that follows a push, binding what can be bound without asking and activating every eligible draft.</summary>
    /// <param name="environment">The environment the push targeted.</param>
    /// <param name="projectDirectory">The workspace that was pushed.</param>
    /// <param name="principal">The identity a connection has to belong to before it is bound without asking, or <see langword="null"/> to bind nothing.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What the pass observed.</returns>
    Task<CopilotWorkflowActivationReport> ActivateWorkflowsAsync(CopilotEnvironmentContext environment, string projectDirectory, string? principal, CancellationToken cancellationToken);
}

/// <summary>Decides connection references by the sync library's binding rules: what each reference is, and which connection may be bound to it without asking.</summary>
public interface IConnectionBindingRules
{
    /// <summary>Reports what is true of one reference without proposing a binding.</summary>
    /// <param name="view">The reference to read.</param>
    /// <returns>The reference's observed state.</returns>
    ConnectionPlanItem Observe(CopilotConnectionView view);

    /// <summary>Decides what a run that may write does with one reference, binding an unbound one only to a connected connection the principal owns.</summary>
    /// <param name="view">The reference to plan for, with its candidates.</param>
    /// <param name="principal">The signed-in identity a connection has to belong to, or <see langword="null"/> to bind nothing.</param>
    /// <returns>The decision.</returns>
    ConnectionPlanItem Plan(CopilotConnectionView view, string? principal);

    /// <summary>Picks the connections that can satisfy a reference, in the order the rules consider them.</summary>
    /// <param name="candidates">The connections the catalog reported.</param>
    /// <returns>The connected ones.</returns>
    IReadOnlyList<CopilotConnectionCandidate> Connected(IReadOnlyList<CopilotConnectionCandidate> candidates);
}

/// <summary>What the activation pass that follows a push observed.</summary>
public sealed record CopilotWorkflowActivationReport
{
    /// <summary>The display name of every workflow the workspace defines, in ordinal order.</summary>
    public IReadOnlyList<string> Workflows { get; init; } = [];

    /// <summary>The workflows the service reports as running.</summary>
    public IReadOnlyList<string> Activated { get; init; } = [];

    /// <summary>The explanations the service gave for refusing an activation.</summary>
    public IReadOnlyList<string> Refused { get; init; } = [];

    /// <summary>Whether the service reported each workflow it observed as running, keyed by the identifier it assigned.</summary>
    public IReadOnlyDictionary<string, bool> ObservedById { get; init; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many workflows are not known to be running.</summary>
    public int NotRunningCount { get; init; }

    /// <summary>How many connection references the pass left without a confirmed binding.</summary>
    public int UnboundConnectionCount { get; init; }
}

/// <summary>What activating an agent's connection-dependent workflows achieved.</summary>
public sealed record CopilotWorkflowActivation
{
    /// <summary>The workflows the service reports as active.</summary>
    public IReadOnlyList<string> Activated { get; init; } = [];

    /// <summary>The workflows the service declined to activate, described for the operator.</summary>
    public IReadOnlyList<string> Refused { get; init; } = [];

    /// <summary>
    /// What the service reported per workflow: its name, and whether it ended up running.
    /// </summary>
    public IReadOnlyDictionary<string, bool> Observed { get; init; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What the service reported per workflow, keyed by the identifier it assigned.
    /// </summary>
    public IReadOnlyDictionary<string, bool> ObservedById { get; init; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One connector an environment serves.</summary>
public sealed record CopilotConnectorInfo
{
    /// <summary>The connector's internal id, such as <c>shared_teams</c>.</summary>
    public required string InternalId { get; init; }

    /// <summary>The connector's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The publisher the catalog names, when it names one.</summary>
    public string? Publisher { get; init; }

    /// <summary>The licensing tier the catalog names, when it names one.</summary>
    public string? Tier { get; init; }
}

/// <summary>The connectors an environment serves.</summary>
public interface IConnectorCatalogPort
{
    Task<IReadOnlyList<CopilotConnectorInfo>> ListConnectorsAsync(CopilotEnvironmentContext environment, CancellationToken cancellationToken);

    Task<IReadOnlyList<CopilotConnectionCandidate>> ListConnectionsAsync(CopilotEnvironmentContext environment, string connectorInternalId, CancellationToken cancellationToken);

    /// <summary>Lists the internal ids of the connectors the environment serves.</summary>
    /// <param name="environment">The environment whose catalog is read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One internal id per connector.</returns>
    async Task<IReadOnlyList<string>> ListConnectorInternalIdsAsync(CopilotEnvironmentContext environment, CancellationToken cancellationToken) =>
        [.. (await ListConnectorsAsync(environment, cancellationToken).ConfigureAwait(false)).Select(connector => connector.InternalId)];
}

/// <summary>Deletes a connection from the environment that holds it.</summary>
public interface IConnectionLifecyclePort
{
    Task DeleteConnectionAsync(CopilotEnvironmentContext environment, string connectorInternalId, string connectionId, CancellationToken cancellationToken);
}
