using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using DeploymentModel = Microsoft.Agents.Hosting.Cli.Core.Deployments.CodeAgentDeployment;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;

/// <summary><c>msagent deployment delete</c>, routed to a deployment of the local project or to AMS.</summary>
[Command("delete", Description = "Delete a deployment.", ParentGroup = typeof(DeploymentGroup), SortPriority = 50, ProgressLabel = "Deleting deployment")]
public sealed class DeploymentDeleteCommand : CommandBase<AgentDeploymentDeleteRequest, AgentDeploymentDeleteResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. DeploymentSelector.Symbols,
        OptionCatalog.Switch("--delete-deployed-mcs-agent", "Also delete the Copilot Studio agent this deployment targets. MCS only."),
        AmsRoute.EnvironmentOption("AMS agent home environment, which selects the AMS route. Defaults to the active environment."),
    ];

    private static readonly AmsRouteRules AmsRules = new("deployment delete") { LocalOptions = ["--project", "--resource-group", "--subscription", "--tenant", "--delete-deployed-mcs-agent"], NeedsDeployment = true };

    /// <inheritdoc />
    public override IReadOnlyList<RouteMetadata> Routes =>
    [
        new RouteMetadata(typeof(AgentDeploymentDeleteRequest), typeof(AgentDeploymentDeleteResult)),
        new RouteMetadata(typeof(DeploymentDeleteRequest), typeof(DeploymentResult<DeploymentModel>)),
        new RouteMetadata(typeof(AgentDeploymentAmsDeleteRequest), typeof(AgentDeploymentAmsDeleteResult)),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes =>
    [
        new(UsageRoute.Deployment) { Summary = "Delete an MCS deployment" },
        new(UsageRoute.DeploymentAddress)
        {
            Summary = "Delete an Azure Bot Service deployment",
            RequiredFeatures = [FeatureName.AzureBotServiceFeatures],
        },
        AmsRoute.Usage("Delete the record of an AMS deployment", withDeployment: true),
    ];

    /// <inheritdoc />
    protected override AgentDeploymentDeleteRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new AgentDeploymentDeleteRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            DeploymentId = input.GetString("--deployment-id"),
            DeploymentName = input.GetString("--deployment-name"),
            DeleteDeployedMcsAgent = input.GetSwitch("--delete-deployed-mcs-agent"),
            NonInteractive = input.NonInteractive,
        };
    }

    /// <inheritdoc />
    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken) => AmsRoute.RouteAsync(input, services, AmsRules, () => InvokeAsync<AgentDeploymentAmsDeleteRequest, AgentDeploymentAmsDeleteResult>(services, AmsRoute.Address(input, new AgentDeploymentAmsDeleteRequest { NonInteractive = input.NonInteractive }), cancellationToken), () => DispatchLocalAsync(input, services, cancellationToken));

    private Task<object> DispatchLocalAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken)
    {
        var resolved = Resolve(input, services);
        DeploymentSelector.RequireAddressable(services, input, resolved);

        if (resolved.IsMcs)
        {
            return InvokeAsync<AgentDeploymentDeleteRequest, AgentDeploymentDeleteResult>(services, Bind(input), cancellationToken);
        }

        FeatureGate.RequireEnabled(services, FeatureName.AzureBotServiceFeatures, "CodeAgent Azure Bot Service deployment deletion");

        if (input.GetSwitch("--delete-deployed-mcs-agent"))
        {
            throw new CommandUsageException(UsageMessages.WrongAgentType("--delete-deployed-mcs-agent", "MCS", resolved.AgentType));
        }

        return InvokeAsync<DeploymentDeleteRequest, DeploymentResult<DeploymentModel>>(
            services,
            new DeploymentDeleteRequest
            {
                DeploymentId = input.GetString("--deployment-id"),
                AgentId = input.GetString("--agent-id"),
                DeploymentName = input.GetString("--deployment-name"),
                ResourceGroup = input.GetString("--resource-group"),
                SubscriptionId = input.GetString("--subscription"),
                TenantId = input.GetString("--tenant"),
                Project = input.GetString("--project"),
            },
            cancellationToken);
    }

    private static DeploymentAgentTypeResult Resolve(ICommandInput input, IServiceProvider services) =>
        DeploymentSelector.Resolve(services, DeploymentSelector.Query(input, hasMcsOptions: input.GetSwitch("--delete-deployed-mcs-agent")));
}
