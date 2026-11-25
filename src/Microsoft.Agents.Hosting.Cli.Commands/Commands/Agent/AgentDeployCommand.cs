using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary><c>msagent agent deploy</c>, routed to an agent of the local MCS project or to an AMS code agent.</summary>
[Command("deploy", Description = "Deploy an agent to a named deployment.", ParentGroup = typeof(AgentGroup), SortPriority = 70, ProgressLabel = "Deploying agent")]
public sealed class AgentDeployCommand : CommandBase<ManagedAgentDeployRequest, ManagedAgentDeployResult>
{
    private const string MovedToDeploymentCreate = "this option moved to `msagent deployment create`.";

    private static readonly AmsRouteRules AmsRules = new("agent deploy")
    {
        MovedOptions = new Dictionary<string, string>
        {
            ["--target-environment-id"] = MovedToDeploymentCreate,
            ["--deployment-type"] = MovedToDeploymentCreate,
            ["--entra-id"] = MovedToDeploymentCreate,
            ["--entra-id-type"] = MovedToDeploymentCreate,
            ["--endpoint-url"] = "this option moved to `msagent deployment create` and `msagent deployment update`.",
            ["--package-revision"] = "use `--commit-id`; `msagent agent commit list` shows the commits.",
        },
        LocalOptions = ["--project", "--schema-name", "--publish", "--overwrite", "--async"],
        NeedsDeployment = true,
    };

    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--project", "Local MCS project directory."),
        OptionCatalog.Value("--agent-id", "Agent to deploy. Wins over --agent-name."),
        OptionCatalog.Value("--agent-name", "Agent to deploy, by name."),
        OptionCatalog.Value("--deployment-id", "Deployment to target. Wins over --deployment-name."),
        OptionCatalog.Value("--deployment-name", "Deployment to target, by name."),
        OptionCatalog.Value("--schema-name", "Schema name for this deploy, overriding the project's. MCS only."),
        OptionCatalog.Switch("--publish", "Publish after a push that fully landed, making the content live. MCS only."),
        OptionCatalog.Switch("--overwrite", "Overwrite cloud drift and shared environment variables, and replace an unverified agent. Only dev refuses drift without it. MCS only."),
        OptionCatalog.Value("--commit-id", "Commit of the code agent's uploads to deploy to a Dev or Test deployment; a dev endpoint deployment switches to it. Without it, the deployment's requested or deployed dev endpoint, else the newest upload. Not supported for MCS, which has no Git Repository Service in this build."),
        OptionCatalog.Switch("--async", "Not supported: this deploy is synchronous."),
        AmsRoute.EnvironmentOption("AMS agent home environment, which selects the AMS route. Defaults to the active environment."),
        .. AmsRules.MovedOptions.Keys.Select(option => OptionCatalog.Value(option, $"Moved for code agents: {AmsRules.MovedOptions[option]}") with { RequiredFeatures = [FeatureName.AmsFeatures] }),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<RouteMetadata> Routes =>
    [
        new RouteMetadata(typeof(ManagedAgentDeployRequest), typeof(ManagedAgentDeployResult)),
        new RouteMetadata(typeof(AgentDeployRequest), typeof(AgentDeployResult)),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes =>
    [
        new(UsageRoute.Deployment) { Summary = "Deploy an MCS project to a deployment" },
        AmsRoute.Usage("Deploy an AMS code agent to one of its deployments", withDeployment: true),
    ];

    /// <inheritdoc />
    protected override ManagedAgentDeployRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new ManagedAgentDeployRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            DeploymentId = input.GetString("--deployment-id"),
            DeploymentName = input.GetString("--deployment-name"),
            SchemaName = input.GetString("--schema-name"),
            Publish = input.GetSwitch("--publish"),
            Overwrite = input.GetSwitch("--overwrite"),
        };
    }

    /// <inheritdoc />
    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken) => AmsRoute.RouteAsync(input, services, AmsRules, () => InvokeAsync<AgentDeployRequest, AgentDeployResult>(services, AmsRoute.Address(input, new AgentDeployRequest { CommitId = input.GetString("--commit-id") }), cancellationToken), () => DispatchLocalAsync(input, services, cancellationToken));

    private Task<object> DispatchLocalAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(input.GetString("--commit-id")))
        {
            throw new CommandUsageException("--commit-id is not supported: this build has no Git Repository Service, so deploy always publishes the current working tree. Remove --commit-id.");
        }

        return input.GetSwitch("--async")
            ? throw new CommandUsageException("--async is not supported: this deploy is synchronous and returns when the push completes. There is no operation to poll. Remove --async.")
            : InvokeAsync<ManagedAgentDeployRequest, ManagedAgentDeployResult>(services, Bind(input), cancellationToken);
    }
}
