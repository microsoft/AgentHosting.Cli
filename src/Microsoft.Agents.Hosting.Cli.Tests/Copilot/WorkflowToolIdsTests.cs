using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class WorkflowToolIdsTests : IDisposable
{
    private const string LayoutMarker = "layoutVersion: 1";

    private const string Settings =
        """
        displayName: MCS CLI Agent
        schemaName: ahe2e_MCSCliAgent
        configuration:
          publishOnCreate: false
          publishOnImport: true
          isLightweightBot: false
          recognizer:
            kind: CLICopilotRecognizer

          agentSettings:
            instructions:
              segments:
                - kind: StaticSegment

        language: 1033
        """;

    private readonly string _workspacePath = Path.Combine(Path.GetTempPath(), "ah-workflow-tool-ids-" + Guid.NewGuid().ToString("N"));

    public WorkflowToolIdsTests()
    {
        Directory.CreateDirectory(Path.Combine(_workspacePath, "capabilities", "tools"));
        File.WriteAllText(Path.Combine(_workspacePath, "agent.sync.yaml"), LayoutMarker);
        File.WriteAllText(Path.Combine(_workspacePath, "settings.mcs.yml"), Settings);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
    }

    private void WriteTool(string fileName, string content) =>
        File.WriteAllText(Path.Combine(_workspacePath, "capabilities", "tools", fileName), content);

    private void WriteWorkflowTool(string fileName, string displayName, string workflowId) =>
        WriteTool(
            fileName,
            $"""
            mcs.metadata:
              componentName: {displayName}
              description: A workflow tool.
            kind: WorkflowTool
            workflowId: {workflowId}
            toolInputs:
              - name: input1
                displayName: Input 1
            toolOutputs:
              - name: output1
                displayName: Output 1
            connectionProperties:
              $kind: ConnectionProperties
              mode: Invoker
            """);

    private async Task PushAsync()
    {
        await CopilotTestFixtures.WorkspaceAdapter(new RecordingWorkspaceSynchronizer { DefinitionReturns = await CopilotTestFixtures.ReadWorkspaceDefinitionAsync(_workspacePath) }).PushAsync(
            new CopilotWorkspaceInfo
            {
                WorkspacePath = _workspacePath,
                AgentId = "11111111-2222-3333-4444-555555555555",
                EnvironmentId = "env-1",
                DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
            },
            overwrite: false,
            CancellationToken.None);
    }

    [Fact]
    public async Task AnAllZeroWorkflowIdIsRefused()
    {
        WriteWorkflowTool("EmptyWorkflowIdTool.mcs.yml", "Empty Workflow Id Tool", "00000000-0000-0000-0000-000000000000");
        WriteWorkflowTool("AgentFlow3_kvb.mcs.yml", "Agent Flow 3", "6e5229e3-e343-f111-88b5-7ced8d3b6119");

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(PushAsync);

        Assert.Equal(CopilotErrorKinds.WorkflowToolIdEmpty, failure.ErrorKind);
        Assert.Contains("1 workflow tool", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Empty Workflow Id Tool", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Agent Flow 3", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOmittedWorkflowIdIsRefused()
    {
        WriteTool(
            "NoWorkflowTool.mcs.yml",
            """
            mcs.metadata:
              componentName: No Workflow Tool
              description: Declares no workflowId at all.
            kind: WorkflowTool
            toolInputs:
              - name: input1
                displayName: Input 1
            connectionProperties:
              $kind: ConnectionProperties
              mode: Invoker
            """);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(PushAsync);

        Assert.Equal(CopilotErrorKinds.WorkflowToolIdEmpty, failure.ErrorKind);
        Assert.Contains("No Workflow Tool", failure.Message, StringComparison.Ordinal);
    }
}
