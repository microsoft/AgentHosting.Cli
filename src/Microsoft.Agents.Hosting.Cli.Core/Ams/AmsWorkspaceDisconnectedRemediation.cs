using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Tells the operator of an agent AMS registered how to bring Copilot Studio's changes into the repository, and how a Dev deploy connects the folder.</summary>
public sealed class AmsWorkspaceDisconnectedRemediation : IWorkspaceDisconnectedRemediation
{
    /// <inheritdoc />
    public string? For(ManagedAgentRecord agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return agent.SourceControlMetadata is null ? null : "To bring Copilot Studio changes into the repository, run `msagent agent capture`, then `git pull`. To connect this folder, deploy it to Dev: `msagent agent deploy` deploys the folder, and `msagent agent deploy --remote` connects it when the folder holds exactly the commit it deploys.";
    }
}
