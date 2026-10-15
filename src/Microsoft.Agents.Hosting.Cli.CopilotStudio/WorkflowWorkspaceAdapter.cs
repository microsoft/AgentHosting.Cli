using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Finds workflow folders by delegating to the sync library's workspace layout.</summary>
public sealed class WorkflowWorkspaceAdapter : IWorkflowWorkspacePort
{
    /// <inheritdoc />
    public IReadOnlyList<LocalWorkflow> FindAll(string workspaceRoot) =>
        [.. WorkflowWorkspace.FindAll(workspaceRoot).Select(folder => new LocalWorkflow(folder.WorkflowId, folder.FolderName, folder.FolderPath))];

    /// <inheritdoc />
    public LocalWorkflow RequireSingle(string workspaceRoot)
    {
        try
        {
            var file = WorkflowWorkspace.RequireSingle(WorkflowWorkspace.OpenWorkspace(workspaceRoot), workspaceRoot);

            return new LocalWorkflow(file.WorkflowId, file.FolderName, Path.Combine(workspaceRoot, WorkflowWorkspace.WorkflowsFolderName, file.FolderName));
        }
        catch (StandaloneWorkflowException failure)
        {
            throw WorkflowFailureTranslation.Translate(failure);
        }
    }

    /// <inheritdoc />
    public bool IsStandaloneWorkspace(string directory) => WorkflowWorkspace.IsStandaloneWorkspace(directory);

    /// <inheritdoc />
    public bool HasAgentProject(string directory) => Directory.Exists(directory) && WorkflowWorkspace.HasAgentProject(WorkflowWorkspace.OpenWorkspace(directory));
}
