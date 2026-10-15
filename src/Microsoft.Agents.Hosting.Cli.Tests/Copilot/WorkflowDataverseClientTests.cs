using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

internal sealed class RecordingWorkflowClient : RecordingConnectionReferenceClient
{
    public List<string> Calls { get; } = [];

    public Dictionary<Guid, SyncDataverseClient.WorkflowMetadata> Cloud { get; } = [];

    public List<SyncDataverseClient.WorkflowSummary> Listed { get; } = [];

    public string? InsertError { get; init; }

    public string? UpdateError { get; init; }

    public string? RestoreError { get; init; }

    public Exception? UpdateThrows { get; init; }

    public Exception? InsertThrows { get; init; }

    public List<CancellationToken> RestoreTokens { get; } = [];

    public List<string> ExistenceChecks { get; } = [];

    public override Task<bool> ConnectionReferenceExistsAsync(string connectionReferenceLogicalName, CancellationToken cancellationToken)
    {
        ExistenceChecks.Add(connectionReferenceLogicalName);

        return Task.FromResult(References.ContainsKey(connectionReferenceLogicalName));
    }

    public override Task<SyncDataverseClient.ConnectionReferenceInfo[]> GetConnectionReferencesByLogicalNamesAsync(IEnumerable<string> logicalNames, CancellationToken cancellationToken)
    {
        Calls.Add($"references:{string.Join(",", logicalNames)}");

        return Task.FromResult(logicalNames.Where(References.ContainsKey).Select(logicalName => References[logicalName]).ToArray());
    }

    public override Task<WorkflowResponse> InsertWorkflowAsync(SyncDataverseClient.WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken)
    {
        Calls.Add($"insert:none:{workflowMetadata!.WorkflowId}");
        WrittenStateCodes.Add(workflowMetadata.StateCode);
        WrittenStatusCodes.Add(workflowMetadata.StatusCode);

        var restoring = Deleted.Contains(workflowMetadata.WorkflowId) && _writesAfterDelete++ > 0;

        if (restoring)
        {
            RestoreTokens.Add(cancellationToken);
        }
        else if (InsertThrows is not null)
        {
            throw InsertThrows;
        }

        var error = restoring ? RestoreError ?? string.Empty : InsertError ?? string.Empty;

        if (string.IsNullOrEmpty(error))
        {
            Cloud[workflowMetadata.WorkflowId] = workflowMetadata;
        }

        return Task.FromResult(new WorkflowResponse { WorkflowId = workflowMetadata.WorkflowId, WorkflowName = workflowMetadata.Name ?? workflowMetadata.WorkflowId.ToString(), ErrorMessage = error });
    }

    public List<int?> WrittenStateCodes { get; } = [];

    public List<int?> WrittenStatusCodes { get; } = [];

    public HashSet<Guid> Deleted { get; } = [];

    private int _writesAfterDelete;

    public override Task<WorkflowResponse> UpdateWorkflowDefinitionAsync(SyncDataverseClient.WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken)
    {
        Calls.Add($"update:none:{workflowMetadata!.WorkflowId}");
        WrittenStateCodes.Add(workflowMetadata.StateCode);
        WrittenStatusCodes.Add(workflowMetadata.StatusCode);

        if (UpdateThrows is not null)
        {
            throw UpdateThrows;
        }

        if (string.IsNullOrEmpty(UpdateError))
        {
            Cloud[workflowMetadata.WorkflowId] = workflowMetadata;
        }

        return Task.FromResult(new WorkflowResponse { WorkflowId = workflowMetadata.WorkflowId, WorkflowName = workflowMetadata.Name ?? workflowMetadata.WorkflowId.ToString(), ErrorMessage = UpdateError ?? string.Empty });
    }

    public override Task<SyncDataverseClient.WorkflowMetadata?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        Calls.Add($"get:{workflowId}");

        return Task.FromResult(Cloud.TryGetValue(workflowId, out var metadata) ? metadata : null);
    }

    public override Task<SyncDataverseClient.WorkflowSummary[]> ListWorkflowsAsync(string? nameFilter, int? maximumCount, CancellationToken cancellationToken)
    {
        Calls.Add($"list:{nameFilter ?? "*"}:{maximumCount?.ToString() ?? "*"}");

        return Task.FromResult(Listed.ToArray());
    }

    public override Task SetWorkflowStateAsync(Guid workflowId, bool activate, CancellationToken cancellationToken)
    {
        Calls.Add($"state:{workflowId}:{(activate ? "activate" : "draft")}");

        return ActivateError is null ? Task.CompletedTask : Task.FromException(new DataverseRequestException(System.Net.HttpStatusCode.BadRequest, ActivateError));
    }

    public string? ActivateError { get; init; }

    public override Task<bool> DeleteWorkflowAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        Calls.Add($"delete:{workflowId}");
        Deleted.Add(workflowId);

        return Task.FromResult(Cloud.Remove(workflowId));
    }
}

