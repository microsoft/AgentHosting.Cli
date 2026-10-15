using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.Extensions.DependencyInjection;

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

    private async Task<DefinitionBase> ReadAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITokenProvider>(new RecordingTokenProvider());
        services.AddAgentHostCopilotStudio(TextWriter.Null, PowerPlatformApiRingSelection.Default);

        await using var provider = services.BuildServiceProvider();

        return await provider.GetRequiredService<IWorkspaceSynchronizer>()
            .ReadWorkspaceDefinitionAsync(new DirectoryPath(_workspacePath.Replace('\\', '/')), CancellationToken.None);
    }

    [Fact]
    public async Task AnAllZeroWorkflowIdIsRefused()
    {
        WriteWorkflowTool("EmptyWorkflowIdTool.mcs.yml", "Empty Workflow Id Tool", "00000000-0000-0000-0000-000000000000");
        WriteWorkflowTool("AgentFlow3_kvb.mcs.yml", "Agent Flow 3", "6e5229e3-e343-f111-88b5-7ced8d3b6119");

        var definition = await ReadAsync();

        var failure = Assert.Throws<CopilotStudioException>(() => WorkflowToolIds.RequireNotEmpty(definition));

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

        var definition = await ReadAsync();

        var failure = Assert.Throws<CopilotStudioException>(() => WorkflowToolIds.RequireNotEmpty(definition));

        Assert.Equal(CopilotErrorKinds.WorkflowToolIdEmpty, failure.ErrorKind);
        Assert.Contains("No Workflow Tool", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonEmptyWorkflowIdIsAccepted()
    {
        WriteWorkflowTool("AgentFlow3_kvb.mcs.yml", "Agent Flow 3", "6e5229e3-e343-f111-88b5-7ced8d3b6119");

        WorkflowToolIds.RequireNotEmpty(await ReadAsync());
    }

    [Fact]
    public async Task AWorkspaceWithNoWorkflowToolsIsAccepted()
    {
        WorkflowToolIds.RequireNotEmpty(await ReadAsync());
    }
}
