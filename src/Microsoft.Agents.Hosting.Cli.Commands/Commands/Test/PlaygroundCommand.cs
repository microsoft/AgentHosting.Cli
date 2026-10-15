using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Test;

/// <summary>Launches the M365 Agents Playground against a selected Copilot Studio runtime.</summary>
[Command("playground", Description = "Launch the M365 Agents Playground for a Copilot Studio agent. Live actions can have real effects. Native browser/device-code sign-in may still require user action even with --non-interactive; --json is unsupported in this MVP.", ParentGroup = typeof(TestGroup), SortPriority = 20)]
[RequiredFeatureFlag(FeatureName.LiveTestFeatures)]
public sealed class PlaygroundCommand : CommandBase<PlaygroundRequest, ILiveTestStreamingResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--agent-harness", "Agent harness: standard or github-copilot.", required: true),
        OptionCatalog.Value("--project", "Local project directory holding agent.config.json."),
        OptionCatalog.Value("--agent-id", "Managed-agent record id."),
        OptionCatalog.Value("--agent-name", "Managed-agent record name."),
        OptionCatalog.Value("--schema-name", "MCS schema name, not a managed-agent id."),
        OptionCatalog.Value("--deployment-id", "Deployment record id."),
        OptionCatalog.Value("--deployment-name", "Deployment name."),
        OptionCatalog.Value("--environment-id", "Destination environment id, asserted against configured targets."),
        OptionCatalog.Value("--tenant", "Destination tenant id, asserted against configured targets."),
        OptionCatalog.Switch("--plain", "Final-only output, without transient progress."),
        OptionCatalog.Switch("--install-playground", "Allow a global npm install when the playground is missing or below the minimum version; may execute package lifecycle scripts."),
    ];

    /// <inheritdoc />
    protected override PlaygroundRequest Bind(ICommandInput input) => new()
    {
        AgentType = input.GetString("--agent-harness")!,
        Project = input.GetString("--project"),
        AgentId = input.GetString("--agent-id"),
        AgentName = input.GetString("--agent-name"),
        SchemaName = input.GetString("--schema-name"),
        DeploymentId = input.GetString("--deployment-id"),
        DeploymentName = input.GetString("--deployment-name"),
        EnvironmentId = input.GetString("--environment-id"),
        Tenant = input.GetString("--tenant"),
        Json = input.Json,
        Plain = input.GetSwitch("--plain"),
        NonInteractive = input.NonInteractive,
        InstallPlayground = input.GetSwitch("--install-playground"),
    };
}
