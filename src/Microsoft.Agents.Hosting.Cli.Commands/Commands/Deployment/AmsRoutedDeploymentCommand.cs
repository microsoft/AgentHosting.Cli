using System.Reflection;
using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;

/// <summary>A deployment command that serves a CodeAgent deployment of the local project, or the AMS deployment the route chooser picks.</summary>
public abstract class AmsRoutedDeploymentCommand<TRequest, TResult, TAmsRequest> : CodeAgentDeploymentCommand<TRequest, TResult>
    where TRequest : DeploymentRequest
    where TResult : notnull
    where TAmsRequest : AgentDeploymentAmsRequest
{
    /// <inheritdoc />
    public override IReadOnlyList<RouteMetadata> Routes => [new(typeof(TRequest), typeof(TResult)), new(typeof(TAmsRequest), typeof(TResult))];

    /// <summary>Gets the rules that choose and check the AMS route, where the agent name selects AMS because the local route takes none.</summary>
    protected AmsRouteRules AmsRules => new($"{CommandPath["msagent ".Length..]} {GetType().GetCustomAttribute<CommandAttribute>()!.Name}") { SelectingOptions = ["--environment-id", "--agent-name"], LocalOptions = ["--project"], NeedsDeployment = true };

    /// <summary>Builds the deployment selectors of both routes with the AMS agent name and home environment.</summary>
    protected static IReadOnlyList<CommandSymbol> RoutedAddressSymbols(string environmentDescription) => [.. DeploymentAddressSymbols, OptionCatalog.Value("--agent-name", "The owning agent name. AMS only.") with { RequiredFeatures = [FeatureName.AmsFeatures] }, AmsRoute.EnvironmentOption(environmentDescription)];

    /// <summary>Builds the usage lines of a deployment recorded in the project and of an AMS deployment, each with the given required options.</summary>
    protected static IReadOnlyList<UsageRoute> RoutedUsage(params string[] requiredOptions) => [new([UsageRoute.DeploymentAddress, .. requiredOptions]) { Summary = "Use a deployment recorded in the project" }, AmsRoute.Usage("Use an AMS deployment", withDeployment: true, requiredOptions)];

    /// <summary>Wraps the bound content in the AMS request, whose address the route fills in.</summary>
    protected abstract TAmsRequest BindAms(TRequest content);

    /// <inheritdoc />
    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        return AmsRoute.RouteAsync(input, services, AmsRules, () => InvokeAsync<TAmsRequest, TResult>(services, AmsRoute.Address(input, BindAms(Bind(input))), cancellationToken), () => DispatchLocalAsync(input, services, cancellationToken));
    }

    private Task<object> DispatchLocalAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken)
    {
        EnsureCodeAgentDeployment(BindDeploymentRequest(input), services);
        return base.DispatchAsync(input, services, cancellationToken);
    }
}
