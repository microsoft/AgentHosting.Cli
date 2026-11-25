using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary><c>msagent agent delete</c>, routed to an agent of the local MCS project or to an AMS code agent.</summary>
[Command("delete", Description = "Delete an MCS agent's cloud resources and every record for it, or an AMS code agent with its deployments and bots. Local files are never touched.", ParentGroup = typeof(AgentGroup), SortPriority = 60, ProgressLabel = "Deleting agent")]
public sealed class AgentDeleteCommand : CommandBase<ManagedAgentDeleteRequest, ManagedAgentDeleteResult>
{
    private static readonly AmsRouteRules AmsRules = new("agent delete") { LocalOptions = ["--project", "--deployment-id", "--deployment-name", "--all-resources"] };

    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--project", "Project directory to act on. Defaults to the current directory."),
        OptionCatalog.Value("--agent-id", "Record to delete. Wins over --agent-name."),
        OptionCatalog.Value("--agent-name", "Record to delete, by name."),
        OptionCatalog.Value("--deployment-id", "Deployment whose cloud agent is deleted. Wins over --deployment-name. MCS only."),
        OptionCatalog.Value("--deployment-name", "Deployment whose cloud agent is deleted, by name. MCS only."),
        OptionCatalog.Switch("--all-resources", "Also delete the flows, AI prompts, connection references, and custom connectors the agent uses. MCS agents only."),
        AmsRoute.EnvironmentOption("AMS agent home environment, which selects the AMS route. Defaults to the active environment."),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<RouteMetadata> Routes =>
    [
        new RouteMetadata(typeof(ManagedAgentDeleteRequest), typeof(ManagedAgentDeleteResult)),
        new RouteMetadata(typeof(AgentAmsDeleteRequest), typeof(AgentAmsDeleteResult)),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes =>
    [
        new() { Summary = "Delete an MCS agent of the local project" },
        AmsRoute.Usage("Delete an AMS code agent with its deployments and bots", withDeployment: false),
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

    /// <inheritdoc />
    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken) => AmsRoute.RouteAsync(input, services, AmsRules, () => InvokeAsync<AgentAmsDeleteRequest, AgentAmsDeleteResult>(services, AmsRoute.Address(input, new AgentAmsDeleteRequest { NonInteractive = input.NonInteractive }), cancellationToken), () => base.DispatchAsync(input, services, cancellationToken));
}
