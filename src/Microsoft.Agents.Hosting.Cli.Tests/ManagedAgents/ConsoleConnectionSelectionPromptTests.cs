using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ConsoleConnectionSelectionPromptTests
{
    private static ConnectionSelectionReference Reference(string logicalName, params string[] usages) =>
        new() { LogicalName = logicalName, Usages = usages };

    private static ConnectionSelectionOption Option(int number, string connectionId, string? owner = null, bool isCurrent = false) =>
        new() { Number = number, ConnectionId = connectionId, Owner = owner, IsCurrent = isCurrent };

    private static ConnectionSelectionRequest Request(params ConnectionSelectionReference[] references) =>
        new() { ConnectorId = "shared_teams", References = references.Length == 0 ? [Reference("msdyn_expensebot_teams_notify")] : references };

    private static ConnectionSelectionRequest Offering(params ConnectionSelectionOption[] options) =>
        Request() with { Options = options };

    private static ConnectorChoice Connector(int number, string connectorId, string status, int referenceCount, bool needsAttention, bool selectable = true, params string[] connections) =>
        new()
        {
            Number = number,
            ConnectorId = connectorId,
            Status = status,
            References = [.. Enumerable.Range(1, referenceCount).Select(index => $"cr_{connectorId}_{index}")],
            Connections = connections,
            NeedsAttention = needsAttention,
            Selectable = selectable,
        };

    private static ConnectorMenu Menu(params ConnectorChoice[] connectors) =>
        new() { Connectors = connectors };

    private static async Task<(ConnectionSelection Selection, IReadOnlyList<string> Lines)> Select(string typed, ConnectionSelectionRequest request)
    {
        var output = new StringWriter();
        var selection = await new ConsoleConnectionSelectionPrompt(new StringReader(typed), output).SelectAsync(request, CancellationToken.None);

        return (selection, output.ToString().Split(Environment.NewLine));
    }

    private static async Task<(IReadOnlyList<int> Chosen, IReadOnlyList<string> Lines)> Choose(string typed, ConnectorMenu menu)
    {
        var output = new StringWriter();
        var chosen = await new ConsoleConnectionSelectionPrompt(new StringReader(typed), output).ChooseConnectorsAsync(menu, CancellationToken.None);

        return (chosen, output.ToString().Split(Environment.NewLine));
    }

    [Fact]
    public async Task Choose_ListsEveryConnectorWithItsNumberStatusConnectionAndReferences()
    {
        var (_, lines) = await Choose(
            "q",
            Menu(
                Connector(1, "spo", "connected (1), not connected (1)", 2, needsAttention: true, connections: "conn-1"),
                Connector(2, "teams", "connected", 1, needsAttention: false, connections: "conn-2")));

        Assert.Equal(
            [
                "1 of 2 connectors are not connected.",
                "  No     CONNECTOR  STATUS                            CONNECTION  REFERENCES",
                "  [1] *  spo        connected (1), not connected (1)  conn-1      2",
                "  [2]    teams      connected                         conn-2      1",
                "  [q]    Quit",
                string.Empty,
                "Select a connector to change it: ",
            ],
            lines);
    }

    [Fact]
    public async Task Choose_AConnectorBoundToNothingShowsADash()
    {
        var (_, lines) = await Choose("q", Menu(Connector(1, "teams", "not connected", 1, needsAttention: true)));

        Assert.Contains(lines, line => line.Contains("teams", StringComparison.Ordinal) && line.Contains("  -  ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Choose_WhenNothingNeedsAttention_SaysSoAndMakesQuitTheDefault()
    {
        var (_, lines) = await Choose(
            "q",
            Menu(
                Connector(1, "spo", "connected", 1, needsAttention: false),
                Connector(2, "teams", "connected", 1, needsAttention: false)));

        Assert.Equal("All 2 connector(s) are connected.", lines[0]);
        Assert.Contains(lines, line => line == "  [q] *  Quit");
        Assert.DoesNotContain(lines, line => line.Contains("[1] *", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Choose_WithAConnectorThatCannotBeActedOn_CountsItApartFromTheBoundOnes()
    {
        var (_, lines) = await Choose(
            "q",
            Menu(
                Connector(1, "spo", "connected", 1, needsAttention: false),
                Connector(2, "teams", "not connected", 2, needsAttention: false, selectable: false)));

        Assert.Equal("All 2 connector(s) are connected, and 1 could not be checked.", lines[0]);
    }

    [Fact]
    public async Task Choose_WithAConnectorThatCannotBeActedOnAndWorkOutstanding_CountsBoth()
    {
        var (_, lines) = await Choose(
            "q",
            Menu(
                Connector(1, "spo", "not connected", 1, needsAttention: true),
                Connector(2, "teams", "not connected", 1, needsAttention: false, selectable: false)));

        Assert.Equal("1 of 2 connectors are not connected, and 1 could not be checked.", lines[0]);
    }

    [Fact]
    public async Task Choose_WithNoAnswer_TakesTheFirstConnectorNeedingAttention()
    {
        var (chosen, _) = await Choose(
            Environment.NewLine,
            Menu(
                Connector(1, "spo", "not connected", 1, needsAttention: true),
                Connector(2, "teams", "not connected", 1, needsAttention: true)));

        Assert.Equal([0], chosen);
    }

    [Fact]
    public async Task Choose_WithNoAnswerAndNothingNeedingAttention_Quits()
    {
        var (chosen, _) = await Choose(
            Environment.NewLine,
            Menu(
                Connector(1, "spo", "connected", 1, needsAttention: false),
                Connector(2, "teams", "connected", 1, needsAttention: false)));

        Assert.Empty(chosen);
    }

    [Fact]
    public async Task Choose_WithNoInputAtAll_Quits()
    {
        var output = new StringWriter();
        var chosen = await new ConsoleConnectionSelectionPrompt(TextReader.Null, output)
            .ChooseConnectorsAsync(Menu(Connector(1, "spo", "not connected", 1, needsAttention: true)), CancellationToken.None);

        Assert.Empty(chosen);
    }

    [Fact]
    public async Task Choose_ANumber_ReturnsThatConnectorsIndex()
    {
        var (chosen, _) = await Choose(
            "3",
            Menu(
                Connector(1, "shared_sharepointonline", "1 of 2 connected", 2, needsAttention: true),
                Connector(2, "shared_teams", "connected", 3, needsAttention: false),
                Connector(3, "shared_office365", "not connected", 3, needsAttention: true)));

        Assert.Equal([2], chosen);
    }

    [Fact]
    public async Task Choose_ABoundConnector_IsAcceptedSoItsConnectionCanBeChanged()
    {
        var (chosen, lines) = await Choose(
            "2",
            Menu(
                Connector(1, "shared_sharepointonline", "not connected", 1, needsAttention: true),
                Connector(2, "shared_teams", "connected", 3, needsAttention: false)));

        Assert.Equal([1], chosen);
        Assert.DoesNotContain(lines, line => line.Contains("cannot be changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Choose_AConnectorWhoseConnectionsCouldNotBeRead_SaysSoAndAsksAgain()
    {
        var (chosen, lines) = await Choose(
            $"2{Environment.NewLine}1",
            Menu(
                Connector(1, "shared_sharepointonline", "not connected", 1, needsAttention: true),
                Connector(2, "shared_teams", "unknown", 3, needsAttention: false, selectable: false)));

        Assert.Contains(lines, line => line.Contains("shared_teams cannot be changed: its connections could not be read.", StringComparison.Ordinal));
        Assert.Equal([0], chosen);
    }

    [Theory]
    [InlineData("1,3")]
    [InlineData("1;3")]
    [InlineData(" 1 , 3 ")]
    [InlineData("1,,3")]
    public async Task Choose_SeveralNumbers_ReturnsThemAll(string typed)
    {
        var (chosen, _) = await Choose(
            typed,
            Menu(
                Connector(1, "shared_sharepointonline", "not connected", 1, needsAttention: true),
                Connector(2, "shared_teams", "not connected", 1, needsAttention: true),
                Connector(3, "shared_office365", "not connected", 1, needsAttention: true)));

        Assert.Equal([0, 2], chosen);
    }

    [Fact]
    public async Task Choose_SeveralNumbers_KeepsTheOrderTyped()
    {
        var (chosen, _) = await Choose(
            "3,1",
            Menu(
                Connector(1, "shared_sharepointonline", "not connected", 1, needsAttention: true),
                Connector(2, "shared_teams", "not connected", 1, needsAttention: true),
                Connector(3, "shared_office365", "not connected", 1, needsAttention: true)));

        Assert.Equal([2, 0], chosen);
    }

    [Fact]
    public async Task Choose_TheSameNumberTwice_SelectsItOnce()
    {
        var (chosen, _) = await Choose(
            "2,2",
            Menu(
                Connector(1, "a", "not connected", 1, needsAttention: true),
                Connector(2, "b", "not connected", 1, needsAttention: true)));

        Assert.Equal([1], chosen);
    }

    [Theory]
    [InlineData("q")]
    [InlineData("Q")]
    [InlineData("")]
    public async Task Choose_QuitOrNothing_ReturnsNothing(string typed)
    {
        var (chosen, _) = await Choose(typed, Menu(Connector(1, "shared_teams", "not connected", 1, needsAttention: true)));

        Assert.Empty(chosen);
    }

    [Fact]
    public async Task Choose_ANumberThatIsNotListed_AsksAgain()
    {
        var (chosen, lines) = await Choose(
            $"9{Environment.NewLine}1",
            Menu(Connector(1, "shared_teams", "not connected", 1, needsAttention: true)));

        Assert.Contains(lines, line => line.Contains("There is no connector 9.", StringComparison.Ordinal));
        Assert.Equal([0], chosen);
    }

    [Fact]
    public async Task Choose_SomethingThatIsNotANumber_AsksAgain()
    {
        var (chosen, lines) = await Choose(
            $"teams{Environment.NewLine}1",
            Menu(Connector(1, "shared_teams", "not connected", 1, needsAttention: true)));

        Assert.Contains(lines, line => line.Contains("'teams' is not a connector number.", StringComparison.Ordinal));
        Assert.Equal([0], chosen);
    }

    [Fact]
    public async Task Choose_OneBadNumberAmongGoodOnes_RejectsTheWholeAnswer()
    {
        var (chosen, lines) = await Choose(
            $"1,9{Environment.NewLine}2",
            Menu(
                Connector(1, "a", "not connected", 1, needsAttention: true),
                Connector(2, "b", "not connected", 1, needsAttention: true)));

        Assert.Contains(lines, line => line.Contains("There is no connector 9.", StringComparison.Ordinal));
        Assert.Equal([1], chosen);
    }

    [Fact]
    public async Task Select_WithNoConnections_NamesTheConnectorItsReferencesAndTheTwoActions()
    {
        var (_, lines) = await Select(
            "n",
            Request(
                Reference("msdyn_expensebot_teams_notify", "topics/NotifyManager.mcs.yml"),
                Reference("msdyn_expensebot_teams_escalate", "workflows/Escalate/workflow.json")));

        Assert.Equal(
            [
                "shared_teams   2 references",
                string.Empty,
                "  REFERENCE                        USED BY",
                "  msdyn_expensebot_teams_notify    topics/NotifyManager.mcs.yml",
                "  msdyn_expensebot_teams_escalate  workflows/Escalate/workflow.json",
                string.Empty,
                "  No     ACTION",
                "  [n] *  Create a new connection",
                "  [b]    Back",
                string.Empty,
                "Select a connection: ",
            ],
            lines);
    }

    [Fact]
    public async Task Select_WithReferencesThatHaveNoUsages_OmitsTheUsedByColumn()
    {
        var (_, lines) = await Select(
            "n",
            Request(
                Reference("cr_meetingroombookeragent.shared_rss.0cad052bfbcd4a4bbc85f6f57b919ebf"),
                Reference("cr_meetingroombookeragent.shared_rss.3bef10a7a4d7442b84bdd78efa3efbf0")));

        Assert.Equal(
            [
                "shared_teams   2 references",
                string.Empty,
                "  REFERENCE",
                "  cr_meetingroombookeragent.shared_rss.0cad052bfbcd4a4bbc85f6f57b919ebf",
                "  cr_meetingroombookeragent.shared_rss.3bef10a7a4d7442b84bdd78efa3efbf0",
                string.Empty,
            ],
            lines.Take(6));
    }

    [Fact]
    public async Task Select_NamesEveryReferenceExactlyOnce()
    {
        var (_, lines) = await Select(
            "n",
            Request(Reference("cr_meetingroombookeragent.shared_rss.0cad052bfbcd4a4bbc85f6f57b919ebf")));

        Assert.Equal(1, lines.Count(line => line.Contains("cr_meetingroombookeragent.shared_rss.0cad052bfbcd4a4bbc85f6f57b919ebf", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Select_WhereOnlySomeReferencesAreUsed_DashesTheUnusedOnes()
    {
        var (_, lines) = await Select("n", Request(Reference("cr_a", "topics/A.mcs.yml"), Reference("cr_b")));

        Assert.Equal(
            [
                "  REFERENCE  USED BY",
                "  cr_a       topics/A.mcs.yml",
                "  cr_b       -",
            ],
            lines.Skip(2).Take(3));
    }

    [Fact]
    public async Task Select_WithOneReference_CountsItInTheSingular()
    {
        var (_, lines) = await Select("n", Request(Reference("cr_only")));

        Assert.Equal("shared_teams   1 reference", lines[0]);
    }

    [Fact]
    public async Task Select_WithConnections_ListsThemAboveTheActionsAndStarsTheFirst()
    {
        var (_, lines) = await Select(
            "b",
            Offering(
                Option(1, "conn-alpha", "alice@contoso.com", isCurrent: true),
                Option(2, "conn-beta", "bob@contoso.com")));

        Assert.Equal(
            [
                "  No     CONNECTION               OWNER              IN USE",
                "  [1] *  conn-alpha               alice@contoso.com  in use",
                "  [2]    conn-beta                bob@contoso.com",
                "  [n]    Create a new connection",
                "  [b]    Back",
            ],
            lines.Skip(5).Take(5));
    }

    [Fact]
    public async Task Choose_CountsReferencesInsteadOfNamingThem()
    {
        var (_, lines) = await Choose("q", Menu(Connector(1, "spo", "not connected", 3, needsAttention: true)));

        Assert.DoesNotContain(lines, line => line.Contains("cr_spo_1", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("spo", StringComparison.Ordinal) && line.EndsWith('3'));
    }

    [Fact]
    public async Task Select_ANumber_ReturnsThatConnection()
    {
        var (selection, _) = await Select("2", Offering(Option(1, "conn-alpha"), Option(2, "conn-beta")));

        Assert.Equal(ConnectionSelectionKind.Existing, selection.Kind);
        Assert.Equal("conn-beta", selection.ConnectionId);
    }

    [Fact]
    public async Task Select_Enter_TakesTheLeadingConnection()
    {
        var (selection, _) = await Select(Environment.NewLine, Offering(Option(1, "conn-alpha", isCurrent: true), Option(2, "conn-beta")));

        Assert.Equal(ConnectionSelectionKind.Existing, selection.Kind);
        Assert.Equal("conn-alpha", selection.ConnectionId);
    }

    [Fact]
    public async Task Select_TheCreateToken_CreatesEvenWhenConnectionsExist()
    {
        var (selection, _) = await Select("n", Offering(Option(1, "conn-alpha")));

        Assert.Equal(ConnectionSelectionKind.CreateNew, selection.Kind);
        Assert.Null(selection.ConnectionId);
    }

    [Theory]
    [InlineData("n")]
    [InlineData("N")]
    [InlineData("")]
    public async Task Select_TheCreateTokenOrEnter_CreatesWhenNoConnectionExists(string typed)
    {
        var (selection, _) = await Select(typed + Environment.NewLine, Request());

        Assert.Equal(ConnectionSelectionKind.CreateNew, selection.Kind);
    }

    [Theory]
    [InlineData("b")]
    [InlineData("B")]
    public async Task Select_TheBackToken_GoesBack(string typed)
    {
        var (selection, _) = await Select(typed, Request());

        Assert.Equal(ConnectionSelectionKind.Back, selection.Kind);
    }

    [Fact]
    public async Task Select_WithNoInputAtAll_GoesBackRatherThanTakingTheDefault()
    {
        var selection = await new ConsoleConnectionSelectionPrompt(TextReader.Null, new StringWriter())
            .SelectAsync(Offering(Option(1, "conn-alpha")), CancellationToken.None);

        Assert.Equal(ConnectionSelectionKind.Back, selection.Kind);
        Assert.Null(selection.ConnectionId);
    }

    [Fact]
    public async Task Select_WithAnAnswerThatIsNotAnOption_AsksAgain()
    {
        var (selection, lines) = await Select($"maybe{Environment.NewLine}b", Request());

        Assert.Contains(lines, line => line.Contains("'maybe' is not one of the options.", StringComparison.Ordinal));
        Assert.Equal(ConnectionSelectionKind.Back, selection.Kind);
    }

    [Fact]
    public async Task Select_ANumberThatIsNotOnOffer_AsksAgain()
    {
        var (selection, lines) = await Select($"7{Environment.NewLine}b", Offering(Option(1, "conn-alpha")));

        Assert.Contains(lines, line => line.Contains("'7' is not one of the options.", StringComparison.Ordinal));
        Assert.Equal(ConnectionSelectionKind.Back, selection.Kind);
    }

    [Fact]
    public async Task Select_AReferenceWithNoUsages_RendersWithoutATrailingGap()
    {
        var (_, lines) = await Select("n", Request(Reference("cr_sql")));

        Assert.Contains(lines, line => line == "  cr_sql");
    }
}
