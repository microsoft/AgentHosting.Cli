using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.Sync;
using CoreCluster = Microsoft.Agents.Hosting.Cli.Core.Environments.CoreServicesClusterCategory;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncClusterCategoryTests
{
    public static TheoryData<CoreCluster> EveryHostCategory()
    {
        var data = new TheoryData<CoreCluster>();

        foreach (var category in Enum.GetValues<CoreCluster>())
        {
            data.Add(category);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryHostCategory))]
    public void EveryHostCategoryMapsToTheLibraryCategoryOfTheSameName(CoreCluster category)
    {
        Assert.Equal(category.ToString(), SyncClusterCategory.For(category).ToString());
    }

    [Fact]
    public void TheTwoEnumsDeclareTheSameMembers()
    {
        Assert.Equal(
            Enum.GetNames<CoreCluster>().OrderBy(name => name, StringComparer.Ordinal),
            Enum.GetNames<Microsoft.CopilotStudio.Sync.CoreServicesClusterCategory>().OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void ACategoryTheLibraryDoesNotDeclareIsRefused()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => SyncClusterCategory.For((CoreCluster)(-1)));

        Assert.Contains("diverged", failure.Message, StringComparison.Ordinal);
    }
}

public sealed class SyncConnectionAdapterCatalogTargetTests
{
    private static CopilotEnvironmentContext Environment() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "11111111-2222-3333-4444-555555555555",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static string Workspace() => Path.Combine(Path.GetTempPath(), $"ah-cluster-{Guid.NewGuid():N}");

    private static SyncConnectionAdapter Create(RecordingConnectionManagementService connections, PowerPlatformApiRing ring, StubTokenProvider? tokenProvider = null) =>
        CopilotTestFixtures.ConnectionAdapter(connections, tokenProvider: tokenProvider, apiTarget: PowerPlatformApiRingSelection.TargetFor(ring));

    [Theory]
    [InlineData(PowerPlatformApiRing.Production, "https://en.v1.environment.api.powerplatform.com/")]
    [InlineData(PowerPlatformApiRing.Preprod, "https://env.1.environment.api.preprod.powerplatform.com/")]
    [InlineData(PowerPlatformApiRing.Test, "https://env.1.environment.api.test.powerplatform.com/")]
    public async Task TheCatalogIsAskedThroughTheEnvironmentsPowerPlatformApiEndpointWithATokenForItsAudience(PowerPlatformApiRing ring, string expectedEndpoint)
    {
        var connections = new RecordingConnectionManagementService();
        var tokenProvider = new StubTokenProvider();

        await Create(connections, ring, tokenProvider).ReadConnectionsAsync(Environment(), Workspace(), CancellationToken.None);

        Assert.Equal(expectedEndpoint, Assert.Single(connections.CatalogEndpoints)?.AbsoluteUri);
        Assert.Equal(PowerPlatformApiRingSelection.TargetFor(ring).Audience, Assert.Single(tokenProvider.RequestedResources));
    }

    [Fact]
    public async Task TheConnectorAndConnectionListsAreAskedThroughTheEnvironmentsPowerPlatformApiEndpointWithItsToken()
    {
        var catalog = new StubConnectionCatalogClient();
        var adapter = CopilotTestFixtures.ConnectionAdapter(catalogClient: catalog);

        await adapter.ListConnectorsAsync(Environment(), CancellationToken.None);
        await adapter.ListConnectionsAsync(Environment(), "shared_teams", CancellationToken.None);

        Assert.Equal([("https://en.v1.environment.api.powerplatform.com/", "env-1", "token"), ("https://en.v1.environment.api.powerplatform.com/", "env-1", "token")], catalog.Contexts.Select(context => (context.PowerPlatformApiEndpoint?.AbsoluteUri, context.EnvironmentId, context.AccessToken)));
    }

