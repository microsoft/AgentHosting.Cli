using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;

/// <summary><c>msagent deployment connection list</c>.</summary>
[Command("list", Description = "Report the agent's connection references and the connection behind each. Changes nothing. MCS only.", ParentGroup = typeof(DeploymentConnectionGroup), SortPriority = 10)]
public sealed class DeploymentConnectionListCommand : CommandBase<AgentDeploymentConnectionShowRequest, AgentDeploymentConnectionResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. DeploymentSelector.McsSymbols,
        OptionCatalog.Value("--schema-name", "Schema used for connection discovery. MCS only."),
        OptionCatalog.Switch("--by-reference", "Row per connection reference instead of per connector. MCS only."),
    ];

    protected override string? Validate(ICommandInput input) => null;

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes => [new(UsageRoute.Deployment)];

    protected override AgentDeploymentConnectionShowRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new AgentDeploymentConnectionShowRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            DeploymentId = input.GetString("--deployment-id"),
            DeploymentName = input.GetString("--deployment-name"),
            SchemaName = input.GetString("--schema-name"),
            ByReference = input.GetSwitch("--by-reference"),
        };
    }

    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        DeploymentSelector.RequireMcs(services, input, "msagent deployment connection list");

        return InvokeAsync<AgentDeploymentConnectionShowRequest, AgentDeploymentConnectionResult>(services, Bind(input), cancellationToken);
    }
}
