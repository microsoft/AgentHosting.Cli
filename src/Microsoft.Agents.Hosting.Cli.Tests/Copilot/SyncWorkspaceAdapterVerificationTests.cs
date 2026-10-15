using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncWorkspaceAdapterVerificationTests
{
    private static CopilotWorkspaceInfo Workspace() => new()
    {
        WorkspacePath = Path.Combine(Path.GetTempPath(), "ah-verify-tests"),
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
    public async Task Push_ConfirmsWhatTheServiceStored()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.VerifyPushCalls);
    }

    [Fact]
    public async Task Push_ConfirmsAfterTheContentIsSent()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(
            [nameof(IWorkspaceSynchronizer.PushLocalChangesAsync), nameof(IWorkspaceSynchronizer.VerifyPushAsync)],
            synchronizer.Calls.Where(call => call is nameof(IWorkspaceSynchronizer.PushLocalChangesAsync) or nameof(IWorkspaceSynchronizer.VerifyPushAsync)));
    }

    [Fact]
    public async Task Push_WhenEverythingWasStored_WarnsAboutNothing()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public async Task Push_WhenTheServiceDidNotStoreEverything_Warns()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            VerificationReturns = new PushVerificationResult
            {
                IsFullyAccepted = false,
                EntityTypes = [new EntityTypeVerification { ChangeKind = "topic", PushedCount = 3, VerifiedCount = 1 }],
            },
        };

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Contains(outcome.Warnings, warning => warning.Contains("topic", StringComparison.Ordinal) && warning.Contains("2 of 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Push_WhenTheServiceReportsAMismatchTheWorkspaceCannotSee_StillWarns()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            VerificationReturns = new PushVerificationResult
            {
                IsFullyAccepted = false,
                EntityTypes = [new EntityTypeVerification { ChangeKind = "BotEntity", PushedCount = 1, VerifiedCount = 0 }],
            },
        };

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(["The service did not report 1 of 1 pushed BotEntity change(s) as stored."], outcome.Warnings);
    }

    [Fact]
    public async Task Push_WhenTheServiceReportsSeveralMismatches_WarnsAboutEveryKind()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            VerificationReturns = new PushVerificationResult
            {
                IsFullyAccepted = false,
                EntityTypes =
                [
                    new EntityTypeVerification { ChangeKind = "BotEntity", PushedCount = 1, VerifiedCount = 0 },
                    new EntityTypeVerification { ChangeKind = "topic", PushedCount = 2, VerifiedCount = 0 },
                ],
            },
        };

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(
            [
                "The service did not report 1 of 1 pushed BotEntity change(s) as stored.",
                "The service did not report 2 of 2 pushed topic change(s) as stored.",
            ],
            outcome.Warnings);
    }

    [Fact]
    public async Task Push_ConfirmsTheServiceWithoutRereadingTheWorkspace()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            VerificationReturns = new PushVerificationResult
            {
                IsFullyAccepted = false,
                EntityTypes = [new EntityTypeVerification { ChangeKind = "topic", PushedCount = 1, VerifiedCount = 0 }],
            },
        };

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.Calls.Count(call => call == nameof(IWorkspaceSynchronizer.GetLocalChangesAsync)));
    }

    [Fact]
    public async Task Push_WhenConfirmationItselfFails_WarnsRatherThanFailing()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer { VerifyPushThrows = new InvalidOperationException("the service is unavailable") };

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Contains(outcome.Warnings, warning => warning.Contains("could not be confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Push_ReportsTheAuxiliaryResourcesItSent()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate" });
        synchronizer.PromptResponses.Add(new Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient.AIPromptResponse { PromptName = "Summarize" });

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(2, outcome.AuxiliaryChangeCount);
    }

    [Fact]
    public async Task Push_WithNothingAuxiliaryToSend_ReportsNone()
    {
        var outcome = await Create(new RecordingWorkspaceSynchronizer()).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(0, outcome.AuxiliaryChangeCount);
    }

    [Fact]
    public async Task Push_ReportsTheFilesTheUploadSent()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer { UploadedFileNames = ["SKILL.md", "File 1.txt"] };

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(["SKILL.md", "File 1.txt"], outcome.UploadedFileNames);
    }

    [Fact]
    public async Task Push_WithNoFilesUploaded_ReportsNone()
    {
        var outcome = await Create(new RecordingWorkspaceSynchronizer()).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Empty(outcome.UploadedFileNames);
    }
}

public sealed class DataverseMalformedPageTests
{
    private const string Organization = "https://contoso.crm.dynamics.com/";

    private static CopilotEnvironmentContext Environment() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "tenant-1",
        DataverseOrganization = new Uri(Organization),
    };

    private static DataverseCatalogAdapter Create(ScriptedHttpMessageHandler handler)
    {
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler);
        accessor.SetDataverseOrganization(new Uri(Organization));

        return new DataverseCatalogAdapter(new StubSolutionVersionsClient(), accessor);
    }

    [Fact]
    public async Task ASecondPageWithNoValueArrayIsRefusedRatherThanTruncating()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[{"botid":"b1","schemaname":"a_one"}],"@odata.nextLink":"https://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=p2"}""")
            .Respond(HttpStatusCode.OK, """{"unexpected":true}""");

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(handler).ListAgentsAsync(Environment(), null, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.ServiceError, failure.ErrorKind);
    }

    [Fact]
    public async Task ASecondPageWithAnEmptyBodyIsRefusedRatherThanTruncating()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"value":[{"botid":"b1","schemaname":"a_one"}],"@odata.nextLink":"https://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=p2"}""")
            .Respond(HttpStatusCode.OK, string.Empty);

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(handler).ListAgentsAsync(Environment(), null, CancellationToken.None));
    }

    [Fact]
    public async Task AFirstPageThatIsAbsentIsAFailedRead()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.NotFound, string.Empty);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(handler).ListAgentsAsync(Environment(), null, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.ServiceError, failure.ErrorKind);
    }

    [Fact]
    public async Task AWellFormedEmptyPageIsAccepted()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, """{"value":[]}""");

        var agents = await Create(handler).ListAgentsAsync(Environment(), null, CancellationToken.None);

        Assert.Empty(agents);
    }
}
