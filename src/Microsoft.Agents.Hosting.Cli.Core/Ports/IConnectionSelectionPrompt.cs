namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>What the operator chose for one connector.</summary>
public enum ConnectionSelectionKind
{
    /// <summary>Return to the connector list without deciding.</summary>
    Back,

    /// <summary>Point the connector's references at a connection that already exists.</summary>
    Existing,

    /// <summary>Create a new connection for the connector.</summary>
    CreateNew,
}

/// <summary>The operator's answer for one connector.</summary>
/// <param name="Kind">What they chose.</param>
/// <param name="ConnectionId">The connection they chose, when <paramref name="Kind"/> is <see cref="ConnectionSelectionKind.Existing"/>.</param>
public sealed record ConnectionSelection(ConnectionSelectionKind Kind, string? ConnectionId = null);

/// <summary>One connection a connector's references can be pointed at.</summary>
public sealed record ConnectionSelectionOption
{
    /// <summary>The number that selects it.</summary>
    public required int Number { get; init; }

    /// <summary>The connection's identifier, which is what <c>--connection-id</c> takes.</summary>
    public required string ConnectionId { get; init; }

    /// <summary>The identity that owns it, when the catalog reports one.</summary>
    public string? Owner { get; init; }

    /// <summary>Whether a reference on this connector already uses it.</summary>
    public required bool IsCurrent { get; init; }
}

/// <summary>One reference a connector serves.</summary>
public sealed record ConnectionSelectionReference
{
    /// <summary>The reference's logical name.</summary>
    public required string LogicalName { get; init; }

    /// <summary>The workspace files that use it.</summary>
    public IReadOnlyList<string> Usages { get; init; } = [];
}

/// <summary>One connector on the menu.</summary>
public sealed record ConnectorChoice
{
    /// <summary>The number that selects it, stable for the whole run.</summary>
    public required int Number { get; init; }

    /// <summary>The connector's internal id.</summary>
    public required string ConnectorId { get; init; }

    /// <summary>What the operator would be deciding, or that nothing is left to decide.</summary>
    public required string Status { get; init; }

    /// <summary>The references the connector serves in this run, by logical name.</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>The connections its references are bound to, as this run has decided them.</summary>
    public IReadOnlyList<string> Connections { get; init; } = [];

    /// <summary>Whether the connector still has a reference left to bind.</summary>
    public required bool NeedsAttention { get; init; }

    /// <summary>
    /// Whether the operator can act on the connector at all, which a settled one still can be:
    /// a bound reference can be pointed at a different connection. Only a connector whose
    /// connections could not be read is closed, because creating one there risks a duplicate.
    /// </summary>
    public required bool Selectable { get; init; }
}

/// <summary>
/// The connectors a run can act on, and how much of the agent is already settled.
/// </summary>
public sealed record ConnectorMenu
{
    /// <summary>The connectors, sorted once so a number never moves.</summary>
    public IReadOnlyList<ConnectorChoice> Connectors { get; init; } = [];
}

/// <summary>One connector the operator is resolving.</summary>
public sealed record ConnectionSelectionRequest
{
    /// <summary>The connector being resolved.</summary>
    public required string ConnectorId { get; init; }

    /// <summary>The references the answer would be bound to.</summary>
    public IReadOnlyList<ConnectionSelectionReference> References { get; init; } = [];

    /// <summary>The connections already in the environment, the ones in use first.</summary>
    public IReadOnlyList<ConnectionSelectionOption> Options { get; init; } = [];
}

/// <summary>
/// Asks the operator which connection a connector's references should use.
/// </summary>
public interface IConnectionSelectionPrompt
{
    /// <summary>Shows the connectors and waits for one or more to be chosen.</summary>
    /// <param name="menu">The connectors and the counts around them.</param>
    /// <param name="cancellationToken">Cancels the prompt.</param>
    /// <returns>The indexes of the chosen connectors, in the order given, or empty to stop.</returns>
    Task<IReadOnlyList<int>> ChooseConnectorsAsync(ConnectorMenu menu, CancellationToken cancellationToken);

    /// <summary>Offers one connector's connections and waits for an answer.</summary>
    /// <param name="request">The connector, the references it serves, and the connections on offer.</param>
    /// <param name="cancellationToken">Cancels the prompt.</param>
    /// <returns>What the operator chose.</returns>
    Task<ConnectionSelection> SelectAsync(ConnectionSelectionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Says what the command is doing while the operator waits for it.
    ///
    /// The work after the menu closes reaches the service and takes seconds, which without a word
    /// reads as a command that has hung rather than one that is working.
    /// </summary>
    /// <param name="activity">What is being done, in the present participle.</param>
    void Report(string activity);
}
