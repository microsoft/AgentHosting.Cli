using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncWorkspaceAdapterPushOutcomeTests
{
    private static CopilotWorkspaceInfo Workspace() => new()
    {
        WorkspacePath = Path.Combine(Path.GetTempPath(), "ah-push-outcome-tests"),
        AgentId = "11111111-2222-3333-4444-555555555555",
        EnvironmentId = "env-1",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static SyncWorkspaceAdapter Create(RecordingWorkspaceSynchronizer synchronizer) => CopilotTestFixtures.WorkspaceAdapter(synchronizer);

    [Fact]
    public async Task Push_CountsTheWorkflowsItSentButNotThePrompts()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate" });
        synchronizer.PromptResponses.Add(new Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient.AIPromptResponse { PromptName = "Summarize" });

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(1, outcome.AuxiliaryChangeCount);
    }

    [Fact]
    public async Task Push_DoesNotCountAWorkflowTwiceWhenItIsAlsoAReportedChange()
    {
        var workflowId = Guid.NewGuid();
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.LocalChanges.Add(new Change { Name = "Escalate", SchemaName = $"{WorkflowLayout.SchemaNamePrefix}{workflowId}", ChangeKind = "CloudFlowDefinition", ChangeType = ChangeType.Update });
        synchronizer.LocalChanges.Add(new Change { Name = "Escalate", SchemaName = $"{WorkflowLayout.SchemaNamePrefix}{workflowId}.metadata", ChangeKind = "CloudFlowDefinition", ChangeType = ChangeType.Update });
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowId = workflowId, WorkflowName = "Escalate" });

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal($"{WorkflowLayout.SchemaNamePrefix}{workflowId}", Assert.Single(outcome.Changes).SchemaName);
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
