using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class DataverseAgentResourceAdapterTests
{
    private const string ResourceId = "b0b0b0b0-1111-2222-3333-444444444444";

    private static readonly Guid ReferenceRecordId = Guid.Parse("8f14e45f-ceea-467a-9ae9-1b2c3d4e5f60");

    private static CopilotEnvironmentContext Environment() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "11111111-2222-3333-4444-555555555555",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static DataverseAgentResourceAdapter Create(CapturingHttpMessageHandler handler, FakeWorkflowStorePort? workflows = null, SyncDataverseClient? dataverse = null)
    {
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler);
        var client = dataverse ?? new SyncDataverseClient(accessor, "AgentHost.Cli.Tests");

        return new DataverseAgentResourceAdapter(workflows ?? new FakeWorkflowStorePort(), client, new DataverseWebApiClient(client, accessor));
    }

    [Fact]
    public async Task Delete_OfAFlow_DeletesItThroughTheWorkflowStore()
    {
        var handler = new CapturingHttpMessageHandler();
        var workflows = new FakeWorkflowStorePort();
        var workflowId = Guid.Parse(ResourceId);
        workflows.Cloud[workflowId] = new WorkflowDocument { WorkflowId = workflowId, Name = "Weather", State = WorkflowStateProjection.Activated };

        await Create(handler, workflows: workflows).DeleteAsync(Environment(), new CopilotCopyLink(CopilotCopyIdentityKinds.Workflow, ResourceId), CancellationToken.None);

        Assert.Equal(["env-1"], workflows.DeletedIn);
        Assert.Contains($"delete:{workflowId}", workflows.Calls);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(CopilotCopyIdentityKinds.AIModel, "msdyn_aimodels")]
    [InlineData(CopilotCopyIdentityKinds.Connector, "connectors")]
    public async Task Delete_OfAPromptOrACustomConnector_DeletesItsRow(string kind, string entitySet)
    {
        var handler = new CapturingHttpMessageHandler { Status = HttpStatusCode.NoContent };

        await Create(handler).DeleteAsync(Environment(), new CopilotCopyLink(kind, ResourceId), CancellationToken.None);

        var deleted = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, deleted.Method);
        Assert.Equal($"https://contoso.crm.dynamics.com/api/data/v9.2/{entitySet}({ResourceId})", deleted.Uri.ToString());
    }

    [Fact]
    public async Task Delete_OfARowAlreadyGone_Succeeds()
    {
        var handler = new CapturingHttpMessageHandler { Status = HttpStatusCode.NotFound };

        await Create(handler).DeleteAsync(Environment(), new CopilotCopyLink(CopilotCopyIdentityKinds.Connector, ResourceId), CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Delete_OfAConnectionReference_DeletesTheRowItsLogicalNameNames()
    {
        var handler = new CapturingHttpMessageHandler { Status = HttpStatusCode.NoContent };
        var dataverse = new RecordingConnectionReferenceClient();
        dataverse.References["con_weather"] = new SyncDataverseClient.ConnectionReferenceInfo { ConnectionReferenceId = ReferenceRecordId, ConnectionReferenceLogicalName = "con_weather", ConnectorId = "shared_weather" };

        await Create(handler, dataverse: dataverse).DeleteAsync(Environment(), new CopilotCopyLink(CopilotCopyIdentityKinds.ConnectionReference, "con_weather"), CancellationToken.None);

        var deleted = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, deleted.Method);
        Assert.Equal($"/api/data/v9.2/connectionreferences({ReferenceRecordId})", deleted.Uri.AbsolutePath);
    }

    [Theory]
    [InlineData(CopilotCopyIdentityKinds.Schema, "con_agent")]
    [InlineData("agent", ResourceId)]
    [InlineData(CopilotCopyIdentityKinds.AIModel, "not-an-identifier")]
    public async Task Delete_OfAKindAnAgentDoesNotHoldOrARowNotNamedByAnIdentifier_IsRefused(string kind, string identity)
    {
        var handler = new CapturingHttpMessageHandler();

        await Assert.ThrowsAsync<ArgumentException>(() => Create(handler).DeleteAsync(Environment(), new CopilotCopyLink(kind, identity), CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void List_ReadsTheWorkspaceThroughTheSyncLibrary()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        var workflowId = WorkflowFixtures.Write(workspace.Root, "Weather");

        var resources = Create(new CapturingHttpMessageHandler()).List(workspace.Root);

        Assert.Equal([new CopilotCopyLink(CopilotCopyIdentityKinds.Workflow, workflowId.ToString("D"))], resources);
    }

    [Fact]
    public void List_OfAWorkspaceWithAnUnreadableFile_NamesTheFile()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        workspace.AddFile("connectors/broken/metadata.yml", "{ not json");

        var failure = Assert.Throws<CopilotStudioException>(() => Create(new CapturingHttpMessageHandler()).List(workspace.Root));

        Assert.Equal(CopilotErrorKinds.WorkspaceFilesUnreadable, failure.ErrorKind);
        Assert.StartsWith("connectors/broken/metadata.yml could not be read: ", failure.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidDataException>(failure.InnerException);
    }

    [Fact]
    public async Task FindExisting_TargetsTheEnvironmentAndReturnsOnlyWhatItHolds()
    {
        var handler = new CapturingHttpMessageHandler { Body = $$"""{"value":[{"workflowid":"{{ResourceId}}"}]}""" };

        var existing = await Create(handler).FindExistingAsync(Environment(), [new(CopilotCopyIdentityKinds.Workflow, "e0e0e0e0-1111-2222-3333-444444444444"), new(CopilotCopyIdentityKinds.Workflow, ResourceId)], CancellationToken.None);

        var lookup = Assert.Single(handler.Requests);
        Assert.Equal([new CopilotCopyLink(CopilotCopyIdentityKinds.Workflow, ResourceId)], existing);
        Assert.Equal(HttpMethod.Get, lookup.Method);
        Assert.Equal("contoso.crm.dynamics.com", lookup.Uri.Host);
        Assert.Equal("/api/data/v9.2/workflows", lookup.Uri.AbsolutePath);
    }
}
