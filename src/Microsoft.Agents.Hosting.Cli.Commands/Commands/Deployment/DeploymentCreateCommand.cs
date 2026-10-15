using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using DeploymentModel = Microsoft.Agents.Hosting.Cli.Core.Deployments.CodeAgentDeployment;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;

/// <summary><c>msagent deployment create</c>, routed on the resolved agent type.</summary>
[Command("create", Description = "Create a deployment.", ParentGroup = typeof(DeploymentGroup), SortPriority = 10)]
public sealed class DeploymentCreateCommand : CommandBase<AgentDeploymentCreateRequest, AgentDeploymentCreateResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--project", "Project directory holding the agent. Defaults to the current directory. MCS only until the stores converge."),
        OptionCatalog.Value("--agent-id", "Owning agent id. Defaults to the project's agent for an MCS deployment."),
        OptionCatalog.Value("--agent-name", "Owning agent name, used when the agent id is omitted. MCS only."),
        OptionCatalog.Value("--deployment-name", "Deployment name, unique within the agent. 4-42 characters from -, a-z, A-Z, 0-9 and _."),
        OptionCatalog.Value("--deployment-type", "Stage: dev, test, or prod."),
        OptionCatalog.Value("--environment-id", "Target environment. Required for test and prod, and defaults to the agent's home environment. MCS only."),
        OptionCatalog.Value("--entra-id", "Entra application (client) id the deployment binds to. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.Value("--entra-id-type", "Entra identity kind: appReg, blueprint, or default. Only appReg is supported. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.Value("--sku", "Deployment SKU: F0 or S1. Defaults to F0. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.Value("--location", "Azure region. Defaults to global. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.Value("--resource-group", "Target resource group. Auto-resolved when the subscription has exactly one. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.Value("--subscription", "Target subscription id. Auto-resolved from the signed-in account when omitted. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.Value("--tenant", "Target tenant id. Taken from the resolved subscription when omitted. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.Value("--endpoint-url", "Messaging endpoint. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.Value("--display-name", "Deployment display name. Defaults to the deployment name. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.Value("--description", "Deployment description. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
        OptionCatalog.Switch("--default", "Make this the agent's default deployment. CodeAgent only.") with { RequiredFeatures = [FeatureName.AzureBotServiceFeatures] },
    ];

    /// <inheritdoc />
    public override IReadOnlyList<RouteMetadata> Routes =>
    [
        new RouteMetadata(typeof(AgentDeploymentCreateRequest), typeof(AgentDeploymentCreateResult)),
        new RouteMetadata(typeof(DeploymentCreateRequest), typeof(DeploymentResult<DeploymentModel>)),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes =>
    [
        new(UsageRoute.Agent, "--deployment-name", "--deployment-type") { Summary = "Record an MCS deployment" },
        new("--agent-id", "--deployment-name", "--deployment-type", "--entra-id", "--entra-id-type")
        {
            Summary = "Provision an Azure Bot Service deployment",
            RequiredFeatures = [FeatureName.AzureBotServiceFeatures],
        },
    ];

    /// <inheritdoc />
    protected override AgentDeploymentCreateRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new AgentDeploymentCreateRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            DeploymentType = input.GetString("--deployment-type")!,
            DeploymentName = input.GetString("--deployment-name"),
            EnvironmentId = input.GetString("--environment-id"),
        };
    }

    /// <inheritdoc />
    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var resolved = Resolve(input, services);

        DeploymentSelector.RequireIdentified(
            resolved,
            DeploymentSelector.SuppliedSelectors(input, "--agent-id", "--agent-name"),
            "--agent-id or --agent-name",
            "msagent agent show");

        if (resolved.IsMcs)
        {
            if (UsageMessages.FirstSupplied(CodeAgentOnly(input)) is { } codeOnly)
            {
                throw new CommandUsageException(UsageMessages.WrongAgentType(codeOnly, "CodeAgent", resolved.AgentType));
            }

            return UsageMessages.RequiredOptions(
                ("--deployment-name", input.GetString("--deployment-name")),
                ("--deployment-type", input.GetString("--deployment-type"))) is { } mcsMissing
                ? throw new CommandUsageException(mcsMissing)
                : InvokeAsync<AgentDeploymentCreateRequest, AgentDeploymentCreateResult>(services, Bind(input), cancellationToken);
        }

        FeatureGate.RequireEnabled(services, FeatureName.AzureBotServiceFeatures, "CodeAgent Azure Bot Service deployment creation");

        if (UsageMessages.FirstSupplied(McsOnly(input)) is { } mcsOnly)
        {
            throw new CommandUsageException(UsageMessages.WrongAgentType(mcsOnly, "MCS", resolved.AgentType));
        }

        if (UsageMessages.RequiredOptions(
            ("--agent-id", input.GetString("--agent-id")),
            ("--deployment-name", input.GetString("--deployment-name")),
            ("--deployment-type", input.GetString("--deployment-type")),
            ("--entra-id", input.GetString("--entra-id")),
            ("--entra-id-type", input.GetString("--entra-id-type"))) is { } codeAgentMissing)
        {
            throw new CommandUsageException(codeAgentMissing);
        }

        return InvokeAsync<DeploymentCreateRequest, DeploymentResult<DeploymentModel>>(
            services,
            new DeploymentCreateRequest
            {
                AgentId = input.GetString("--agent-id")!,
                DeploymentName = input.GetString("--deployment-name")!,
                EntraId = input.GetString("--entra-id")!,
                EntraIdType = input.GetString("--entra-id-type")!,
                DeploymentType = input.GetString("--deployment-type")!,
                IsDefault = input.GetSwitch("--default"),
                ResourceGroup = input.GetString("--resource-group"),
                SubscriptionId = input.GetString("--subscription"),
                TenantId = input.GetString("--tenant"),
                Sku = input.GetString("--sku"),
                Location = input.GetString("--location"),
                DisplayName = input.GetString("--display-name"),
                Description = input.GetString("--description"),
                Endpoint = input.GetString("--endpoint-url"),
                Project = input.GetString("--project"),
            },
            cancellationToken);
    }

    private static (string Option, bool Supplied)[] CodeAgentOnly(ICommandInput input) =>
    [
        ("--entra-id", !string.IsNullOrWhiteSpace(input.GetString("--entra-id"))),
        ("--entra-id-type", !string.IsNullOrWhiteSpace(input.GetString("--entra-id-type"))),
        ("--sku", !string.IsNullOrWhiteSpace(input.GetString("--sku"))),
        ("--location", !string.IsNullOrWhiteSpace(input.GetString("--location"))),
        ("--resource-group", !string.IsNullOrWhiteSpace(input.GetString("--resource-group"))),
        ("--subscription", !string.IsNullOrWhiteSpace(input.GetString("--subscription"))),
        ("--tenant", !string.IsNullOrWhiteSpace(input.GetString("--tenant"))),
        ("--display-name", !string.IsNullOrWhiteSpace(input.GetString("--display-name"))),
        ("--description", !string.IsNullOrWhiteSpace(input.GetString("--description"))),
        ("--endpoint-url", !string.IsNullOrWhiteSpace(input.GetString("--endpoint-url"))),
        ("--default", input.GetSwitch("--default")),
    ];

    private static (string Option, bool Supplied)[] McsOnly(ICommandInput input) =>
    [
        ("--environment-id", !string.IsNullOrWhiteSpace(input.GetString("--environment-id"))),
        ("--agent-name", !string.IsNullOrWhiteSpace(input.GetString("--agent-name"))),
    ];

    private static DeploymentAgentTypeResult Resolve(ICommandInput input, IServiceProvider services) =>
        DeploymentSelector.Resolve(services, new DeploymentAgentTypeQuery
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            DeploymentName = input.GetString("--deployment-name"),
            HasMcsOptions = McsOnly(input).Any(entry => entry.Supplied),
            HasCodeAgentOptions = CodeAgentOnly(input).Any(entry => entry.Supplied),
        });
}
