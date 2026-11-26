using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using DeploymentModel = Microsoft.Agents.Hosting.Cli.Core.Deployments.CodeAgentDeployment;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;

/// <summary><c>msagent deployment show</c>, routed to a deployment of the local project or to AMS.</summary>
[Command("show", Description = "Get an existing deployment.", ParentGroup = typeof(DeploymentGroup), SortPriority = 30)]
public sealed class DeploymentShowCommand : CommandBase<AgentDeploymentShowRequest, AgentDeploymentShowResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. DeploymentSelector.Symbols,
        AmsRoute.EnvironmentOption("AMS agent home environment, which selects the AMS route. Defaults to the active environment."),
    ];

    private static readonly AmsRouteRules AmsRules = new("deployment show") { LocalOptions = ["--project", "--resource-group", "--subscription", "--tenant"], NeedsDeployment = true };

    /// <inheritdoc />
    public override IReadOnlyList<RouteMetadata> Routes =>
    [
        new RouteMetadata(typeof(AgentDeploymentShowRequest), typeof(AgentDeploymentShowResult)),
        new RouteMetadata(typeof(DeploymentShowRequest), typeof(DeploymentResult<DeploymentModel>)),
        new RouteMetadata(typeof(AgentDeploymentAmsShowRequest), typeof(AgentDeploymentAmsResult)),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes =>
    [
        new(UsageRoute.Deployment) { Summary = "Get an MCS deployment" },
        new(UsageRoute.DeploymentAddress)
        {
            Summary = "Get an Azure Bot Service deployment",
            RequiredFeatures = [FeatureName.AzureBotServiceFeatures],
        },
        AmsRoute.Usage("Get an AMS deployment", withDeployment: true),
    ];

    /// <inheritdoc />
    protected override AgentDeploymentShowRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new AgentDeploymentShowRequest
        {
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            DeploymentId = input.GetString("--deployment-id"),
            DeploymentName = input.GetString("--deployment-name"),
            Project = input.GetString("--project"),
        };
    }

    /// <inheritdoc />
    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken) => AmsRoute.RouteAsync(input, services, AmsRules, () => InvokeAsync<AgentDeploymentAmsShowRequest, AgentDeploymentAmsResult>(services, AmsRoute.Address(input, new AgentDeploymentAmsShowRequest()), cancellationToken), () => DispatchLocalAsync(input, services, cancellationToken));

    private Task<object> DispatchLocalAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken)
    {
        var resolved = DeploymentSelector.Resolve(services, DeploymentSelector.Query(input));
        DeploymentSelector.RequireAddressable(services, input, resolved);

        if (resolved.IsMcs)
        {
            return InvokeAsync<AgentDeploymentShowRequest, AgentDeploymentShowResult>(services, Bind(input), cancellationToken);
        }

        FeatureGate.RequireEnabled(services, FeatureName.AzureBotServiceFeatures, "CodeAgent Azure Bot Service deployment inspection");
        return InvokeAsync<DeploymentShowRequest, DeploymentResult<DeploymentModel>>(
            services,
            new DeploymentShowRequest
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
}
