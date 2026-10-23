using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary><c>msagent agent pull</c>.</summary>
[Command("pull", Description = "Bring the remote MCS agent's content into the project, taking every component collection beside it.", ParentGroup = typeof(AgentGroup), SortPriority = 50, ProgressLabel = "Pulling agent")]
public sealed class AgentPullCommand : CommandBase<ManagedAgentPullRequest, ManagedAgentPullResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--project", "Directory to pull into. Defaults to the current directory."),
        OptionCatalog.Value("--agent-id", "Record to pull. Wins over --agent-name."),
        OptionCatalog.Value("--agent-name", "Record to pull, by name."),
        OptionCatalog.Value("--deployment-id", "Deployment whose environment is pulled from. Wins over --deployment-name. MCSWorkflow only."),
        OptionCatalog.Value("--deployment-name", "Deployment whose environment is pulled from, by name. MCSWorkflow only."),
        OptionCatalog.Switch("--overwrite", "Replace local workflow edits the environment has not seen. MCSWorkflow only."),
    ];

    /// <inheritdoc />
    protected override ManagedAgentPullRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new ManagedAgentPullRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            DeploymentId = input.GetString("--deployment-id"),
            DeploymentName = input.GetString("--deployment-name"),
            Overwrite = input.GetSwitch("--overwrite"),
        };
    }
}
