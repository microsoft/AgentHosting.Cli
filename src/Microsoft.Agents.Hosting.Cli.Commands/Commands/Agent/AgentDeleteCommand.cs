using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary><c>msagent agent delete</c>.</summary>
[Command("delete", Description = "Delete an MCS agent's cloud resources and every record for it. Local files are never touched.", ParentGroup = typeof(AgentGroup), SortPriority = 60, ProgressLabel = "Deleting agent")]
public sealed class AgentDeleteCommand : CommandBase<ManagedAgentDeleteRequest, ManagedAgentDeleteResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--project", "Project directory to act on. Defaults to the current directory."),
        OptionCatalog.Value("--agent-id", "Record to delete. Wins over --agent-name."),
        OptionCatalog.Value("--agent-name", "Record to delete, by name."),
        OptionCatalog.Value("--deployment-id", "Deployment whose cloud agent is deleted. Wins over --deployment-name."),
        OptionCatalog.Value("--deployment-name", "Deployment whose cloud agent is deleted, by name."),
        OptionCatalog.Switch("--all-resources", "Also delete the flows, AI prompts, connection references, and custom connectors the agent uses. MCS agents only."),
    ];

    /// <inheritdoc />
    protected override ManagedAgentDeleteRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new ManagedAgentDeleteRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            DeploymentId = input.GetString("--deployment-id"),
            DeploymentName = input.GetString("--deployment-name"),
            AllResources = input.GetSwitch("--all-resources"),
            NonInteractive = input.NonInteractive,
        };
    }
}
