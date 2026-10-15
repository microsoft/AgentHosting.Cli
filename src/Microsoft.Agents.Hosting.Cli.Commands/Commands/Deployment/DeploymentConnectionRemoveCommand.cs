using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;

/// <summary><c>msagent deployment connection remove</c>.</summary>
[Command("remove", Description = "Remove a connection reference from the workspace, delete a connection from the environment, or both. MCS only.", ParentGroup = typeof(DeploymentConnectionGroup), SortPriority = 40)]
public sealed class DeploymentConnectionRemoveCommand : CommandBase<AgentDeploymentConnectionRemoveRequest, AgentDeploymentConnectionRemoveResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. DeploymentSelector.McsSymbols,
        OptionCatalog.Value("--connection-reference", "Connection reference to remove from the workspace, by logical name. MCS only."),
        OptionCatalog.Value("--connection-id", "Connection to delete from the environment. MCS only."),
        OptionCatalog.Value("--connector", "Connector the connection serves. Inferred from the workspace when omitted. MCS only."),
        OptionCatalog.Value("--environment-id", "Delete from this environment instead of a deployment's. Not with a deployment selector. MCS only."),
        OptionCatalog.Value("--schema-name", "Schema used to resolve the deployed agent. MCS only."),
        OptionCatalog.Switch("--force", "Remove even while something still uses what was named. MCS only."),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes =>
    [
        new(UsageRoute.Deployment, "(--connection-reference <connection-reference> | --connection-id <connection-id>)") { Summary = "Remove from a deployment" },
        new("--connection-id", "--environment-id") { Summary = "Delete a connection from a named environment" },
    ];

    protected override string? Validate(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return string.IsNullOrWhiteSpace(input.GetString("--connection-reference")) && string.IsNullOrWhiteSpace(input.GetString("--connection-id"))
            ? UsageMessages.Required("--connection-reference or --connection-id")
            : null;
    }

    protected override AgentDeploymentConnectionRemoveRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new AgentDeploymentConnectionRemoveRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            DeploymentId = input.GetString("--deployment-id"),
            DeploymentName = input.GetString("--deployment-name"),
            SchemaName = input.GetString("--schema-name"),
            EnvironmentId = input.GetString("--environment-id"),
            Connector = input.GetString("--connector"),
            ConnectionId = input.GetString("--connection-id"),
            ConnectionReference = input.GetString("--connection-reference"),
            Force = input.GetSwitch("--force"),
            NonInteractive = input.NonInteractive,
        };
    }

    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.GetString("--environment-id")))
        {
            DeploymentSelector.RequireMcs(services, input, "msagent deployment connection remove");
        }

        return InvokeAsync<AgentDeploymentConnectionRemoveRequest, AgentDeploymentConnectionRemoveResult>(services, Bind(input), cancellationToken);
    }
}
