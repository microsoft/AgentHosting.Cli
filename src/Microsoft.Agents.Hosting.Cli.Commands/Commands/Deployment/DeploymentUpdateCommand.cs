using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using DeploymentModel = Microsoft.Agents.Hosting.Cli.Core.Deployments.CodeAgentDeployment;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;

/// <summary><c>msagent deployment update</c>, routed on the resolved agent type.</summary>
[Command("update", Description = "Update an existing deployment.", ParentGroup = typeof(DeploymentGroup), SortPriority = 40, ProgressLabel = "Updating deployment")]
public sealed class DeploymentUpdateCommand : CommandBase<AgentDeploymentUpdateRequest, AgentDeploymentUpdateResult>
{
    private static readonly string[] AmsOptions = ["--package-revision", "--environment-id"];
    private static readonly string[] RequiredAmsOptions =
        ["--agent-id", "--deployment-id", "--package-revision"];

    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. DeploymentSelector.Symbols,
        OptionCatalog.Value("--package-revision", "Existing immutable AMS package revision id.") with { RequiredFeatures = [FeatureName.AmsFeatures] },
        OptionCatalog.Value("--environment-id", "AMS agent home environment. Defaults to the active environment.") with { RequiredFeatures = [FeatureName.AmsFeatures] },
        OptionCatalog.Value("--deployment-type", "New stage: dev, test, or prod. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.Value("--new-name", "New deployment name, unique within the agent. 4-42 characters from -, a-z, A-Z, 0-9 and _. MCS only."),
        OptionCatalog.Value("--sku", "New SKU: F0 or S1. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.Value("--display-name", "New deployment display name. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.Value("--description", "New deployment description. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.Value("--endpoint-url", "New messaging endpoint. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.OptionalSwitch("--default", "Make this the agent's default deployment. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
    ];

    /// <inheritdoc />
    public override IReadOnlyList<RouteMetadata> Routes =>
    [
        new RouteMetadata(typeof(AgentDeploymentUpdateRequest), typeof(AgentDeploymentUpdateResult)),
        new RouteMetadata(typeof(DeploymentUpdateRequest), typeof(DeploymentResult<DeploymentModel>)),
        new RouteMetadata(typeof(AgentDeploymentPackageUpdateRequest), typeof(AgentDeployResult)),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes =>
    [
        new(UsageRoute.Deployment, "--new-name") { Summary = "Rename an MCS deployment" },
        new(
            UsageRoute.DeploymentAddress,
            "(--deployment-type <deployment-type> | --sku <sku> | --display-name <display-name> | --description <description> | --endpoint-url <endpoint-url> | --default)")
        {
            Summary = "Change an Azure Bot Service deployment",
            RequiredFeatures = [FeatureName.AzureBotServiceFeatures],
        },
        new("--agent-id", "--deployment-id", "--package-revision")
        {
            Summary = "Point a deployment at a new package revision",
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
                "AMS deployment package update",
                "Remove --package-revision and --environment-id to use an available local or Azure deployment update route.");
        }
    }

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!SelectsAms(input))
        {
            return null;
        }

        string[] incompatibleOptions =
        [
            "--project",
            "--agent-name",
            "--deployment-name",
            "--resource-group",
            "--subscription",
            "--tenant",
            "--deployment-type",
            "--new-name",
            "--sku",
            "--display-name",
            "--description",
            "--endpoint-url",
            "--default",
        ];
        if (incompatibleOptions.FirstOrDefault(input.WasProvided) is { } incompatible)
        {
            return $"{incompatible} cannot be combined with an AMS deployment package update.";
        }

        return RequiredAmsOptions
            .FirstOrDefault(option => string.IsNullOrWhiteSpace(input.GetString(option))) is { } missing
                ? $"{missing} is required for an AMS deployment package update."
                : null;
    }

    /// <inheritdoc />
    protected override AgentDeploymentUpdateRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new AgentDeploymentUpdateRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            DeploymentId = input.GetString("--deployment-id"),
            DeploymentName = input.GetString("--deployment-name"),
            NewName = input.GetString("--new-name"),
            EndpointUrl = input.GetString("--endpoint-url"),
        };
    }

    /// <inheritdoc />
    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (SelectsAms(input))
        {
            return InvokeAsync<AgentDeploymentPackageUpdateRequest, AgentDeployResult>(
                services,
                new AgentDeploymentPackageUpdateRequest
                {
                    AgentId = input.GetString("--agent-id"),
                    DeploymentId = input.GetString("--deployment-id"),
                    PackageRevisionId = input.GetString("--package-revision"),
                    EnvironmentId = input.GetString("--environment-id"),
                },
                cancellationToken);
        }

        var resolved = Resolve(input, services);
        DeploymentSelector.RequireAddressable(services, input, resolved);

        if (resolved.IsMcs)
        {
            return UsageMessages.FirstSupplied(CodeAgentOnly(input)) is { } codeOnly
                ? throw new CommandUsageException(UsageMessages.WrongAgentType(codeOnly, "CodeAgent", resolved.AgentType))
                : InvokeAsync<AgentDeploymentUpdateRequest, AgentDeploymentUpdateResult>(services, Bind(input), cancellationToken);
        }

        FeatureGate.RequireEnabled(services, FeatureName.AzureBotServiceFeatures, "CodeAgent Azure Bot Service deployment update");

        if (!string.IsNullOrWhiteSpace(input.GetString("--new-name")))
        {
            throw new CommandUsageException(UsageMessages.WrongAgentType("--new-name", "MCS", resolved.AgentType));
        }

        return InvokeAsync<DeploymentUpdateRequest, DeploymentResult<DeploymentModel>>(
            services,
            new DeploymentUpdateRequest
            {
                DeploymentId = input.GetString("--deployment-id"),
                AgentId = input.GetString("--agent-id"),
                DeploymentName = input.GetString("--deployment-name"),
                DeploymentType = input.GetString("--deployment-type"),
                Sku = input.GetString("--sku"),
                DisplayName = input.GetString("--display-name"),
                Description = input.GetString("--description"),
                Endpoint = input.GetString("--endpoint-url"),
                IsDefault = input.GetOptionalSwitch("--default"),
                ResourceGroup = input.GetString("--resource-group"),
                SubscriptionId = input.GetString("--subscription"),
                TenantId = input.GetString("--tenant"),
                Project = input.GetString("--project"),
            },
            cancellationToken);
    }

    private static (string Option, bool Supplied)[] CodeAgentOnly(ICommandInput input) =>
    [
        ("--deployment-type", !string.IsNullOrWhiteSpace(input.GetString("--deployment-type"))),
        ("--sku", !string.IsNullOrWhiteSpace(input.GetString("--sku"))),
        ("--display-name", !string.IsNullOrWhiteSpace(input.GetString("--display-name"))),
        ("--description", !string.IsNullOrWhiteSpace(input.GetString("--description"))),
        ("--endpoint-url", !string.IsNullOrWhiteSpace(input.GetString("--endpoint-url"))),
        ("--default", input.GetOptionalSwitch("--default") is not null),
    ];

    private static DeploymentAgentTypeResult Resolve(ICommandInput input, IServiceProvider services) =>
        DeploymentSelector.Resolve(services, DeploymentSelector.Query(
            input,
            hasMcsOptions: !string.IsNullOrWhiteSpace(input.GetString("--new-name")),
            hasCodeAgentOptions: CodeAgentOnly(input).Any(entry => entry.Supplied)));

    private static bool SelectsAms(ICommandInput input) =>
        AmsOptions.Any(input.WasProvided);
}
