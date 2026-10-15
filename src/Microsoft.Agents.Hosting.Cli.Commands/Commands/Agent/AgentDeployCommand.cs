using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary><c>msagent agent deploy</c>, routed on whether a deployment is named.</summary>
[Command("deploy", Description = "Deploy an agent to a named deployment.", ParentGroup = typeof(AgentGroup), SortPriority = 70)]
public sealed class AgentDeployCommand : CommandBase<ManagedAgentDeployRequest, ManagedAgentDeployResult>
{
    private static readonly string[] AmsOptions =
        ["--package-revision", "--environment-id", "--target-environment-id", "--deployment-type", "--entra-id", "--entra-id-type"];

    private static readonly string[] RequiredAmsOptions =
        ["--agent-id", "--package-revision", "--target-environment-id", "--deployment-name", "--deployment-type", "--entra-id", "--entra-id-type"];

    private static readonly string[] McsSelectorOptions = ["--agent-id", "--agent-name", "--schema-name", "--publish", "--overwrite"];

    private static readonly string[] McsOnlyOptions =
        ["--project", "--agent-name", "--deployment-id", "--schema-name", "--publish", "--overwrite", "--commit-id", "--async"];
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--project", "Local MCS project directory."),
        OptionCatalog.Value("--agent-id", "Agent to deploy."),
        OptionCatalog.Value("--agent-name", "Record to deploy, by name. MCS only."),
        OptionCatalog.Value("--deployment-id", "Deployment to target. Wins over --deployment-name. MCS only."),
        OptionCatalog.Value("--deployment-name", "Deployment to target, by name."),
        OptionCatalog.Value("--schema-name", "Schema name for this deploy, overriding the project's. MCS only."),
        OptionCatalog.Switch("--publish", "Publish after a push that fully landed, making the content live. MCS only."),
        OptionCatalog.Switch("--overwrite", "Overwrite cloud drift and replace an unverified agent. Only dev refuses drift without it. MCS only."),
        OptionCatalog.Value("--commit-id", "Not supported: this build has no Git Repository Service."),
        OptionCatalog.Switch("--async", "Not supported: this deploy is synchronous."),
        OptionCatalog.Value("--package-revision", "Existing immutable AMS package revision id. No package bytes are uploaded.") with { RequiredFeatures = [FeatureName.AmsFeatures] },
        OptionCatalog.Value("--environment-id", "AMS agent home environment. Defaults to the active environment.") with { RequiredFeatures = [FeatureName.AmsFeatures] },
        OptionCatalog.Value("--target-environment-id", "Explicit AMS target environment, including for same-home deployment.") with { RequiredFeatures = [FeatureName.AmsFeatures] },
        OptionCatalog.Value("--deployment-type", "AMS deployment stage: dev, test, or prod.") with { RequiredFeatures = [FeatureName.AmsFeatures] },
        OptionCatalog.Value("--entra-id", "AMS deployment Entra application id.") with { RequiredFeatures = [FeatureName.AmsFeatures] },
        OptionCatalog.Value("--entra-id-type", "AMS identity kind: app-reg, blueprint, or default.") with { RequiredFeatures = [FeatureName.AmsFeatures] },
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
        new("--agent-id", "--package-revision", "--target-environment-id", "--deployment-name", "--deployment-type", "--entra-id", "--entra-id-type")
        {
            Summary = "Deploy an AMS package revision",
            RequiredFeatures = [FeatureName.AmsFeatures],
        },
    ];

    /// <inheritdoc />
    protected override void EnsureAvailable(ICommandInput input, IServiceProvider services)
    {
        if (SelectsAms(input))
        {
            FeatureGate.RequireEnabled(
                services,
                FeatureName.AmsFeatures,
                "AMS package-revision deployment",
                "Use --deployment-id or --deployment-name for the available MCS route.");
        }
    }

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(input);
        var amsEnabled = FeatureGate.IsEnabled(services, FeatureName.AmsFeatures);

        if (SelectsAms(input))
        {
            return ValidateAms(input);
        }

        if (!string.IsNullOrWhiteSpace(input.GetString("--commit-id")))
        {
            return "--commit-id is not supported: this build has no Git Repository Service, so deploy always publishes the current working tree. Remove --commit-id.";
        }

        if (input.GetSwitch("--async"))
        {
            return "--async is not supported: this deploy is synchronous and returns when the push completes. There is no operation to poll. Remove --async.";
        }

        if (SelectsDeployment(input))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(input.GetString("--project")))
        {
            return amsEnabled
                ? "`msagent agent deploy` requires --deployment-id or --deployment-name for an MCS agent, or --agent, --package-revision and explicit deployment inputs for AMS. There is no active-environment fallback: deploying to an unnamed target is the mistake deployments exist to prevent."
                : "`msagent agent deploy` requires --deployment-id or --deployment-name for an MCS agent. There is no active-environment fallback: deploying to an unnamed target is the mistake deployments exist to prevent.";
        }

        return McsSelectorOptions.FirstOrDefault(option => Supplied(input, option)) is { } conflicting
            ? $"{conflicting} applies to MCS agents only. Add --deployment-id or --deployment-name to deploy the MCS project in that directory."
            : amsEnabled
                ? "CodeAgent package bytes must first be uploaded with `msagent agent package upload`, then deployed with --agent, --package-revision and explicit deployment inputs. --project alone does not deploy them."
                : "--project must identify an MCS deployment with --deployment-id or --deployment-name.";
    }

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
    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (SelectsAms(input))
        {
            return InvokeAsync<AgentDeployRequest, AgentDeployResult>(services, BindAms(input), cancellationToken);
        }

        return InvokeAsync<ManagedAgentDeployRequest, ManagedAgentDeployResult>(services, Bind(input), cancellationToken);
    }

    private static AgentDeployRequest BindAms(ICommandInput input) => new()
    {
        AgentId = input.GetString("--agent-id"),
        PackageRevisionId = input.GetString("--package-revision"),
        EnvironmentId = input.GetString("--environment-id"),
        TargetEnvironmentId = input.GetString("--target-environment-id")!,
        DeploymentName = input.GetString("--deployment-name"),
        DeploymentType = input.GetString("--deployment-type"),
        EntraId = input.GetString("--entra-id"),
        EntraIdType = input.GetString("--entra-id-type"),
    };

    /// <summary>Reports the first AMS rule the input breaks, or null when the AMS route is well formed.</summary>
    private static string? ValidateAms(ICommandInput input)
    {
        if (McsOnlyOptions.FirstOrDefault(option => Supplied(input, option)) is { } conflicting)
        {
            return $"{conflicting} cannot be combined with AMS package-revision deployment inputs. MCS deployment and AMS deployment are separate operations.";
        }

        return RequiredAmsOptions.FirstOrDefault(option => string.IsNullOrWhiteSpace(input.GetString(option))) is { } missing
            ? $"{missing} is required for an AMS package-revision deploy."
            : null;
    }

    private static bool SelectsAms(ICommandInput input) => AmsOptions.Any(option => Supplied(input, option));

    private static bool Supplied(ICommandInput input, string option) => input.WasProvided(option);

    private static bool SelectsDeployment(ICommandInput input) =>
        !string.IsNullOrWhiteSpace(input.GetString("--deployment-id"))
        || !string.IsNullOrWhiteSpace(input.GetString("--deployment-name"));
}
