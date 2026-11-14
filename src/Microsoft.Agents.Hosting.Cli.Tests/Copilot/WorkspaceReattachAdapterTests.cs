using System.Collections.Immutable;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class WorkspaceReattachAdapterTests : IDisposable
{
    private static readonly Guid CreatedAgentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ah-reattach-" + Guid.NewGuid().ToString("N"));

    public WorkspaceReattachAdapterTests() => new EmbeddedTemplateScaffolder().Scaffold(new CopilotScaffoldCommand
    {
        ProjectDirectory = _directory,
        TemplateFolder = "cli-copilot",
        DisplayName = "Contoso Helpdesk",
        SchemaName = "con_contosohelpdesk",
        Instructions = "Be helpful.",
    });

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static WorkspaceReattachAdapter Create(RecordingWorkspaceSynchronizer synchronizer, RecordingRetargetService retargetService, RecordingAgentCreationClient? dataverseClient = null, IEnvironmentVariableDependencyValidator? environmentVariableValidator = null) =>
        new(
            synchronizer,
            environmentVariableValidator ?? synchronizer,
            retargetService,
            dataverseClient ?? new RecordingAgentCreationClient(),
            new StubOperationContextProvider(),
            new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider()));

    private static RecordingWorkspaceSynchronizer Unconnected() => new() { SyncInfoAvailable = false };

    private void DeclareClassic() =>
        File.WriteAllText(Path.Combine(_directory, "connectionreferences.mcs.yml"), """
connectionReferences:
  - connectionReferenceLogicalName: new_sharedmsnweather_08889
    connectorId: /providers/Microsoft.PowerApps/apis/shared_msnweather
""");

    private CopilotAttachCommand Command(string schemaName = "con_contosohelpdesk") => new()
    {
        Environment = new CopilotEnvironmentContext
        {
            EnvironmentId = "env-1",
            TenantId = Guid.Empty.ToString(),
            DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
        },
        ProjectDirectory = _directory,
        DisplayName = "Contoso Helpdesk",
        SchemaName = schemaName,
    };

    [Fact]
    public async Task Attach_ChecksTheWorkspaceBeforeTheFirstCloudWrite()
    {
        var synchronizer = Unconnected();
        var dataverseClient = new RecordingAgentCreationClient();

        await Create(synchronizer, new RecordingRetargetService(), dataverseClient).AttachAsync(Command(), CancellationToken.None);

        var checkedAt = synchronizer.Calls.IndexOf(nameof(IWorkspaceSynchronizer.ThrowIfWorkspaceInvalidAsync));
        var firstWriteAt = synchronizer.Calls.IndexOf(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync));

        Assert.True(checkedAt >= 0);
        Assert.True(checkedAt < firstWriteAt);
    }

    [Fact]
    public async Task Attach_WhenTheWorkspaceIsInvalid_CreatesNoAgentAndSendsNothing()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            SyncInfoAvailable = false,
            ThrowIfWorkspaceInvalidThrows = new WorkspaceValidationException(
                [new WorkspaceDiagnostic("tools/lookup_order.tool.yml", "The file still contains merge conflict markers.", 12, 1, WorkspaceDiagnosticKind.MergeConflict)]),
        };
        var dataverseClient = new RecordingAgentCreationClient();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, new RecordingRetargetService(), dataverseClient).AttachAsync(Command(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceFilesUnreadable, failure.ErrorKind);
        Assert.Equal(0, dataverseClient.CreateCalls);
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), synchronizer.Calls);
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.UpsertWorkflowForAgentAsync), synchronizer.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Attach_WhenTheWorkspaceWouldOverwriteASharedEnvironmentVariableTheTargetHolds_CreatesNoAgentAndSendsNothing(bool retargeting)
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            SyncInfoAvailable = retargeting,
            DefinitionReturns = CopilotTestFixtures.AgentDefinition("con_contosohelpdesk", "cr9_shared"),
            EnvironmentVariableCheck = CopilotTestFixtures.TargetHolding("cr9_shared"),
        };
        var dataverseClient = new RecordingAgentCreationClient();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, new RecordingRetargetService(), dataverseClient).AttachAsync(Command() with { AllowRetarget = true }, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.SharedEnvironmentVariablesRequireOverwrite, failure.ErrorKind);
        Assert.Equal("This deploy would overwrite 1 shared environment variable that env-1 already holds: cr9_shared.", failure.Message);
        Assert.Equal(retargeting, Assert.Single(synchronizer.EnvironmentVariableChecksComparedWithTheCloudCache));
        Assert.Equal(0, dataverseClient.CreateCalls);
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), synchronizer.Calls);
    }

    [Fact]
    public async Task Attach_WhenTheTargetLacksAnEnvironmentVariableTheWorkspaceUsesBesideASharedOverwrite_RefusesTheMissingVariableBeforeCreatingTheAgent()
    {
        CopilotTestFixtures.WriteEnvironmentVariableFile(_directory, "cr9_shared");
        using var provider = CopilotTestFixtures.SyncServices();
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            SyncInfoAvailable = false,
            DefinitionReturns = CopilotTestFixtures.AgentDefinition("con_contosohelpdesk", "cr9_shared").WithComponents([CopilotTestFixtures.GreetingTopic("con_contosohelpdesk", "{Env.cr9_shared} {Env.cr9_missing}")]),
        };
        var dataverseClient = new RecordingAgentCreationClient();
        dataverseClient.EnvironmentVariables.Add(new SyncDataverseClient.EnvironmentVariableDefinitionInfo { DefinitionId = Guid.NewGuid(), SchemaName = "cr9_shared" });

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, new RecordingRetargetService(), dataverseClient, provider.GetRequiredService<IEnvironmentVariableDependencyValidator>()).AttachAsync(Command(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceFilesUnreadable, failure.ErrorKind);
        Assert.Contains("'cr9_missing' does not exist in the target environment", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, dataverseClient.CreateCalls);
        Assert.DoesNotContain(nameof(RecordingAgentCreationClient.GetAgentIdBySchemaNameAsync), dataverseClient.Calls);
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), synchronizer.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Attach_WhenTheTargetManagesASharedEnvironmentVariableTheWorkspaceChanges_RefusesItRatherThanAskingForOverwriteAndCreatesNoAgent(bool alongsideAnUnmanagedOverwrite)
    {
        string[] workspaceVariables = alongsideAnUnmanagedOverwrite ? ["cr9_managed", "cr9_shared"] : ["cr9_managed"];
        foreach (var schemaName in workspaceVariables)
        {
            CopilotTestFixtures.WriteEnvironmentVariableFile(_directory, schemaName);
        }

        using var provider = CopilotTestFixtures.SyncServices();
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            SyncInfoAvailable = false,
            DefinitionReturns = CopilotTestFixtures.AgentDefinition("con_contosohelpdesk", workspaceVariables),
        };
        var dataverseClient = new RecordingAgentCreationClient();
        dataverseClient.EnvironmentVariables.Add(new SyncDataverseClient.EnvironmentVariableDefinitionInfo { DefinitionId = Guid.NewGuid(), SchemaName = "cr9_managed", IsManaged = true });
        dataverseClient.EnvironmentVariables.Add(new SyncDataverseClient.EnvironmentVariableDefinitionInfo { DefinitionId = Guid.NewGuid(), SchemaName = "cr9_shared" });

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, new RecordingRetargetService(), dataverseClient, provider.GetRequiredService<IEnvironmentVariableDependencyValidator>()).AttachAsync(Command(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceFilesUnreadable, failure.ErrorKind);
        Assert.Contains("'cr9_managed' is managed in the target environment", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cr9_shared", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, dataverseClient.CreateCalls);
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), synchronizer.Calls);
    }

    [Fact]
    public async Task Attach_WhenOverwritingSharedEnvironmentVariables_CreatesTheAgentWithoutAskingTheTargetWhichItHolds()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            SyncInfoAvailable = false,
            DefinitionReturns = CopilotTestFixtures.AgentDefinition("con_contosohelpdesk", "cr9_shared"),
            EnvironmentVariableCheck = CopilotTestFixtures.TargetHolding("cr9_shared"),
        };
        var dataverseClient = new RecordingAgentCreationClient();

        await Create(synchronizer, new RecordingRetargetService(), dataverseClient).AttachAsync(Command() with { OverwriteSharedEnvironmentVariables = true }, CancellationToken.None);

        Assert.Empty(synchronizer.EnvironmentVariableChecksComparedWithTheCloudCache);
        Assert.Equal(1, dataverseClient.CreateCalls);
    }

    [Fact]
    public async Task Attach_OfAWorkspaceHoldingOnlyItsOwnEnvironmentVariables_CreatesTheAgentWithoutAskingTheTargetWhichItHolds()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            SyncInfoAvailable = false,
            DefinitionReturns = CopilotTestFixtures.AgentDefinition("con_contosohelpdesk", "con_contosohelpdesk.greeting"),
            EnvironmentVariableCheck = CopilotTestFixtures.TargetHolding("con_contosohelpdesk.greeting"),
        };
        var dataverseClient = new RecordingAgentCreationClient();

        await Create(synchronizer, new RecordingRetargetService(), dataverseClient).AttachAsync(Command(), CancellationToken.None);

        Assert.Empty(synchronizer.EnvironmentVariableChecksComparedWithTheCloudCache);
        Assert.Equal(1, dataverseClient.CreateCalls);
    }

    [Fact]
    public async Task Attach_WithNoDefinitionToRead_HasNothingToCheck()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            SyncInfoAvailable = false,
            ReadWorkspaceDefinitionThrows = new FileNotFoundException(".mcs/botdefinition.json was not found. Please resync."),
        };

        await Create(synchronizer, new RecordingRetargetService()).AttachAsync(Command(), CancellationToken.None);

        Assert.Equal(0, synchronizer.ThrowIfWorkspaceInvalidCalls);
    }

    [Fact]
    public async Task Attach_WithNoCloudBaselineAndNoCachelessDefinition_StillReattaches()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            SyncInfoAvailable = false,
            ReadWorkspaceDefinitionThrows = new FileNotFoundException(".mcs/botdefinition.json was not found. Please resync."),
        };
        var dataverseClient = new RecordingAgentCreationClient();

        var outcome = await Create(synchronizer, new RecordingRetargetService(), dataverseClient).AttachAsync(Command(), CancellationToken.None);

        Assert.Equal(CreatedAgentId.ToString(), outcome.AgentId);
        Assert.Equal(1, dataverseClient.CreateCalls);
    }

    [Fact]
    public async Task Attach_WithNoDefinitionAndNoDeclarations_SavesTheBindingWithoutProvisioning()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            SyncInfoAvailable = false,
            ReadWorkspaceDefinitionThrows = new FileNotFoundException(".mcs/botdefinition.json was not found. Please resync."),
        };

        await Create(synchronizer, new RecordingRetargetService()).AttachAsync(Command(), CancellationToken.None);

        Assert.Equal(0, synchronizer.DefinitionOnlyProvisionCount);
        Assert.Contains(nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync), synchronizer.Calls);
    }

    [Fact]
    public async Task Attach_WithADefinition_ProvisionsConnectionReferencesThroughTheSdk()
    {
        DeclareClassic();
        var synchronizer = Unconnected();

        await Create(synchronizer, new RecordingRetargetService()).AttachAsync(Command(), CancellationToken.None);

        Assert.Contains(nameof(IWorkspaceSynchronizer.ProvisionConnectionReferencesAsync), synchronizer.Calls);
        Assert.Contains(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), synchronizer.Calls);
    }

    [Fact]
    public async Task Attach_UploadsTheLocalConnectorsAndWarnsAboutAnyNotSaved()
    {
        const string reason = "Failed to push custom connector 'Weather': denied";
        var synchronizer = Unconnected();
        synchronizer.ConnectorFailureReasons.Add(reason);

        var outcome = await Create(synchronizer, new RecordingRetargetService()).AttachAsync(Command(), CancellationToken.None);

        Assert.True(synchronizer.LastConnectorPushOverwroteRemoteChanges);
        Assert.Contains(reason, outcome.Warnings);
    }

    [Fact]
    public async Task Attach_ProvisionsConnectionReferencesBeforeUpsertingTheWorkflowsThatNeedThem()
    {
        DeclareClassic();
        var synchronizer = Unconnected();

        await Create(synchronizer, new RecordingRetargetService()).AttachAsync(Command(), CancellationToken.None);

        var provision = synchronizer.Calls.IndexOf(nameof(IWorkspaceSynchronizer.ProvisionConnectionReferencesAsync));
        var workflows = synchronizer.Calls.IndexOf(nameof(IWorkspaceSynchronizer.UpsertWorkflowForAgentAsync));

        Assert.InRange(provision, 0, int.MaxValue);
        Assert.InRange(workflows, 0, int.MaxValue);
        Assert.True(provision < workflows, "a workflow cannot be created against a connection reference the target environment does not hold yet.");
    }

    [Fact]
    public async Task Attach_SeedsTheBaselineWithEveryDeclaredConnectionReference()
    {
        var connections = Path.Combine(_directory, "infrastructure", "connections");
        Directory.CreateDirectory(connections);
        File.WriteAllText(Path.Combine(connections, "new_sharedteams_777c895a.sync.yaml"), """
connectionReferences:
  - connectionReferenceLogicalName: new_sharedteams_777c895a
    connectorId: /providers/Microsoft.PowerApps/apis/shared_teams
""");
        File.WriteAllText(Path.Combine(connections, "crf9a_nb1.cr.shared_sharepointonline.a972.sync.yaml"), """
connectionReferences:
  - connectionReferenceLogicalName: crf9a_nb1.cr.shared_sharepointonline.a972
    connectorId: /providers/Microsoft.PowerApps/apis/shared_sharepointonline
""");
        var retargetService = new RecordingRetargetService();

        await Create(Unconnected(), retargetService).AttachAsync(Command(), CancellationToken.None);

        var seeded = retargetService.LastCloudFlowMetadata!.ConnectionReferences
            .Select(reference => reference.ConnectionReferenceLogicalName.Value)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(["crf9a_nb1.cr.shared_sharepointonline.a972", "new_sharedteams_777c895a"], seeded);
    }

    [Fact]
    public async Task Attach_SeedsTheBaselineWithAClassicWorkspacesDeclarations()
    {
        File.WriteAllText(Path.Combine(_directory, "connectionreferences.mcs.yml"), """
connectionReferences:
  - connectionReferenceLogicalName: new_sharedmsnweather_08889
    connectorId: /providers/Microsoft.PowerApps/apis/shared_msnweather
""");
        var retargetService = new RecordingRetargetService();

        await Create(Unconnected(), retargetService).AttachAsync(Command(), CancellationToken.None);

        Assert.Contains(
            retargetService.LastCloudFlowMetadata!.ConnectionReferences,
            reference => reference.ConnectionReferenceLogicalName.Value == "new_sharedmsnweather_08889");
    }

    [Fact]
    public async Task Attach_SeedsNothingWhenTheWorkspaceDeclaresNoConnectionReferences()
    {
        var retargetService = new RecordingRetargetService();

        await Create(Unconnected(), retargetService).AttachAsync(Command(), CancellationToken.None);

        Assert.Empty(retargetService.LastCloudFlowMetadata!.ConnectionReferences);
    }

    [Fact]
    public async Task Attach_WithNoDefinition_ProvisionsClassicDeclarationsBeforeSeeding()
    {
        File.WriteAllText(Path.Combine(_directory, "connectionreferences.mcs.yml"), """
connectionReferences:
  - connectionReferenceLogicalName: new_sharedmsnweather_08889
    connectorId: /providers/Microsoft.PowerApps/apis/shared_msnweather
""");
        var dataverseClient = new RecordingAgentCreationClient();

        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            SyncInfoAvailable = false,
            ReadWorkspaceDefinitionThrows = new FileNotFoundException(".mcs/botdefinition.json was not found. Please resync."),
        };
        var retarget = new RecordingRetargetService();

        await Create(synchronizer, retarget, dataverseClient).AttachAsync(Command(), CancellationToken.None);

        Assert.Equal(["new_sharedmsnweather_08889"], dataverseClient.EnsuredConnectionReferences);
        var cached = Assert.Single(retarget.LastCloudFlowMetadata!.ConnectionReferences);
        Assert.Equal(dataverseClient.References[cached.ConnectionReferenceLogicalName.Value].ConnectionReferenceId, cached.Id.Value);
        Assert.Equal(0, synchronizer.FolderAwareProvisionCount);
    }

    [Fact]
    public async Task Attach_EnsuresNoConnectionReferencesWhenTheWorkspaceDeclaresNone()
    {
        var dataverseClient = new RecordingAgentCreationClient();

        await Create(Unconnected(), new RecordingRetargetService(), dataverseClient).AttachAsync(Command(), CancellationToken.None);

        Assert.Empty(dataverseClient.EnsuredConnectionReferences);
    }

    [Fact]
    public async Task Attach_DoesNotSeedOrSaveWhenADeclarationWasNotConfirmed()
    {
        File.WriteAllText(Path.Combine(_directory, "connectionreferences.mcs.yml"), """
connectionReferences:
  - connectionReferenceLogicalName: classic.shared_teams
    connectorId: /providers/Microsoft.PowerApps/apis/shared_teams
""");
        var client = new RecordingAgentCreationClient { OmitProvisionedReferences = true };
        var synchronizer = Unconnected();
        var retarget = new RecordingRetargetService();

        await Assert.ThrowsAsync<CopilotStudioException>(() =>
            Create(synchronizer, retarget, client).AttachAsync(Command(), CancellationToken.None));

        Assert.Null(retarget.LastCloudFlowMetadata);
        Assert.Empty(synchronizer.SavedSyncInfo);
        Assert.Equal(0, synchronizer.UpsertWorkflowCalls);
    }

    [Fact]
    public async Task Attach_DraftsWorkflowsBecauseItsConnectionReferencesAreNotYetBound()
    {
        var synchronizer = Unconnected();

        await Create(synchronizer, new RecordingRetargetService()).AttachAsync(Command(), CancellationToken.None);

        Assert.Equal(WorkflowActivationMode.DraftWhenConnectionReferencesExist, synchronizer.LastWorkflowActivationMode);
    }

    [Fact]
    public async Task Attach_CreatesTheAgent()
    {
        var dataverseClient = new RecordingAgentCreationClient();

        var outcome = await Create(Unconnected(), new RecordingRetargetService(), dataverseClient).AttachAsync(Command(), CancellationToken.None);

        Assert.Equal(1, dataverseClient.CreateCalls);
        Assert.Equal(CreatedAgentId.ToString(), outcome.AgentId);
    }

    [Fact]
    public async Task Attach_PassesTheShapeReadFromTheWorkspace()
    {
        var dataverseClient = new RecordingAgentCreationClient();

        await Create(Unconnected(), new RecordingRetargetService(), dataverseClient).AttachAsync(Command(), CancellationToken.None);

        Assert.Equal(AuthoringShape.CliCopilot, dataverseClient.LastAuthoringShape);
    }

    [Fact]
    public async Task Attach_LeavesTheScaffoldedFilesUntouched()
    {
        var retargetService = new RecordingRetargetService();

        await Create(Unconnected(), retargetService).AttachAsync(Command(), CancellationToken.None);

        Assert.False(retargetService.LastUpdateWorkspaceDirectory);
    }

    [Fact]
    public async Task Attach_WhenTheServiceRenamesTheSchema_AllowsTheWorkspaceToBeUpdated()
    {
        var retargetService = new RecordingRetargetService();
        var dataverseClient = new RecordingAgentCreationClient { SchemaNameReturns = "con_contosohelpdesk1" };

        var outcome = await Create(Unconnected(), retargetService, dataverseClient).AttachAsync(Command(), CancellationToken.None);

        Assert.True(retargetService.LastUpdateWorkspaceDirectory);
        Assert.Equal("con_contosohelpdesk1", outcome.SchemaName);
    }

    [Fact]
    public async Task Attach_SavesTheSyncInfoAfterTheBaselineIsWritten()
    {
        var synchronizer = Unconnected();
        var retargetService = new RecordingRetargetService { Calls = synchronizer.Calls };

        await Create(synchronizer, retargetService).AttachAsync(Command(), CancellationToken.None);

        Assert.Equal(
            [nameof(IWorkspaceRetargetService.SyncWorkspaceAsync), nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync)],
            synchronizer.Calls.Where(call => call is nameof(IWorkspaceRetargetService.SyncWorkspaceAsync) or nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync)));
    }

    [Fact]
    public async Task Attach_TargetsTheDataverseOrganizationBeforeTheFirstAwait()
    {
        var dataverseClient = new RecordingAgentCreationClient();

        await Create(Unconnected(), new RecordingRetargetService(), dataverseClient).AttachAsync(Command(), CancellationToken.None);

        Assert.Equal(["SetDataverseUrl", "GetAgentIdBySchemaNameAsync", "CreateNewAgentAsync", "GetSolutionVersionsAsync"], dataverseClient.Calls);
    }

    [Fact]
    public async Task Attach_NeverWritesTheCloudCacheItself()
    {
        await Create(Unconnected(), new RecordingRetargetService()).AttachAsync(Command(), CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_directory, ".mcs", "botdefinition.json")));
        Assert.False(File.Exists(Path.Combine(_directory, ".mcs", "changetoken.txt")));
        Assert.False(File.Exists(Path.Combine(_directory, ".mcs", "conn.json")));
    }

    [Fact]
    public async Task Attach_PassesNoChangeTokenSoTheBaselineComesFromTheService()
    {
        var retargetService = new RecordingRetargetService();

        await Create(Unconnected(), retargetService).AttachAsync(Command(), CancellationToken.None);

        Assert.Null(retargetService.LastChangeToken);
    }

    [Fact]
    public async Task Attach_DefersWorkflowsAndPromptsToThePush()
    {
        var retargetService = new RecordingRetargetService();

        await Create(Unconnected(), retargetService).AttachAsync(Command(), CancellationToken.None);

        Assert.False(retargetService.LastSyncWorkflowsAndPrompts);
        Assert.False(retargetService.LastSyncCustomConnectors);
    }

    [Fact]
    public async Task Attach_WhenTheBaselineFails_ReportsTheAgentThatNowExists()
    {
        var retargetService = new RecordingRetargetService { Throws = new InvalidOperationException("service unavailable") };

        var error = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Unconnected(), retargetService).AttachAsync(Command(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.BootstrapReattachFailed, error.ErrorKind);
        Assert.Contains(CreatedAgentId.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Attach_WhenTheBaselineFails_LeavesNoPartialCloudCache()
    {
        var retargetService = new RecordingRetargetService { Throws = new InvalidOperationException("service unavailable") };
        Directory.CreateDirectory(Path.Combine(_directory, ".mcs"));
        File.WriteAllText(Path.Combine(_directory, ".mcs", "botdefinition.json"), "{}");

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Unconnected(), retargetService).AttachAsync(Command(), CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(_directory, ".mcs")));
    }

    [Fact]
    public async Task Attach_WhenTheBaselineFails_LeavesTheScaffoldedFilesInPlace()
    {
        var retargetService = new RecordingRetargetService { Throws = new InvalidOperationException("service unavailable") };

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Unconnected(), retargetService).AttachAsync(Command(), CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(_directory, "settings.mcs.yml")));
        Assert.True(File.Exists(Path.Combine(_directory, "agent.sync.yaml")));
    }

    [Fact]
    public async Task Attach_WhenAWorkflowIsRejected_StillAttachesAndWarns()
    {
        var synchronizer = Unconnected();
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", ErrorMessage = "this is not a valid connection" });

        var outcome = await Create(synchronizer, new RecordingRetargetService()).AttachAsync(Command(), CancellationToken.None);

        Assert.Contains(outcome.Warnings, warning => warning.Contains("Escalate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Attach_WhenAWorkflowIsDraftedPendingItsConnection_Warns()
    {
        var synchronizer = Unconnected();
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", IsDisabled = true });

        var outcome = await Create(synchronizer, new RecordingRetargetService()).AttachAsync(Command(), CancellationToken.None);

        Assert.Contains(outcome.Warnings, warning => warning.Contains("Escalate", StringComparison.Ordinal) && warning.Contains("disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Attach_WhenAWorkflowIsRejected_StillWritesTheBaseline()
    {
        var synchronizer = Unconnected();
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", ErrorMessage = "this is not a valid connection" });
        var retargetService = new RecordingRetargetService();

        await Create(synchronizer, retargetService).AttachAsync(Command(), CancellationToken.None);

        Assert.Contains(nameof(RecordingRetargetService.SyncWorkspaceAsync), retargetService.Calls);
    }

    [Fact]
    public async Task Attach_WhenAPromptIsRejected_StillAttachesAndWarns()
    {
        var synchronizer = Unconnected();
        synchronizer.PromptResponses.Add(new SyncDataverseClient.AIPromptResponse { PromptName = "Summarize", ErrorMessage = "invalid model" });

        var outcome = await Create(synchronizer, new RecordingRetargetService()).AttachAsync(Command(), CancellationToken.None);

        Assert.True(synchronizer.LastPromptUpsertOverwroteRemoteChanges);
        Assert.Contains(outcome.Warnings, warning => warning.Contains("Summarize", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Attach_WhenCancelled_StillRemovesThePartialCloudCache()
    {
        var retargetService = new RecordingRetargetService { Throws = new OperationCanceledException() };
        Directory.CreateDirectory(Path.Combine(_directory, ".mcs"));
        File.WriteAllText(Path.Combine(_directory, ".mcs", "botdefinition.json"), "{}");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create(Unconnected(), retargetService).AttachAsync(Command(), CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(_directory, ".mcs")));
    }

    [Fact]
    public async Task Attach_WhenCancelled_ReportsTheCancellationRatherThanAReattachFailure()
    {
        var retargetService = new RecordingRetargetService { Throws = new OperationCanceledException() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create(Unconnected(), retargetService).AttachAsync(Command(), CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(_directory, "settings.mcs.yml")));
    }

    [Fact]
    public async Task Attach_WhenCreationIsAmbiguousButTheAgentNowExists_ReportsIt()
    {
        var client = new RecordingAgentCreationClient
        {
            CreateThrows = new InvalidOperationException("the response was lost"),
            SchemaProbeReturns = Guid.Parse("cccccccc-dddd-eeee-ffff-000000000000"),
        };

        var error = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Unconnected(), new RecordingRetargetService(), client).AttachAsync(Command(), CancellationToken.None));

        Assert.Contains("cccccccc-dddd-eeee-ffff-000000000000", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nothing was left behind", error.Remediation ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attach_WhenCreationFailsAndNothingExists_SaysSo()
    {
        var client = new RecordingAgentCreationClient { CreateThrows = new InvalidOperationException("quota exceeded") };

        var error = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Unconnected(), new RecordingRetargetService(), client).AttachAsync(Command(), CancellationToken.None));

        Assert.Contains("Nothing was left behind", error.Remediation ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attach_WhenCreationFailsAndTheProbeAlsoFails_DoesNotClaimTheEnvironmentIsUntouched()
    {
        var client = new RecordingAgentCreationClient
        {
            CreateThrows = new InvalidOperationException("the response was lost"),
            SchemaProbeThrows = new InvalidOperationException("the service is unavailable"),
        };

        var error = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Unconnected(), new RecordingRetargetService(), client).AttachAsync(Command(), CancellationToken.None));

        Assert.Contains("could not be re-read", error.Message, StringComparison.Ordinal);
        Assert.Contains("may or may not exist", error.Remediation ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attach_WhenSyncInfoFails_ReportsTheAgentItLeftBehind()
    {
        var synchronizer = Unconnected();
        var client = new RecordingAgentCreationClient { SolutionVersionsThrows = new InvalidOperationException("service unavailable") };

        var error = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer, new RecordingRetargetService(), client).AttachAsync(Command(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.BootstrapReattachFailed, error.ErrorKind);
        Assert.Contains(CreatedAgentId.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Attach_WhenTheAgentCannotBeCreated_ReportsThatNothingWasLeftBehind()
    {
        var client = new RecordingAgentCreationClient { CreateThrows = new InvalidOperationException("quota exceeded") };

        var error = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Unconnected(), new RecordingRetargetService(), client).AttachAsync(Command(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.BootstrapReattachFailed, error.ErrorKind);
        Assert.DoesNotContain(CreatedAgentId.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Attach_WhenSyncInfoFails_LeavesNoPartialCloudCache()
    {
        var client = new RecordingAgentCreationClient { SolutionVersionsThrows = new InvalidOperationException("service unavailable") };
        Directory.CreateDirectory(Path.Combine(_directory, ".mcs"));
        File.WriteAllText(Path.Combine(_directory, ".mcs", "botdefinition.json"), "{}");

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Unconnected(), new RecordingRetargetService(), client).AttachAsync(Command(), CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(_directory, ".mcs")));
    }
}

internal sealed class RecordingAgentCreationClient : RecordingConnectionReferenceClient, ISyncDataverseClient
{
    public List<string> Calls { get; } = [];

    public int CreateCalls { get; private set; }

    public AuthoringShape LastAuthoringShape { get; private set; } = AuthoringShape.Unknown;

    public Guid ExistingAgentId { get; init; }

    public string? SchemaNameReturns { get; init; }

    public Exception? SolutionVersionsThrows { get; init; }

    public Exception? CreateThrows { get; init; }

    public Guid SchemaProbeReturns { get; init; }

    public Exception? SchemaProbeThrows { get; init; }

    public List<string> EnsuredConnectionReferences => EnsuredLogicalNames;

    public List<EnvironmentVariableDefinitionInfo> EnvironmentVariables { get; } = [];

    public override Task<EnvironmentVariableDefinitionInfo[]> GetEnvironmentVariableDefinitionsBySchemaNamesAsync(IEnumerable<string> schemaNames, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(GetEnvironmentVariableDefinitionsBySchemaNamesAsync));
        return Task.FromResult(EnvironmentVariables.Where(definition => schemaNames.Contains(definition.SchemaName, StringComparer.OrdinalIgnoreCase)).ToArray());
    }

    public override Task<SolutionInfo> GetSolutionVersionsAsync(CancellationToken cancellationToken)
    {
        Calls.Add(nameof(GetSolutionVersionsAsync));
        return SolutionVersionsThrows is not null ? Task.FromException<SolutionInfo>(SolutionVersionsThrows) : Task.FromResult(new SolutionInfo());
    }

    public override Task<Guid> GetAgentIdBySchemaNameAsync(string schemaName, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(GetAgentIdBySchemaNameAsync));

        if (Calls.Count(call => call == nameof(GetAgentIdBySchemaNameAsync)) > 1)
        {
            return SchemaProbeThrows is not null
                ? Task.FromException<Guid>(SchemaProbeThrows)
                : Task.FromResult(SchemaProbeReturns);
        }

        return Task.FromResult(ExistingAgentId);
    }

    void ISyncDataverseClient.SetDataverseUrl(string dataverseUrl)
    {
        Calls.Add("SetDataverseUrl");
        base.SetDataverseUrl(dataverseUrl);
    }

    public override Task<AgentInfo> CreateNewAgentAsync(string displayName, string schemaName, AuthoringShape authoringShape, CancellationToken cancellationToken)
    {
        Calls.Add("CreateNewAgentAsync");
        CreateCalls++;
        LastAuthoringShape = authoringShape;

        if (CreateThrows is not null)
        {
            return Task.FromException<AgentInfo>(CreateThrows);
        }

        return Task.FromResult(new AgentInfo
        {
            AgentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            DisplayName = displayName,
            SchemaName = SchemaNameReturns ?? schemaName,
        });
    }
}

internal sealed class RecordingRetargetService : IWorkspaceRetargetService
{
    public List<string> Calls { get; set; } = [];

    public bool LastUpdateWorkspaceDirectory { get; private set; } = true;

    public string? LastChangeToken { get; private set; } = "unset";

    public bool LastSyncCustomConnectors { get; private set; } = true;

    public bool LastSyncWorkflowsAndPrompts { get; private set; } = true;

    public CloudFlowMetadata? LastCloudFlowMetadata { get; private set; }

    public ImmutableArray<SyncDataverseClient.AIPromptMetadata> LastAIPromptMetadata { get; private set; }

    public Exception? Throws { get; init; }

    public RemoteBindingSnapshot ResetRemoteBindingState(DirectoryPath workspaceFolder) => RemoteBindingSnapshot.Empty;

    public void RestoreRemoteBindingState(DirectoryPath workspaceFolder, RemoteBindingSnapshot snapshot)
    {
    }

    public void PersistRetargetBackup(DirectoryPath workspaceFolder, RemoteBindingSnapshot snapshot)
    {
    }

    public bool FinalizeRetarget(DirectoryPath workspaceFolder, bool pushSucceeded) => true;

    public void ClearRetargetBackup(DirectoryPath workspaceFolder)
    {
    }

    public Task<WorkspaceSyncInfo> SyncWorkspaceAsync(
        DirectoryPath workspaceFolder,
        AuthoringOperationContextBase operationContext,
        string? changeToken,
        bool updateWorkspaceDirectory,
        ISyncDataverseClient dataverseClient,
        AgentSyncInfo syncInfo,
        CloudFlowMetadata? cloudFlowMetadata,
        CancellationToken cancellationToken,
        ImmutableArray<SyncDataverseClient.AIPromptMetadata> aiPromptMetadata,
        bool syncCustomConnectors = true,
        bool syncWorkflowsAndPrompts = true,
        bool preserveLocalConnectorAndPromptChanges = false)
    {
        Calls.Add(nameof(SyncWorkspaceAsync));
        LastUpdateWorkspaceDirectory = updateWorkspaceDirectory;
        LastChangeToken = changeToken;
        LastSyncCustomConnectors = syncCustomConnectors;
        LastSyncWorkflowsAndPrompts = syncWorkflowsAndPrompts;
        LastCloudFlowMetadata = cloudFlowMetadata;
        LastAIPromptMetadata = aiPromptMetadata;

        if (Throws is not null)
        {
            throw Throws;
        }

        return Task.FromResult(new WorkspaceSyncInfo());
    }
}
