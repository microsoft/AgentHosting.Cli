using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncWorkspaceAdapterWorkflowToolTests
{
    private const string EmptySchemaName = "cli_Agent.tool.EmptyWorkflowIdTool";
    private const string ResolvedSchemaName = "cli_Agent.tool.AgentFlow3_kvb";

    private static CopilotWorkspaceInfo Workspace() => new()
    {
        WorkspacePath = Path.Combine(Path.GetTempPath(), "ah-workflow-tool-tests"),
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

    private static DialogComponent WorkflowToolComponent(string displayName, string schemaName, Guid workflowId) =>
        new DialogComponent.Builder
        {
            DisplayName = displayName,
            SchemaName = new DialogSchemaName(schemaName),
            Dialog = new WorkflowTool.Builder { WorkflowId = workflowId },
        }.Build();

    private static DialogComponent ConnectorToolComponent() =>
        new DialogComponent.Builder
        {
            DisplayName = "Get a section",
            SchemaName = new DialogSchemaName("cli_Agent.tool.Getasection_fcU"),
            Dialog = new ConnectorTool.Builder(),
        }.Build();

    private static RecordingWorkspaceSynchronizer Declaring(params BotComponentBase[] components) =>
        new() { DefinitionReturns = BotDefinition.Empty.WithComponents(components) };

    [Fact]
    public async Task Push_WhenAWorkflowToolHasTheAllZeroWorkflowId_Fails()
    {
        var synchronizer = Declaring(WorkflowToolComponent("Empty Workflow Id Tool", EmptySchemaName, Guid.Empty));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkflowToolIdEmpty, failure.ErrorKind);
        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Contains("Empty Workflow Id Tool", failure.Message, StringComparison.Ordinal);
        Assert.Contains(EmptySchemaName, failure.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(failure.Remediation));
    }

    [Fact]
    public async Task Push_WhenAWorkflowToolHasAnEmptyWorkflowId_SendsNothingToTheCloud()
    {
        var synchronizer = Declaring(WorkflowToolComponent("Empty Workflow Id Tool", EmptySchemaName, Guid.Empty));

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), synchronizer.Calls);
        Assert.Equal(0, synchronizer.UpsertWorkflowCalls);
        Assert.Equal(0, synchronizer.PushLocalChangesCalls);
        Assert.Equal(0, synchronizer.UploadKnowledgeFilesCalls);
    }

    [Fact]
    public async Task Push_WithOverwrite_StillRefusesAWorkflowToolWithAnEmptyId()
    {
        var synchronizer = Declaring(WorkflowToolComponent("Empty Workflow Id Tool", EmptySchemaName, Guid.Empty));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: true, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkflowToolIdEmpty, failure.ErrorKind);
        Assert.Equal(0, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Push_WhenSeveralWorkflowToolsHaveAnEmptyWorkflowId_NamesEveryOne()
    {
        var synchronizer = Declaring(
            WorkflowToolComponent("Empty Workflow Id Tool", EmptySchemaName, Guid.Empty),
            WorkflowToolComponent("Second Broken Tool", "cli_Agent.tool.SecondBrokenTool", Guid.Empty),
            WorkflowToolComponent("Agent Flow 3", ResolvedSchemaName, Guid.Parse("6e5229e3-e343-f111-88b5-7ced8d3b6119")));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Contains("2 workflow tools", failure.Message, StringComparison.Ordinal);
        Assert.Contains(EmptySchemaName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("cli_Agent.tool.SecondBrokenTool", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ResolvedSchemaName, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_WhenEveryWorkflowToolHasAWorkflowId_ReachesThePush()
    {
        var synchronizer = Declaring(
            WorkflowToolComponent("Agent Flow 3", ResolvedSchemaName, Guid.Parse("6e5229e3-e343-f111-88b5-7ced8d3b6119")),
            ConnectorToolComponent());

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public async Task Push_WithNoWorkflowTools_ReachesThePush()
    {
        var synchronizer = Declaring(ConnectorToolComponent());

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
    }
}