public sealed class WorkflowDataverseClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ah-wf-adapter-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CopilotEnvironmentContext Environment() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "11111111-2222-3333-4444-555555555555",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static WorkflowDataverseClient Create(RecordingWorkflowClient client)
    {
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider());
        accessor.SetDataverseOrganization(new Uri("https://contoso.crm.dynamics.com/"));

        return new WorkflowDataverseClient(client, new StandaloneWorkflowSynchronizer(client), accessor);
    }

    private static void AssertRunningWorkflowUntouched(RecordingWorkflowClient client, Guid workflowId)
    {
        Assert.DoesNotContain($"state:{workflowId}:draft", client.Calls);
        Assert.DoesNotContain($"state:{workflowId}:activate", client.Calls);
    }

    private Guid WriteWorkflow(string name = "WF Hook 2", string? definition = null, bool activated = true)
    {
        var workflowId = Guid.NewGuid();
        var folder = Path.Combine(WorkflowWorkspace.WorkflowsDirectory(_root), WorkflowWorkspace.FolderNameFor(name, workflowId));
        var metadata = WorkflowScaffolder.NewMetadata(name, workflowId, WorkflowWorkspace.RelativeFolderFor(name, workflowId));

        metadata.StateCode = activated ? 1 : 0;
        metadata.StatusCode = activated ? 2 : 1;

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, WorkflowWorkspace.DefinitionFileName), definition ?? "{\"properties\":{}}");
        File.WriteAllText(Path.Combine(folder, WorkflowWorkspace.MetadataFileName), WorkflowMetadataFile.Write(metadata));

        return workflowId;
    }

    [Fact]
    public async Task Upsert_WhenTheEnvironmentHasNoRow_Creates()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient();

        var result = await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.True(result.Created);
        Assert.Contains($"insert:none:{workflowId}", client.Calls);
        Assert.DoesNotContain(client.Calls, call => call.StartsWith("update:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upsert_WhenTheEnvironmentHasTheRow_Updates()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = "WF Hook 2" };

        var result = await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.False(result.Created);
        Assert.Contains($"update:none:{workflowId}", client.Calls);
        Assert.DoesNotContain(client.Calls, call => call.StartsWith("insert:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upsert_WhenTheServiceRefuses_Fails()
    {
        WriteWorkflow();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() =>
            Create(new RecordingWorkflowClient { InsertError = "Failed to create workflow: denied" }).UpsertAsync(Environment(), _root, CancellationToken.None));

        Assert.Contains("denied", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_WhenOnlyTheDeclarationIsMissing_IsReportedAsAChange()
    {
        var workflowId = WriteWorkflow(activated: false, definition: """
        {"properties":{"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
        """);

        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 0, statusCode: 1);

        await Create(client).DownloadAsync(Environment(), workflowId, _root, CancellationToken.None);
        File.Delete(Path.Combine(_root, "agent.sync.yaml"));

        var second = await Create(client).DownloadAsync(Environment(), workflowId, _root, CancellationToken.None);

        Assert.True(second.Changed);
    }

    [Fact]
    public async Task Download_WhenNothingOnDiskDiffers_IsNotReportedAsAChange()
    {
        var workflowId = WriteWorkflow(activated: false, definition: """
        {"properties":{"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
        """);

        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 0, statusCode: 1);

        await Create(client).DownloadAsync(Environment(), workflowId, _root, CancellationToken.None);

        Assert.False((await Create(client).DownloadAsync(Environment(), workflowId, _root, CancellationToken.None)).Changed);
    }

    [Fact]
    public async Task Upsert_DeclaresEveryConnectionReferenceTheDefinitionBinds()
    {
        WriteWorkflow(definition: """
        {"properties":{"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
        """);

        var client = new RecordingWorkflowClient();

        var result = await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Equal(["new_teams"], client.EnsuredLogicalNames);
        Assert.Equal([WorkflowDefinitionFile.ConnectorIdPrefix + "shared_teams"], client.EnsuredConnectorIds);
        Assert.Equal(["new_teams"], result.CreatedConnectionReferences);
    }

    [Fact]
    public async Task Upsert_WhenAReferenceAlreadyExists_DoesNotDeclareItAgain()
    {
        WriteWorkflow(definition: """
        {"properties":{"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
        """);

        var client = new RecordingWorkflowClient();
        client.References["new_teams"] = new SyncDataverseClient.ConnectionReferenceInfo { ConnectionReferenceLogicalName = "new_teams", ConnectorId = "x" };

        var result = await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Empty(client.EnsuredLogicalNames);
        Assert.Empty(result.CreatedConnectionReferences);
    }

    [Fact]
    public async Task Upsert_WhenDeclaringAReferenceFails_StillPushesTheWorkflow()
    {
        var workflowId = WriteWorkflow(definition: """
        {"properties":{"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
        """);

        var client = new RecordingWorkflowClient { ProvisioningThrows = new InvalidOperationException("refused") };

        var result = await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Empty(result.CreatedConnectionReferences);
        Assert.Contains($"insert:none:{workflowId}", client.Calls);
    }

    [Fact]
    public async Task Upsert_WhenTheMetadataNamesAnotherWorkflow_Fails()
    {
        var workflowId = WriteWorkflow();
        var folder = WorkflowWorkspace.FindAll(_root).Single();
        File.WriteAllText(folder.MetadataPath, File.ReadAllText(folder.MetadataPath).Replace(workflowId.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(new RecordingWorkflowClient()).UpsertAsync(Environment(), _root, CancellationToken.None));

        Assert.Contains("but the folder names", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upsert_WhenTheEnvironmentWillNotAcceptAnUpdate_KeepsTheCloudWorkflow()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient { UpdateError = "Failed to update workflow: ConnectionAuthorizationFailed. Connection 'new_teams' cannot be used to activate this flow" };
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 0, statusCode: 1, clientData: """{"properties":{"definition":{"marker":"original"}}}""");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(client).UpsertAsync(Environment(), _root, CancellationToken.None));

        Assert.Equal("workflow-connections-not-ready", failure.ErrorKind);
        Assert.DoesNotContain(client.Calls, call => call.StartsWith("delete:", StringComparison.Ordinal));
        Assert.DoesNotContain(client.Calls, call => call.StartsWith("insert:", StringComparison.Ordinal));
        Assert.Contains("original", client.Cloud[workflowId].ClientData, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upsert_WhenAnUpdateIsRefused_PutsTheOriginalWorkflowBackAsItWasRunning()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient
        {
            UpdateError = "Failed to update workflow: ConnectionAuthorizationFailed",
            InsertError = "Failed to create workflow: ConnectionAuthorizationFailed. Connection 'new_teams' cannot be used to activate this flow",
        };

        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 1, statusCode: 2, clientData: """{"properties":{"definition":{"marker":"original"}}}""");

        await Assert.ThrowsAsync<ManagedAgentException>(() => Create(client).UpsertAsync(Environment(), _root, CancellationToken.None));

        Assert.Contains("original", client.Cloud[workflowId].ClientData, StringComparison.Ordinal);
        Assert.Equal(1, client.Cloud[workflowId].StateCode);
        Assert.Equal(2, client.Cloud[workflowId].StatusCode);
    }

    [Fact]
    public async Task Upsert_WhenTheUpdateFailsForAnotherReason_LeavesARunningWorkflowUntouched()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient { UpdateError = "Failed to update workflow: something else" };
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 1, statusCode: 2, clientData: """{"properties":{"definition":{}}}""");

        await Assert.ThrowsAsync<ManagedAgentException>(() => Create(client).UpsertAsync(Environment(), _root, CancellationToken.None));

        AssertRunningWorkflowUntouched(client, workflowId);
    }

    [Fact]
    public async Task Upsert_WhenTheUpdateFailsAndTheCloudWorkflowWasADraft_DoesNotStartIt()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient { UpdateError = "Failed to update workflow: something else" };
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 0, statusCode: 1, clientData: """{"properties":{"definition":{}}}""");

        await Assert.ThrowsAsync<ManagedAgentException>(() => Create(client).UpsertAsync(Environment(), _root, CancellationToken.None));

        Assert.DoesNotContain($"state:{workflowId}:activate", client.Calls);
    }

    [Fact]
    public async Task Upsert_WhenTheWriteThrows_LeavesARunningWorkflowUntouched()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient { UpdateThrows = new TimeoutException("the gateway gave up") };
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 1, statusCode: 2, clientData: """{"properties":{"definition":{}}}""");

        await Assert.ThrowsAsync<TimeoutException>(() => Create(client).UpsertAsync(Environment(), _root, CancellationToken.None));

        AssertRunningWorkflowUntouched(client, workflowId);
    }

    [Fact]
    public async Task Upsert_WhenTheCallerCancels_LeavesARunningWorkflowUntouched()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient { UpdateThrows = new OperationCanceledException() };
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 1, statusCode: 2, clientData: """{"properties":{"definition":{}}}""");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(client).UpsertAsync(Environment(), _root, CancellationToken.None));

        AssertRunningWorkflowUntouched(client, workflowId);
    }

    [Fact]
    public async Task Upsert_WhenAnUpdateIsRefusedAndTheCallerCancels_LeavesTheOriginalInPlace()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient
        {
            UpdateError = "Failed to update workflow: ConnectionAuthorizationFailed",
            InsertThrows = new OperationCanceledException(),
        };

        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 0, statusCode: 1, clientData: """{"properties":{"definition":{"marker":"original"}}}""");

        await Assert.ThrowsAsync<ManagedAgentException>(() => Create(client).UpsertAsync(Environment(), _root, CancellationToken.None));

        Assert.Contains("original", client.Cloud[workflowId].ClientData, StringComparison.Ordinal);
        Assert.DoesNotContain(client.Calls, call => call.StartsWith("insert:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upsert_WhenAnUpdateIsRefused_NeverReachesTheRestorePath()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient
        {
            UpdateError = "Failed to update workflow: ConnectionAuthorizationFailed",
            InsertError = "Failed to create workflow: ConnectionAuthorizationFailed",
        };

        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 0, statusCode: 1, clientData: """{"properties":{"definition":{"marker":"original"}}}""");

        using var caller = new CancellationTokenSource();

        await Assert.ThrowsAsync<ManagedAgentException>(() => Create(client).UpsertAsync(Environment(), _root, caller.Token));

        Assert.Contains("original", client.Cloud[workflowId].ClientData, StringComparison.Ordinal);
        Assert.Empty(client.RestoreTokens);
    }

    [Fact]
    public async Task Upsert_WhenTheStopIsRefused_DoesNotReportTheWorkflowAsADraft()
    {
        var workflowId = WriteWorkflow(activated: false);
        var client = new RecordingWorkflowClient { ActivateError = "refused" };
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 1, statusCode: 2, clientData: """{"properties":{"definition":{}}}""");

        var result = await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Contains($"update:none:{workflowId}", client.Calls);
        Assert.Equal(WorkflowStateProjection.Activated, result.Workflow.State);
    }

    [Fact]
    public async Task Upsert_AsksTheEnvironmentForItsReferencesInOneBatch()
    {
        WriteWorkflow(definition: """
        {"properties":{"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}},
          "shared_sql":{"api":{"name":"shared_sql"},"connection":{"connectionReferenceLogicalName":"new_sql"}}}}}
        """);

        var client = new RecordingWorkflowClient();

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Equal(["new_teams", "new_sql"], client.EnsuredLogicalNames);
        Assert.Empty(client.ExistenceChecks);
        Assert.Equal("references:new_teams,new_sql", client.Calls.First(call => call.StartsWith("references:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Upsert_DoesNotDeclareAReferenceTheEnvironmentAlreadyHas()
    {
        WriteWorkflow(definition: """
        {"properties":{"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
        """);

        var client = new RecordingWorkflowClient();
        client.References["new_teams"] = new SyncDataverseClient.ConnectionReferenceInfo { ConnectionReferenceLogicalName = "new_teams", ConnectorId = "shared_teams", ConnectionId = "conn-1" };

        var result = await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Empty(result.CreatedConnectionReferences);
        Assert.Empty(client.EnsuredLogicalNames);
    }

    [Fact]
    public async Task Upsert_WhenAnUpdateIsRefusedAndAWriteBackWouldAlsoFail_NeverLosesTheCloudCopy()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient
        {
            UpdateError = "Failed to update workflow: ConnectionAuthorizationFailed",
            InsertError = "Failed to create workflow: ConnectionAuthorizationFailed",
            RestoreError = "Failed to create workflow: the service is unavailable",
        };

        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 0, statusCode: 1, clientData: """{"properties":{"definition":{}}}""");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(client).UpsertAsync(Environment(), _root, CancellationToken.None));

        Assert.Equal("workflow-connections-not-ready", failure.ErrorKind);
        Assert.True(client.Cloud.ContainsKey(workflowId));
    }

    [Fact]
    public async Task Upsert_WhenACreateIsRefused_IsNotRetriedByDeleting()
    {
        WriteWorkflow();

        var client = new RecordingWorkflowClient { InsertError = "Failed to create workflow: ConnectionAuthorizationFailed" };

        await Assert.ThrowsAsync<ManagedAgentException>(() => Create(client).UpsertAsync(Environment(), _root, CancellationToken.None));

        Assert.DoesNotContain(client.Calls, call => call.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upsert_WhenAnUpdateIsRefused_SaysWhichConnectionReferenceAndHowToFixIt()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient
        {
            UpdateError = "Failed to update workflow: ConnectionAuthorizationFailed. Connection 'new_sharedteams_aa643948' to 'shared_teams' cannot be used to activate this flow",
            InsertError = "Failed to create workflow: ConnectionAuthorizationFailed. Connection 'new_sharedteams_aa643948' to 'shared_teams' cannot be used to activate this flow",
        };

        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 0, statusCode: 1, clientData: """{"properties":{"definition":{}}}""");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(client).UpsertAsync(Environment(), _root, CancellationToken.None));

        Assert.Equal("workflow-connections-not-ready", failure.ErrorKind);
        Assert.Contains("new_sharedteams_aa643948", failure.Message, StringComparison.Ordinal);
        Assert.Contains("deployment connection update", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upsert_WhenTheWriteFailsForAnotherReason_DoesNotReplaceTheWorkflow()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient { UpdateError = "Failed to update workflow: something else" };
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 0, statusCode: 1, clientData: """{"properties":{"definition":{}}}""");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(client).UpsertAsync(Environment(), _root, CancellationToken.None));

        Assert.Equal("workflow-push-failed", failure.ErrorKind);
        Assert.DoesNotContain(client.Calls, call => call.StartsWith("delete:", StringComparison.Ordinal));
        Assert.Equal(1, client.Calls.Count(call => call.StartsWith("update:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Upsert_WhenOnlyTheCloudCopyCarriesEnvironmentConnectionIds_WritesNothing()
    {
        var workflowId = WriteWorkflow(definition: """
        {"properties":{"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
        """);

        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 0, statusCode: 1, clientData: """
        {"properties":{"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"},"connectionName":"shared-teams-fc4f409d"}}}}
        """);

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.DoesNotContain(client.Calls, call => call.StartsWith("update:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upsert_WhenOnlyTheDescriptionChanged_WritesTheDescription()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 0, statusCode: 1, description: "the old description");

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Contains($"update:none:{workflowId}", client.Calls);
    }

    [Fact]
    public async Task Upsert_WhenTheLocalWorkflowIsADraftAndTheCloudCopyIsRunning_StopsIt()
    {
        var workflowId = WriteWorkflow(activated: false);
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 1, statusCode: 2);

        var result = await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Contains($"state:{workflowId}:draft", client.Calls);
        Assert.Equal(WorkflowStateProjection.Draft, result.Workflow.State);
    }

    [Fact]
    public async Task Upsert_WhenTheEnvironmentRefusesToStopTheWorkflow_ReportsItAsStillRunning()
    {
        var workflowId = WriteWorkflow(activated: false);
        var client = new RecordingWorkflowClient { ActivateError = "refused" };
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 1, statusCode: 2);

        var result = await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Activated, result.Workflow.State);
    }

    [Fact]
    public async Task Upsert_WhenNothingChanged_WritesNothing()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 0, statusCode: 1);

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.DoesNotContain(client.Calls, call => call.StartsWith("update:", StringComparison.Ordinal) || call.StartsWith("insert:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upsert_WhenNothingChanged_ReportsTheStateTheCloudIsIn()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 1, statusCode: 2);

        var result = await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Activated, result.Workflow.State);
        Assert.False(result.Created);
    }

    [Fact]
    public async Task Upsert_WhenNothingChanged_LeavesARunningWorkflowRunning()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 1, statusCode: 2);

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.DoesNotContain($"state:{workflowId}:draft", client.Calls);
    }

    [Fact]
    public async Task Upsert_WhenTheDefinitionChanged_Writes()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 0, statusCode: 1, clientData: """{"properties":{"definition":{"actions":{}}}}""");

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Contains($"update:none:{workflowId}", client.Calls);
    }

    [Fact]
    public async Task Upsert_WhenOnlyTheNameChanged_Writes()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 0, statusCode: 1, name: "Renamed");

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Contains($"update:none:{workflowId}", client.Calls);
    }

    [Fact]
    public async Task Upsert_WhenOnlyTheFormattingDiffers_WritesNothing()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = CloudCopy(workflowId, stateCode: 0, statusCode: 1, clientData: "{\"properties\":{}}");

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.DoesNotContain(client.Calls, call => call.StartsWith("update:", StringComparison.Ordinal));
    }

    private SyncDataverseClient.WorkflowMetadata CloudCopy(Guid workflowId, int stateCode, int statusCode, string? clientData = null, string name = "WF Hook 2", string? description = null)
    {
        var row = WorkflowScaffolder.NewMetadata(name, workflowId, WorkflowWorkspace.RelativeFolderFor(name, workflowId));

        row.Description = description;
        row.StateCode = stateCode;
        row.StatusCode = statusCode;
        row.ClientData = clientData ?? File.ReadAllText(WorkflowWorkspace.FindAll(_root).Single().DefinitionPath);

        return row;
    }

    [Fact]
    public async Task Upsert_WhenUpdating_SendsNoStateSoTheServiceDoesNotRevalidateConnections()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = "WF Hook 2", StateCode = 0, StatusCode = 1 };

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Null(Assert.Single(client.WrittenStateCodes));
        Assert.Null(Assert.Single(client.WrittenStatusCodes));
    }

    [Fact]
    public async Task Upsert_WhenUpdatingARunningWorkflow_SendsNoStateAndDoesNotStopIt()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = "WF Hook 2", StateCode = 1, StatusCode = 2 };

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        AssertRunningWorkflowUntouched(client, workflowId);
        Assert.Null(Assert.Single(client.WrittenStateCodes));
    }

    [Fact]
    public async Task Upsert_WhenUpdating_StillReportsTheWorkflowState()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = "WF Hook 2", StateCode = 0, StatusCode = 1 };

        var result = await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.False(result.Created);
        Assert.Equal(WorkflowStateProjection.Activated, result.Workflow.State);
    }

    [Fact]
    public async Task Upsert_DoesNotPushTheConnectionIdsOfTheEnvironmentTheWorkflowCameFrom()
    {
        WriteWorkflow(definition: """
        {"properties":{"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"},"connectionName":"shared-teams-13d033ce"}}}}
        """);

        var client = new RecordingWorkflowClient();
        client.References["new_teams"] = new SyncDataverseClient.ConnectionReferenceInfo { ConnectionReferenceLogicalName = "new_teams", ConnectorId = "shared_teams", ConnectionId = "conn-1" };

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.DoesNotContain("shared-teams-13d033ce", client.Cloud.Values.Single().ClientData, StringComparison.Ordinal);
        Assert.Contains("new_teams", client.Cloud.Values.Single().ClientData, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upsert_LeavesTheLocalDefinitionAlone()
    {
        WriteWorkflow(definition: """
        {"properties":{"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"},"connectionName":"shared-teams-13d033ce"}}}}
        """);

        await Create(new RecordingWorkflowClient()).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Contains("shared-teams-13d033ce", File.ReadAllText(WorkflowWorkspace.FindAll(_root).Single().DefinitionPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upsert_WhenTheCloudWorkflowIsRunning_RewritesTheDefinitionWithoutStoppingIt()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = "WF Hook 2", StateCode = 1, StatusCode = 2 };

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Equal(
            [$"update:none:{workflowId}"],
            client.Calls.Where(call => call.StartsWith("state:", StringComparison.Ordinal) || call.StartsWith("update:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Upsert_WhenTheCloudWorkflowIsAlreadyADraft_DoesNotStopIt()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = "WF Hook 2", StateCode = 0, StatusCode = 1 };

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.DoesNotContain($"state:{workflowId}:draft", client.Calls);
    }

    [Fact]
    public async Task Upsert_WhenTheWorkflowIsNotInTheCloud_Creates()
    {
        WriteWorkflow();

        var client = new RecordingWorkflowClient();
        var result = await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.True(result.Created);
        Assert.DoesNotContain(client.Calls, call => call.EndsWith(":draft", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upsert_WhenTheEnvironmentRefusesToStartTheWorkflow_LandsItAsADraftRatherThanFailing()
    {
        WriteWorkflow(definition: """
        {"properties":{"connectionReferences":{
          "shared_msnweather":{"api":{"name":"shared_msnweather"},"connection":{"connectionReferenceLogicalName":"new_weather"}}}}}
        """);

        var client = new RecordingWorkflowClient { ActivateError = "ConnectionAuthorizationFailed" };
        client.References["new_weather"] = new SyncDataverseClient.ConnectionReferenceInfo { ConnectionReferenceLogicalName = "new_weather", ConnectorId = "shared_msnweather", ConnectionId = "conn-1" };

        var result = await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Draft, result.Workflow.State);
        Assert.True(result.ActivationRequested);
    }

    [Fact]
    public async Task Upsert_WritesTheRowAsADraftSoTheServiceNeverActivatesDuringTheWrite()
    {
        WriteWorkflow();

        var client = new RecordingWorkflowClient();

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Equal(0, Assert.Single(client.WrittenStateCodes));
    }

    [Fact]
    public async Task Upsert_WhenTheReferencesAreUnbound_NeverAsksTheServiceToStartTheWorkflow()
    {
        WriteWorkflow(definition: """
        {"properties":{"connectionReferences":{
          "shared_msnweather":{"api":{"name":"shared_msnweather"},"connection":{"connectionReferenceLogicalName":"new_weather"}}}}}
        """);

        var client = new RecordingWorkflowClient();
        client.References["new_weather"] = new SyncDataverseClient.ConnectionReferenceInfo { ConnectionReferenceLogicalName = "new_weather", ConnectorId = "shared_msnweather", ConnectionId = string.Empty };

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.DoesNotContain(client.Calls, call => call.StartsWith("state:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upsert_WhenAReferenceHasNoConnection_PushesAsADraftRatherThanFailingToActivate()
    {
        WriteWorkflow(definition: """
        {"properties":{"connectionReferences":{
          "shared_msnweather":{"api":{"name":"shared_msnweather"},"connection":{"connectionReferenceLogicalName":"new_weather"}}}}}
        """);

        var client = new RecordingWorkflowClient();
        client.References["new_weather"] = new SyncDataverseClient.ConnectionReferenceInfo { ConnectionReferenceLogicalName = "new_weather", ConnectorId = "shared_msnweather", ConnectionId = string.Empty };

        var result = await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Draft, result.Workflow.State);
    }

    [Fact]
    public async Task Upsert_WhenEveryReferenceIsBound_KeepsTheWorkflowActivated()
    {
        WriteWorkflow(definition: """
        {"properties":{"connectionReferences":{
          "shared_msnweather":{"api":{"name":"shared_msnweather"},"connection":{"connectionReferenceLogicalName":"new_weather"}}}}}
        """);

        var client = new RecordingWorkflowClient();
        client.References["new_weather"] = new SyncDataverseClient.ConnectionReferenceInfo { ConnectionReferenceLogicalName = "new_weather", ConnectorId = "shared_msnweather", ConnectionId = "conn-1" };

        var result = await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Activated, result.Workflow.State);
    }

    [Fact]
    public async Task Upsert_WhenTheDefinitionBindsNothing_KeepsTheWorkflowActivated()
    {
        WriteWorkflow();

        var result = await Create(new RecordingWorkflowClient()).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Activated, result.Workflow.State);
    }

    [Fact]
    public async Task Upsert_ReadsTheReferencesFromTheDefinitionNotTheMetadata()
    {
        WriteWorkflow(definition: """
        {"properties":{"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
        """);

        var folder = WorkflowWorkspace.FindAll(_root).Single();
        Assert.Contains("connectionReferences: []", File.ReadAllText(folder.MetadataPath), StringComparison.Ordinal);

        var client = new RecordingWorkflowClient();
        client.References["new_teams"] = new SyncDataverseClient.ConnectionReferenceInfo { ConnectionReferenceLogicalName = "new_teams", ConnectorId = "shared_teams", ConnectionId = string.Empty };

        var result = await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Draft, result.Workflow.State);
    }

    [Fact]
    public async Task ReadLocal_ReadsTheWorkflowTheWorkspaceDefines()
    {
        var workflowId = WriteWorkflow("Local Flow");

        var workflow = await Create(new RecordingWorkflowClient()).ReadLocalAsync(_root, CancellationToken.None);

        Assert.Equal(workflowId, workflow.WorkflowId);
        Assert.Equal("Local Flow", workflow.Name);
    }

    [Fact]
    public async Task ReadLocal_WhenTheWorkspaceHoldsNoWorkflow_Fails()
    {
        Directory.CreateDirectory(_root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(new RecordingWorkflowClient()).ReadLocalAsync(_root, CancellationToken.None));

        Assert.Contains("holds no workflow", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_DelegatesAndProjectsEveryColumn()
    {
        var workflowId = Guid.NewGuid();
        var client = new RecordingWorkflowClient();
        client.Listed.Add(new SyncDataverseClient.WorkflowSummary { WorkflowId = workflowId, Name = "Beta", Description = "d", StateCode = 1, ModifiedOn = DateTimeOffset.UnixEpoch });
        client.Listed.Add(new SyncDataverseClient.WorkflowSummary { WorkflowId = Guid.NewGuid(), Name = "Alpha", StateCode = 0 });

        var listed = await Create(client).ListAsync(Environment(), "flow", 10, CancellationToken.None);

        Assert.Contains("list:flow:10", client.Calls);
        Assert.Equal(["Alpha", "Beta"], listed.Select(workflow => workflow.Name));
        Assert.Equal(WorkflowStateProjection.Activated, listed.Single(workflow => workflow.WorkflowId == workflowId).State);
        Assert.Equal(DateTimeOffset.UnixEpoch, listed.Single(workflow => workflow.WorkflowId == workflowId).ModifiedOn);
    }

    [Fact]
    public async Task Get_DelegatesAndReturnsNullWhenAbsent()
    {
        var client = new RecordingWorkflowClient();

        Assert.Null(await Create(client).GetAsync(Environment(), Guid.NewGuid(), CancellationToken.None));
        Assert.Contains(client.Calls, call => call.StartsWith("get:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_Delegates()
    {
        var workflowId = Guid.NewGuid();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = "Gone" };

        Assert.True(await Create(client).DeleteAsync(Environment(), workflowId, CancellationToken.None));
        Assert.Contains($"delete:{workflowId}", client.Calls);
    }

    [Fact]
    public async Task Delete_WhenTheRowIsAbsent_ReportsNothingDeleted()
    {
        var client = new RecordingWorkflowClient();

        Assert.False(await Create(client).DeleteAsync(Environment(), Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Download_WritesTheDefinitionMetadataAndDeclaration()
    {
        var workflowId = Guid.NewGuid();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = new SyncDataverseClient.WorkflowMetadata
        {
            WorkflowId = workflowId,
            Name = "Cloud Flow",
            StateCode = 1,
            ClientData = """{"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}""",
        };

        var downloaded = await Create(client).DownloadAsync(Environment(), workflowId, _root, CancellationToken.None);

        Assert.True(downloaded.Changed);
        Assert.True(File.Exists(Path.Combine(downloaded.FolderPath, WorkflowWorkspace.DefinitionFileName)));
        Assert.True(File.Exists(Path.Combine(downloaded.FolderPath, WorkflowWorkspace.MetadataFileName)));
        Assert.Contains("new_teams", File.ReadAllText(Path.Combine(_root, "infrastructure", "connections", "new_teams.sync.yaml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_WhenNothingChanged_ReportsNoChange()
    {
        var workflowId = Guid.NewGuid();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = "Cloud Flow", StateCode = 1, ClientData = "{}" };

        await Create(client).DownloadAsync(Environment(), workflowId, _root, CancellationToken.None);

        Assert.False((await Create(client).DownloadAsync(Environment(), workflowId, _root, CancellationToken.None)).Changed);
    }

    [Fact]
    public async Task Download_DoesNotCountTheCloudMirrorCacheAsAChange()
    {
        var workflowId = Guid.NewGuid();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = "Cloud Flow", StateCode = 1, ClientData = "{}" };

        await Create(client).DownloadAsync(Environment(), workflowId, _root, CancellationToken.None);

        var cache = Path.Combine(_root, ".mcs");
        Directory.Delete(cache, recursive: true);

        var downloaded = await Create(client).DownloadAsync(Environment(), workflowId, _root, CancellationToken.None);

        Assert.True(Directory.Exists(cache));
        Assert.False(downloaded.Changed);
    }

    [Fact]
    public async Task Download_WithOverwrite_WhenAnAuthoredFileDiffers_IsStillReportedAsAChange()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient();

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);
        await Create(client).DownloadAsync(Environment(), workflowId, _root, CancellationToken.None);

        File.WriteAllText(WorkflowWorkspace.FindAll(_root).Single().DefinitionPath, "{}");

        Assert.True((await Create(client).DownloadAsync(Environment(), workflowId, _root, CancellationToken.None, overwriteLocalChanges: true)).Changed);
    }

    [Fact]
    public async Task Download_WhenAnAuthoredFileDiffers_IsRefusedWithoutOverwrite()
    {
        var workflowId = WriteWorkflow();
        var client = new RecordingWorkflowClient();

        await Create(client).UpsertAsync(Environment(), _root, CancellationToken.None);
        await Create(client).DownloadAsync(Environment(), workflowId, _root, CancellationToken.None);

        File.WriteAllText(WorkflowWorkspace.FindAll(_root).Single().DefinitionPath, "{}");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(client).DownloadAsync(Environment(), workflowId, _root, CancellationToken.None));

        Assert.Equal("workflow-local-changes-would-be-lost", failure.ErrorKind);
        Assert.Equal("{}", File.ReadAllText(WorkflowWorkspace.FindAll(_root).Single().DefinitionPath));
    }

    [Fact]
    public async Task Download_WhenThePortalRenamedTheWorkflow_MovesTheFolder()
    {
        var workflowId = Guid.NewGuid();
        var client = new RecordingWorkflowClient();
        client.Cloud[workflowId] = new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = "Before", StateCode = 1, ClientData = "{}" };

        await Create(client).DownloadAsync(Environment(), workflowId, _root, CancellationToken.None);
        client.Cloud[workflowId].Name = "After";

        var downloaded = await Create(client).DownloadAsync(Environment(), workflowId, _root, CancellationToken.None);

        Assert.Equal($"After-{workflowId}", WorkflowWorkspace.FindAll(_root).Single().FolderName);
        Assert.True(downloaded.Changed);
    }

    [Fact]
    public async Task Download_WhenTheWorkflowIsAbsent_Fails()
    {
        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() =>
            Create(new RecordingWorkflowClient()).DownloadAsync(Environment(), Guid.NewGuid(), _root, CancellationToken.None));

        Assert.Contains("No workflow with id", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_WritesWhatTheDefinitionBinds()
    {
        WriteWorkflow(definition: """
        {"properties":{"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
        """);

        Create(new RecordingWorkflowClient()).WriteConnectionReferenceDeclaration(_root);

        Assert.Contains("new_teams", File.ReadAllText(Path.Combine(_root, "infrastructure", "connections", "new_teams.sync.yaml")), StringComparison.Ordinal);
    }
}

public sealed class WorkflowWorkspaceAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ah-wf-workspace-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void FindAll_ProjectsEveryFolderTheLibraryFinds()
    {
        var workflowId = WorkflowFixtures.Write(_root, "WF Hook 2");

        var found = Assert.Single(new WorkflowWorkspaceAdapter().FindAll(_root));

        Assert.Equal(workflowId, found.WorkflowId);
        Assert.Equal($"WFHook2-{workflowId}", found.FolderName);
        Assert.True(Directory.Exists(found.FolderPath));
    }

    [Fact]
    public void FindAll_WhenThereIsNoWorkflow_IsEmpty() => Assert.Empty(new WorkflowWorkspaceAdapter().FindAll(_root));

    [Fact]
    public void IsStandaloneWorkspace_MatchesTheLibrary()
    {
        WorkflowFixtures.Write(_root, "WF Hook 2");

        Assert.Equal(WorkflowWorkspace.IsStandaloneWorkspace(_root), new WorkflowWorkspaceAdapter().IsStandaloneWorkspace(_root));
        Assert.True(new WorkflowWorkspaceAdapter().IsStandaloneWorkspace(_root));
    }
}
