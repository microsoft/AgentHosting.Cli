using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>Reads the identity an agent workspace declares, as the sync library defines it.</summary>
public interface IAgentWorkspaceIdentityPort
{
    /// <summary>Reads the identity the workspace's settings file, or its agent file when it holds no settings file, declares, or returns null when that file is missing, empty, malformed or names no schema name.</summary>
    /// <param name="workspaceDirectory">The agent workspace's directory.</param>
    /// <returns>The declared identity, or <see langword="null"/>.</returns>
    ProjectIdentity? Read(string workspaceDirectory);

    /// <summary>Detects how the workspace was authored, as the sync library classifies it: Classic or CliCopilot.</summary>
    /// <param name="workspaceDirectory">The agent workspace's directory.</param>
    /// <returns>Classic, CliCopilot, or null when the sync library can't tell.</returns>
    string? DetectAuthoringShape(string workspaceDirectory);
}