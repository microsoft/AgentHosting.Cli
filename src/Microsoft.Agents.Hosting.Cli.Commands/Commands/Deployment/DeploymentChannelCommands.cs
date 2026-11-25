using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment.Channel;

[Command("create", Description = "Create (enable) a channel on a deployment. CodeAgent only", ParentGroup = typeof(DeploymentChannelGroup), SortPriority = 10)]
public sealed class DeploymentChannelCreateCommand : DeploymentChannelCommand<DeploymentChannelCreateRequest, DeploymentResult<DeploymentChannelRecord>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => MutationCommandSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => RoutedUsage("--channel");

    protected override DeploymentChannelCreateRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var deploymentRequest = BindDeploymentRequest(input);
        return new DeploymentChannelCreateRequest
        {
            Project = deploymentRequest.Project,
            AgentId = deploymentRequest.AgentId,
            DeploymentId = deploymentRequest.DeploymentId,
            DeploymentName = deploymentRequest.DeploymentName,
            ChannelName = input.GetString("--channel")!,
            Parameters = input.GetStrings("--parameters"),
            NonInteractive = input.NonInteractive,
        };
    }
}

[Command("update", Description = "Update a channel on a deployment. CodeAgent only", ParentGroup = typeof(DeploymentChannelGroup), SortPriority = 20)]
public sealed class DeploymentChannelUpdateCommand : DeploymentChannelCommand<DeploymentChannelUpdateRequest, DeploymentResult<DeploymentChannelRecord>>
{
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. MutationCommandSymbols,
        AssignmentOption("--add-site", "Add a Direct Line or Web Chat site using Key=Value fields."),
        AssignmentOption("--set-site", "Update a Direct Line or Web Chat site using Key=Value fields."),
        AssignmentOption("--remove-site", "Remove a Direct Line or Web Chat site using `siteName` as a selector."),
        AssignmentOption("--add-page", "Add a Facebook page using Key=Value fields."),
        AssignmentOption("--set-page", "Update a Facebook page using Key=Value fields."),
        AssignmentOption("--remove-page", "Remove a Facebook page using `id` as a selector."),
        AssignmentOption("--add-registration", "Add a LINE registration using Key=Value fields."),
        AssignmentOption("--set-registration", "Update a LINE registration using Key=Value fields."),
        AssignmentOption("--remove-registration", "Remove a LINE registration using `generatedId` as a selector."),
    ];

    public override IReadOnlyList<UsageRoute> UsageRoutes => RoutedUsage("--channel");

    protected override DeploymentChannelUpdateRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var deploymentRequest = BindDeploymentRequest(input);
        return new DeploymentChannelUpdateRequest
        {
            Project = deploymentRequest.Project,
            AgentId = deploymentRequest.AgentId,
            DeploymentId = deploymentRequest.DeploymentId,
            DeploymentName = deploymentRequest.DeploymentName,
            ChannelName = input.GetString("--channel")!,
            Parameters = input.GetStrings("--parameters"),
            NonInteractive = input.NonInteractive,
            AddSite = input.GetStrings("--add-site"),
            SetSite = input.GetStrings("--set-site"),
            RemoveSite = input.GetStrings("--remove-site"),
            AddPage = input.GetStrings("--add-page"),
            SetPage = input.GetStrings("--set-page"),
            RemovePage = input.GetStrings("--remove-page"),
            AddRegistration = input.GetStrings("--add-registration"),
            SetRegistration = input.GetStrings("--set-registration"),
            RemoveRegistration = input.GetStrings("--remove-registration"),
        };
    }
}

[Command("delete", Description = "Delete a channel from a deployment. CodeAgent only", ParentGroup = typeof(DeploymentChannelGroup), SortPriority = 30)]
public sealed class DeploymentChannelDeleteCommand : DeploymentChannelCommand<DeploymentChannelDeleteRequest, DeploymentResult<DeploymentChannelRecord>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => ChannelCommandSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => RoutedUsage("--channel");

    protected override DeploymentChannelDeleteRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var deploymentRequest = BindDeploymentRequest(input);
        return new DeploymentChannelDeleteRequest
        {
            Project = deploymentRequest.Project,
            AgentId = deploymentRequest.AgentId,
            DeploymentId = deploymentRequest.DeploymentId,
            DeploymentName = deploymentRequest.DeploymentName,
            ChannelName = input.GetString("--channel")!,
        };
    }
}

[Command("show", Description = "Show details of a channel on a deployment. CodeAgent only", ParentGroup = typeof(DeploymentChannelGroup), SortPriority = 40)]
public sealed class DeploymentChannelShowCommand : DeploymentChannelCommand<DeploymentChannelShowRequest, DeploymentResult<DeploymentChannelRecord>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => ChannelCommandSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => RoutedUsage("--channel");

    protected override DeploymentChannelShowRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var deploymentRequest = BindDeploymentRequest(input);
        return new DeploymentChannelShowRequest
        {
            Project = deploymentRequest.Project,
            AgentId = deploymentRequest.AgentId,
            DeploymentId = deploymentRequest.DeploymentId,
            DeploymentName = deploymentRequest.DeploymentName,
            ChannelName = input.GetString("--channel")!,
        };
    }
}

[Command("list", Description = "List all channels on a deployment. CodeAgent only", ParentGroup = typeof(DeploymentChannelGroup), SortPriority = 50)]
public sealed class DeploymentChannelListCommand : DeploymentChannelCommand<DeploymentChannelListRequest, DeploymentResult<DeploymentChannelRecord[]>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => ChannelAddressSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => RoutedUsage();

    protected override DeploymentChannelListRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var deploymentRequest = BindDeploymentRequest(input);
        return new DeploymentChannelListRequest
        {
            Project = deploymentRequest.Project,
            AgentId = deploymentRequest.AgentId,
            DeploymentId = deploymentRequest.DeploymentId,
            DeploymentName = deploymentRequest.DeploymentName,
        };
    }
}

