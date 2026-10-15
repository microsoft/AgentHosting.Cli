using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;

/// <summary><c>msagent deployment connection connectors</c>.</summary>
[Command("connectors", Description = "List the connectors the environment serves, so a reference can be declared against one. MCS only.", ParentGroup = typeof(DeploymentConnectionGroup), SortPriority = 50)]
public sealed class DeploymentConnectionConnectorsCommand : CommandBase<AgentDeploymentConnectorListRequest, AgentDeploymentConnectorListResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. DeploymentSelector.McsSymbols,
        OptionCatalog.Value("--environment-id", "Read this environment instead of a deployment's. Not with a deployment selector. MCS only."),
        OptionCatalog.Value("--filter", "Match connectors whose name, internal id or publisher contains this text. MCS only."),
        OptionCatalog.Value("--connector", "Match the connector with this internal id. MCS only."),
        OptionCatalog.Integer("--limit", "Maximum rows to return. Defaults to 50."),
    ];

    protected override string? Validate(ICommandInput input) => null;

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes =>
    [
        new(UsageRoute.Deployment) { Summary = "List the connectors a deployment's environment serves" },
        new("--environment-id") { Summary = "List the connectors a named environment serves" },
    ];

    protected override AgentDeploymentConnectorListRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new AgentDeploymentConnectorListRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            DeploymentId = input.GetString("--deployment-id"),
            DeploymentName = input.GetString("--deployment-name"),
            EnvironmentId = input.GetString("--environment-id"),
            Filter = input.GetString("--filter"),
            Connector = input.GetString("--connector"),
            Limit = input.GetInt("--limit"),
        };
    }

    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.GetString("--environment-id")))
        {
            DeploymentSelector.RequireMcs(services, input, "msagent deployment connection connectors");
        }

        return InvokeAsync<AgentDeploymentConnectorListRequest, AgentDeploymentConnectorListResult>(services, Bind(input), cancellationToken);
    }
}
