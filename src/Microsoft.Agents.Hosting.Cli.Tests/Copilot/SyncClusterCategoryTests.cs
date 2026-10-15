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
            Enum.GetNames<CoreServicesClusterCategory>().OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void ACategoryTheLibraryDoesNotDeclareIsRefused()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => SyncClusterCategory.For((CoreCluster)(-1)));

        Assert.Contains("diverged", failure.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// Covers the cluster the connection catalog is read against, and which bindings survive to the
/// status table. A binding counts only where the catalog answered for it: one rule for every ring, so
/// a connection deleted in the service cannot keep reading as connected because the read failed.
/// </summary>
public sealed class SyncConnectionAdapterClusterTests
{
    private static CopilotEnvironmentContext Environment() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "11111111-2222-3333-4444-555555555555",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static string Workspace() => Path.Combine(Path.GetTempPath(), $"ah-cluster-{Guid.NewGuid():N}");

    private static SyncConnectionAdapter Create(RecordingConnectionManagementService connections, CoreCluster cluster) =>
        CopilotTestFixtures.ConnectionAdapter(connections, cluster: cluster);

    [Theory]
    [InlineData(CoreCluster.Prod)]
    [InlineData(CoreCluster.Preprod)]
    [InlineData(CoreCluster.Test)]
    [InlineData(CoreCluster.Gov)]
    [InlineData(CoreCluster.Mooncake)]
    public async Task TheCatalogIsAskedAgainstTheHostsCluster(CoreCluster cluster)
    {
        var connections = new RecordingConnectionManagementService();

        await Create(connections, cluster).ReadConnectionsAsync(Environment(), Workspace(), CancellationToken.None);

        Assert.Equal(cluster.ToString(), Assert.Single(connections.CatalogClusters).ToString());
    }

    [Fact]
    public async Task TheCatalogIsNeverLeftOnTheEnumsDefault()
    {
        var connections = new RecordingConnectionManagementService();

        await Create(connections, CoreCluster.Preprod).ReadConnectionsAsync(Environment(), Workspace(), CancellationToken.None);

        Assert.NotEqual(default, Assert.Single(connections.CatalogClusters));
    }

    private static async Task<CopilotConnectionView> ReadOneAsync(AgentConnectionView stored)
    {
        var connections = new RecordingConnectionManagementService();
        connections.Views.Add(stored);

        var snapshot = await Create(connections, CoreCluster.Preprod)
            .ReadConnectionsAsync(Environment(), Workspace(), CancellationToken.None);

        return Assert.Single(snapshot.References);
    }

    [Fact]
    public async Task AnUnverifiableBindingIsNotReportedAsAnExistingConnection()
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

        Assert.Null(view.BoundConnectionId);
        Assert.False(view.BoundConnectionExists);
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

        var item = Core.ManagedAgents.ConnectionReadinessPlanner.Observe(view);

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

        var item = Core.ManagedAgents.ConnectionReadinessPlanner.Observe(view);

        Assert.Equal(Core.ManagedAgents.ConnectionReadinessPlanner.AlreadyBound, item.State);
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

        Assert.Null(view.BoundConnectionId);
        Assert.Equal("not connected", Core.ManagedAgents.ConnectionSelectionScope.DescribeState(
            Core.ManagedAgents.ConnectionReadinessPlanner.Observe(view).State));
    }
}
