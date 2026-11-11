using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;
using Microsoft.CopilotStudio.Sync.Copy;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class WorkspaceCopyAdapterTests : IDisposable
{
    private const string CopyWorkflowId = "d0d0d0d0-1111-2222-3333-444444444444";

    private const string SourceWorkflowId = "c0c0c0c0-1111-2222-3333-444444444444";

    private readonly string _workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ah-copy-adapter-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ProposeSchemaName_KeepsTheSourcesPublisherPrefixAndLeavesRoomForTheLongestComponent()
    {
        var topicName = new string('t', 70);
        using var workspace = TemporaryWorkspace.Classic(schemaName: "con_source", displayName: "Source Agent");
        workspace.AddFile(Path.Combine("topics", $"{topicName}.mcs.yml"), "mcs.metadata:\n  componentName: Greeting\nkind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n  intent:\n    triggerQueries:\n      - Hello\n\n  actions:\n    - kind: SendActivity\n      id: sendMessage_abmysR\n      activity: Welcome.\n");
        using var provider = BuildProvider();

        var proposal = await provider.GetRequiredService<ICopilotCopyPort>().ProposeSchemaNameAsync(workspace.Root, "con_source", "A very long display name for the copy", CancellationToken.None);

        Assert.Matches("^con_averylongdis_[A-Za-z]{6}$", proposal);
        Assert.Equal(100, $"{proposal}.topic.{topicName}".Length);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Prepare_HandsTheSyncPreparationTheCopysEnvironmentOnlyWhenOneIsNamed(bool namesAnEnvironment, bool capturedInTargetEnvironment)
    {
        var client = new RecordingConnectionReferenceClient();
        var copies = new RecordingCopyService();
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider());
        var command = CopyCommand() with
        {
            CapturedInTargetEnvironment = capturedInTargetEnvironment,
            Environment = namesAnEnvironment ? ManagedAgentFixtures.EnvironmentContext() : null,
        };

        await new WorkspaceCopyAdapter(copies, client, client, accessor).PrepareAsync(command, CancellationToken.None);

        var request = Assert.Single(copies.Requests);
        Assert.Equal(capturedInTargetEnvironment, request.CapturedInTargetEnvironment);
        Assert.Same(namesAnEnvironment ? client : null, request.TargetEnvironment);
        Assert.Equal(namesAnEnvironment ? new Uri(CopilotTestFixtures.DataverseOrganization) : null, copies.ConnectedOrganization);
    }

    [Fact]
    public async Task Prepare_HandsTheSyncPreparationTheStagedWorkspaceAndTheCopysNames()
    {
        var client = new RecordingConnectionReferenceClient();
        var copies = new RecordingCopyService();
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider());
        var definition = Path.Combine(_workspace, "botdefinition.json");

        await new WorkspaceCopyAdapter(copies, client, client, accessor).PrepareAsync(CopyCommand() with { SourceDefinitionPath = definition }, CancellationToken.None);

        var request = Assert.Single(copies.Requests);
        Assert.Equal(_workspace, request.WorkspacePath);
        Assert.Equal("con_source", request.SourceSchemaName);
        Assert.Equal("Copy Agent", request.DisplayName);
        Assert.Equal("con_copy", request.SchemaName);
        Assert.Equal(definition, request.SourceDefinitionPath);
    }

    [Fact]
    public async Task Prepare_CarriesWhatTheSyncPreparationWrote()
    {
        var client = new RecordingConnectionReferenceClient();
        var copies = new RecordingCopyService
        {
            Result = new CopyPreparationResult
            {
                Identities = [new CopyIdentityMapping(CopyIdentityKinds.Schema, "con_source", "con_copy"), new CopyIdentityMapping(CopyIdentityKinds.Workflow, SourceWorkflowId, CopyWorkflowId)],
                RewrittenFiles = ["settings.mcs.yml"],
                RenamedPaths = [new CopyIdentityMapping("path", $"workflows/Weather-{SourceWorkflowId}", $"workflows/Weather-{CopyWorkflowId}")],
                DraftWorkflows = ["Copy Agent - Weather"],
            },
        };
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider());

        var preparation = await new WorkspaceCopyAdapter(copies, client, client, accessor).PrepareAsync(CopyCommand(), CancellationToken.None);

        Assert.Equal([new CopilotCopyIdentity(CopilotCopyIdentityKinds.Schema, "con_source", "con_copy"), new CopilotCopyIdentity(CopilotCopyIdentityKinds.Workflow, SourceWorkflowId, CopyWorkflowId)], preparation.Identities);
        Assert.Equal(["settings.mcs.yml"], preparation.RewrittenFiles);
        Assert.Equal([new CopilotCopyIdentity("path", $"workflows/Weather-{SourceWorkflowId}", $"workflows/Weather-{CopyWorkflowId}")], preparation.RenamedPaths);
        Assert.Equal(["Copy Agent - Weather"], preparation.DraftWorkflows);
        Assert.Empty(preparation.Blockers);
    }

    [Fact]
    public async Task Prepare_CarriesTheSyncPreparationsNoticesAsFindings()
    {
        var client = new RecordingConnectionReferenceClient();
        var copies = new RecordingCopyService
        {
            Result = new CopyPreparationResult { Audit = new CopyAuditReport { Notices = [new CopyAuditFinding(CopyAuditCodes.UnsupportedResource, "The knowledge source Handbook is copied as it is.", "knowledge/Handbook.mcs.yml")] } },
        };
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider());

        var preparation = await new WorkspaceCopyAdapter(copies, client, client, accessor).PrepareAsync(CopyCommand(), CancellationToken.None);

        Assert.Equal([new CopilotCopyFinding(CopyAuditCodes.UnsupportedResource, "The knowledge source Handbook is copied as it is.", "knowledge/Handbook.mcs.yml")], preparation.Notices);
    }

    [Fact]
    public async Task Prepare_WhenTheSyncPreparationIsBlocked_ReturnsItsFindingsAsBlockersAndNothingWritten()
    {
        var client = new RecordingConnectionReferenceClient();
        var copies = new RecordingCopyService { Throws = new CopyPreparationException([new CopyAuditFinding(CopyAuditCodes.IdentityTaken, "The schema name con_copy already exists in the environment (Other Agent). Copy the agent again with another schema name.", "settings.mcs.yml"), new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, "Greeting.mcs.yml still names con_source.")]) };
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider());

        var preparation = await new WorkspaceCopyAdapter(copies, client, client, accessor).PrepareAsync(CopyCommand() with { Environment = ManagedAgentFixtures.EnvironmentContext() }, CancellationToken.None);

        Assert.Equal([new CopilotCopyFinding(CopyAuditCodes.IdentityTaken, "The schema name con_copy already exists in the environment (Other Agent). Copy the agent again with another schema name.", "settings.mcs.yml"), new CopilotCopyFinding(CopyAuditCodes.LeftoverSourceIdentity, "Greeting.mcs.yml still names con_source.")], preparation.Blockers);
        Assert.Empty(preparation.Identities);
        Assert.Empty(preparation.RewrittenFiles);
    }

    private CopilotCopyPrepareCommand CopyCommand() => new()
    {
        WorkspaceDirectory = _workspace,
        SourceSchemaName = "con_source",
        DisplayName = "Copy Agent",
        SchemaName = "con_copy",
    };

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITokenProvider>(new RecordingTokenProvider());
        services.AddAgentHostCopilotStudio(TextWriter.Null, PowerPlatformApiRingSelection.Default);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
    }

    private sealed class RecordingCopyService : IWorkspaceCopyService
    {
        public List<CopyPreparationRequest> Requests { get; } = [];

        public CopyPreparationResult Result { get; init; } = new();

        public Exception? Throws { get; init; }

        public Uri? ConnectedOrganization { get; private set; }

        public Task<CopyPreparationResult> PrepareAsync(CopyPreparationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            ConnectedOrganization = DataverseOperationScope.Organization;

            return Throws is null ? Task.FromResult(Result) : Task.FromException<CopyPreparationResult>(Throws);
        }

        public Task<string?> ProposeSchemaNameAsync(string workspacePath, string sourceSchemaName, string displayName, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
