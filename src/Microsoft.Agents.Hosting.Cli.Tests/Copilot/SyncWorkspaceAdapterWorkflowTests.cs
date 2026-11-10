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

    private static SyncWorkspaceAdapter Create(RecordingWorkspaceSynchronizer synchronizer) => CopilotTestFixtures.WorkspaceAdapter(synchronizer);

    private static RecordingWorkspaceSynchronizer SynchronizerWithWorkflow(string errorMessage)
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", ErrorMessage = errorMessage });

        return synchronizer;
    }

    [Fact]
    public async Task Push_WhenAWorkflowIsRejected_StillCompletesAndWarns()
    {
        var synchronizer = SynchronizerWithWorkflow(errorMessage: "this is not a valid connection");

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Contains(outcome.Warnings, warning => warning.Contains("Escalate", StringComparison.Ordinal));
        Assert.Contains(outcome.Warnings, warning => warning.Contains("this is not a valid connection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Push_WhenARefusalCarriesARawDataverseFailure_WarnsWithItsRedactedDescription()
    {
        Assert.Equal(["Workflow 'Escalate' was not activated: HTTP 403 Forbidden. Principal user is missing prvCreateworkflow privilege. (0x80040220)"], (await Create(SynchronizerWithWorkflow(errorMessage: """Dataverse request failed (403): {"error":{"code":"0x80040220","message":"Principal user is missing prvCreateworkflow privilege."},"trace":"sample.person@contoso.com"}""")).PushAsync(Workspace(), overwrite: false, CancellationToken.None)).Warnings);
    }

    [Fact]
    public async Task Push_ReportsAPushedConnectorOnceAsAChange()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.LocalChanges.Add(ConnectorChange());
        synchronizer.ConnectorRowIds["cre98_weather"] = ConnectorId;

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(CustomConnectorLayout.ChangeKind, Assert.Single(outcome.Changes).ComponentType);
        Assert.Equal(0, outcome.AuxiliaryChangeCount);
    }

    [Fact]
    public async Task Push_ReportsANewConnectorOnceHoweverManyOfItsFilesChanged()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.LocalChanges.Add(ConnectorChange());
        synchronizer.LocalChanges.Add(ConnectorChange() with { Uri = $"connectors/Weather_{ConnectorId}/openapidefinition.json", SchemaName = $"{CustomConnectorLayout.SchemaNamePrefix}{ConnectorId}.openapidefinition" });
        synchronizer.LocalChanges.Add(ConnectorChange() with { Uri = $"connectors/Weather_{ConnectorId}/connectionparameters.json", SchemaName = $"{CustomConnectorLayout.SchemaNamePrefix}{ConnectorId}.connectionparameters" });

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        var change = Assert.Single(outcome.Changes);
        Assert.Equal(CustomConnectorLayout.SchemaNamePrefix + ConnectorId, change.SchemaName);
        Assert.Equal(CopilotChangeKinds.Created, change.Kind);
    }

    [Fact]
    public async Task Push_ReportsANewFileInAnExistingConnectorAsAnUpdateToTheConnector()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.LocalChanges.Add(ConnectorChange() with { Uri = $"connectors/Weather_{ConnectorId}/iconblob.png", SchemaName = $"{CustomConnectorLayout.SchemaNamePrefix}{ConnectorId}.iconblob" });

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        var change = Assert.Single(outcome.Changes);
        Assert.Equal(CustomConnectorLayout.SchemaNamePrefix + ConnectorId, change.SchemaName);
        Assert.Equal(CopilotChangeKinds.Updated, change.Kind);
    }

    [Fact]
    public async Task Push_WhenAConnectorIsNotSaved_WarnsWithTheReason()
    {
        const string reason = "Failed to push custom connector 'Weather': it changed in the cloud since it was last synced. Get the latest changes, or overwrite the cloud copy.";
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.FailedConnectors.Add(ConnectorChange() with { ChangeType = ChangeType.Update });
        synchronizer.ConnectorFailureReasons.Add(reason);

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(reason, Assert.Single(outcome.Warnings));
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
    public async Task Push_WhenAWorkflowIsLeftDisabledWithoutAnError_Warns()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.WorkflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", IsDisabled = true });

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Contains(outcome.Warnings, warning => warning.Contains("Escalate", StringComparison.Ordinal) && warning.Contains("disabled", StringComparison.Ordinal));
    }

    private static RecordingWorkspaceSynchronizer Failing(string message) => new()
    {
        PushLocalChangesThrows = new InvalidOperationException(message),
    };

    private const string DuplicateConnectionReference = CopilotTestFixtures.DuplicateConnectionReferenceMessage;

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
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.PushFailedAfterRefusedComponents, failure.ErrorKind);
        Assert.Contains("1 component", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Escalate", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("new_sharedteams", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Does Not Exist", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Does Not Exist", failure.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_WhenAConnectionReferenceStillCollidesAfterTheRetry_FailsRatherThanWarning()
    {
        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(Failing(DuplicateConnectionReference))
                .PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.DuplicateConnectionReference, failure.ErrorKind);
        Assert.Contains("did not complete", failure.Message, StringComparison.Ordinal);
        Assert.Contains("msagent deployment connection update", failure.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("cre98_AgentC1.cr.WX3p-EQ4", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ExportKeyInvalidCreate", failure.Message, StringComparison.Ordinal);
        Assert.Contains("ExportKeyInvalidCreate", failure.InnerException!.Message, StringComparison.Ordinal);
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
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Contains("1 component", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Flow F1", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("need connections created", failure.Message, StringComparison.Ordinal);
    }
}
