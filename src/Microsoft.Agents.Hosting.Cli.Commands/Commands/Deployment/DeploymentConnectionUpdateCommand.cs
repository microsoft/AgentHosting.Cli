using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;

/// <summary><c>msagent deployment connection update</c>.</summary>
[Command("update", Description = "Bind the agent's connection references, resolving one connector at a time. MCS only.", ParentGroup = typeof(DeploymentConnectionGroup), SortPriority = 20)]
public sealed class DeploymentConnectionUpdateCommand : CommandBase<AgentDeploymentConnectionRequest, AgentDeploymentConnectionResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. DeploymentSelector.McsSymbols,
        OptionCatalog.Value("--schema-name", "Schema used for connection discovery and binding. MCS only."),
        OptionCatalog.Value("--connection-reference", "Act on this connection reference only, by logical name. MCS only."),
        OptionCatalog.Value("--connector", "Act only on references targeting this connector. MCS only."),
        OptionCatalog.Value("--connection-id", "Bind to this connection. Requires the selectors to resolve exactly one connector. MCS only."),
        OptionCatalog.Switch("--by-reference", "Resolve each connection reference on its own instead of per connector. MCS only."),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes => [new(UsageRoute.Deployment)];

    protected override AgentDeploymentConnectionRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new AgentDeploymentConnectionRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            DeploymentId = input.GetString("--deployment-id"),
            DeploymentName = input.GetString("--deployment-name"),
            SchemaName = input.GetString("--schema-name"),
            ConnectionReference = input.GetString("--connection-reference"),
            Connector = input.GetString("--connector"),
            ConnectionId = input.GetString("--connection-id"),
            ByReference = input.GetSwitch("--by-reference"),
            NonInteractive = input.NonInteractive,
        };
    }

    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        DeploymentSelector.RequireMcs(services, input, "msagent deployment connection update");

        return InvokeAsync<AgentDeploymentConnectionRequest, AgentDeploymentConnectionResult>(services, Bind(input), cancellationToken);
    }
}
