using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary><c>msagent agent init</c>.</summary>
[Command("init", Description = "Register an MCS project or standalone workflow that already exists on disk. Creates no files and nothing in Copilot Studio.", ParentGroup = typeof(AgentGroup), SortPriority = 20)]
public sealed class AgentInitCommand : CommandBase<ManagedAgentInitRequest, ManagedAgentInitResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--agent-name", "Name for the managed-agent record.", required: true),
        OptionCatalog.Value("--agent-type", "Agent type. MCSAgent and MCSWorkflow are supported."),
        OptionCatalog.Value("--mcs-agent-source", "Directory holding the project. Defaults to the current directory."),
        OptionCatalog.Value("--mcs-workflow-source", "Directory holding the workflow. Defaults to the current directory. MCSWorkflow only."),
    ];

    /// <inheritdoc />
    protected override ManagedAgentInitRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new ManagedAgentInitRequest
        {
            Name = input.GetString("--agent-name"),
            AgentType = input.GetString("--agent-type"),
            McsAgentSource = input.GetString("--mcs-agent-source"),
            McsWorkflowSource = input.GetString("--mcs-workflow-source"),
        };
    }
}
