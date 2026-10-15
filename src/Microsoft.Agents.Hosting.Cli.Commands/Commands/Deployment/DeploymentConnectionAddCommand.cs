using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;

/// <summary><c>msagent deployment connection add</c>.</summary>
[Command("add", Description = "Declare a connection reference for a connector and bind it, creating the connection when none can be. MCS only.", ParentGroup = typeof(DeploymentConnectionGroup), SortPriority = 20)]
public sealed class DeploymentConnectionAddCommand : CommandBase<AgentDeploymentConnectionReferenceCreateRequest, AgentDeploymentConnectionReferenceCreateResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. DeploymentSelector.McsSymbols,
        OptionCatalog.Value("--connector", "Connector to declare a reference for, by internal id. MCS only.", required: true),
        OptionCatalog.Value("--connection-id", "Bind the new reference to this connection instead of resolving one. MCS only."),
        OptionCatalog.Value("--environment-id", "Create in this environment instead of a deployment's. Requires --connection-only. MCS only."),
        OptionCatalog.Value("--schema-name", "Schema used to resolve the deployed agent. MCS only."),
        OptionCatalog.Switch("--connection-only", "Create the connection only, declaring no reference. MCS only."),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<RouteMetadata> Routes =>
    [
        new RouteMetadata(typeof(AgentDeploymentConnectionReferenceCreateRequest), typeof(AgentDeploymentConnectionReferenceCreateResult)),
        new RouteMetadata(typeof(AgentDeploymentConnectionCreateRequest), typeof(AgentDeploymentConnectionCreateResult)),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes =>
    [
        new(UsageRoute.Deployment, "--connector") { Summary = "Declare a connection reference and bind it" },
        new("--connector", "--connection-only", "--environment-id") { Summary = "Create a connection in a named environment" },
    ];

    protected override string? Validate(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (UsageMessages.RequiredOptions(("--connector", input.GetString("--connector"))) is { } missing)
        {
            return missing;
        }

        if (input.GetSwitch("--connection-only"))
        {
            return string.IsNullOrWhiteSpace(input.GetString("--connection-id"))
                ? null
                : "--connection-id does not apply with --connection-only: no connection reference is declared to bind.";
        }

        return string.IsNullOrWhiteSpace(input.GetString("--environment-id"))
            ? null
            : "--environment-id applies only with --connection-only: a connection reference is declared in the environment its deployment targets.";
    }

    protected override AgentDeploymentConnectionReferenceCreateRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new AgentDeploymentConnectionReferenceCreateRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            DeploymentId = input.GetString("--deployment-id"),
            DeploymentName = input.GetString("--deployment-name"),
            SchemaName = input.GetString("--schema-name"),
            Connector = input.GetString("--connector"),
            ConnectionId = input.GetString("--connection-id"),
            NonInteractive = input.NonInteractive,
        };
    }

    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.GetSwitch("--connection-only"))
        {
            DeploymentSelector.RequireMcs(services, input, "msagent deployment connection add");

            return InvokeAsync<AgentDeploymentConnectionReferenceCreateRequest, AgentDeploymentConnectionReferenceCreateResult>(services, Bind(input), cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(input.GetString("--environment-id")))
        {
            DeploymentSelector.RequireMcs(services, input, "msagent deployment connection add");
        }

        return InvokeAsync<AgentDeploymentConnectionCreateRequest, AgentDeploymentConnectionCreateResult>(
            services,
            new AgentDeploymentConnectionCreateRequest
            {
                Project = input.GetString("--project"),
                AgentId = input.GetString("--agent-id"),
                AgentName = input.GetString("--agent-name"),
                DeploymentId = input.GetString("--deployment-id"),
                DeploymentName = input.GetString("--deployment-name"),
                SchemaName = input.GetString("--schema-name"),
                EnvironmentId = input.GetString("--environment-id"),
                Connector = input.GetString("--connector"),
                NonInteractive = input.NonInteractive,
            },
            cancellationToken);
    }
}
