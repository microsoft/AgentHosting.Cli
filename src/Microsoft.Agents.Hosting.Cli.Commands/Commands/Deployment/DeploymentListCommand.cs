using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;

/// <summary><c>msagent deployment list</c>, routed to the local project or to AMS.</summary>
[Command("list", Description = "List deployment slots, in promotion order: the local project's, or an AMS code agent's.", ParentGroup = typeof(DeploymentGroup), SortPriority = 20)]
public sealed class DeploymentListCommand : CommandBase<AgentDeploymentListRequest, AgentDeploymentListResult>
{
    private static readonly AmsRouteRules AmsRules = new("deployment list") { LocalOptions = ["--project"] };

    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--project", "Project directory holding the agent. Defaults to the current directory."),
        OptionCatalog.Value("--agent-id", "Record whose deployments are listed. Wins over --agent-name. Every agent when omitted."),
        OptionCatalog.Value("--agent-name", "Record whose deployments are listed, by name."),
        AmsRoute.EnvironmentOption("AMS agent home environment, which selects the AMS route. Defaults to the active environment."),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<RouteMetadata> Routes =>
    [
        new RouteMetadata(typeof(AgentDeploymentListRequest), typeof(AgentDeploymentListResult)),
        new RouteMetadata(typeof(AgentDeploymentAmsListRequest), typeof(AgentDeploymentAmsListResult)),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes =>
    [
        new() { Summary = "List the deployments of the local project" },
        AmsRoute.Usage("List the deployments of an AMS code agent", withDeployment: false),
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

    /// <inheritdoc />
    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken) => AmsRoute.RouteAsync(input, services, AmsRules, () => InvokeAsync<AgentDeploymentAmsListRequest, AgentDeploymentAmsListResult>(services, AmsRoute.Address(input, new AgentDeploymentAmsListRequest()), cancellationToken), () => base.DispatchAsync(input, services, cancellationToken));
}
