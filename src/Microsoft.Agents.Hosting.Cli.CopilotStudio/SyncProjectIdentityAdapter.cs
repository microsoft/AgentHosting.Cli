using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Reads a project's declared identity through the sync library's workspace identity reader.</summary>
public sealed class SyncProjectIdentityAdapter : ICopilotProjectIdentityPort
{
    /// <inheritdoc />
    public ProjectIdentity? Read(string projectDirectory) => WorkspaceIdentityReader.Read(projectDirectory) is { } identity
        ? new ProjectIdentity
        {
            SchemaName = identity.SchemaName,
            DisplayName = identity.DisplayName,
            Description = identity.Description,
            AuthoringMode = identity.AuthoringShape == AuthoringShape.CliCopilot ? CopilotAuthoringModes.CliCopilot : CopilotAuthoringModes.Classic,
        }
        : null;
}
