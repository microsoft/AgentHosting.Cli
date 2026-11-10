using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>Words what to do about a pull into a folder that isn't connected to Copilot Studio, where the advice to deploy it doesn't fit.</summary>
public interface IWorkspaceDisconnectedRemediation
{
    /// <summary>The remediation for the agent, or null for the advice to deploy it.</summary>
    /// <param name="agent">The agent whose folder isn't connected.</param>
    string? For(ManagedAgentRecord agent);
}
