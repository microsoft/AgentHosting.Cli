using System.Collections.Immutable;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class WorkspaceReattachAdapterTests : IDisposable
{
    private const string TenantId = "12345678-90ab-cdef-1234-567890abcdef";

    private static readonly Guid CreatedAgentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static readonly Guid ExistingAgentId = Guid.Parse("cccccccc-dddd-eeee-ffff-000000000000");

    private static readonly Guid SharedCollectionId = Guid.Parse("dddddddd-eeee-ffff-0000-111111111111");

    private static readonly Guid PoliciesCollectionId = Guid.Parse("eeeeeeee-ffff-0000-1111-222222222222");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ah-reattach-" + Guid.NewGuid().ToString("N"));

    public WorkspaceReattachAdapterTests() => AgentScaffolder.Scaffold(_directory, "cli-copilot", "Contoso Helpdesk", "con_contosohelpdesk", "Be helpful.");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static WorkspaceReattachAdapter Create(IWorkspaceSynchronizer synchronizer, RecordingRetargetService retargetService, RecordingAgentCreationClient? dataverseClient = null, TimeProvider? timeProvider = null) =>
        new(
            new WorkspaceAttachService(synchronizer, retargetService, retargetService, new StubOperationContextProvider(), new WorkspaceFileAccessorFactory(), timeProvider ?? TimeProvider.System),
            dataverseClient ?? new RecordingAgentCreationClient(),
            new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider()));

    private static RecordingWorkspaceSynchronizer Unconnected() => new() { SyncInfoAvailable = false };

    private CopilotAttachCommand Command(string tenantId = "00000000-0000-0000-0000-000000000000") => new()
    {
        Environment = new CopilotEnvironmentContext
        {
            EnvironmentId = "env-1",
            TenantId = tenantId,
            DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
        },
        ProjectDirectory = _directory,
        DisplayName = "Contoso Helpdesk",
        SchemaName = "con_contosohelpdesk",
    };

    private void PlantCloudCache()
    {
        Directory.CreateDirectory(Path.Combine(_directory, ".mcs"));
        File.WriteAllText(Path.Combine(_directory, ".mcs", "botdefinition.json"), "{}");
    }

    [Fact]
    public async Task Attach_CreatesTheAgentWhenNoneCarriesTheSchemaName()
    {
        var dataverseClient = new RecordingAgentCreationClient();

        var outcome = await Create(Unconnected(), new RecordingRetargetService(), dataverseClient).AttachAsync(Command(), CancellationToken.None);

        Assert.Equal(1, dataverseClient.CreateCalls);
        Assert.Equal(CreatedAgentId.ToString(), outcome.AgentId);
        Assert.Equal("con_contosohelpdesk", outcome.SchemaName);
        Assert.True(outcome.AgentCreated);
        Assert.False(outcome.Retargeted);
        Assert.Empty(outcome.Warnings);
        Assert.Empty(outcome.Notices);
    }

    [Fact]
    public async Task Attach_OfSettingsEditedToANameLongerThanAnAgentCanTake_IsASettingsFileErrorWithSyncsReason_BeforeTheAgentIsCreated()
    {
        CopilotTestFixtures.DeclareDisplayName(_directory, CopilotTestFixtures.NameOfFortyThreeCharacters);
        var dataverseClient = new RecordingAgentCreationClient();
        await using var services = CopilotTestFixtures.CopilotStudioServices();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(services.GetRequiredService<IWorkspaceSynchronizer>(), new RecordingRetargetService(), dataverseClient).AttachAsync(Command() with { DisplayName = new SyncAgentWorkspaceIdentityReader().Read(_directory)!.DisplayName }, CancellationToken.None));

        Assert.Equal((ErrorCodes.Workspace.CopilotWorkspaceFilesUnreadable, ExitCodes.Usage, CopilotErrorKinds.WorkspaceFilesUnreadable), (failure.ErrorCode, failure.ExitCode, failure.ErrorKind));
        Assert.Equal($"{WorkspaceFileNames.Settings}: {CopilotTestFixtures.AgentNameTooLongReason}", failure.Message);
        Assert.Equal(AgentScaffoldFailure.DisplayNameTooLong, Assert.IsType<AgentScaffoldException>(failure.InnerException).Failure);
        Assert.Equal(["SetDataverseUrl", nameof(RecordingAgentCreationClient.GetAgentIdBySchemaNameAsync)], dataverseClient.Calls);
    }

    [Fact]
    public async Task Attach_WhenTheServiceRenamesTheSchema_ReportsTheNameItAssignedAndReRootsTheFolderWithoutRewritingItFromTheAgent()
    {
        var retargetService = new RecordingRetargetService();

        var outcome = await Create(Unconnected(), retargetService, new RecordingAgentCreationClient { SchemaNameReturns = "con_contosohelpdesk1" }).AttachAsync(Command(), CancellationToken.None);

        Assert.False(retargetService.LastUpdateWorkspaceDirectory);
        Assert.Equal("con_contosohelpdesk1", outcome.SchemaName);
        Assert.Equal(("con_contosohelpdesk1", "Contoso Helpdesk"), AgentWorkspaceIdentity.TryRead(WorkflowWorkspace.OpenWorkspace(_directory)) is { } identity ? (identity.SchemaName, identity.DisplayName) : default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Attach_WithoutAnExpectedAgent_AdoptsTheOneCarryingTheSchemaNameAndSaysSo(string? expectedAgentId)
    {
        var dataverseClient = new RecordingAgentCreationClient { ExistingAgentId = ExistingAgentId };

        var outcome = await Create(Unconnected(), new RecordingRetargetService(), dataverseClient).AttachAsync(Command() with { ExpectedAgentId = expectedAgentId }, CancellationToken.None);

        Assert.Equal(0, dataverseClient.CreateCalls);
        Assert.Equal(ExistingAgentId.ToString(), outcome.AgentId);
        Assert.False(outcome.AgentCreated);
        Assert.Equal(["An agent already carried the schema name con_contosohelpdesk in env-1; it was adopted rather than duplicated."], outcome.Notices);
    }

    [Fact]
    public async Task Attach_AdoptsTheExpectedAgentWithoutANotice()
    {
        var outcome = await Create(Unconnected(), new RecordingRetargetService(), new RecordingAgentCreationClient { ExistingAgentId = ExistingAgentId }).AttachAsync(Command() with { ExpectedAgentId = ExistingAgentId.ToString() }, CancellationToken.None);

        Assert.Equal(ExistingAgentId.ToString(), outcome.AgentId);
        Assert.False(outcome.AgentCreated);
        Assert.Empty(outcome.Notices);
    }

    [Fact]
    public async Task Attach_WhileCopilotStudioIsStillProvisioningTheAgent_SaysToRunItAgainInsteadOfThatTheLinkFailed()
    {
        var retargetService = new RecordingRetargetService();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Unconnected(), retargetService, new RecordingAgentCreationClient { ExistingAgentId = ExistingAgentId, ProvisioningStatus = "Provisioning" }, new ImmediateTimeProvider()).AttachAsync(Command() with { ExpectedAgentId = ExistingAgentId.ToString() }, CancellationToken.None));

        Assert.Equal((ErrorCodes.CopilotStudio.AgentNotProvisioned, ExitCodes.Failure, CopilotErrorKinds.AgentNotProvisioned), (failure.ErrorCode, failure.ExitCode, failure.ErrorKind));
        Assert.Equal($"Copilot Studio was still provisioning the classic bot '{ExistingAgentId}' after 00:05:00, so nothing was pushed into it. The bot is kept, and a later deploy pushes into the same bot once Copilot Studio finishes provisioning it.", failure.Message);
        Assert.Equal(WorkspaceAttachFailure.ReattachFailed, Assert.IsType<WorkspaceAttachException>(failure.InnerException).Failure);
        Assert.Empty(retargetService.Calls);
    }

    [Theory]
    [InlineData("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("not-an-agent-id")]
    public async Task Attach_WhenAnotherAgentCarriesTheSchemaName_RefusesItAndLeavesTheWorkspaceAlone(string expectedAgentId)
    {
        var dataverseClient = new RecordingAgentCreationClient { ExistingAgentId = ExistingAgentId };
        PlantCloudCache();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Unconnected(), new RecordingRetargetService(), dataverseClient).AttachAsync(Command() with { ExpectedAgentId = expectedAgentId }, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.BootstrapReattachFailed, failure.ErrorKind);
        Assert.Contains(ExistingAgentId.ToString(), failure.Message, StringComparison.Ordinal);
        Assert.Equal(CopilotErrorKinds.AgentAlreadyExists, Assert.IsType<CopilotStudioException>(failure.InnerException).ErrorKind);
        Assert.Equal(0, dataverseClient.CreateCalls);
        Assert.Single(dataverseClient.Calls, call => call == "GetAgentIdBySchemaNameAsync");
        Assert.True(File.Exists(Path.Combine(_directory, ".mcs", "botdefinition.json")));
    }

    [Fact]
    public async Task Attach_WhenTheWorkspaceIsAlreadyConnected_RefusesUnlessRetargetIsAllowed()
    {
        var dataverseClient = new RecordingAgentCreationClient();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(new RecordingWorkspaceSynchronizer(), new RecordingRetargetService(), dataverseClient).AttachAsync(Command(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceAlreadyConnected, failure.ErrorKind);
        Assert.Equal($"The workspace at '{_directory}' is already connected to a cloud agent.", failure.Message);
        Assert.Equal(["SetDataverseUrl"], dataverseClient.Calls);
    }

    [Fact]
    public async Task Attach_RetargetingAConnectedWorkspace_KeepsABackupOfItsBindingUntilItIsRebound()
    {
        var retargetService = new RecordingRetargetService();

        Assert.True((await Create(new RecordingWorkspaceSynchronizer(), retargetService).AttachAsync(Command() with { AllowRetarget = true }, CancellationToken.None)).Retargeted);
        Assert.Equal(1, retargetService.PersistedBackups);
        Assert.Equal(1, retargetService.FinalizedRetargets);
    }

    [Fact]
    public async Task Attach_WhenARetargetFailsBeforeTheBindingIsReset_KeepsTheExistingCloudCache()
    {
        var retargetService = new RecordingRetargetService();
        var dataverseClient = new RecordingAgentCreationClient { SolutionVersionsThrows = new InvalidOperationException("service unavailable") };
        PlantCloudCache();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(new RecordingWorkspaceSynchronizer(), retargetService, dataverseClient).AttachAsync(Command() with { AllowRetarget = true }, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.BootstrapReattachFailed, failure.ErrorKind);
        Assert.True(File.Exists(Path.Combine(_directory, ".mcs", "botdefinition.json")));
        Assert.Equal(0, retargetService.PersistedBackups);
    }

    [Fact]
    public async Task Attach_WhenTheProjectCannotBeReattached_RefusesBeforeLookingForTheAgent()
    {
        var project = Directory.CreateDirectory(Path.Combine(_directory, "unrecognized")).FullName;
        var dataverseClient = new RecordingAgentCreationClient();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Unconnected(), new RecordingRetargetService(), dataverseClient).AttachAsync(Command() with { ProjectDirectory = project }, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.ReattachNotSupported, failure.ErrorKind);
        Assert.StartsWith($"The project at '{project}' cannot be connected to a cloud agent: ", failure.Message, StringComparison.Ordinal);
        Assert.Equal(["SetDataverseUrl"], dataverseClient.Calls);
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

    [Fact]
    public async Task Attach_TargetsTheDataverseOrganizationBeforeTheFirstAwait()
    {
        var dataverseClient = new RecordingAgentCreationClient();

        await Create(Unconnected(), new RecordingRetargetService(), dataverseClient).AttachAsync(Command(), CancellationToken.None);

        Assert.Equal(["SetDataverseUrl", "GetAgentIdBySchemaNameAsync", "CreateNewAgentAsync", "GetSolutionVersionsAsync"], dataverseClient.Calls);
    }

    [Fact]
    public async Task Attach_BindsTheWorkspaceToTheEnvironmentAndTenantItTargets()
    {
        var synchronizer = Unconnected();

        await Create(synchronizer, new RecordingRetargetService()).AttachAsync(Command("12345678-90ab-cdef-1234-567890abcdef"), CancellationToken.None);

        var syncInfo = Assert.Single(synchronizer.SavedSyncInfo).SyncInfo;
        Assert.Equal(CreatedAgentId, syncInfo.AgentId);
        Assert.Equal("env-1", syncInfo.EnvironmentId);
        Assert.Equal(new Uri("https://contoso.crm.dynamics.com/"), syncInfo.DataverseEndpoint);
        Assert.Equal(Guid.Parse("12345678-90ab-cdef-1234-567890abcdef"), syncInfo.AccountInfo!.TenantId);
    }

    [Fact]
    public async Task Attach_WithATenantThatIsNotAnIdentifier_RecordsNoTenant()
    {
        var synchronizer = Unconnected();

        await Create(synchronizer, new RecordingRetargetService()).AttachAsync(Command("contoso.onmicrosoft.com"), CancellationToken.None);

        Assert.Equal(Guid.Empty, Assert.Single(synchronizer.SavedSyncInfo).SyncInfo.AccountInfo!.TenantId);
    }

    [Fact]
    public async Task Attach_WhenTheBaselineFails_ReportsTheAgentThatNowExists()
    {
        var retargetService = new RecordingRetargetService { Throws = new InvalidOperationException("service unavailable") };

        var error = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Unconnected(), retargetService).AttachAsync(Command(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.BootstrapReattachFailed, error.ErrorKind);
        Assert.Contains(CreatedAgentId.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("service unavailable", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
    }

    [Fact]
    public async Task Attach_WhenTheDeclaredConnectionReferencesConflict_NamesBothFilesInTheProject()
    {
        var classic = Path.Combine(_directory, "connectionreferences.mcs.yml");
        File.WriteAllText(classic, "connectionReferences:\n  - connectionReferenceLogicalName: cr_teams\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_teams\n");
        var connections = Directory.CreateDirectory(Path.Combine(_directory, "infrastructure", "connections")).FullName;
        File.WriteAllText(Path.Combine(connections, "cr_teams.sync.yaml"), "connectionReferences:\n  - connectionReferenceLogicalName: cr_teams\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_office365\n");

        var error = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Unconnected(), new RecordingRetargetService()).AttachAsync(Command(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.BootstrapReattachFailed, error.ErrorKind);
        var cause = Assert.IsType<CopilotStudioException>(error.InnerException);
        Assert.Equal(CopilotErrorKinds.ConnectionReferencesConflict, cause.ErrorKind);
        Assert.Equal($"Connection reference 'cr_teams' is declared for two connectors: 'shared_teams' at '{classic}' and 'shared_office365' at '{Path.Combine(connections, "cr_teams.sync.yaml")}'.", cause.Message);
    }

    [Theory]
    [InlineData("this is not a valid connection", "Workflow 'Escalate' was not activated: this is not a valid connection")]
    [InlineData("Failed to update workflow: Dataverse request failed (400): {\"error\":{\"code\":\"0x80060888\",\"message\":\"The flow definition is invalid.\"},\"trace\":\"sample.person@contoso.com\"}", "Workflow 'Escalate' was not activated: Failed to update workflow: HTTP 400 Bad Request. The flow definition is invalid. (0x80060888)")]
    public async Task Attach_WhenAWorkflowIsRejected_StillAttachesAndWarns(string errorMessage, string warning)
    {
        var synchronizer = Unconnected();
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", ErrorMessage = errorMessage });

        var outcome = await Create(synchronizer, new RecordingRetargetService()).AttachAsync(Command(), CancellationToken.None);

        Assert.Equal([warning], outcome.Warnings);
    }

    [Fact]
    public async Task Attach_WhenAWorkflowIsDraftedPendingItsConnection_Warns()
    {
        var synchronizer = Unconnected();
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", IsDisabled = true });

        var outcome = await Create(synchronizer, new RecordingRetargetService()).AttachAsync(Command(), CancellationToken.None);

        Assert.Contains(outcome.Warnings, warning => warning.Contains("Escalate", StringComparison.Ordinal) && warning.Contains("disabled", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("invalid model", "AI prompt 'Summarize' was not saved: invalid model")]
    [InlineData("Dataverse request failed (400): {\"error\":{\"code\":\"0x80048d19\",\"message\":\"The prompt model is unavailable.\"},\"trace\":\"sample.person@contoso.com\"}", "AI prompt 'Summarize' was not saved: HTTP 400 Bad Request. The prompt model is unavailable. (0x80048d19)")]
    public async Task Attach_WhenAPromptIsRejected_StillAttachesAndWarns(string errorMessage, string warning)
    {
        var synchronizer = Unconnected();
        synchronizer.PromptResponses.Add(new SyncDataverseClient.AIPromptResponse { PromptName = "Summarize", ErrorMessage = errorMessage });

        Assert.Equal([warning], (await Create(synchronizer, new RecordingRetargetService()).AttachAsync(Command(), CancellationToken.None)).Warnings);
    }

    [Fact]
    public async Task Attach_WhenAConnectorIsNotSaved_WarnsWithItsReasonBeforeTheOtherRefusals()
    {
        const string reason = "Failed to push custom connector 'Weather': denied";
        var synchronizer = Unconnected();
        synchronizer.ConnectorFailureReasons.Add(reason);
        synchronizer.PromptResponses.Add(new SyncDataverseClient.AIPromptResponse { PromptName = "Summarize", ErrorMessage = "invalid model" });

        var outcome = await Create(synchronizer, new RecordingRetargetService()).AttachAsync(Command(), CancellationToken.None);

        Assert.Equal([reason, "AI prompt 'Summarize' was not saved: invalid model"], outcome.Warnings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Attach_WhenAWorkflowAndAPromptReportNoError_WarnsOfNeither(string? errorMessage)
    {
        var synchronizer = Unconnected();
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", ErrorMessage = errorMessage! });
        synchronizer.PromptResponses.Add(new SyncDataverseClient.AIPromptResponse { PromptName = "Summarize", ErrorMessage = errorMessage });

        var outcome = await Create(synchronizer, new RecordingRetargetService()).AttachAsync(Command(), CancellationToken.None);

        Assert.Empty(outcome.Warnings);
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
            SchemaProbeReturns = ExistingAgentId,
        };

        var error = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Unconnected(), new RecordingRetargetService(), client).AttachAsync(Command(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.BootstrapReattachFailed, error.ErrorKind);
        Assert.Contains(ExistingAgentId.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nothing was left behind", error.Remediation ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attach_WhenCreationFailsAndNothingExists_SaysNothingWasLeftBehind()
    {
        var client = new RecordingAgentCreationClient { CreateThrows = new InvalidOperationException("quota exceeded") };

        var error = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Unconnected(), new RecordingRetargetService(), client).AttachAsync(Command(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.BootstrapReattachFailed, error.ErrorKind);
        Assert.Equal("Agent 'con_contosohelpdesk' could not be created or adopted in env-1.", error.Message);
        Assert.Contains("Nothing was left behind", error.Remediation ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("quota exceeded", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
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
        var client = new RecordingAgentCreationClient { SolutionVersionsThrows = new InvalidOperationException("service unavailable") };

        var error = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Unconnected(), new RecordingRetargetService(), client).AttachAsync(Command(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.BootstrapReattachFailed, error.ErrorKind);
        Assert.Contains(CreatedAgentId.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Connect_FindsTheAgentBySchemaNameAndConnectsTheWorkspaceAndEachCollectionKeyedByItsSchemaName()
    {
        var collection = CollectionFolder("HR Shared", "schemaName: con_hrshared\n");
        var synchronizer = new RecordingWorkspaceSynchronizer { SyncInfoAvailable = false };
        var retargetService = new RecordingRetargetService();
        var dataverseClient = new RecordingAgentCreationClient { ExistingAgentId = ExistingAgentId, Collections = { ["con_hrshared"] = new ComponentCollectionInfo { Id = SharedCollectionId, DisplayName = "HR Shared" } } };

        var outcome = await Create(synchronizer, retargetService, dataverseClient).ConnectAsync(ConnectCommand(collection), CancellationToken.None);

        Assert.Equal(ExistingAgentId.ToString(), outcome.AgentId);
        Assert.Equal(["con_contosohelpdesk"], dataverseClient.SchemaNameLookups);
        Assert.Empty(outcome.SkippedCollectionDirectories);
        Assert.Equal([["con_hrshared"]], dataverseClient.CollectionLookups);
        Assert.Equal([Folder(_directory), Folder(collection)], retargetService.CloudCacheWrites.Select(folder => folder.ToString()));
        Assert.Equal([(Folder(_directory), (Guid?)null), (Folder(collection), SharedCollectionId)], synchronizer.SavedSyncInfo.Select(saved => (saved.Folder, saved.SyncInfo.ComponentCollectionId)));
        Assert.All(synchronizer.SavedSyncInfo, saved => Assert.Equal((Guid.Parse(TenantId), "env-1", new Uri("https://contoso.crm.dynamics.com/")), (saved.SyncInfo.AccountInfo!.TenantId, saved.SyncInfo.EnvironmentId, saved.SyncInfo.DataverseEndpoint)));
        Assert.Equal(0, dataverseClient.CreateCalls);
    }

    [Fact]
    public async Task Connect_ReturnsTheFolderOfEachCollectionTheEnvironmentDoesNotHold()
    {
        var held = CollectionFolder("HR Shared", "schemaName: con_hrshared\n");
        var missing = CollectionFolder("Finance Shared", "schemaName: con_financeshared\n");
        var synchronizer = new RecordingWorkspaceSynchronizer { SyncInfoAvailable = false };
        var retargetService = new RecordingRetargetService();
        var dataverseClient = new RecordingAgentCreationClient { ExistingAgentId = ExistingAgentId, Collections = { ["con_hrshared"] = new ComponentCollectionInfo { Id = SharedCollectionId, DisplayName = "HR Shared" } } };

        var outcome = await Create(synchronizer, retargetService, dataverseClient).ConnectAsync(ConnectCommand(missing, held), CancellationToken.None);

        Assert.Equal([missing], outcome.SkippedCollectionDirectories);
        Assert.Equal([Folder(_directory), Folder(held)], retargetService.CloudCacheWrites.Select(folder => folder.ToString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connect_WhenNoAgentCarriesTheSchemaName_ReportsItAsNotFound(bool merge)
    {
        var retargetService = new RecordingRetargetService();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => ConnectAsync(Create(Unconnected(), retargetService), ConnectCommand(), merge));

        Assert.Equal((ErrorCodes.CopilotStudio.AgentNotFound, "No agent matching 'con_contosohelpdesk' was found in environment env-1."), (failure.ErrorCode, failure.Message));
        Assert.Empty(retargetService.CloudCacheWrites);
    }

    [Fact]
    public async Task Connect_WhenTheEnvironmentCannotBeRead_PassesTheFailureOnUnchanged()
    {
        var dataverseClient = new RecordingAgentCreationClient { ExistingAgentId = ExistingAgentId, SolutionVersionsThrows = new InvalidOperationException("service unavailable") };

        Assert.Same(dataverseClient.SolutionVersionsThrows, await Assert.ThrowsAsync<InvalidOperationException>(() => Create(Unconnected(), new RecordingRetargetService(), dataverseClient).ConnectAsync(ConnectCommand(), CancellationToken.None)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connect_WhenTheFolderIsConnectedToAnotherAgent_RefusesNamingItAndKeepsItsCache(bool merge)
    {
        PlantCloudCache();
        var retargetService = new RecordingRetargetService();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => ConnectAsync(Create(new RecordingWorkspaceSynchronizer(), retargetService, new RecordingAgentCreationClient { ExistingAgentId = ExistingAgentId }), ConnectCommand(), merge));

        Assert.Equal((ErrorCodes.Workspace.CopilotWorkspaceAlreadyConnected, CopilotErrorKinds.WorkspaceAlreadyConnected), (failure.ErrorCode, failure.ErrorKind));
        Assert.Equal(Assert.IsType<WorkspaceAttachException>(failure.InnerException).Message, failure.Message);
        Assert.Contains($"'{Folder(_directory)}'", failure.Message, StringComparison.Ordinal);
        Assert.Empty(retargetService.CloudCacheWrites);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_directory, ".mcs", "botdefinition.json")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("displayName: HR Shared\n")]
    [InlineData("kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n")]
    public async Task Connect_WhenACollectionFolderNamesNoSchemaName_RefusesBeforeTargetingDataverse(string? collectionFile)
    {
        var collection = CollectionFolder("HR Shared", collectionFile);
        var dataverseClient = new RecordingAgentCreationClient();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(new RecordingWorkspaceSynchronizer(), new RecordingRetargetService(), dataverseClient).ConnectAsync(ConnectCommand(collection), CancellationToken.None));

        Assert.Equal((ErrorCodes.Workspace.CopilotWorkspaceFilesUnreadable, $"The collection folder '{collection}' holds no collection.mcs.yml that names a schemaName."), (failure.ErrorCode, failure.Message));
        Assert.Empty(dataverseClient.Calls);
    }

    [Fact]
    public async Task Connect_WhenTwoCollectionFoldersDeclareOneSchemaNameIgnoringCase_RefusesBeforeTargetingDataverse()
    {
        var first = CollectionFolder("HR Shared", "schemaName: con_hrshared\n");
        var second = CollectionFolder("HR Shared Copy", "schemaName: CON_HRSHARED\n");
        var dataverseClient = new RecordingAgentCreationClient();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(new RecordingWorkspaceSynchronizer(), new RecordingRetargetService(), dataverseClient).ConnectAsync(ConnectCommand(first, second), CancellationToken.None));

        Assert.Equal((ErrorCodes.Workspace.CopilotWorkspaceFilesUnreadable, $"The collection folders '{first}' and '{second}' both declare the schema name 'CON_HRSHARED'."), (failure.ErrorCode, failure.Message));
        Assert.Empty(dataverseClient.Calls);
    }

    [Fact]
    public async Task Connect_WhenSeveralCollectionFoldersAreRefused_NamesTheFirstRefusalInTheOrderTheFoldersAreRead()
    {
        var first = CollectionFolder("HR Shared", "schemaName: con_hrshared\n");
        var second = CollectionFolder("HR Shared Copy", "schemaName: con_hrshared\n");
        var third = CollectionFolder("Finance Shared");

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(new RecordingWorkspaceSynchronizer(), new RecordingRetargetService()).ConnectAsync(ConnectCommand(first, second, third), CancellationToken.None));

        Assert.Equal($"The collection folders '{first}' and '{second}' both declare the schema name 'con_hrshared'.", failure.Message);
    }

    public static TheoryData<string, string> FilesBesideTheCollectionFile => new()
    {
        { Path.Combine(".mcs", "botdefinition.json"), CopilotTestFixtures.CloudCacheJson(CollectionDefinition("con_cached")) },
        { Path.Combine(".mcs", "botdefinition.json"), "{not json" },
        { Path.Combine("topics", "conflict.mcs.yml"), "kind: AdaptiveDialog\n<<<<<<< HEAD\nbeginDialog: ours\n=======\nbeginDialog: theirs\n>>>>>>> theirs\n" },
    };

    [Theory]
    [MemberData(nameof(FilesBesideTheCollectionFile))]
    public async Task Connect_KeysEachCollectionByItsCollectionFileAlone_WhateverElseItsFolderHolds(string file, string content)
    {
        var collection = CollectionFolder("HR Shared", "schemaName: con_hrshared\n");
        var path = Path.Combine(collection, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        var dataverseClient = new RecordingAgentCreationClient { ExistingAgentId = ExistingAgentId, Collections = { ["con_hrshared"] = new ComponentCollectionInfo { Id = SharedCollectionId, DisplayName = "HR Shared" } } };
        var synchronizer = new RecordingWorkspaceSynchronizer { SyncInfoAvailable = false };
        var retargetService = new RecordingRetargetService();

        var outcome = await Create(synchronizer, retargetService, dataverseClient).ConnectAsync(ConnectCommand(collection), CancellationToken.None);

        Assert.Equal(ExistingAgentId.ToString(), outcome.AgentId);
        Assert.Empty(outcome.SkippedCollectionDirectories);
        Assert.Equal([["con_hrshared"]], dataverseClient.CollectionLookups);
        Assert.Equal([Folder(_directory), Folder(collection)], retargetService.CloudCacheWrites.Select(folder => folder.ToString()));
        Assert.Equal([(Folder(_directory), (Guid?)null), (Folder(collection), SharedCollectionId)], synchronizer.SavedSyncInfo.Select(saved => (saved.Folder, saved.SyncInfo.ComponentCollectionId)));
    }

    [Fact]
    public async Task ConnectAndMerge_MapsTheFoldersAndBaseToSync_AndEachOutcomeBack_SkippingAnUnlinkedCollection_AndAddingALinkedOneBesideWhatTheProjectHolds()
    {
        var project = Directory.CreateDirectory(Path.Combine(_directory, "repo")).FullName;
        var agent = ProjectFolder(project, "Contoso Helpdesk", new Dictionary<string, string> { ["updated.mcs.yml"] = "kind: A\n", ["deleted.mcs.yml"] = "kind: D\n", ["merged.mcs.yml"] = "ONE\ntwo\nthree\n", ["conflicted.mcs.yml"] = "local\n", ["replaced.bin"] = "\0local" });
        var held = ProjectFolder(project, "HR Shared", new Dictionary<string, string> { [WorkspaceFileNames.Collection] = "schemaName: con_hrshared\n" });
        var unlinked = ProjectFolder(project, "Finance Shared", new Dictionary<string, string> { [WorkspaceFileNames.Collection] = "schemaName: con_financeshared\n" });
        File.WriteAllText(Path.Combine(project, "Policies"), "taken");
        var dataverseClient = new RecordingAgentCreationClient { ExistingAgentId = ExistingAgentId, LinkedCollections = { new ComponentCollectionInfo { Id = SharedCollectionId, SchemaName = "con_hrshared", DisplayName = "HR Shared" }, new ComponentCollectionInfo { Id = PoliciesCollectionId, SchemaName = "con_policies", DisplayName = "Policies" } } };
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            SyncInfoAvailable = false,
            ClonedFiles = new Dictionary<string, string> { ["updated.mcs.yml"] = "kind: B\n", ["added.mcs.yml"] = "kind: N\n", ["merged.mcs.yml"] = "one\ntwo\nTHREE\n", ["conflicted.mcs.yml"] = "remote\n", ["replaced.bin"] = "\0remote" },
            ClonedCollectionFiles = new Dictionary<string, string> { [WorkspaceFileNames.Collection] = "schemaName: con_hrshared\n" },
        };
        var baseFiles = new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["Contoso Helpdesk/updated.mcs.yml"] = Encoding.UTF8.GetBytes("kind: A\n"),
            ["Contoso Helpdesk/deleted.mcs.yml"] = Encoding.UTF8.GetBytes("kind: D\n"),
            ["Contoso Helpdesk/merged.mcs.yml"] = Encoding.UTF8.GetBytes("one\ntwo\nthree\n"),
            ["Contoso Helpdesk/conflicted.mcs.yml"] = Encoding.UTF8.GetBytes("base\n"),
            ["Contoso Helpdesk/replaced.bin"] = Encoding.UTF8.GetBytes("\0base"),
        };

        var merged = await Create(synchronizer, new RecordingRetargetService(), dataverseClient).ConnectAndMergeAsync(new CopilotConnectMergeCommand(ConnectCommand(held, unlinked) with { WorkspaceDirectory = agent }, baseFiles), CancellationToken.None);

        Assert.Equal(ExistingAgentId.ToString(), merged.Connected.AgentId);
        Assert.Equal([unlinked], merged.Connected.SkippedCollectionDirectories);
        Assert.Equal([Path.Combine(project, "Policies-2")], merged.AddedCollectionDirectories);
        Assert.Equal(["Contoso Helpdesk/merged.mcs.yml"], merged.MergedPaths);
        Assert.Equal(["Contoso Helpdesk/added.mcs.yml", "Policies-2/collection.mcs.yml"], merged.AddedPaths);
        Assert.Equal(["Contoso Helpdesk/updated.mcs.yml"], merged.UpdatedPaths);
        Assert.Equal(["Contoso Helpdesk/deleted.mcs.yml"], merged.DeletedPaths);
        Assert.Equal(["Contoso Helpdesk/conflicted.mcs.yml"], merged.ConflictedPaths);
        Assert.Equal(["Contoso Helpdesk/replaced.bin"], merged.ReplacedPaths);
        Assert.Equal("ONE\ntwo\nTHREE\n", File.ReadAllText(Path.Combine(agent, "merged.mcs.yml")));
        Assert.Equal("taken", File.ReadAllText(Path.Combine(project, "Policies")));
        Assert.Equal(["con_contosohelpdesk"], dataverseClient.SchemaNameLookups);
    }

    private static Task<CopilotConnectOutcome> ConnectAsync(WorkspaceReattachAdapter adapter, CopilotConnectCommand command, bool merge) => merge ? MergedAsync(adapter, command) : adapter.ConnectAsync(command, CancellationToken.None);

    private static async Task<CopilotConnectOutcome> MergedAsync(WorkspaceReattachAdapter adapter, CopilotConnectCommand command) => (await adapter.ConnectAndMergeAsync(new CopilotConnectMergeCommand(command, new Dictionary<string, ReadOnlyMemory<byte>>()), CancellationToken.None)).Connected;

    private static string ProjectFolder(string project, string name, Dictionary<string, string> files)
    {
        var folder = Directory.CreateDirectory(Path.Combine(project, name)).FullName;
        foreach (var (path, content) in files)
        {
            File.WriteAllText(Path.Combine(folder, path), content);
        }

        return folder;
    }

    private CopilotConnectCommand ConnectCommand(params string[] collectionDirectories) => new()
    {
        Environment = Command(TenantId).Environment,
        WorkspaceDirectory = _directory,
        CollectionDirectories = collectionDirectories,
        SchemaName = "con_contosohelpdesk",
    };

    private string CollectionFolder(string name, string? collectionFile = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_directory, name)).FullName;
        if (collectionFile is not null)
        {
            File.WriteAllText(Path.Combine(folder, WorkspaceFileNames.Collection), collectionFile);
        }

        return folder;
    }

    private static string Folder(string directory) => new DirectoryPath(directory.Replace('\\', '/')).ToString();

    private static BotComponentCollectionDefinition CollectionDefinition(string schemaName) => new BotComponentCollectionDefinition().WithComponentCollection(CodeSerializer.Deserialize<BotComponentCollection>($"schemaName: {schemaName}\ndisplayName: HR Shared")!);
}

internal sealed class RecordingAgentCreationClient : RecordingConnectionReferenceClient, ISyncDataverseClient
{
    public List<string> Calls { get; } = [];

    public int CreateCalls { get; private set; }

    public Guid ExistingAgentId { get; init; }

    public string? SchemaNameReturns { get; init; }

    public Exception? SolutionVersionsThrows { get; init; }

    public Exception? CreateThrows { get; init; }

    public Guid SchemaProbeReturns { get; init; }

    public Exception? SchemaProbeThrows { get; init; }

    public Dictionary<string, ComponentCollectionInfo> Collections { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ComponentCollectionInfo> LinkedCollections { get; } = [];

    public List<string[]> CollectionLookups { get; } = [];

    public List<string> SchemaNameLookups { get; } = [];

    public override Task<AgentInfo> GetAgentInfoAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult(new AgentInfo { ComponentCollections = [.. LinkedCollections] });

    public override Task<IReadOnlyDictionary<string, ComponentCollectionInfo>> GetComponentCollectionsBySchemaNamesAsync(IEnumerable<string> schemaNames, CancellationToken cancellationToken)
    {
        CollectionLookups.Add([.. schemaNames]);
        return Task.FromResult<IReadOnlyDictionary<string, ComponentCollectionInfo>>(Collections);
    }

    public override Task<SolutionInfo> GetSolutionVersionsAsync(CancellationToken cancellationToken)
    {
        Calls.Add(nameof(GetSolutionVersionsAsync));
        return SolutionVersionsThrows is not null ? Task.FromException<SolutionInfo>(SolutionVersionsThrows) : Task.FromResult(new SolutionInfo());
    }

    public override Task<Guid> GetAgentIdBySchemaNameAsync(string schemaName, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(GetAgentIdBySchemaNameAsync));
        SchemaNameLookups.Add(schemaName);

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

internal sealed class RecordingRetargetService : IWorkspaceRetargetService, IWorkspaceCloudCacheWriter
{
    public List<string> Calls { get; set; } = [];

    public List<DirectoryPath> CloudCacheWrites { get; } = [];

    public bool LastUpdateWorkspaceDirectory { get; private set; } = true;

    public ImmutableArray<SyncDataverseClient.AIPromptMetadata> LastAIPromptMetadata { get; private set; }

    public int PersistedBackups { get; private set; }

    public int FinalizedRetargets { get; private set; }

    public Exception? Throws { get; init; }

    public RemoteBindingSnapshot ResetRemoteBindingState(DirectoryPath workspaceFolder) => RemoteBindingSnapshot.Empty;

    public void RestoreRemoteBindingState(DirectoryPath workspaceFolder, RemoteBindingSnapshot snapshot)
    {
    }

    public void PersistRetargetBackup(DirectoryPath workspaceFolder, RemoteBindingSnapshot snapshot) => PersistedBackups++;

    public bool FinalizeRetarget(DirectoryPath workspaceFolder, bool pushSucceeded)
    {
        FinalizedRetargets++;
        return true;
    }

    public void ClearRetargetBackup(DirectoryPath workspaceFolder)
    {
    }

    public Task WriteCloudCacheAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, CancellationToken cancellationToken)
    {
        CloudCacheWrites.Add(workspaceFolder);
        return Task.CompletedTask;
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
        LastAIPromptMetadata = aiPromptMetadata;

        if (Throws is not null)
        {
            throw Throws;
        }

        return Task.FromResult(new WorkspaceSyncInfo());
    }
}
