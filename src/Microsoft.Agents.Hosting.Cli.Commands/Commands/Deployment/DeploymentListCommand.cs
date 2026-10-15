using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;

/// <summary><c>msagent deployment list</c>.</summary>
[Command("list", Description = "List deployment slots, in promotion order. Entirely local.", ParentGroup = typeof(DeploymentGroup), SortPriority = 20)]
public sealed class DeploymentListCommand : CommandBase<AgentDeploymentListRequest, AgentDeploymentListResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--project", "Project directory holding the agent. Defaults to the current directory."),
        OptionCatalog.Value("--agent-id", "Record whose deployments are listed. Wins over --agent-name. Every agent when omitted."),
        OptionCatalog.Value("--agent-name", "Record whose deployments are listed, by name."),
    ];

    /// <inheritdoc />
    protected override AgentDeploymentListRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new AgentDeploymentListRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
        };
    }
}
