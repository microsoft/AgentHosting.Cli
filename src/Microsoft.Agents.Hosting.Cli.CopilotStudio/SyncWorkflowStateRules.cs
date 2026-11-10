using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Names workflow states and their portal labels by the sync library's <see cref="CopilotStudioStates"/>.</summary>
public sealed class SyncWorkflowStateRules : IWorkflowStateRules
{
    /// <inheritdoc />
    public string StateOf(bool? running) => running switch
    {
        true => CopilotStudioStates.Activated,
        false => CopilotStudioStates.Draft,
        null => CopilotStudioStates.Unknown,
    };

    /// <inheritdoc />
    public bool? RunningOf(string state) => state switch
    {
        CopilotStudioStates.Activated => true,
        CopilotStudioStates.Draft => false,
        _ => null,
    };

    /// <inheritdoc />
    public string PortalLabel(string state) => CopilotStudioStates.WorkflowPortalLabel(state);
}
