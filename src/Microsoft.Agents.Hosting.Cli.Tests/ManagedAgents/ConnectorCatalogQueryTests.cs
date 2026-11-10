using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ConnectorCatalogQueryTests
{
    private const string ListCommand = "msagent deployment connection connectors --deployment-name dev --filter <text>";

    private static CopilotConnectorInfo Connector(string internalId, string displayName, string? publisher = null, string? tier = null) =>
        new() { InternalId = internalId, DisplayName = displayName, Publisher = publisher, Tier = tier };

    private static IReadOnlyList<CopilotConnectorInfo> Catalog() =>
    [
        Connector("shared_teams", "Microsoft Teams", "Microsoft", "Standard"),
        Connector("shared_msnweather", "MSN Weather", "Microsoft", "Standard"),
        Connector("shared_cre98-5fweather-20agent-202-5fc6b0b801e19a56f2", "Weather Agent 2", "Hoa Nguyen", "Standard"),
        Connector("shared_nationalweatherservice", "National Weather Service", "Troy Taylor", "Premium"),
    ];

    [Fact]
    public void Query_WithNoSelectors_ReturnsEveryConnector()
    {
        var page = ConnectorCatalogQuery.Query(Catalog(), null, null, null, []);

        Assert.Equal(4, page.Connectors.Count);
        Assert.False(page.Truncated);
        Assert.Equal(ConnectorCatalogQuery.DefaultLimit, page.Limit);
    }

    [Fact]
    public void Query_FilterMatchesTheDisplayName()
    {
        var page = ConnectorCatalogQuery.Query(Catalog(), "Teams", null, null, []);

        Assert.Equal("shared_teams", Assert.Single(page.Connectors).InternalId);
    }

    [Fact]
    public void Query_FilterMatchesTheInternalId()
    {
        var page = ConnectorCatalogQuery.Query(Catalog(), "msnweather", null, null, []);

        Assert.Equal("shared_msnweather", Assert.Single(page.Connectors).InternalId);
    }

    [Fact]
    public void Query_FilterMatchesThePublisher()
    {
        var page = ConnectorCatalogQuery.Query(Catalog(), "Troy Taylor", null, null, []);

        Assert.Equal("shared_nationalweatherservice", Assert.Single(page.Connectors).InternalId);
    }

    [Fact]
    public void Query_FilterIsCaseInsensitive()
    {
        Assert.Equal(
            ConnectorCatalogQuery.Query(Catalog(), "TEAMS", null, null, []).Connectors.Select(connector => connector.InternalId),
            ConnectorCatalogQuery.Query(Catalog(), "teams", null, null, []).Connectors.Select(connector => connector.InternalId));
    }

    [Fact]
    public void Query_FilterReachesACustomConnectorByName()
    {
        var page = ConnectorCatalogQuery.Query(Catalog(), "weathe", null, null, []);

        Assert.Equal(3, page.Connectors.Count);
        Assert.Contains(page.Connectors, connector => connector.DisplayName == "Weather Agent 2");
    }

    [Fact]
    public void Query_FilterAnchoredLikeAPrefixStillWorks()
    {
        Assert.Equal(4, ConnectorCatalogQuery.Query(Catalog(), "shared_", null, null, []).Connectors.Count);
    }

    [Fact]
    public void Query_ConnectorMatchesExactly()
    {
        var page = ConnectorCatalogQuery.Query(Catalog(), null, "shared_teams", null, []);

        Assert.Equal("shared_teams", Assert.Single(page.Connectors).InternalId);
    }

    [Fact]
    public void Query_ConnectorAcceptsAProviderPath()
    {
        var page = ConnectorCatalogQuery.Query(Catalog(), null, "/providers/Microsoft.PowerApps/apis/shared_teams", null, []);

        Assert.Equal("shared_teams", Assert.Single(page.Connectors).InternalId);
    }

    [Fact]
    public void Query_ConnectorThatMissesReturnsNothing()
    {
        Assert.Empty(ConnectorCatalogQuery.Query(Catalog(), null, "shared_nothing", null, []).Connectors);
    }

    [Fact]
    public void Query_FilterAndConnectorNarrowTogether()
    {
        Assert.Empty(ConnectorCatalogQuery.Query(Catalog(), "weather", "shared_teams", null, []).Connectors);
    }

    [Fact]
    public void Query_TakesOneRowBeyondTheLimitToDetectTruncation()
    {
        var page = ConnectorCatalogQuery.Query(Catalog(), null, null, 2, []);

        Assert.Equal(2, page.Connectors.Count);
        Assert.True(page.Truncated);
        Assert.Equal(2, page.Limit);
    }

    [Fact]
    public void Query_AFullPageIsNotReportedAsTruncated()
    {
        Assert.False(ConnectorCatalogQuery.Query(Catalog(), null, null, 4, []).Truncated);
    }

    [Fact]
    public void Query_ConnectorsAlreadyInUseSortFirst()
    {
        var page = ConnectorCatalogQuery.Query(Catalog(), null, null, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "shared_nationalweatherservice" });

        Assert.Equal("shared_nationalweatherservice", page.Connectors[0].InternalId);
    }

    [Fact]
    public void Query_AnEmptyCatalogReturnsNothing()
    {
        var page = ConnectorCatalogQuery.Query([], "teams", null, null, []);

        Assert.Empty(page.Connectors);
        Assert.False(page.Truncated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Query_RejectsANonPositiveLimit(int limit)
    {
        var failure = Assert.Throws<UsageException>(() => ConnectorCatalogQuery.Query(Catalog(), null, null, limit, []));

        Assert.Contains("--limit must be a positive integer", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("shared_nothing")]
    [InlineData("/providers/Microsoft.PowerApps/apis/shared_nothing")]
    public void NotServed_NamesTheConnectorTheEnvironmentDoesNotServe(string connectorId)
    {
        var failure = Assert.IsType<ManagedAgentException>(ConnectorCatalogQuery.NotServed(Catalog(), connectorId, "env-1", ListCommand));

        Assert.Equal(11049, failure.ErrorCode);
        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Equal("connector-not-served", failure.ErrorKind);
        Assert.Contains("serves no connector 'shared_nothing'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("env-1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NotServed_NamesTheClosestAlternatives()
    {
        var failure = ConnectorCatalogQuery.NotServed(Catalog(), "weather", "env-1", ListCommand);

        Assert.Contains("Did you mean", failure.Remediation, StringComparison.Ordinal);
        Assert.Contains("shared_msnweather", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void NotServed_PointsAtTheListingWhenNothingIsClose()
    {
        var failure = ConnectorCatalogQuery.NotServed(Catalog(), "zzz", "env-1", ListCommand);

        Assert.Contains(ListCommand, failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void Find_ReturnsNullForAConnectorTheEnvironmentDoesNotServe()
    {
        Assert.Null(ConnectorCatalogQuery.Find(Catalog(), "shared_nothing"));
    }

    [Fact]
    public void Query_TheLargestLimitStillReturnsTheCatalog()
    {
        var page = ConnectorCatalogQuery.Query(Catalog(), null, null, int.MaxValue, []);

        Assert.Equal(Catalog().Count, page.Connectors.Count);
        Assert.False(page.Truncated);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 1)]
    public void Query_ALimitBeyondTheCatalogNeverTruncates(int limit)
    {
        var page = ConnectorCatalogQuery.Query(Catalog(), null, null, limit, []);

        Assert.Equal(Catalog().Count, page.Connectors.Count);
        Assert.False(page.Truncated);
    }

    [Fact]
    public void RequireConnected_ReturnsTheConnectionTheConnectorServes()
    {
        Assert.Equal("conn-b", RequireConnected([Connection("conn-a"), Connection("conn-b")], "conn-b").ConnectionId);
    }

    [Fact]
    public void RequireConnected_ReturnsTheIdTheServiceReportsRatherThanTheOneAskedFor()
    {
        Assert.Equal("conn-A", RequireConnected([Connection("conn-A")], "CONN-a").ConnectionId);
    }

    [Fact]
    public void RequireConnected_RejectsAConnectionTheConnectorDoesNotServe()
    {
        var failure = Assert.Throws<ManagedAgentException>(() => RequireConnected([Connection("conn-a")], "conn-z"));

        Assert.Equal(11050, failure.ErrorCode);
        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Equal("connection-not-a-candidate", failure.ErrorKind);
        Assert.Contains("shared_teams", failure.Message, StringComparison.Ordinal);
        Assert.Contains("conn-a", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Choose a Connected connection", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireConnected_SaysSoWhenTheConnectorHasNoConnections()
    {
        var failure = Assert.Throws<ManagedAgentException>(() => RequireConnected([], "conn-z"));

        Assert.Equal(11050, failure.ErrorCode);
        Assert.Contains("no connections", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Create a connection", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireConnected_RejectsAConnectionThatIsNotConnected()
    {
        var failure = Assert.Throws<UsageException>(() => RequireConnected([Connection("conn-broken", "Error")], "conn-broken"));

        Assert.Contains("Error", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Choose a Connected connection", failure.Message, StringComparison.Ordinal);
    }

    private static CopilotConnectionCandidate RequireConnected(IReadOnlyList<CopilotConnectionCandidate> candidates, string connectionId) => ConnectionReadinessPlanner.RequireConnected(candidates, ManagedAgentFixtures.Rules.Connected(candidates), connectionId, "connector 'shared_teams'");

    private static CopilotConnectionCandidate Connection(string connectionId, string status = "Connected") =>
        new() { ConnectionId = connectionId, DisplayName = connectionId, Status = status };
}
