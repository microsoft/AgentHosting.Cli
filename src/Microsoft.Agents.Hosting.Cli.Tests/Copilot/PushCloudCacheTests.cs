using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class PushCloudCacheTests
{
    private const string RemoteChangedMessage = "The agent was changed in the cloud while you were editing it. Get the latest changes before pushing again.";

    private static CopilotWorkspaceInfo Workspace() => new()
    {
        WorkspacePath = Path.Combine(Path.GetTempPath(), "ah-push-cache-tests"),
        AgentId = "11111111-2222-3333-4444-555555555555",
        EnvironmentId = "env-1",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static SyncWorkspaceAdapter Create(RecordingWorkspaceSynchronizer synchronizer, RecordingRetargetService retarget, RecordingConnectionReferenceClient? dataverseClient = null) =>
        new(
            synchronizer,
            retarget,
            dataverseClient ?? new RecordingConnectionReferenceClient(),
            new StubComponentCollectionClient(),
            new StubOperationContextProvider(),
            new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider()),
            new CopilotChangeMapper());

    private static RecordingWorkspaceSynchronizer RefusingOnce() => CopilotTestFixtures.FailingOnce(new InvalidOperationException(RemoteChangedMessage));

    private static async Task<RecordingRetargetService> RefreshedBaselineAsync()
    {
        using var workspace = new ClassicWorkspace();
        var retarget = new RecordingRetargetService();

        await Create(RefusingOnce(), retarget).PushAsync(workspace.Info, overwrite: true, CancellationToken.None);

        return retarget;
    }

    [Fact]
    public async Task Push_RefreshesTheBaselineFromTheServiceRatherThanAnIncrementalToken()
    {
        Assert.Null((await RefreshedBaselineAsync()).LastChangeToken);
    }

    [Fact]
    public async Task Push_DoesNotSeedTheBaselineWithLocallyPushedConnectors()
    {
        Assert.False((await RefreshedBaselineAsync()).LastSyncCustomConnectors);
    }

    [Fact]
    public async Task Push_DoesNotReReadWorkflowsAndPromptsWhenRefreshingTheBaseline()
    {
        Assert.False((await RefreshedBaselineAsync()).LastSyncWorkflowsAndPrompts);
    }

    [Fact]
    public async Task Push_RefreshesTheBaselineWithTheReferencesTheRunConfirmed()
    {
        var metadata = (await RefreshedBaselineAsync()).LastCloudFlowMetadata;

        Assert.NotNull(metadata);
        Assert.Equal(
            ["crf9a_Classic.shared_teams.0add1954"],
            metadata.ConnectionReferences.Select(reference => reference.ConnectionReferenceLogicalName.Value));
    }

    [Fact]
    public async Task Push_WithNothingDeclared_StillRefreshesWithoutReReadingWorkflowsAndPrompts()
    {
        var retarget = new RecordingRetargetService();

        await Create(RefusingOnce(), retarget).PushAsync(Workspace(), overwrite: true, CancellationToken.None);

        Assert.False(retarget.LastSyncWorkflowsAndPrompts);
        Assert.NotNull(retarget.LastCloudFlowMetadata);
    }

    [Fact]
    public async Task Push_LeavesTheWorkspaceDirectoryAloneWhenRefreshingTheBaseline()
    {
        Assert.False((await RefreshedBaselineAsync()).LastUpdateWorkspaceDirectory);
    }

    [Fact]
    public async Task Push_PushesCustomConnectorsOnceEvenWhenTheBaselineIsRefreshed()
    {
        var synchronizer = RefusingOnce();

        await Create(synchronizer, new RecordingRetargetService()).PushAsync(Workspace(), overwrite: true, CancellationToken.None);

        Assert.Equal(1, synchronizer.Calls.Count(call => call == nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync)));
    }

    [Fact]
    public async Task Push_ProvisionsConnectionsOnceEvenWhenTheBaselineIsRefreshed()
    {
        using var workspace = new ClassicWorkspace();
        var synchronizer = RefusingOnce();

        await Create(synchronizer, new RecordingRetargetService()).PushAsync(workspace.Info, overwrite: true, CancellationToken.None);

        Assert.Equal(0, synchronizer.FolderAwareProvisionCount);
        Assert.Equal(1, synchronizer.DefinitionOnlyProvisionCount);
    }

    private static async Task<RecordingRetargetService> HealedBaselineAsync()
    {
        using var workspace = new ClassicWorkspace();
        var retarget = new RecordingRetargetService();

        await Create(CopilotTestFixtures.FailingOnce(CopilotTestFixtures.MissingEntity()), retarget).PushAsync(workspace.Info, overwrite: false, CancellationToken.None);

        return retarget;
    }

    [Fact]
    public async Task HealingAMissingConnectionReference_RebuildsTheBaselineFromAFullEnvironmentRead()
    {
        var retarget = await HealedBaselineAsync();

        Assert.Null(retarget.LastChangeToken);
        Assert.False(retarget.LastUpdateWorkspaceDirectory);
        Assert.False(retarget.LastSyncCustomConnectors);
        Assert.False(retarget.LastSyncWorkflowsAndPrompts);
    }

    [Fact]
    public async Task HealingAMissingConnectionReference_RecordsOnlyTheRowsDataverseReturned()
    {
        using var workspace = new ClassicWorkspace();
        var retarget = new RecordingRetargetService();
        var dataverseClient = new RecordingConnectionReferenceClient();

        await Create(CopilotTestFixtures.FailingOnce(CopilotTestFixtures.MissingEntity()), retarget, dataverseClient).PushAsync(workspace.Info, overwrite: false, CancellationToken.None);

        var reference = Assert.Single(retarget.LastCloudFlowMetadata!.ConnectionReferences);
        var row = dataverseClient.References["crf9a_Classic.shared_teams.0add1954"];

        Assert.Equal(row.ConnectionReferenceId, reference.Id.Value);
        Assert.Equal(row.ConnectorId, reference.ConnectorId.Value);
    }

    [Fact]
    public async Task HealingAMissingConnectionReference_LeavesTheWorkspaceFilesUntouched()
    {
        using var workspace = new ClassicWorkspace();
        var before = SnapshotWorkspace(workspace.Info.WorkspacePath);

        await Create(CopilotTestFixtures.FailingOnce(CopilotTestFixtures.MissingEntity()), new RecordingRetargetService()).PushAsync(workspace.Info, overwrite: false, CancellationToken.None);

        Assert.Equal(before, SnapshotWorkspace(workspace.Info.WorkspacePath));
    }

    [Fact]
    public async Task Push_WhenDataverseDoesNotConfirmADeclaredReference_NeverPushesOrRefreshes()
    {
        using var workspace = new ClassicWorkspace();
        var synchronizer = CopilotTestFixtures.FailingOnce(CopilotTestFixtures.MissingEntity());
        var retarget = new RecordingRetargetService();

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, retarget, new RecordingConnectionReferenceClient { OmitProvisionedReferences = true }).PushAsync(workspace.Info, overwrite: false, CancellationToken.None));

        Assert.Equal(0, synchronizer.PushLocalChangesCalls);
        Assert.Empty(retarget.Calls);
    }

    private static Dictionary<string, string> SnapshotWorkspace(string workspacePath) =>
        Directory.GetFiles(workspacePath, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(workspacePath, path), File.ReadAllText, StringComparer.Ordinal);
}
