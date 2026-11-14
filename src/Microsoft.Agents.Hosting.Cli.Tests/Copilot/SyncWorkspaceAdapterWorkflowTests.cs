using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncWorkspaceAdapterWorkflowTests
{
    private static CopilotWorkspaceInfo Workspace() => new()
    {
        WorkspacePath = Path.Combine(Path.GetTempPath(), "ah-workflow-tests"),
        AgentId = "11111111-2222-3333-4444-555555555555",
        EnvironmentId = "env-1",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static SyncWorkspaceAdapter Create(RecordingWorkspaceSynchronizer synchronizer) =>
        new(
            synchronizer,
            synchronizer,
            new RecordingRetargetService(),
            new StubSolutionVersionsClient(),
            new StubComponentCollectionClient(),
            new StubOperationContextProvider(),
            new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider()),
            new CopilotChangeMapper());

    private static RecordingWorkspaceSynchronizer SynchronizerWithWorkflow(string? errorMessage = null)
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", ErrorMessage = errorMessage! });

        return synchronizer;
    }

    [Fact]
    public async Task Push_UpsertsWorkflows()
    {
        var synchronizer = SynchronizerWithWorkflow();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.UpsertWorkflowCalls);
    }

    [Fact]
    public async Task Push_WhenAWorkflowIsRejected_StillCompletesAndWarns()
    {
        var synchronizer = SynchronizerWithWorkflow(errorMessage: "this is not a valid connection");

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Contains(outcome.Warnings, warning => warning.Contains("Escalate", StringComparison.Ordinal));
        Assert.Contains(outcome.Warnings, warning => warning.Contains("this is not a valid connection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Push_WhenAWorkflowIsRejected_StillPushesTheComponents()
    {
        var synchronizer = SynchronizerWithWorkflow(errorMessage: "this is not a valid connection");

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.Equal(1, synchronizer.UploadKnowledgeFilesCalls);
    }

    [Fact]
    public async Task Push_WhenAWorkflowIsRejected_StillUpsertsPrompts()
    {
        var synchronizer = SynchronizerWithWorkflow(errorMessage: "this is not a valid connection");

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.UpsertAIPromptsCalls);
    }

    [Fact]
    public async Task Push_RunsTheConnectorPushWithoutCountingUnchangedConnectors()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.ConnectorRowIds["cre98_weather"] = Guid.NewGuid();

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Contains(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), synchronizer.Calls);
        Assert.Equal(0, outcome.AuxiliaryChangeCount);
        Assert.Empty(outcome.Changes);
    }

    [Fact]
    public async Task Push_ReportsAPushedConnectorOnceAsAChange()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.LocalChanges.Add(ConnectorChange());
        synchronizer.ConnectorRowIds["cre98_weather"] = ConnectorId;

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(CustomConnectorLayout.ChangeKind, Assert.Single(outcome.Changes).ComponentType);
        Assert.Equal(0, outcome.AuxiliaryChangeCount);
    }

    [Fact]
    public async Task Push_DoesNotReportAnyFileOfAConnectorThatFailedToSave()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.LocalChanges.Add(ConnectorChange());
        synchronizer.LocalChanges.Add(ConnectorChange() with { Uri = $"connectors/Weather_{ConnectorId}/openapidefinition.json", SchemaName = $"{CustomConnectorLayout.SchemaNamePrefix}{ConnectorId}.openapidefinition" });
        synchronizer.LocalChanges.Add(new Change { Name = "Greeting", SchemaName = "cre98_agent.topic.Greeting", ChangeKind = "DialogComponent", ChangeType = ChangeType.Update });
        synchronizer.FailedConnectors.Add(ConnectorChange() with { ChangeType = ChangeType.Update });

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal("cre98_agent.topic.Greeting", Assert.Single(outcome.Changes).SchemaName);
    }

    [Fact]
    public async Task Push_ReportsANewConnectorOnceHoweverManyOfItsFilesChanged()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.LocalChanges.Add(ConnectorChange());
        synchronizer.LocalChanges.Add(ConnectorChange() with { Uri = $"connectors/Weather_{ConnectorId}/openapidefinition.json", SchemaName = $"{CustomConnectorLayout.SchemaNamePrefix}{ConnectorId}.openapidefinition" });
        synchronizer.LocalChanges.Add(ConnectorChange() with { Uri = $"connectors/Weather_{ConnectorId}/connectionparameters.json", SchemaName = $"{CustomConnectorLayout.SchemaNamePrefix}{ConnectorId}.connectionparameters" });

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        var change = Assert.Single(outcome.Changes);
        Assert.Equal(CustomConnectorLayout.SchemaNamePrefix + ConnectorId, change.SchemaName);
        Assert.Equal(CopilotChangeKinds.Created, change.Kind);
    }

    [Fact]
    public async Task Push_ReportsANewFileInAnExistingConnectorAsAnUpdateToTheConnector()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.LocalChanges.Add(ConnectorChange() with { Uri = $"connectors/Weather_{ConnectorId}/iconblob.png", SchemaName = $"{CustomConnectorLayout.SchemaNamePrefix}{ConnectorId}.iconblob" });

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        var change = Assert.Single(outcome.Changes);
        Assert.Equal(CustomConnectorLayout.SchemaNamePrefix + ConnectorId, change.SchemaName);
        Assert.Equal(CopilotChangeKinds.Updated, change.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Push_LetsTheConnectorPushOverwriteTheCloudOnlyWhenAskedTo(bool overwrite)
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PushAsync(Workspace(), overwrite, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(overwrite, synchronizer.LastConnectorPushOverwroteRemoteChanges);
    }

    [Fact]
    public async Task Push_WhenAConnectorIsNotSaved_WarnsWithTheReason()
    {
        const string reason = "Failed to push custom connector 'Weather': it changed in the cloud since it was last synced. Get the latest changes, or overwrite the cloud copy.";
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.FailedConnectors.Add(ConnectorChange() with { ChangeType = ChangeType.Update });
        synchronizer.ConnectorFailureReasons.Add(reason);

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(reason, Assert.Single(outcome.Warnings));
    }

    [Fact]
    public async Task Push_DoesNotReportAnyFileOfAWorkflowTheServiceRefused()
    {
        var workflowId = Guid.NewGuid();
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.LocalChanges.Add(new Change { Name = "Escalate", SchemaName = $"{WorkflowLayout.SchemaNamePrefix}{workflowId}", ChangeKind = "CloudFlowDefinition", ChangeType = ChangeType.Update });
        synchronizer.LocalChanges.Add(new Change { Name = "Escalate", SchemaName = $"{WorkflowLayout.SchemaNamePrefix}{workflowId}.metadata", ChangeKind = "CloudFlowDefinition", ChangeType = ChangeType.Update });
        synchronizer.LocalChanges.Add(new Change { Name = "Greeting", SchemaName = "cre98_agent.topic.Greeting", ChangeKind = "DialogComponent", ChangeType = ChangeType.Update });
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowId = workflowId, WorkflowName = "Escalate", ErrorMessage = "the flow definition is invalid" });

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal("cre98_agent.topic.Greeting", Assert.Single(outcome.Changes).SchemaName);
    }

    private static readonly Guid ConnectorId = Guid.Parse("6f1c2b3a-4d5e-4f60-8a9b-0c1d2e3f4a5b");

    private static Change ConnectorChange() => new()
    {
        Name = "Weather",
        Uri = $"connectors/Weather_{ConnectorId}/metadata.yml",
        ChangeType = ChangeType.Create,
        ChangeKind = CustomConnectorLayout.ChangeKind,
        SchemaName = CustomConnectorLayout.SchemaNamePrefix + ConnectorId,
    };

    [Fact]
    public async Task Push_UploadsCustomConnectorsBeforeUpsertingWorkflows()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        var connectors = synchronizer.Calls.IndexOf(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync));
        var workflows = synchronizer.Calls.IndexOf(nameof(IWorkspaceSynchronizer.UpsertWorkflowForAgentAsync));

        Assert.True(connectors >= 0 && workflows >= 0);
        Assert.True(connectors < workflows);
    }

    [Fact]
    public async Task Push_WhenAWorkflowIsLeftDisabledWithoutAnError_Warns()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", IsDisabled = true });

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Contains(outcome.Warnings, warning => warning.Contains("Escalate", StringComparison.Ordinal) && warning.Contains("disabled", StringComparison.Ordinal));
    }

    private static RecordingWorkspaceSynchronizer Failing(string message, bool onlyOnce = false) => new()
    {
        PushLocalChangesThrows = new InvalidOperationException(message),
        PushLocalChangesThrowsOnlyOnce = onlyOnce,
    };

    private const string DuplicateConnectionReference = CopilotTestFixtures.DuplicateConnectionReferenceMessage;

    private const string DuplicateComponent = "[0x80072013:ExportKeyInvalidCreate] Cannot complete the creation of botcomponent because it violates a database constraint. The violation happens on the key schemaname: crf9a_agentD1.file.abcmain2.txt_0Sh.";

    [Fact]
    public async Task Push_WhenAComponentStillCollidesAfterTheRetry_NamesTheComponentRatherThanAConnectionReference()
    {
        var synchronizer = Failing(DuplicateComponent);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.DuplicateComponent, failure.ErrorKind);
        Assert.Contains("crf9a_agentD1.file.abcmain2.txt_0Sh", failure.Message, StringComparison.Ordinal);
        Assert.Contains("env-1", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("connection reference", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("msagent agent pull", failure.Remediation, StringComparison.Ordinal);
        Assert.Contains("ExportKeyInvalidCreate", failure.InnerException!.Message, StringComparison.Ordinal);
        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_WhenAComponentFailsAfterAWorkflowWasRefused_ReportsTheRefusalAsTheCause()
    {
        var synchronizer = Failing("Entity 'Workflow' With Id = 588a3ca3-076c-1b1e-7bee-5e4a397a639d Does Not Exist");
        synchronizer.WorkflowResponses.Add(new WorkflowResponse
        {
            WorkflowName = "Escalate",
            ErrorMessage = "connection reference new_sharedteams is not bound",
        });

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.PushFailedAfterRefusedComponents, failure.ErrorKind);
        Assert.Contains("1 component", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Escalate", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("new_sharedteams", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Does Not Exist", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Does Not Exist", failure.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_WhenAComponentFailsWithNothingRefused_KeepsTheOriginalFailure()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(Failing("something else went wrong")).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal("something else went wrong", failure.Message);
    }

    [Fact]
    public async Task Push_WhenAMissingEntityFailsWithNothingRefused_KeepsTheOriginalFailure()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(Failing("Entity 'Workflow' With Id = abc Does Not Exist"))
                .PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Contains("Does Not Exist", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_WhenAConnectionReferenceStillCollidesAfterTheRetry_FailsRatherThanWarning()
    {
        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Failing(DuplicateConnectionReference))
                .PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.DuplicateConnectionReference, failure.ErrorKind);
        Assert.Contains("did not complete", failure.Message, StringComparison.Ordinal);
        Assert.Contains("msagent deployment connection update", failure.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("cre98_AgentC1.cr.WX3p-EQ4", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ExportKeyInvalidCreate", failure.Message, StringComparison.Ordinal);
        Assert.Contains("ExportKeyInvalidCreate", failure.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_WhenAConnectionReferenceAlreadyExists_RetriesOnceBeforeFailing()
    {
        var synchronizer = Failing(DuplicateConnectionReference);

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Equal(2, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_WhenADuplicateClearsOnRetry_CompletesWithoutWarning()
    {
        var outcome = await Create(Failing(DuplicateConnectionReference, onlyOnce: true))
            .PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public async Task Push_WhenAConnectionReferenceExistsAfterARefusedWorkflow_CountsTheRefusalWithoutQuotingIt()
    {
        var synchronizer = Failing(DuplicateConnectionReference);
        synchronizer.WorkflowResponses.Add(new WorkflowResponse
        {
            WorkflowName = "Flow F1",
            ErrorMessage = "the related connection references need connections created",
        });

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None));

        Assert.Contains("1 component", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Flow F1", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("need connections created", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_WhenAWorkflowIsEnabledAndAccepted_DoesNotWarn()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", IsDisabled = false });

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public async Task Push_WhenOneWorkflowOfSeveralIsRejected_WarnsAboutOnlyTheRejectedOne()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Accepted" });
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Refused", ErrorMessage = "the flow definition is invalid" });

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Contains(outcome.Warnings, warning => warning.Contains("Refused", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.Warnings, warning => warning.Contains("Accepted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Push_WithEveryWorkflowAccepted_WarnsAboutNothing()
    {
        var synchronizer = SynchronizerWithWorkflow();

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public async Task Push_WithEveryWorkflowAccepted_ReachesThePush()
    {
        var synchronizer = SynchronizerWithWorkflow();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_WithNoWorkflows_ReachesThePush()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
    }
}
