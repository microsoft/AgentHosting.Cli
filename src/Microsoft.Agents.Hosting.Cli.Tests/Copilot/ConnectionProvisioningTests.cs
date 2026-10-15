using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class ConnectionProvisioningTests
{
    private static CopilotWorkspaceInfo Workspace() => new()
    {
        WorkspacePath = Path.Combine(Path.GetTempPath(), "ah-connection-provisioning-tests"),
        AgentId = "11111111-2222-3333-4444-555555555555",
        EnvironmentId = "env-1",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static SyncWorkspaceAdapter Create(RecordingWorkspaceSynchronizer synchronizer) =>
        new(
            synchronizer,
            new RecordingRetargetService(),
            new StubSolutionVersionsClient(),
            new StubComponentCollectionClient(),
            new StubOperationContextProvider(),
            new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider()),
            new CopilotChangeMapper());

    [Fact]
    public async Task Push_ProvisionsConnectionReferencesWithoutTrustingTheCache()
    {
        using var workspace = new ClassicWorkspace();
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await CreateWith(synchronizer, new RecordingConnectionReferenceClient()).PushAsync(workspace.Info, overwrite: false, CancellationToken.None);

        Assert.Contains(nameof(IWorkspaceSynchronizer.ProvisionConnectionReferencesAsync), synchronizer.Calls);
        Assert.Equal(0, synchronizer.FolderAwareProvisionCount);
        Assert.Equal(1, synchronizer.DefinitionOnlyProvisionCount);
    }

    [Fact]
    public async Task Push_WithNothingDeclared_DoesNotAskTheServiceToProvisionAnything()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        var dataverseClient = new RecordingConnectionReferenceClient();

        await CreateWith(synchronizer, dataverseClient).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.ProvisionConnectionReferencesAsync), synchronizer.Calls);
        Assert.Empty(dataverseClient.EnsuredLogicalNames);
    }

    [Fact]
    public async Task Push_ProvisionsConnectionReferencesBeforeTheWorkflowsThatNeedThem()
    {
        using var workspace = new ClassicWorkspace();
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await CreateWith(synchronizer, new RecordingConnectionReferenceClient()).PushAsync(workspace.Info, overwrite: false, CancellationToken.None);

        var provision = synchronizer.Calls.IndexOf(nameof(IWorkspaceSynchronizer.ProvisionConnectionReferencesAsync));
        var workflows = synchronizer.Calls.IndexOf(nameof(IWorkspaceSynchronizer.UpsertWorkflowForAgentAsync));

        Assert.InRange(provision, 0, int.MaxValue);
        Assert.InRange(workflows, 0, int.MaxValue);
        Assert.True(provision < workflows, "a workflow cannot be created against a connection reference the environment does not hold yet.");
    }

    [Fact]
    public async Task Push_ProvisionsWithTheConnectorRowIdsTheSameRunPushed()
    {
        using var workspace = new ClassicWorkspace();
        var rowId = Guid.NewGuid();
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.ConnectorRowIds["shared_contoso"] = rowId;

        await CreateWith(synchronizer, new RecordingConnectionReferenceClient()).PushAsync(workspace.Info, overwrite: false, CancellationToken.None);

        Assert.Equal(rowId, Assert.Single(synchronizer.LastProvisionedConnectorIds!).Value);
    }

    [Fact]
    public async Task Push_StillPushesCustomConnectors()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Contains(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), synchronizer.Calls);
    }

    [Fact]
    public async Task Push_PushesCustomConnectorsBeforeTheComponentsThatReferenceThem()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        var connectors = synchronizer.Calls.IndexOf(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync));
        var push = synchronizer.Calls.IndexOf(nameof(IWorkspaceSynchronizer.PushLocalChangesAsync));

        Assert.InRange(connectors, 0, int.MaxValue);
        Assert.InRange(push, 0, int.MaxValue);
        Assert.True(connectors < push, "custom connectors must exist before the push resolves connector ids against them.");
    }

    [Fact]
    public async Task Push_DraftsOnlyTheWorkflowsWhoseConnectionsAreUnbound()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(WorkflowActivationMode.DraftWhenConnectionsUnbound, synchronizer.LastWorkflowActivationMode);
    }

    [Fact]
    public async Task Push_NeverDraftsAWorkflowMerelyForHavingAConnectionReference()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.NotEqual(WorkflowActivationMode.DraftWhenConnectionReferencesExist, synchronizer.LastWorkflowActivationMode);
    }

    [Fact]
    public async Task Push_PushesCustomConnectorsExactlyOnce()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.Calls.Count(call => call == nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync)));
    }

    [Fact]
    public async Task Push_PassesClassicDeclarationsToTheSdk()
    {
        using var workspace = new ClassicWorkspace();
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await CreateWith(synchronizer, new RecordingConnectionReferenceClient()).PushAsync(workspace.Info, overwrite: false, CancellationToken.None);

        Assert.Equal("crf9a_Classic.shared_teams.0add1954",
            Assert.Single(synchronizer.LastProvisionedDefinition!.ConnectionReferences).ConnectionReferenceLogicalName.Value);
    }

    [Fact]
    public async Task Push_EnsuresClassicDeclarationsExistInTheTarget()
    {
        using var workspace = new ClassicWorkspace();
        var dataverseClient = new RecordingConnectionReferenceClient();

        await CreateWith(new RecordingWorkspaceSynchronizer(), dataverseClient).PushAsync(workspace.Info, overwrite: false, CancellationToken.None);

        Assert.Equal(["crf9a_Classic.shared_teams.0add1954"], dataverseClient.EnsuredLogicalNames);
    }

    private static SyncWorkspaceAdapter CreateWith(RecordingWorkspaceSynchronizer synchronizer, ISyncDataverseClient dataverseClient) =>
        new(
            synchronizer,
            new RecordingRetargetService(),
            dataverseClient,
            new StubComponentCollectionClient(),
            new StubOperationContextProvider(),
            new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider()),
            new CopilotChangeMapper());
}

internal sealed class ClassicWorkspace : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ah-classic-" + Guid.NewGuid().ToString("N"));

    public ClassicWorkspace()
    {
        Directory.CreateDirectory(_path);
        File.WriteAllText(Path.Combine(_path, "connectionreferences.mcs.yml"), """
connectionReferences:
  - connectionReferenceLogicalName: crf9a_Classic.shared_teams.0add1954
    connectorId: /providers/Microsoft.PowerApps/apis/shared_teams
""");
    }

    public CopilotWorkspaceInfo Info => new()
    {
        WorkspacePath = _path,
        AgentId = "11111111-2222-3333-4444-555555555555",
        EnvironmentId = "env-1",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    public void Dispose()
    {
        if (Directory.Exists(_path))
        {
            Directory.Delete(_path, recursive: true);
        }
    }
}
