using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncConnectionAdapterCacheTests
{
    private sealed class StandaloneWorkflowWorkspace : IDisposable
    {
        public StandaloneWorkflowWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "ah-standalone-" + Guid.NewGuid().ToString("N"));

            var folder = Path.Combine(Root, "workflows", $"Flow-{Guid.NewGuid()}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "workflow.json"), "{\"properties\":{}}");
            File.WriteAllText(Path.Combine(folder, "metadata.yml"), "name: Flow\nstateCode: 1\n");
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private static CopilotEnvironmentContext Environment() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "11111111-2222-3333-4444-555555555555",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static SyncConnectionAdapter Create(RecordingConnectionManagementService connections, RecordingWorkflowActivationService workflows, StubConnectionCatalogClient? catalog = null) =>
        CopilotTestFixtures.ConnectionAdapter(connections, workflows, catalogClient: catalog);

    [Fact]
    public async Task ReadConnections_ForAStandaloneWorkflow_LeavesActivationUnknownRatherThanTrustingTheLocalFile()
    {
        var connections = new RecordingConnectionManagementService();
        var workflows = new RecordingWorkflowActivationService();
        workflows.Workflows.Add(new WorkflowStatusView { WorkflowId = "wf-1", DisplayName = "Cloned Flow", State = WorkflowState.Activated, CanEnable = true });

        using var workspace = new StandaloneWorkflowWorkspace();

        var snapshot = await Create(connections, workflows).ReadConnectionsAsync(Environment(), workspace.Root, CancellationToken.None);

        Assert.Null(Assert.Single(snapshot.Workflows).IsActivatedLocally);
    }

    [Fact]
    public async Task ReadConnections_ForAnAgentWorkspace_StillReportsTheRecordedActivation()
    {
        var connections = new RecordingConnectionManagementService();
        var workflows = new RecordingWorkflowActivationService();
        workflows.Workflows.Add(new WorkflowStatusView { WorkflowId = "wf-1", DisplayName = "Agent Flow", State = WorkflowState.Activated, CanEnable = true });

        var snapshot = await Create(connections, workflows).ReadConnectionsAsync(Environment(), Path.Combine(Path.GetTempPath(), "ah-connection-cache"), CancellationToken.None);

        Assert.True(Assert.Single(snapshot.Workflows).IsActivatedLocally);
    }

    [Fact]
    public async Task ReadConnections_WritesTheFreshViewsIntoTheLibraryCache()
    {
        var connections = new RecordingConnectionManagementService();
        connections.Views.Add(new AgentConnectionView { ConnectionReferenceLogicalName = "cr_teams", ConnectorId = "shared_teams", BoundConnectionId = "conn-1", BoundConnectionExists = true });

        await Create(connections, new RecordingWorkflowActivationService()).ReadConnectionsAsync(Environment(), Path.Combine(Path.GetTempPath(), "ah-connection-cache"), CancellationToken.None);

        Assert.Equal(1, connections.WriteConnectionsCacheCalls);
        Assert.Equal(["cr_teams"], connections.CachedViews.Select(view => view.ConnectionReferenceLogicalName));
    }

    [Fact]
    public async Task ReadConnections_WhenTheCacheCannotBeWritten_FailsRatherThanEnablingAgainstStaleState()
    {
        var connections = new RecordingConnectionManagementService { WriteConnectionsCacheThrows = new IOException("cache locked") };
        connections.Views.Add(new AgentConnectionView { ConnectionReferenceLogicalName = "cr_teams", ConnectorId = "shared_teams" });

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(connections, new RecordingWorkflowActivationService())
            .ReadConnectionsAsync(Environment(), Path.Combine(Path.GetTempPath(), "ah-connection-cache"), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.ConnectionStateNotRecorded, failure.ErrorKind);
        Assert.Contains("cache locked", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadDeclaredConnectors_ReportsOnlyWhatTheWorkspaceDeclares()
    {
        var connections = new RecordingConnectionManagementService();
        connections.Views.Add(new AgentConnectionView { ConnectionReferenceLogicalName = "cr_teams", ConnectorId = "shared_teams", IsDeclared = true });
        connections.Views.Add(new AgentConnectionView { ConnectionReferenceLogicalName = "cr_stale", ConnectorId = "shared_sql", IsDeclared = false });

        var declared = await Create(connections, new RecordingWorkflowActivationService())
            .ReadDeclaredConnectorsAsync(Environment(), Path.Combine(Path.GetTempPath(), "ah-connection-declared"), CancellationToken.None);

        Assert.Equal(["shared_teams"], declared);
    }

    [Fact]
    public async Task ReadDeclaredConnectors_ReportsTheConnectorIdResolvedForTheTargetEnvironment()
    {
        var connections = new RecordingConnectionManagementService();
        connections.Views.Add(new AgentConnectionView { ConnectionReferenceLogicalName = "cr_weather", ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_target-5fweather", ConnectorName = "shared_target-5fweather", IsDeclared = true });

        var declared = await Create(connections, new RecordingWorkflowActivationService())
            .ReadDeclaredConnectorsAsync(Environment(), Path.Combine(Path.GetTempPath(), "ah-connection-declared"), CancellationToken.None);

        Assert.Equal(["/providers/Microsoft.PowerApps/apis/shared_target-5fweather"], declared);
    }

    [Fact]
    public async Task ReadDeclaredConnectors_DoesNotDiscoverConnectionsOrWriteTheCache()
    {
        var connections = new RecordingConnectionManagementService();
        connections.Views.Add(new AgentConnectionView { ConnectionReferenceLogicalName = "cr_teams", ConnectorId = "shared_teams", IsDeclared = true });
        var catalog = new StubConnectionCatalogClient();

        await Create(connections, new RecordingWorkflowActivationService(), catalog)
            .ReadDeclaredConnectorsAsync(Environment(), Path.Combine(Path.GetTempPath(), "ah-connection-declared"), CancellationToken.None);

        Assert.Equal(0, connections.WriteConnectionsCacheCalls);
        Assert.Empty(catalog.ConnectionLookups);
        Assert.All(connections.CatalogTokens, token => Assert.True(string.IsNullOrEmpty(token)));
    }

    [Fact]
    public async Task ListConnections_AConnectorTheServiceDoesNotKnowIsReportedAsNotFound()
    {
        var catalog = new StubConnectionCatalogClient { ConnectionsThrow = new InvalidOperationException("Connections request failed (404).") };

        var failure = await Assert.ThrowsAsync<ConnectorNotFoundException>(() => Create(new RecordingConnectionManagementService(), new RecordingWorkflowActivationService(), catalog)
            .ListConnectionsAsync(Environment(), "shared_typo", CancellationToken.None));

        Assert.Equal("shared_typo", failure.ConnectorInternalId);
        Assert.Equal("env-1", failure.EnvironmentId);
    }

    [Theory]
    [InlineData("Connections request failed (403).")]
    [InlineData("Connections request failed (429).")]
    [InlineData("Connections request failed (500).")]
    public async Task ListConnections_AServiceFailureIsNotReportedAsAnUnknownConnector(string message)
    {
        var catalog = new StubConnectionCatalogClient { ConnectionsThrow = new InvalidOperationException(message) };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(new RecordingConnectionManagementService(), new RecordingWorkflowActivationService(), catalog)
            .ListConnectionsAsync(Environment(), "shared_teams", CancellationToken.None));

        Assert.Equal(message, failure.Message);
    }

    [Fact]
    public async Task ListConnections_ATimeoutIsNotReportedAsAnUnknownConnector()
    {
        var catalog = new StubConnectionCatalogClient { ConnectionsThrow = new TimeoutException("Connections request timed out after 30s.") };

        await Assert.ThrowsAsync<TimeoutException>(() => Create(new RecordingConnectionManagementService(), new RecordingWorkflowActivationService(), catalog)
            .ListConnectionsAsync(Environment(), "shared_teams", CancellationToken.None));
    }
}

internal sealed class StubConnectionCatalogClient : IConnectionCatalogClient
{
    public List<string> ConnectionLookups { get; } = [];

    public List<PowerAppsContext> Contexts { get; } = [];

    public Exception? ConnectionsThrow { get; init; }

    public Task<IReadOnlyList<ConnectionInstance>> ListConnectionsAsync(PowerAppsContext context, string connectorName, CancellationToken cancellationToken)
    {
        Contexts.Add(context);
        ConnectionLookups.Add(connectorName);

        return ConnectionsThrow is not null
            ? Task.FromException<IReadOnlyList<ConnectionInstance>>(ConnectionsThrow)
            : Task.FromResult<IReadOnlyList<ConnectionInstance>>([]);
    }

    public Task<IReadOnlyList<ConnectorInfo>> ListConnectorsAsync(PowerAppsContext context, CancellationToken cancellationToken)
    {
        Contexts.Add(context);

        return Task.FromResult<IReadOnlyList<ConnectorInfo>>([]);
    }
}

internal sealed class RecordingConnectionManagementService : IConnectionManagementService
{
    public List<AgentConnectionView> Views { get; } = [];

    public List<AgentConnectionView> CachedViews { get; } = [];

    public int WriteConnectionsCacheCalls { get; private set; }

    public Exception? WriteConnectionsCacheThrows { get; init; }

    public List<string?> CatalogTokens { get; } = [];

    public List<Uri?> CatalogEndpoints { get; } = [];

    public Task<IReadOnlyList<AgentConnectionView>> GetAgentConnectionViewsAsync(DirectoryPath workspaceFolder, DefinitionBase definition, ISyncDataverseClient dataverseClient, IConnectionCatalogClient catalogClient, PowerAppsContext catalogContext, CancellationToken cancellationToken)
    {
        CatalogTokens.Add(catalogContext?.AccessToken);
        CatalogEndpoints.Add(catalogContext?.PowerPlatformApiEndpoint);

        return Task.FromResult<IReadOnlyList<AgentConnectionView>>(Views);
    }

    public List<ConnectionBindingRequest> AppliedBindings { get; } = [];

    public Task<IReadOnlyList<AgentConnectionView>> ApplyConnectionBindingsAsync(DirectoryPath workspaceFolder, DefinitionBase definition, ISyncDataverseClient dataverseClient, IConnectionCatalogClient catalogClient, PowerAppsContext catalogContext, IReadOnlyList<ConnectionBindingRequest> bindings, CancellationToken cancellationToken)
    {
        AppliedBindings.AddRange(bindings);

        return Task.FromResult<IReadOnlyList<AgentConnectionView>>(Views);
    }

    public void WriteConnectionsCache(DirectoryPath workspaceFolder, IReadOnlyList<AgentConnectionView> views)
    {
        WriteConnectionsCacheCalls++;

        if (WriteConnectionsCacheThrows is not null)
        {
            throw WriteConnectionsCacheThrows;
        }

        CachedViews.AddRange(views);
    }

    public long GetConnectionsCacheGeneration(DirectoryPath workspaceFolder) => 0;

    public bool TryWriteConnectionsCache(DirectoryPath workspaceFolder, IReadOnlyList<AgentConnectionView> views, long expectedGeneration) => true;

    public Task<DeclareConnectionReferencesResult> DeclareConnectionReferencesAsync(DirectoryPath workspaceFolder, DefinitionBase definition, IReadOnlyList<string> logicalNames, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken) =>
        Task.FromResult(new DeclareConnectionReferencesResult());

    public Task<string> CreateConnectionReferenceForConnectorAsync(DirectoryPath workspaceFolder, DefinitionBase definition, string connectorInternalId, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken) =>
        Task.FromResult(string.Empty);

    public Task<ConnectionReferenceRemovalResult> RemoveConnectionReferenceAsync(DirectoryPath workspaceFolder, DefinitionBase definition, string logicalName, bool confirmed, CancellationToken cancellationToken) =>
        Task.FromResult(RemovalReturns);

    public ConnectionReferenceRemovalResult RemovalReturns { get; init; } = new();

    public ConnectionsCacheFile? ReadConnectionsCache(DirectoryPath workspaceFolder) => null;
}

internal sealed class RecordingWorkflowActivationService : IWorkflowActivationService
{
    public List<WorkflowStatusView> Workflows { get; } = [];

    public IReadOnlyList<WorkflowStatusView> GetWorkflowStatusViews(DirectoryPath workspaceFolder, IReadOnlyList<AgentConnectionView> views) => Workflows;

    public WorkflowActivationResult ActivationReturns { get; init; } = new() { Succeeded = true };

    public Task<WorkflowActivationResult> SetWorkflowActivationsAsync(DirectoryPath workspaceFolder, IReadOnlyList<WorkflowActivationRequest> requests, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken) =>
        Task.FromResult(ActivationReturns);
}
