using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncWorkspaceAdapterComponentCollectionTests : IDisposable
{
    private const string SourceAgentId = "11111111-2222-3333-4444-555555555555";

    private static readonly Guid SharedToolsId = new("aaaaaaaa-1111-2222-3333-444444444444");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ah-cc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static SyncWorkspaceAdapter Create(RecordingWorkspaceSynchronizer synchronizer, CloneSourceClient client) =>
        new(
            synchronizer,
            synchronizer,
            new RecordingRetargetService(),
            client,
            new StubComponentCollectionClient(),
            new StubOperationContextProvider(),
            new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider()),
            new CopilotChangeMapper());

    private static CloneSourceClient Linked() => new()
    {
        LinkedCollections = [new ComponentCollectionInfo { Id = SharedToolsId, DisplayName = "Shared Tools" }],
    };

    private CopilotCloneCommand Command(bool includeLinked) => new()
    {
        Environment = new CopilotEnvironmentContext
        {
            EnvironmentId = "env-1",
            TenantId = Guid.Empty.ToString(),
            DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
        },
        AgentId = SourceAgentId,
        WorkspacePath = _directory,
        IncludeLinkedComponentCollections = includeLinked,
    };

    [Fact]
    public async Task Clone_WithLinkedCollections_WritesEachIntoItsOwnFolder()
    {
        Directory.CreateDirectory(_directory);

        var outcome = await Create(new RecordingWorkspaceSynchronizer(), Linked()).CloneAsync(Command(includeLinked: true), CancellationToken.None);

        Assert.Equal(Path.Combine(_directory, "Contoso Helpdesk"), outcome.AgentWorkspacePath);
        Assert.Equal(
            [Path.Combine(_directory, "Contoso Helpdesk"), Path.Combine(_directory, "Shared Tools")],
            outcome.Workspaces.Select(workspace => workspace.Path));
    }

    [Fact]
    public async Task Clone_WithLinkedCollections_LabelsEachWorkspace()
    {
        Directory.CreateDirectory(_directory);

        var outcome = await Create(new RecordingWorkspaceSynchronizer(), Linked()).CloneAsync(Command(includeLinked: true), CancellationToken.None);

        Assert.Equal(["agent", "component-collection"], outcome.Workspaces.Select(workspace => workspace.Kind));
        Assert.Equal(["Contoso Helpdesk", "Shared Tools"], outcome.Workspaces.Select(workspace => workspace.DisplayName));
    }

    [Fact]
    public async Task Clone_WithoutOptingIn_TakesTheAgentAlone()
    {
        Directory.CreateDirectory(_directory);

        var outcome = await Create(new RecordingWorkspaceSynchronizer(), Linked()).CloneAsync(Command(includeLinked: false), CancellationToken.None);

        Assert.Equal("agent", Assert.Single(outcome.Workspaces).Kind);
    }

    [Fact]
    public async Task Clone_RecordsTheCollectionAgainstItsOwnWorkspace()
    {
        Directory.CreateDirectory(_directory);
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer, Linked()).CloneAsync(Command(includeLinked: true), CancellationToken.None);

        var collection = synchronizer.SavedSyncInfo.Single(saved => saved.Folder.Contains("Shared Tools", StringComparison.Ordinal));

        Assert.Null(collection.SyncInfo.AgentId);
        Assert.Equal(SharedToolsId, collection.SyncInfo.ComponentCollectionId);
    }

    [Fact]
    public async Task Clone_RecordsTheAgentAgainstItsOwnWorkspace()
    {
        Directory.CreateDirectory(_directory);
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer, Linked()).CloneAsync(Command(includeLinked: true), CancellationToken.None);

        var agent = synchronizer.SavedSyncInfo.Single(saved => saved.Folder.Contains("Contoso Helpdesk", StringComparison.Ordinal));

        Assert.Null(agent.SyncInfo.ComponentCollectionId);
    }

    [Fact]
    public async Task Clone_CarriesTheEnvironmentIntoEveryWorkspace()
    {
        Directory.CreateDirectory(_directory);
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer, Linked()).CloneAsync(Command(includeLinked: true), CancellationToken.None);

        Assert.All(synchronizer.SavedSyncInfo, saved => Assert.Equal("env-1", saved.SyncInfo.EnvironmentId));
    }
}