/// <summary><c>msagent deployment channel types</c>, which lists the channel types AMS can create and edit on a deployment.</summary>
[Command("types", Description = "List the channel types AMS can create and edit on a deployment. AMS only", ParentGroup = typeof(DeploymentChannelGroup), SortPriority = 60)]
[RequiredFeatureFlag(FeatureName.AmsFeatures)]
public sealed class DeploymentChannelTypesCommand : CommandBase<AgentDeploymentAmsChannelTypesRequest, DeploymentResult<DeploymentChannelType[]>>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols => AmsRoute.AddressSymbols(withDeployment: true);

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes => [AmsRoute.Usage("List the channel types of an AMS deployment", withDeployment: true)];

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input) => AmsRoute.Problem(input, new AmsRouteRules("deployment channel types") { NeedsDeployment = true });

    /// <inheritdoc />
    protected override AgentDeploymentAmsChannelTypesRequest Bind(ICommandInput input) => AmsRoute.Address(input, new AgentDeploymentAmsChannelTypesRequest());
}

/// <summary><c>msagent deployment channel token</c>, which gets a Direct Line token from AMS to start a conversation with the agent.</summary>
[Command("token", Description = "Get a Direct Line token to start a conversation with the agent. AMS only", ParentGroup = typeof(DeploymentChannelGroup), SortPriority = 70)]
[RequiredFeatureFlag(FeatureName.AmsFeatures)]
public sealed class DeploymentChannelTokenCommand : CommandBase<AgentDeploymentAmsChannelTokenRequest, DeploymentResult<DeploymentChannelToken>>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. AmsRoute.AddressSymbols(withDeployment: true),
        new CommandOption("--channel", "The channel that issues the token. Supported: directline.") { IsRequired = true },
        OptionCatalog.Value("--site", "The Direct Line site that issues the token. Defaults to the only enabled site that accepts Direct Line 3.0 clients."),
        OptionCatalog.Value("--user-id", "The user the token is bound to. Must start with dl_."),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes => [AmsRoute.Usage("Get a Direct Line token for an AMS deployment", withDeployment: true, "--channel")];

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input) => AmsRoute.Problem(input, new AmsRouteRules("deployment channel token") { NeedsDeployment = true });

    /// <inheritdoc />
    protected override AgentDeploymentAmsChannelTokenRequest Bind(ICommandInput input) => AmsRoute.Address(input, new AgentDeploymentAmsChannelTokenRequest { ChannelName = input.GetString("--channel"), SiteName = input.GetString("--site"), UserId = input.GetString("--user-id") });
}

/// <summary><c>msagent deployment channel webhook</c>, which gets from AMS the callback URL and verify token that a channel's webhook settings in the third-party console need.</summary>
[Command("webhook", Description = "Get the callback URL and verify token for a channel's webhook settings. AMS only", ParentGroup = typeof(DeploymentChannelGroup), SortPriority = 80)]
[RequiredFeatureFlag(FeatureName.AmsFeatures)]
public sealed class DeploymentChannelWebhookCommand : CommandBase<AgentDeploymentAmsChannelWebhookRequest, DeploymentResult<DeploymentChannelWebhook>>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. AmsRoute.AddressSymbols(withDeployment: true),
        new CommandOption("--channel", "The channel whose webhook values to get. Supported: facebook.") { IsRequired = true },
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes => [AmsRoute.Usage("Get the webhook values of a channel of an AMS deployment", withDeployment: true, "--channel")];

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input) => AmsRoute.Problem(input, new AmsRouteRules("deployment channel webhook") { NeedsDeployment = true });

    /// <inheritdoc />
    protected override AgentDeploymentAmsChannelWebhookRequest Bind(ICommandInput input) => AmsRoute.Address(input, new AgentDeploymentAmsChannelWebhookRequest { ChannelName = input.GetString("--channel") });
}

public abstract class DeploymentChannelCommand<TRequest, TResult> : AmsRoutedDeploymentCommand<TRequest, TResult, AgentDeploymentAmsChannelRequest<TRequest>>
    where TRequest : DeploymentRequest
    where TResult : notnull
{
    protected override string CommandPath => "msagent deployment channel";

    protected static IReadOnlyList<CommandSymbol> ChannelAddressSymbols =>
    [
        .. RoutedAddressSymbols("AMS agent home environment, which selects the AMS route. Defaults to the active environment."),
    ];

    protected static IReadOnlyList<CommandSymbol> ChannelCommandSymbols =>
    [
        .. ChannelAddressSymbols,
        ChannelName,
    ];

    protected static IReadOnlyList<CommandSymbol> MutationCommandSymbols =>
    [
        .. ChannelCommandSymbols,
        Parameters,
    ];

    private static CommandOption ChannelName =>
        new(
            "--channel",
            $"The channel type. Supported: {string.Join(", ", DeploymentChannelCatalog.SupportedChannelNames)}.")
        {
            IsRequired = true,
        };

    private static CommandOption Parameters =>
        AssignmentOption(
            "--parameters",
            "Channel-specific Key=Value assignments. " +
            "Specify --channel <name> to see that channel's parameters");

    protected static CommandOption AssignmentOption(string name, string description) =>
        OptionCatalog.Strings(name, description) with
        {
            AllowMultipleArgumentsPerToken = true,
        };

    /// <inheritdoc />
    protected override AgentDeploymentAmsChannelRequest<TRequest> BindAms(TRequest content) => new() { Channel = content };
}
