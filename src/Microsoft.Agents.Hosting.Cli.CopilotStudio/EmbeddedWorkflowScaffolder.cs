using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Writes a new local workflow by delegating to the sync library's scaffolder.</summary>
public sealed class EmbeddedWorkflowScaffolder : IWorkflowScaffoldPort
{
    /// <inheritdoc />
    public IReadOnlyList<string> Scaffold(string workspaceRoot, string name, Guid workflowId) => WorkflowScaffolder.Scaffold(workspaceRoot, name, workflowId);
}
