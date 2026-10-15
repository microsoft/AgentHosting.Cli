using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ConnectionCreationFlowTests
{
    private const string CustomConnector = "shared_weather-5f0123456789abcdef";

    private static ConnectionCreationFlow Flow(FakeConnectorCatalogPort catalog, ScriptedConsentFlow consentFlow) =>
        new(ManagedAgentFixtures.ConnectorReadiness(catalog), consentFlow);

    private static ScriptedConsentFlow Consent()
    {
        var consentFlow = new ScriptedConsentFlow();

        consentFlow.Outcomes.Enqueue(new ConnectionConsentResult(ConnectionConsentStatus.Created, "conn-1", "conn-1", "operator@contoso.com", null));

        return consentFlow;
    }

    private static ConnectorResolution Resolution(string internalId, IReadOnlyList<CopilotConnectionCandidate>? connections) =>
        new(new CopilotConnectorInfo { InternalId = internalId, DisplayName = internalId }, connections);

    [Fact]
    public async Task Create_WhenResolvingAlreadyProvedTheConnectorServable_NeverPollsTheCatalog()
    {
        var catalog = new FakeConnectorCatalogPort();
        var consentFlow = Consent();

        var result = await Flow(catalog, consentFlow).CreateAsync(ManagedAgentFixtures.EnvironmentContext(), Resolution(CustomConnector, []), openBrowser: false, CancellationToken.None);

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.Equal(CustomConnector, Assert.Single(consentFlow.RequestedConnectors));
        Assert.Equal(0, catalog.Calls);
    }

    [Fact]
    public async Task Create_WhenResolvingReadNoConnections_WaitsForTheCatalog()
    {
        var catalog = new FakeConnectorCatalogPort();

        catalog.InternalIds.Add(CustomConnector);

        await Flow(catalog, Consent()).CreateAsync(ManagedAgentFixtures.EnvironmentContext(), Resolution(CustomConnector, null), openBrowser: false, CancellationToken.None);

        Assert.True(catalog.Calls > 0);
    }

    [Fact]
    public async Task Create_ByConnectorId_WaitsForTheCatalog()
    {
        var catalog = new FakeConnectorCatalogPort();

        catalog.InternalIds.Add(CustomConnector);

        await Flow(catalog, Consent()).CreateAsync(ManagedAgentFixtures.EnvironmentContext(), CustomConnector, openBrowser: false, CancellationToken.None);

        Assert.True(catalog.Calls > 0);
    }

    [Fact]
    public async Task Create_ASharedConnectorIsNeverWaitedForEitherWay()
    {
        var catalog = new FakeConnectorCatalogPort();

        await Flow(catalog, Consent()).CreateAsync(ManagedAgentFixtures.EnvironmentContext(), Resolution("shared_teams", null), openBrowser: false, CancellationToken.None);

        Assert.Equal(0, catalog.Calls);
    }

    [Fact]
    public async Task Create_CarriesTheBrowserPreferenceToTheConsentFlow()
    {
        var consentFlow = Consent();

        await Flow(new FakeConnectorCatalogPort(), consentFlow).CreateAsync(ManagedAgentFixtures.EnvironmentContext(), Resolution("shared_teams", []), openBrowser: true, CancellationToken.None);

        Assert.True(Assert.Single(consentFlow.OpenedBrowser));
    }

    [Fact]
    public async Task Create_WithoutAResolution_Throws()
    {
        var flow = Flow(new FakeConnectorCatalogPort(), Consent());

        await Assert.ThrowsAsync<ArgumentNullException>(() => flow.CreateAsync(ManagedAgentFixtures.EnvironmentContext(), (ConnectorResolution)null!, openBrowser: false, CancellationToken.None));
    }
}
