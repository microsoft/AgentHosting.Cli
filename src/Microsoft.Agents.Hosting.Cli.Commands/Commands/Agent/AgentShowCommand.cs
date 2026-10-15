using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary><c>msagent agent show</c>.</summary>
[Command("show", Description = "Show the managed-agent records this project holds. Reads the local configuration and contacts nothing.", ParentGroup = typeof(AgentGroup), SortPriority = 40)]
public sealed class AgentShowCommand : CommandBase<ManagedAgentShowRequest, ManagedAgentShowResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--project", "Project directory to read. Defaults to the current directory."),
        OptionCatalog.Value("--agent-id", "Record to show. Wins over --agent-name. Every record when omitted."),
        OptionCatalog.Value("--agent-name", "Record to show, by name."),
    ];

    /// <inheritdoc />
    protected override ManagedAgentShowRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new ManagedAgentShowRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
        };
    }
}
