using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncWorkspaceAdapterCloneTests : IDisposable
{
    private const string SourceAgentId = "11111111-2222-3333-4444-555555555555";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ah-clone-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static SyncWorkspaceAdapter Create(RecordingWorkspaceSynchronizer synchronizer) => CopilotTestFixtures.WorkspaceAdapter(synchronizer, dataverseClient: new CloneSourceClient());

    private CopilotCloneCommand Command() => new()
    {
        Environment = new CopilotEnvironmentContext
        {
            EnvironmentId = "env-1",
            TenantId = Guid.Empty.ToString(),
            DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
        },
        AgentId = SourceAgentId,
        WorkspacePath = _directory,
    };

    private string WriteRegistry()
    {
        var config = Path.Combine(_directory, AgentConfigLocator.ConfigDirectoryName);
        Directory.CreateDirectory(config);
        var path = Path.Combine(config, AgentConfigLocator.ConfigFileName);
        File.WriteAllText(path, "{\"schemaVersion\":1}");

        return path;
    }

    [Fact]
    public async Task ReadWorkspaceInfo_CarriesTheTenantTheConnectionRecords()
    {
        Directory.CreateDirectory(_directory);

        var workspace = await Create(new RecordingWorkspaceSynchronizer()).ReadWorkspaceInfoAsync(_directory, CancellationToken.None);

        Assert.Equal("11111111-2222-3333-4444-555555555555", workspace.AgentId);
        Assert.Null(workspace.TenantId);
    }

    [Fact]
    public async Task Clone_IntoAnEmptyDirectory_NestsTheAgentUnderItsDisplayName()
    {
        Directory.CreateDirectory(_directory);

        var outcome = await Create(new RecordingWorkspaceSynchronizer()).CloneAsync(Command(), CancellationToken.None);

        Assert.Equal(Path.Combine(_directory, "Contoso Helpdesk"), outcome.AgentWorkspacePath);
    }

    [Fact]
    public async Task Clone_ReportsTheAgentWorkspaceItCreated()
    {
        Directory.CreateDirectory(_directory);

        var outcome = await Create(new RecordingWorkspaceSynchronizer()).CloneAsync(Command(), CancellationToken.None);

        var workspace = Assert.Single(outcome.Workspaces);

        Assert.Equal("agent", workspace.Kind);
        Assert.Equal("Contoso Helpdesk", workspace.DisplayName);
    }

    [Fact]
    public async Task Clone_IntoADirectoryHoldingOnlyTheRegistry_Succeeds()
    {
        WriteRegistry();

        var outcome = await Create(new RecordingWorkspaceSynchronizer()).CloneAsync(Command(), CancellationToken.None);

        Assert.Equal(Path.Combine(_directory, "Contoso Helpdesk"), outcome.AgentWorkspacePath);
    }

    [Fact]
    public async Task Clone_LeavesTheRegistryInPlace()
    {
        var registry = WriteRegistry();

        await Create(new RecordingWorkspaceSynchronizer()).CloneAsync(Command(), CancellationToken.None);

        Assert.True(File.Exists(registry));
    }

    [Fact]
    public async Task Clone_IntoADirectoryHoldingWorkspaceContent_Fails()
    {
        WriteRegistry();
        var agentFolder = Path.Combine(_directory, "Contoso Helpdesk");
        Directory.CreateDirectory(agentFolder);
        File.WriteAllText(Path.Combine(agentFolder, "settings.mcs.yml"), "displayName: Existing\n");

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(new RecordingWorkspaceSynchronizer()).CloneAsync(Command(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.DestinationNotEmpty, failure.ErrorKind);
    }

    [Fact]
    public async Task Clone_IntoADirectoryHoldingAnUnrelatedFile_Succeeds()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "notes.txt"), "hello");

        var outcome = await Create(new RecordingWorkspaceSynchronizer()).CloneAsync(Command(), CancellationToken.None);

        Assert.Equal(Path.Combine(_directory, "Contoso Helpdesk"), outcome.AgentWorkspacePath);
        Assert.True(File.Exists(Path.Combine(_directory, "notes.txt")));
    }

    [Fact]
    public async Task Clone_IntoAnAgentFolderHoldingAnUnrelatedFile_Fails()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Contoso Helpdesk"));
        File.WriteAllText(Path.Combine(_directory, "Contoso Helpdesk", "notes.txt"), "hello");

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(new RecordingWorkspaceSynchronizer()).CloneAsync(Command(), CancellationToken.None));
    }

    [Fact]
    public async Task Clone_WhenTheContentFails_DiscardsWhatItStaged()
    {
        WriteRegistry();
        var synchronizer = new RecordingWorkspaceSynchronizer { CloneChangesThrows = new InvalidOperationException("service unavailable") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(synchronizer).CloneAsync(Command(), CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(_directory, "Contoso Helpdesk")));
    }

    [Fact]
    public async Task Clone_WhenTheContentFails_KeepsTheRegistry()
    {
        var registry = WriteRegistry();
        var synchronizer = new RecordingWorkspaceSynchronizer { CloneChangesThrows = new InvalidOperationException("service unavailable") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(synchronizer).CloneAsync(Command(), CancellationToken.None));

        Assert.True(File.Exists(registry));
    }

    [Fact]
    public async Task Clone_WhenTouchupsFail_DiscardsWhatItStaged()
    {
        WriteRegistry();
        var synchronizer = new RecordingWorkspaceSynchronizer { ApplyTouchupsThrows = new InvalidOperationException("unresolvable reference") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(synchronizer).CloneAsync(Command(), CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(_directory, "Contoso Helpdesk")));
    }

    [Fact]
    public async Task Clone_WhenTouchupsFail_KeepsTheRegistry()
    {
        var registry = WriteRegistry();
        var synchronizer = new RecordingWorkspaceSynchronizer { ApplyTouchupsThrows = new InvalidOperationException("unresolvable reference") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(synchronizer).CloneAsync(Command(), CancellationToken.None));

        Assert.True(File.Exists(registry));
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("classic")]
    public async Task Clone_WithADisplayName_DeclaresItInTheClonedSettingsOnly_AndTheCacheKeepsTheSourceName(string shape)
    {
        Directory.CreateDirectory(_directory);
        var files = ClonedFiles(shape);

        var outcome = await Create(new RecordingWorkspaceSynchronizer { ClonedFiles = files }).CloneAsync(Command() with { DisplayName = "Contoso Copy" }, CancellationToken.None);

        Assert.Equal(files[WorkspaceFileNames.Settings].Replace("displayName: Contoso Helpdesk", "displayName: \"Contoso Copy\"", StringComparison.Ordinal), File.ReadAllText(Path.Combine(outcome.AgentWorkspacePath, WorkspaceFileNames.Settings)));
        Assert.All(files.Where(file => file.Key != WorkspaceFileNames.Settings), file => Assert.Equal(file.Value, File.ReadAllText(Path.Combine(outcome.AgentWorkspacePath, file.Key))));
        Assert.Equal(("Contoso Copy", "Contoso Copy"), (outcome.DisplayName, Assert.Single(outcome.Workspaces).DisplayName));
    }

    [Fact]
    public async Task Clone_WithoutADisplayName_LeavesTheClonedFilesAsWritten()
    {
        Directory.CreateDirectory(_directory);
        var files = ClonedFiles("cli");

        var outcome = await Create(new RecordingWorkspaceSynchronizer { ClonedFiles = files }).CloneAsync(Command(), CancellationToken.None);

        Assert.All(files, file => Assert.Equal(file.Value, File.ReadAllText(Path.Combine(outcome.AgentWorkspacePath, file.Key))));
        Assert.Equal("Contoso Helpdesk", outcome.DisplayName);
    }

    [Fact]
    public async Task Clone_WhenTheDisplayNameCannotBeDeclared_DiscardsWhatItStaged()
    {
        WriteRegistry();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(new RecordingWorkspaceSynchronizer()).CloneAsync(Command() with { DisplayName = "Contoso Copy" }, CancellationToken.None));

        Assert.Equal([Path.Combine(_directory, AgentConfigLocator.ConfigDirectoryName)], Directory.EnumerateFileSystemEntries(_directory));
    }

    private static Dictionary<string, string> ClonedFiles(string shape) => shape == "classic"
        ? new()
        {
            [WorkspaceFileNames.Settings] = "displayName: Contoso Helpdesk\r\nschemaName: con_contosohelpdesk\r\ntemplate: default-2.1.0\r\nlanguage: 1033\r\n",
            [WorkspaceFileNames.Agent] = "mcs.metadata:\r\n  componentName: Contoso Helpdesk\r\nkind: GptComponentMetadata\r\ninstructions: Answer helpdesk questions.\r\n",
            [".mcs/botdefinition.json"] = "{\"entity\":{\"displayName\":\"Contoso Helpdesk\",\"schemaName\":\"con_contosohelpdesk\"}}",
        }
        : new()
        {
            [WorkspaceFileNames.Settings] = "displayName: Contoso Helpdesk\r\nschemaName: con_contosohelpdesk\r\nconfiguration:\r\n  authoringModel: CliCopilot\r\ntemplate: cliagent-1.0.0\r\nlanguage: 1033\r\n",
            ["agent.sync.yaml"] = "layoutVersion: 1\r\n",
            [".mcs/botdefinition.json"] = "{\"entity\":{\"displayName\":\"Contoso Helpdesk\",\"schemaName\":\"con_contosohelpdesk\"}}",
        };
}

internal sealed class CloneSourceClient : OfflineSyncDataverseClient
{
    public IReadOnlyList<ComponentCollectionInfo> LinkedCollections { get; init; } = [];

    public override Task<AgentInfo> GetAgentInfoAsync(Guid agentId, CancellationToken cancellationToken) =>
        Task.FromResult(new AgentInfo
        {
            DisplayName = "Contoso Helpdesk",
            SchemaName = "con_contosohelpdesk",
            ComponentCollections = [.. LinkedCollections],
        });

    public override Task<SolutionInfo> GetSolutionVersionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SolutionInfo());
}
