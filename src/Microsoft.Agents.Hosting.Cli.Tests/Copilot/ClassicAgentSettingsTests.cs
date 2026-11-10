using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class ClassicAgentSettingsTests : IDisposable
{
    private const string CloudSettings = "displayName: Contoso Helpdesk\nschemaName: crf9a_classicAgent\nauthenticationMode: None\nauthenticationTrigger: AsNeeded\nconfiguration:\n  recognizer:\n    kind: GenerativeAIRecognizer\ntemplate: empty-1.0.0\nlanguage: 1033\n";

    private readonly string _workspacePath = Path.Combine(Path.GetTempPath(), "ah-classic-settings-" + Guid.NewGuid().ToString("N"));

    public ClassicAgentSettingsTests()
    {
        Directory.CreateDirectory(Path.Combine(_workspacePath, ".mcs"));
        File.WriteAllText(Path.Combine(_workspacePath, ".mcs", "botdefinition.json"), CopilotTestFixtures.CloudCacheJson(BotDefinition.Empty.WithEntity(Entity(CloudSettings))));
    }

    public static TheoryData<string> UnreadableSettings => ["", "displayName: \"Contoso Helpdesk\nschemaName: crf9a_classicAgent\n", "displayName: Contoso Helpdesk\ndisplayName: Contoso Desk\n", "<<<<<<< HEAD\ndisplayName: Contoso Helpdesk\n=======\ndisplayName: Contoso Desk\n>>>>>>> theirs\n"];

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
    }

    private static BotEntity Entity(string settings) => CodeSerializer.Deserialize<BotEntity>(settings)!;

    private void WriteSettings(string settings) => File.WriteAllText(Path.Combine(_workspacePath, WorkspaceFileNames.Settings), settings);

    private CopilotWorkspaceInfo Workspace() => new()
    {
        WorkspacePath = _workspacePath,
        AgentId = "11111111-2222-3333-4444-555555555555",
        EnvironmentId = "env-1",
        DataverseOrganization = new Uri(CopilotTestFixtures.DataverseOrganization),
    };

    [Fact]
    public async Task Push_ReportsWhenTheAgentWasLastPublished_AsTheDefinitionItPushedSays()
    {
        var publishedOn = new DateTime(2026, 10, 25, 19, 2, 38, DateTimeKind.Utc);
        var synchronizer = new RecordingWorkspaceSynchronizer { DefinitionReturns = BotDefinition.Empty.WithEntity(new BotEntity.Builder { SchemaName = new BotEntitySchemaName("crf9a_classicAgent"), DisplayName = "Contoso Helpdesk", PublishedOn = publishedOn }.Build()) };

        var pushed = await CopilotTestFixtures.WorkspaceAdapter(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(new DateTimeOffset(publishedOn), pushed.PublishedOn);
    }

    [Theory]
    [MemberData(nameof(UnreadableSettings))]
    public async Task AnUnreadableSettingsFile_IsOneFileErrorThatStopsPushPullAndChanges(string settings)
    {
        WriteSettings(settings);
        var unreadable = await Assert.ThrowsAsync<WorkspaceValidationException>(() => CopilotTestFixtures.ReadWorkspaceDefinitionAsync(_workspacePath));
        var synchronizer = new RecordingWorkspaceSynchronizer { ReadWorkspaceDefinitionThrows = unreadable };
        var adapter = CopilotTestFixtures.WorkspaceAdapter(synchronizer);

        CopilotStudioException[] failures =
        [
            await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.PushAsync(Workspace(), overwrite: true, CancellationToken.None)),
            await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.PullAsync(Workspace(), CancellationToken.None)),
            await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.GetChangesAsync(Workspace(), CancellationToken.None)),
        ];

        Assert.Equal(WorkspaceFileNames.Settings, Assert.Single(unreadable.Diagnostics).FilePath);
        Assert.All(failures, failure => Assert.Equal((ErrorCodes.Workspace.CopilotWorkspaceFilesUnreadable, ExitCodes.Usage), (failure.ErrorCode, failure.ExitCode)));
        Assert.All(failures, failure => Assert.StartsWith("1 workspace file could not be read:", failure.Message, StringComparison.Ordinal));
        Assert.All(failures, failure => Assert.Contains(WorkspaceFileNames.Settings, failure.Message, StringComparison.Ordinal));
        Assert.Equal(0, synchronizer.PushLocalChangesCalls);
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.PullExistingChangesAsync), synchronizer.Calls);
    }
}
