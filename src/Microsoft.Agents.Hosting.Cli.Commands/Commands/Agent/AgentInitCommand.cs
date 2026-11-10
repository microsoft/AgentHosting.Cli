using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
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
        OptionCatalog.Value("--agent-name", "Name for the managed-agent record.", required: true) with { OptionalWithFeatures = [FeatureName.AmsMcsAgents], EnabledDescription = new([FeatureName.AmsMcsAgents], "Name for the managed-agent record. Not needed when the run only restores agents from AMS or registers them in AMS.") },
        OptionCatalog.Value("--agent-type", "Agent type. MCSAgent and MCSWorkflow are supported."),
        OptionCatalog.Value("--mcs-agent-source", "Directory holding the project. Defaults to the current directory."),
        OptionCatalog.Value("--mcs-workflow-source", "Directory holding the workflow. Defaults to the current directory. MCSWorkflow only."),
        OptionCatalog.Value("--repository-id", "GRS repository the project folder joins.") with { RequiredFeatures = [FeatureName.AmsMcsAgents] },
    ];

    /// <inheritdoc />
    public override FeatureDescription? EnabledDescription => new([FeatureName.AmsMcsAgents], "Register an MCS project or standalone workflow that already exists on disk. Creates nothing in Copilot Studio; it also registers the agent in AMS, restores the agents AMS has for the folder's repository, links the folder to the repository, and commits and pushes the agent's folder.");

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
            RepositoryId = input.GetString("--repository-id"),
            NonInteractive = input.NonInteractive,
        };
    }
}
