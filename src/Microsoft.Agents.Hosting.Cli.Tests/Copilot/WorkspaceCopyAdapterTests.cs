using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;
using Microsoft.CopilotStudio.Sync;
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
        using var provider = CopilotTestFixtures.SyncServices();

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

    [Theory]
    [InlineData(AgentTypes.McsAgent, "con_copy", null, CopyWorkspaceKinds.Agent, "con_copy")]
    [InlineData(AgentTypes.McsWorkflow, null, SourceWorkflowId, CopyWorkspaceKinds.Workflow, "")]
    public async Task Prepare_MapsTheKindSchemaAndSourceWorkflow(string agentType, string? schemaName, string? sourceWorkflowId, string workspaceKind, string requestedSchemaName)
    {
        var client = new RecordingConnectionReferenceClient();
        var copies = new RecordingCopyService();
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider());
        var command = CopyCommand() with
        {
            AgentType = agentType,
            SchemaName = schemaName,
            SourceWorkflowId = sourceWorkflowId is null ? null : Guid.Parse(sourceWorkflowId),
        };

        await new WorkspaceCopyAdapter(copies, client, client, accessor).PrepareAsync(command, CancellationToken.None);

        var request = Assert.Single(copies.Requests);
        Assert.Equal(workspaceKind, request.WorkspaceKind);
        Assert.Equal(requestedSchemaName, request.SchemaName);
        Assert.Equal(command.SourceWorkflowId, request.SourceWorkflowId);
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

    [Fact]
    public async Task ACopyDeployedBesideItsSource_IsRefusedBeforeItsAgentIsCreatedRatherThanOverwritingTheSourcesSharedEnvironmentVariable()
    {
        new EmbeddedTemplateScaffolder().Scaffold(new CopilotScaffoldCommand
        {
            ProjectDirectory = _workspace,
            TemplateFolder = "cli-copilot",
            DisplayName = "Source Agent",
            SchemaName = "con_source",
            Instructions = "Be helpful.",
        });
        var sharedVariableFile = CopilotTestFixtures.WriteEnvironmentVariableFile(_workspace, "cr9_shared", "source-local-edit");

        using var provider = CopilotTestFixtures.SyncServices();
        var dataverseClient = new RecordingAgentCreationClient();
        dataverseClient.EnvironmentVariables.Add(new SyncDataverseClient.EnvironmentVariableDefinitionInfo { DefinitionId = Guid.Parse("22222222-2222-2222-2222-222222222222"), SchemaName = "cr9_shared", Version = 200 });
        var attach = new WorkspaceReattachAdapter(provider.GetRequiredService<IWorkspaceSynchronizer>(), provider.GetRequiredService<IEnvironmentVariableDependencyValidator>(), new RecordingRetargetService(), dataverseClient, new StubOperationContextProvider(), new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider()));

        var preparation = await provider.GetRequiredService<ICopilotCopyPort>().PrepareAsync(CopyCommand(), CancellationToken.None);

        Assert.Empty(preparation.Blockers);
        Assert.True(File.Exists(sharedVariableFile));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => attach.AttachAsync(new CopilotAttachCommand { Environment = ManagedAgentFixtures.EnvironmentContext(), ProjectDirectory = _workspace, DisplayName = "Copy Agent", SchemaName = "con_copy" }, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.SharedEnvironmentVariablesRequireOverwrite, failure.ErrorKind);
        Assert.Contains("cr9_shared", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, dataverseClient.CreateCalls);
    }

    private CopilotCopyPrepareCommand CopyCommand() => new()
    {
        WorkspaceDirectory = _workspace,
        SourceSchemaName = "con_source",
        DisplayName = "Copy Agent",
        SchemaName = "con_copy",
    };


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
