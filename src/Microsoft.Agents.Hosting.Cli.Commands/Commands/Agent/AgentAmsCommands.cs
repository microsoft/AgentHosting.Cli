using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary><c>msagent agent operation show</c>: reports one deploy or capture operation of an agent AMS registered.</summary>
[Command("show", Description = "Show a deploy or capture operation that AMS runs for an agent.", ParentGroup = typeof(AgentOperationGroup), SortPriority = 10)]
[RequiredFeatureFlag(FeatureName.AmsMcsAgents)]
public sealed class AgentOperationShowCommand : CommandBase<McsAmsOperationShowRequest, McsAmsOperationShowResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--operation-id", "Operation to show.", required: true),
        OptionCatalog.Value("--project", "Project directory whose .config names the agent. Defaults to the current directory."),
        OptionCatalog.Value("--agent-id", "Agent the operation belongs to. Wins over --agent-name."),
        OptionCatalog.Value("--agent-name", "Agent the operation belongs to, by name."),
        OptionCatalog.Value("--environment-id", "The agent's home environment, which selects the agent by --agent-id outside its project."),
    ];

    /// <inheritdoc />
    protected override McsAmsOperationShowRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new McsAmsOperationShowRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            EnvironmentId = input.GetString("--environment-id"),
            OperationId = input.GetString("--operation-id"),
        };
    }
}

/// <summary><c>msagent agent capture</c>: commits the Copilot Studio content of an agent's Dev deployment to its GRS repository through AMS.</summary>
[Command("capture", Description = "Commit the Copilot Studio changes of an agent's Dev deployment to its GRS repository.", ParentGroup = typeof(AgentGroup), SortPriority = 55, ProgressLabel = "Capturing agent")]
[RequiredFeatureFlag(FeatureName.AmsMcsAgents)]
public sealed class AgentCaptureCommand : CommandBase<McsAmsAgentCaptureRequest, McsAmsAgentCaptureResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--project", "Project directory whose .config names the agent. Defaults to the current directory."),
        OptionCatalog.Value("--agent-id", "Agent to capture. Wins over --agent-name."),
        OptionCatalog.Value("--agent-name", "Agent to capture, by name."),
        OptionCatalog.Value("--environment-id", "The agent's home environment, which selects the agent by --agent-id outside its project."),
        OptionCatalog.Value("--message", "Commit message."),
        OptionCatalog.Value("--resolve", "Conflict resolution: cloud, where Dev's side wins every conflict with a newer commit; the other changes of both sides merge as usual."),
        OptionCatalog.Switch("--async", "Return once AMS accepts the capture, without waiting for it."),
    ];

    /// <inheritdoc />
    protected override McsAmsAgentCaptureRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new McsAmsAgentCaptureRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            EnvironmentId = input.GetString("--environment-id"),
            Message = input.GetString("--message"),
            Resolve = input.GetString("--resolve"),
            Async = input.GetSwitch("--async"),
        };
    }
}