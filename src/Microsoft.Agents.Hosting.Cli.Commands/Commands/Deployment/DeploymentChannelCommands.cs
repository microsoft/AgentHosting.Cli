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

    public override IReadOnlyList<CommandExample> GetExamples(ICommandInput input) => MutationExamples(input, "create");

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

    protected override Task<DeploymentChannelCreateRequest> PrepareRequestAsync(
        DeploymentChannelCreateRequest request,
        CancellationToken cancellationToken)
    {
        return DispatchParametersAsync(
            request, (_, parameters) => Task.FromResult(request with { Parameters = parameters }), cancellationToken);
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

    public override IReadOnlyList<CommandExample> GetExamples(ICommandInput input) => MutationExamples(input, "update");

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

    protected override string? Validate(ICommandInput input)
    {
        if (input.WasProvided("--parameters"))
        {
            foreach (var item in new[] { "site", "page", "registration" })
            {
                if (input.WasProvided($"--add-{item}") || input.WasProvided($"--set-{item}") ||
                    input.WasProvided($"--remove-{item}"))
                {
                    return $"Specify only one of --parameters, --add-{item}, --set-{item}, or --remove-{item}.";
                }
            }
        }

        return null;
    }

    protected override Task<DeploymentChannelUpdateRequest> PrepareRequestAsync(
        DeploymentChannelUpdateRequest request,
        CancellationToken cancellationToken)
    {
        return DispatchParametersAsync(request, async (channelDefinition, parameters) =>
        {
            var siteFields = channelDefinition.Parameters.FirstOrDefault(field => field.Name == "sites")?.ItemParameters ?? [];
            return request with
            {
                Parameters = parameters,
                AddSite = await DeploymentParameterInput.ReadCollectionAssignmentsAsync(
                    request.AddSite, siteFields, "--add-site", cancellationToken).ConfigureAwait(false),
                SetSite = await DeploymentParameterInput.ReadCollectionAssignmentsAsync(
                    request.SetSite, siteFields, "--set-site", cancellationToken).ConfigureAwait(false),
            };
        }, cancellationToken);
    }
}