    private static async Task<CopilotConnectionView> ReadOneAsync(AgentConnectionView stored)
    {
        var connections = new RecordingConnectionManagementService();
        connections.Views.Add(stored);

        var snapshot = await Create(connections, PowerPlatformApiRing.Preprod)
            .ReadConnectionsAsync(Environment(), Workspace(), CancellationToken.None);

        return Assert.Single(snapshot.References);
    }

    [Fact]
    public async Task AnUnverifiableBindingIsNotDecidedAsAnExistingConnection_ButKeepsTheRecordedConnection()
    {
        var view = await ReadOneAsync(new AgentConnectionView
        {
            ConnectionReferenceLogicalName = "cr_onedrive",
            ConnectorId = "shared_onedriveforbusiness",
            BoundConnectionId = "63a493629ccb4cd1ba3df8b066789a3d",
            BoundConnectionExists = true,
            CatalogUnavailable = true,
            IsDeclared = true,
        });

        var item = new SyncConnectionBindingRules().Observe(view);

        Assert.Equal("63a493629ccb4cd1ba3df8b066789a3d", view.BoundConnectionId);
        Assert.NotEqual(Core.ManagedAgents.ConnectionReadinessPlanner.AlreadyBound, item.State);
        Assert.Null(item.ConnectionId);
    }

    [Fact]
    public async Task AnUnverifiableBindingReadsAsNotConnectedAndStaysActionable()
    {
        var view = await ReadOneAsync(new AgentConnectionView
        {
            ConnectionReferenceLogicalName = "cr_onedrive",
            ConnectorId = "shared_onedriveforbusiness",
            BoundConnectionId = "63a493629ccb4cd1ba3df8b066789a3d",
            BoundConnectionExists = true,
            CatalogUnavailable = true,
            IsDeclared = true,
        });

        var item = new SyncConnectionBindingRules().Observe(view);

        Assert.Equal("not connected", Core.ManagedAgents.ConnectionSelectionScope.DescribeState(item.State));
        Assert.True(Core.ManagedAgents.ConnectionReadinessPlanner.IsActionable(item));
        Assert.False(Core.ManagedAgents.ConnectionReadinessPlanner.IsResolved(item));
    }

    [Fact]
    public async Task AReadableCatalogStillReportsARealBindingAsConnected()
    {
        var view = await ReadOneAsync(new AgentConnectionView
        {
            ConnectionReferenceLogicalName = "cr_onedrive",
            ConnectorId = "shared_onedriveforbusiness",
            BoundConnectionId = "63a493629ccb4cd1ba3df8b066789a3d",
            BoundConnectionExists = true,
            CatalogUnavailable = false,
            IsDeclared = true,
        });

        Assert.Equal("63a493629ccb4cd1ba3df8b066789a3d", view.BoundConnectionId);
        Assert.True(view.BoundConnectionExists);

        var item = new SyncConnectionBindingRules().Observe(view);

        Assert.Equal(Core.ManagedAgents.ConnectionReadinessPlanner.AlreadyBound, item.State);
        Assert.Equal("63a493629ccb4cd1ba3df8b066789a3d", item.ConnectionId);
        Assert.Equal("connected", Core.ManagedAgents.ConnectionSelectionScope.DescribeState(item.State));
    }

    [Fact]
    public async Task AReadableCatalogThatDeniesTheBindingStillReadsAsNotConnected()
    {
        var view = await ReadOneAsync(new AgentConnectionView
        {
            ConnectionReferenceLogicalName = "cr_onedrive",
            ConnectorId = "shared_onedriveforbusiness",
            BoundConnectionId = "63a493629ccb4cd1ba3df8b066789a3d",
            BoundConnectionExists = false,
            CatalogUnavailable = false,
            IsDeclared = true,
        });

        Assert.Equal("63a493629ccb4cd1ba3df8b066789a3d", view.BoundConnectionId);
        Assert.Equal("not connected", Core.ManagedAgents.ConnectionSelectionScope.DescribeState(new SyncConnectionBindingRules().Observe(view).State));
    }
}
