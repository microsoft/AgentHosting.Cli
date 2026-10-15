using System.Globalization;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Ports;

/// <summary>
/// Lists an agent's connectors on the console and reads which one to resolve, then which connection
/// it should use.
/// </summary>
public sealed class ConsoleConnectionSelectionPrompt : IConnectionSelectionPrompt
{
    private const string CreateToken = "n";
    private const string BackToken = "b";
    private const string QuitToken = "q";
    private const string InUseLabel = "in use";

    private readonly TextReader _input;
    private readonly TextWriter _output;

    /// <summary>Creates the prompt.</summary>
    /// <param name="input">Where the operator's answer is read from.</param>
    /// <param name="output">Where the options are written.</param>
    public ConsoleConnectionSelectionPrompt(TextReader input, TextWriter output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ChooseConnectorsAsync(ConnectorMenu menu, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(menu);
        cancellationToken.ThrowIfCancellationRequested();

        await WriteMenuAsync(menu).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            await _output.WriteAsync("Select a connector to change it: ").ConfigureAwait(false);

            var answer = (await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();

            if (answer is null || answer.Equals(QuitToken, StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            if (answer.Length == 0)
            {
                var fallback = FirstNeedingAttention(menu);

                return fallback < 0 ? [] : [fallback];
            }

            var chosen = new List<int>();
            var rejected = Reject(answer, menu, chosen);

            if (rejected is null)
            {
                return chosen;
            }

            await _output.WriteLineAsync(rejected).ConfigureAwait(false);
        }

        return [];
    }

    /// <summary>
    /// Reads one or more connector numbers, and says which one spoiled the answer.
    /// </summary>
    /// <param name="answer">What the operator typed.</param>
    /// <param name="menu">The connectors on offer.</param>
    /// <param name="chosen">Receives the selected indexes, in the order given, without repeats.</param>
    /// <returns>The complaint to show, or <see langword="null"/> when every number was usable.</returns>
    private static string? Reject(string answer, ConnectorMenu menu, List<int> chosen)
    {
        foreach (var token in answer.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return $"'{token}' is not a connector number.";
            }

            var index = menu.Connectors.Select((connector, position) => (connector, position)).FirstOrDefault(entry => entry.connector.Number == number, (null!, -1)).position;

            if (index < 0)
            {
                return $"There is no connector {number}.";
            }

            if (!menu.Connectors[index].Selectable)
            {
                return $"{menu.Connectors[index].ConnectorId} cannot be changed: its connections could not be read.";
            }

            if (!chosen.Contains(index))
            {
                chosen.Add(index);
            }
        }

        return chosen.Count == 0 ? $"'{answer}' is not a connector number." : null;
    }

    /// <inheritdoc />
    public async Task<ConnectionSelection> SelectAsync(ConnectionSelectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        await WriteOptionsAsync(request).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            await _output.WriteAsync("Select a connection: ").ConfigureAwait(false);

            var answer = (await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();

            if (answer is null || answer.Equals(BackToken, StringComparison.OrdinalIgnoreCase))
            {
                return new ConnectionSelection(ConnectionSelectionKind.Back);
            }

            if (answer.Equals(CreateToken, StringComparison.OrdinalIgnoreCase))
            {
                return new ConnectionSelection(ConnectionSelectionKind.CreateNew);
            }

            if (answer.Length == 0)
            {
                return Default(request);
            }

            var chosen = request.Options.FirstOrDefault(option => int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && option.Number == number);

            if (chosen is not null)
            {
                return new ConnectionSelection(ConnectionSelectionKind.Existing, chosen.ConnectionId);
            }

            await _output.WriteLineAsync($"'{answer}' is not one of the options.").ConfigureAwait(false);
        }

        return new ConnectionSelection(ConnectionSelectionKind.Back);
    }

    /// <summary>
    /// What a bare Enter takes: the leading connection where one exists, and creating where none does.
    /// </summary>
    private static ConnectionSelection Default(ConnectionSelectionRequest request) =>
        request.Options.Count == 0
            ? new ConnectionSelection(ConnectionSelectionKind.CreateNew)
            : new ConnectionSelection(ConnectionSelectionKind.Existing, request.Options[0].ConnectionId);

    /// <inheritdoc />
    public void Report(string activity) => _output.WriteLine(activity);

    /// <summary>
    /// Lists the connectors, settled ones included, so progress is visible, a number never moves, and
    /// a binding that is already good can still be changed.
    /// </summary>
    private async Task WriteMenuAsync(ConnectorMenu menu)
    {
        var fallback = FirstNeedingAttention(menu);

        await _output.WriteLineAsync(Headline(menu)).ConfigureAwait(false);

        string[][] rows =
        [
            .. menu.Connectors.Select((connector, position) => new[] { Token(connector.Number.ToString(CultureInfo.InvariantCulture), position == fallback), connector.ConnectorId, connector.Status, DescribeConnections(connector.Connections), connector.References.Count.ToString(CultureInfo.InvariantCulture) }),
            [Token(QuitToken, fallback < 0), "Quit", string.Empty, string.Empty, string.Empty],
        ];

        foreach (var line in TextGrid.RenderRows(["No", "CONNECTOR", "STATUS", "CONNECTION", "REFERENCES"], rows))
        {
            await _output.WriteLineAsync(line.TrimEnd()).ConfigureAwait(false);
        }

        await _output.WriteLineAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Says how many connectors still need a connection, counted in the same unit as the rows below.
    ///
    /// Counted by connector rather than by reference because the connector is what the operator
    /// selects: a count of references would not match the numbered rows it sits above. A connector
    /// whose connections could not be read is counted apart, being neither settled nor selectable.
    /// </summary>
    private static string Headline(ConnectorMenu menu)
    {
        var outstanding = menu.Connectors.Count(connector => connector.NeedsAttention);
        var unreadable = menu.Connectors.Count(connector => !connector.Selectable);

        return outstanding == 0
            ? $"All {menu.Connectors.Count} connector(s) are connected{Unreadable(unreadable)}."
            : $"{outstanding} of {menu.Connectors.Count} connectors are not connected{Unreadable(unreadable)}.";
    }

    /// <summary>Names the connectors nothing could be learned about, when there are any.</summary>
    private static string Unreadable(int unreadable) =>
        unreadable == 0 ? string.Empty : $", and {unreadable} could not be checked";

    /// <summary>
    /// Finds the connector a bare Enter takes, which is the first one still needing a decision, and
    /// no connector at all once none does: leaving is the right default when nothing is outstanding.
    /// </summary>
    private static int FirstNeedingAttention(ConnectorMenu menu)
    {
        for (var position = 0; position < menu.Connectors.Count; position++)
        {
            if (menu.Connectors[position].NeedsAttention)
            {
                return position;
            }
        }

        return -1;
    }

    /// <summary>
    /// Brackets the key that selects a row, and stars the one a bare Enter takes.
    /// </summary>
    private static string Token(string key, bool isDefault) => isDefault ? $"[{key}] *" : $"[{key}]";

    private static string ReferenceCount(int references) => references == 1 ? "1 reference" : $"{references} references";

    private static string DescribeConnections(IReadOnlyList<string> connections) =>
        connections.Count == 0 ? "-" : string.Join(", ", connections);

    /// <summary>
    /// Names every reference the answer would be bound to, then the connections and the two actions.
    /// </summary>
    private async Task WriteOptionsAsync(ConnectionSelectionRequest request)
    {
        await _output.WriteLineAsync($"{request.ConnectorId}   {ReferenceCount(request.References.Count)}").ConfigureAwait(false);

        foreach (var line in ReferenceLines(request.References))
        {
            await _output.WriteLineAsync(line.TrimEnd()).ConfigureAwait(false);
        }

        await _output.WriteLineAsync().ConfigureAwait(false);

        foreach (var line in ActionLines(request.Options))
        {
            await _output.WriteLineAsync(line.TrimEnd()).ConfigureAwait(false);
        }

        await _output.WriteLineAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Renders the connections on offer above the two actions, and drops the connection columns
    /// entirely where none exists, so a connector that can only be created for reads as such.
    /// </summary>
    private static IEnumerable<string> ActionLines(IReadOnlyList<ConnectionSelectionOption> options) =>
        options.Count == 0
            ? TextGrid.RenderRows(["No", "ACTION"], [[Token(CreateToken, isDefault: true), "Create a new connection"], [Token(BackToken, isDefault: false), "Back"]])
            : TextGrid.RenderRows(
                ["No", "CONNECTION", "OWNER", "IN USE"],
                [
                    .. options.Select(option => new[] { Token(option.Number.ToString(CultureInfo.InvariantCulture), option.Number == 1), option.ConnectionId, option.Owner ?? string.Empty, option.IsCurrent ? InUseLabel : string.Empty }),
                    [Token(CreateToken, isDefault: false), "Create a new connection", string.Empty, string.Empty],
                    [Token(BackToken, isDefault: false), "Back", string.Empty, string.Empty],
                ]);

    private static IReadOnlyList<string> ReferenceLines(IReadOnlyList<ConnectionSelectionReference> references) => references.Count == 0 ? [] : [string.Empty, .. ReferenceTable(references)];

    private static IReadOnlyList<string> ReferenceTable(IReadOnlyList<ConnectionSelectionReference> references) =>
        references.Any(reference => reference.Usages.Count > 0)
            ? TextGrid.RenderRows(["REFERENCE", "USED BY"], [.. references.Select(reference => new[] { reference.LogicalName, DescribeConnections(reference.Usages) })])
            : TextGrid.RenderRows(["REFERENCE"], [.. references.Select(reference => new[] { reference.LogicalName })]);
}
