using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary><c>msagent agent show</c>, routed to the local project or to an AMS code agent.</summary>
[Command("show", Description = "Show the managed-agent records this project holds, read locally, or an AMS code agent with its deployments.", ParentGroup = typeof(AgentGroup), SortPriority = 40)]
public sealed class AgentShowCommand : CommandBase<ManagedAgentShowRequest, ManagedAgentShowResult>
{
    private static readonly AmsRouteRules AmsRules = new("agent show") { LocalOptions = ["--project"] };

    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--project", "Project directory to read. Defaults to the current directory."),
        OptionCatalog.Value("--agent-id", "Record to show. Wins over --agent-name. Every record when omitted."),
        OptionCatalog.Value("--agent-name", "Record to show, by name."),
        AmsRoute.EnvironmentOption("AMS agent home environment, which selects the AMS route. Defaults to the active environment."),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<RouteMetadata> Routes =>
    [
        new RouteMetadata(typeof(ManagedAgentShowRequest), typeof(ManagedAgentShowResult)),
        new RouteMetadata(typeof(AgentAmsShowRequest), typeof(AgentAmsShowResult)),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes =>
    [
        new() { Summary = "Show the records of the local project" },
        AmsRoute.Usage("Show an AMS code agent with its deployments", withDeployment: false),
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

    /// <inheritdoc />
    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken) => AmsRoute.RouteAsync(input, services, AmsRules, () => InvokeAsync<AgentAmsShowRequest, AgentAmsShowResult>(services, AmsRoute.Address(input, new AgentAmsShowRequest()), cancellationToken), () => base.DispatchAsync(input, services, cancellationToken));
}
