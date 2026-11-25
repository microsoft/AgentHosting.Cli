using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Test;

/// <summary>Starts a conversation or sends exactly one live query to a selected Copilot Studio runtime.</summary>
[Command("ask", Description = "Start a conversation or send one live query to a Copilot Studio agent. Live actions can have real effects.", ParentGroup = typeof(TestGroup), SortPriority = 10)]
[RequiredFeatureFlag(FeatureName.LiveTestFeatures)]
public sealed class AskCommand : CommandBase<LiveTestRequest, ILiveTestStreamingResult>
{
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--query", "Literal query to send. May enter shell history, and live actions are not rolled back."),
        OptionCatalog.Strings("--file", "Attach a local file to the query. Repeat for up to five files, at most 10 MiB total."),
        OptionCatalog.Switch("--start-conversation", "Start a new conversation and receive opening activities without sending a query. Cannot combine with --query or --conversation-id."),
        OptionCatalog.Value("--project", "Local project directory holding agent.config.json."),
        OptionCatalog.Value("--agent-id", "Managed-agent record id."),
        OptionCatalog.Value("--agent-name", "Managed-agent record name."),
        OptionCatalog.Value("--schema-name", "MCS schema name, not a managed-agent id."),
        OptionCatalog.Value("--deployment-id", "Deployment record id."),
        OptionCatalog.Value("--deployment-name", "Deployment name."),
        OptionCatalog.Value("--environment-id", "Destination environment id, asserted against configured targets."),
        OptionCatalog.Value("--tenant", "Destination tenant id, asserted against configured targets."),
        OptionCatalog.Value("--conversation-id", "Continue this conversation. Starts a new one when omitted, and no last-used id is saved."),
        OptionCatalog.Switch("--plain", "Final-only output, without transient progress."),
    ];

    protected override LiveTestRequest Bind(ICommandInput input) => new()
    {
        Query = input.GetString("--query"),
        Files = input.GetStrings("--file"),
        StartConversation = input.GetSwitch("--start-conversation"),
        Project = input.GetString("--project"),
        AgentId = input.GetString("--agent-id"),
        AgentName = input.GetString("--agent-name"),
        SchemaName = input.GetString("--schema-name"),
        DeploymentId = input.GetString("--deployment-id"),
        DeploymentName = input.GetString("--deployment-name"),
        EnvironmentId = input.GetString("--environment-id"),
        Tenant = input.GetString("--tenant"),
        ConversationId = input.GetString("--conversation-id"),
        Json = input.Json,
        Plain = input.GetSwitch("--plain"),
        NonInteractive = input.NonInteractive,
    };
}
