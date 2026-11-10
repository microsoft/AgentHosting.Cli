using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Reads the identity an agent workspace declares through the sync library's settings reader.</summary>
public sealed class SyncAgentWorkspaceIdentityReader : IAgentWorkspaceIdentityPort
{
    /// <inheritdoc />
    public ProjectIdentity? Read(string workspaceDirectory) => Directory.Exists(workspaceDirectory) && AgentWorkspaceIdentity.TryRead(WorkflowWorkspace.OpenWorkspace(workspaceDirectory)) is { } identity ? new ProjectIdentity { SchemaName = identity.SchemaName, DisplayName = identity.DisplayName, Description = identity.Description } : null;

    /// <inheritdoc />
    public string? DetectAuthoringShape(string workspaceDirectory) => Directory.Exists(workspaceDirectory) && AgentClassifier.DetectAuthoringShapeFromFolder(workspaceDirectory) is var shape && shape != AuthoringShape.Unknown ? shape.ToString() : null;
}
