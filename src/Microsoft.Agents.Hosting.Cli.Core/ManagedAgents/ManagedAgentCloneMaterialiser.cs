using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Writes a clone's content into its project directory.
/// </summary>
public sealed class ManagedAgentCloneMaterialiser
{
    private readonly ICopilotWorkspacePort _workspaces;
    private readonly CopilotProjectIdentityReader _projectIdentities;

    /// <summary>Creates the materialiser.</summary>
    /// <param name="workspaces">Downloads the agent's content.</param>
    /// <param name="projectIdentities">Detects content that is already present.</param>
    public ManagedAgentCloneMaterialiser(ICopilotWorkspacePort workspaces, CopilotProjectIdentityReader projectIdentities)
    {
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _projectIdentities = projectIdentities ?? throw new ArgumentNullException(nameof(projectIdentities));
    }

    /// <summary>Fetches the source agent's content into the project directory.</summary>
    /// <param name="projectDirectory">The project root the content is written under.</param>
    /// <param name="workspaceDirectory">The directory the agent workspace will occupy.</param>
    /// <param name="source">The environment holding the source agent.</param>
    /// <param name="mcsAgentId">The source agent to check out.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>What was written, or <see langword="null"/> when the content was already there.</returns>
    public async Task<CopilotCloneOutcome?> MaterialiseAsync(string projectDirectory, string workspaceDirectory, CopilotEnvironmentContext source, string mcsAgentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(mcsAgentId);

        if (_projectIdentities.Find(workspaceDirectory) is not null)
        {
            return null;
        }

        return await _workspaces.CloneAsync(
            new CopilotCloneCommand
            {
                Environment = source,
                AgentId = mcsAgentId,
                WorkspacePath = projectDirectory,
                AgentFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspaceDirectory)),
                IncludeLinkedComponentCollections = true,
            },
            cancellationToken).ConfigureAwait(false);
    }
}