[Command("delete", Description = "Delete a channel from a deployment. CodeAgent only", ParentGroup = typeof(DeploymentChannelGroup), SortPriority = 30)]
public sealed class DeploymentChannelDeleteCommand : DeploymentChannelCommand<DeploymentChannelDeleteRequest, DeploymentResult<DeploymentChannelRecord>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => ChannelCommandSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => RoutedUsage("--channel");

    public override IReadOnlyList<CommandExample> GetExamples(ICommandInput input)
    {
        var channel = ExampleChannel(input, "msteams");
        return
        [
            new(input.WasProvided("--channel") ? $"Delete {channel} channel from a deployment." : "Delete a channel from a deployment.",
                $"msagent deployment channel delete --deployment-id <deployment-id> --channel {channel}"),
        ];
    }

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

    public override IReadOnlyList<CommandExample> GetExamples(ICommandInput input)
    {
        var channel = ExampleChannel(input, "msteams");
        return
        [
            new(input.WasProvided("--channel") ? $"Show {channel} channel as JSON." : "Show a channel as JSON.",
                $"msagent deployment channel show --deployment-id <deployment-id> --channel {channel} --json"),
        ];
    }

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

    public override IReadOnlyList<CommandExample> GetExamples(ICommandInput input) =>
    [
        new("List the supported channels on a deployment.",
            "msagent deployment channel list --deployment-id <deployment-id>"),
    ];

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

    protected static string ExampleChannel(ICommandInput input, string fallback) =>
        (input.WasProvided("--channel") ? input.GetString("--channel")?.Trim().ToLowerInvariant() : null) ?? fallback;

    protected static IReadOnlyList<CommandExample> MutationExamples(ICommandInput input, string operation)
    {
        var channel = ExampleChannel(input, "msteams");
        var definition = DeploymentChannelCatalog.FindChannelDefinition(channel, ChannelKeyType.Name);
        var command = $"msagent deployment channel {operation} --deployment-id <deployment-id> --channel {channel}";
        if (operation == "update" && channel is "directline" or "webchat" or "facebook" or "line")
        {
            var (item, collection, selector, addFields, setFields) = channel switch
            {
                "directline" => ("site", "sites", "siteName=<existing-site-name>",
                    """siteName=Primary trustedOrigins='["<trusted-origin-url>"]'""",
                    "trustedOrigins='@.\\origins.json'"),
                "webchat" => ("site", "sites", "siteName=<existing-site-name>",
                    "siteName=Primary isEnabled=true", "appId= tenantId="),
                "facebook" => ("page", "pages", "id=<page-id>",
                    "id=<page-id> accessToken=<page-token>", "accessToken=<new-page-token>"),
                _ => ("registration", "lineRegistrations", "generatedId=<registration-id>",
                    "channelSecret=<channel-secret> channelAccessToken=<channel-token>",
                    "channelAccessToken=<new-channel-token>"),
            };
            return
            [
                new($"Add a {item}.", $"{command} --add-{item} {addFields}"),
                new(channel switch
                {
                    "directline" => "Update a site's trusted origins from a JSON array file.",
                    "webchat" => "Clear an existing site's optional application and tenant IDs.",
                    _ => $"Update an existing {item}.",
                }, $"{command} --set-{item} {selector} {setFields}"),
                new(channel is "facebook" or "line" ? $"Remove a {item}; keep at least one." : $"Remove a {item}.",
                    $"{command} --remove-{item} {selector}"),
                new($"Replace {collection} from a JSON array file.",
                    $"{command} --parameters {collection}='@.\\{collection}.json'"),
                new($"Load {channel} parameters from a JSON object file.",
                    $"{command} --parameters '@.\\parameters.json'"),
            ];
        }

        var inline = operation == "create" ? channel switch
        {
            "directline" or "webchat" => """--parameters sites='[{"siteName":"Primary","isEnabled":true}]'""",
            "facebook" => """--parameters appId=<app-id> appSecret=<app-secret> pages='[{"id":"<page-id>","accessToken":"<page-token>"}]'""",
            "line" => """--parameters lineRegistrations='[{"channelSecret":"<channel-secret>","channelAccessToken":"<channel-token>"}]'""",
            "msteams" => "--parameters enableCalling=true callingWebhook=<calling-webhook-url>",
            "slack" => "--parameters clientId=<client-id> clientSecret=<client-secret> scopes=chat:write",
            "sms" => "--parameters accountSID=<account-sid> authToken=<auth-token> phone=<phone-number>",
            "telegram" => "--parameters accessToken=<access-token> isEnabled=true",
            _ => string.Empty,
        } : channel switch
        {
            "msteams" => "--parameters enableCalling=false",
            _ => "--parameters isEnabled=false",
        };
        var examples = new List<CommandExample>
        {
            new(operation == "create" ? $"Enable {channel} channel." : $"Update {channel} setting.",
                string.IsNullOrEmpty(inline) ? command : $"{command} {inline}"),
        };

        if (definition.Parameters.Any(parameter => !parameter.IsReadOnly))
        {
            examples.Add(new($"Load {channel} parameters from a JSON object file.",
                $"{command} --parameters '@.\\parameters.json'"));
        }

        foreach (var collection in definition.Parameters.Where(parameter =>
            parameter.Type == DeploymentParameterType.Array && !parameter.IsReadOnly))
        {
            var credentials = operation == "create" && channel == "facebook"
                ? " appId=<app-id> appSecret=<app-secret>" : string.Empty;
            examples.Add(new(operation == "create"
                ? $"Load {collection.Name} from a JSON array file."
                : $"Replace {collection.Name} from a JSON array file.",
                $"{command} --parameters {collection.Name}='@.\\{collection.Name}.json'{credentials}"));
        }

        if (!input.WasProvided("--channel"))
        {
            examples.Add(new("Show parameters and examples for a selected channel.",
                $"msagent deployment channel {operation} --channel <name> --help"));
        }

        return examples;
    }

    protected async Task<TRequest> DispatchParametersAsync(
        DeploymentChannelCreateRequest request,
        Func<ChannelDefinition, IReadOnlyList<string>, Task<TRequest>> prepareRequest,
        CancellationToken cancellationToken)
    {
        var channelDefinition = DeploymentChannelCatalog.FindChannelDefinition(
            DeploymentChannelCatalog.ValidateChannelName(request.ChannelName), ChannelKeyType.Name);
        var parameters = await DeploymentParameterInput.ReadCollectionAssignmentsAsync(
            request.Parameters, channelDefinition.Parameters, "--parameters", cancellationToken).ConfigureAwait(false);
        parameters = await DeploymentParameterInput.ReadParametersAsync(parameters, cancellationToken).ConfigureAwait(false);
        var preparedRequest = await prepareRequest(channelDefinition, parameters).ConfigureAwait(false);
        return preparedRequest;
    }

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
            "Channel-specific Key=Value assignments or @path to a JSON object file (null becomes Key=). " +
            "Specify --channel <name> to see that channel's parameters");

    protected static CommandOption AssignmentOption(string name, string description) =>
        OptionCatalog.Strings(name, description) with
        {
            AllowMultipleArgumentsPerToken = true,
        };

    /// <inheritdoc />
    protected override AgentDeploymentAmsChannelRequest<TRequest> BindAms(TRequest content) => new() { Channel = content };
}
