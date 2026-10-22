using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Writes a new local workflow by delegating to the sync library's scaffolder.</summary>
public sealed class EmbeddedWorkflowScaffolder : IWorkflowScaffoldPort
{
    /// <inheritdoc />
    public string? ValidateName(string name) => WorkflowNaming.Validate(name);

    /// <inheritdoc />
    public IReadOnlyList<string> Scaffold(string workspaceRoot, string name, Guid workflowId)
    {
        try
        {
            return WorkflowScaffolder.Scaffold(workspaceRoot, name, workflowId);
        }
        catch (StandaloneWorkflowException failure)
        {
            throw WorkflowFailureTranslation.Translate(failure);
        }
    }
}
