using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment.OAuth;

[Command("create", Description = "Create (enable) an OAuth connection setting on a deployment. CodeAgent only.", ParentGroup = typeof(DeploymentOAuthGroup))]
public sealed class DeploymentOAuthCreateCommand :
    DeploymentOAuthCommand<DeploymentOAuthCreateRequest, DeploymentResult<DeploymentOAuthConnection>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => CreateCommandSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => RoutedUsage("--connection", "--provider-name");

    public override IReadOnlyList<CommandExample> GetExamples(ICommandInput input) =>
    [
        new("Inspect the provider's parameters.",
            "msagent deployment oauth list-providers --provider-name Aadv2"),
        new("Create a Microsoft Entra ID v2 connection with inline provider parameters.",
            "msagent deployment oauth create --deployment-id <deployment-id> --connection MyOAuth --provider-name Aadv2 --client-id <app-id> --client-secret <secret> --scopes \"User.Read\" --parameters TenantId=<tenant-id>"),
        new("Load provider-specific parameters from a JSON object file.",
            "msagent deployment oauth create --deployment-id <deployment-id> --connection MyOAuth --provider-name Aadv2 --client-id <app-id> --client-secret <secret> --scopes \"User.Read\" --parameters '@.\\parameters.json'"),
    ];

    protected override DeploymentOAuthCreateRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var deploymentRequest = BindDeploymentRequest(input);
        return new DeploymentOAuthCreateRequest
        {
            Project = deploymentRequest.Project,
            AgentId = deploymentRequest.AgentId,
            DeploymentId = deploymentRequest.DeploymentId,
            DeploymentName = deploymentRequest.DeploymentName,
            ConnectionName = input.GetString("--connection")!,
            ServiceProviderName = input.GetString("--provider-name")!,
            ClientId = input.GetString("--client-id"),
            ClientSecret = input.GetString("--client-secret"),
            ClientSecretFromStandardInput = input.GetSwitch("--client-secret-stdin"),
            Scopes = input.GetString("--scopes"),
            Parameters = input.GetStrings("--parameters"),
        };
    }

    protected override Task<DeploymentOAuthCreateRequest> PrepareRequestAsync(
        DeploymentOAuthCreateRequest request,
        CancellationToken cancellationToken)
    {
        return DispatchParametersAsync(request, cancellationToken);
    }
}

[Command("update", Description = "Update an OAuth connection setting on a deployment. CodeAgent only.", ParentGroup = typeof(DeploymentOAuthGroup))]
public sealed class DeploymentOAuthUpdateCommand :
    DeploymentOAuthCommand<DeploymentOAuthUpdateRequest, DeploymentResult<DeploymentOAuthConnection>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => UpdateCommandSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => RoutedUsage("--connection");

    public override IReadOnlyList<CommandExample> GetExamples(ICommandInput input) =>
    [
        new("Update Microsoft Graph scopes.",
            "msagent deployment oauth update --deployment-id <deployment-id> --connection <connection-name> --scopes \"User.Read Mail.Read\""),
        new("Combine a JSON parameter file with an inline assignment.",
            "msagent deployment oauth update --deployment-id <deployment-id> --connection <connection-name> --parameters '@.\\parameters.json' TenantId=<tenant-id>"),
        new("Clear an optional provider parameter.",
            "msagent deployment oauth update --deployment-id <deployment-id> --connection <connection-name> --parameters TokenExchangeUrl="),
    ];

    protected override DeploymentOAuthUpdateRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var deploymentRequest = BindDeploymentRequest(input);
        return new DeploymentOAuthUpdateRequest
        {
            Project = deploymentRequest.Project,
            AgentId = deploymentRequest.AgentId,
            DeploymentId = deploymentRequest.DeploymentId,
            DeploymentName = deploymentRequest.DeploymentName,
            ConnectionName = input.GetString("--connection")!,
            ClientId = input.GetString("--client-id"),
            ClientSecret = input.GetString("--client-secret"),
            ClientSecretFromStandardInput = input.GetSwitch("--client-secret-stdin"),
            Scopes = input.GetString("--scopes"),
            Parameters = input.GetStrings("--parameters"),
        };
    }

    protected override Task<DeploymentOAuthUpdateRequest> PrepareRequestAsync(
        DeploymentOAuthUpdateRequest request,
        CancellationToken cancellationToken)
    {
        return DispatchParametersAsync(request, cancellationToken);
    }
}

[Command("delete", Description = "Delete an OAuth connection setting on a deployment. CodeAgent only.", ParentGroup = typeof(DeploymentOAuthGroup))]
public sealed class DeploymentOAuthDeleteCommand :
    DeploymentOAuthCommand<DeploymentOAuthDeleteRequest, DeploymentResult<DeploymentOAuthConnection>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => ConnectionCommandSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => RoutedUsage("--connection");

    public override IReadOnlyList<CommandExample> GetExamples(ICommandInput input) =>
    [
        new("Delete an OAuth connection from a deployment.",
            "msagent deployment oauth delete --deployment-id <deployment-id> --connection <connection-name>"),
    ];

    protected override DeploymentOAuthDeleteRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var deploymentRequest = BindDeploymentRequest(input);
        return new DeploymentOAuthDeleteRequest
        {
            Project = deploymentRequest.Project,
            AgentId = deploymentRequest.AgentId,
            DeploymentId = deploymentRequest.DeploymentId,
            DeploymentName = deploymentRequest.DeploymentName,
            ConnectionName = input.GetString("--connection")!,
        };
    }
}

[Command("show", Description = "Show details of an OAuth connection setting on a deployment. CodeAgent only.", ParentGroup = typeof(DeploymentOAuthGroup))]
public sealed class DeploymentOAuthShowCommand :
    DeploymentOAuthCommand<DeploymentOAuthShowRequest, DeploymentResult<DeploymentOAuthConnection>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => ConnectionCommandSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => RoutedUsage("--connection");

    public override IReadOnlyList<CommandExample> GetExamples(ICommandInput input) =>
    [
        new("Show an OAuth connection as JSON.",
            "msagent deployment oauth show --deployment-id <deployment-id> --connection <connection-name> --json"),
    ];

    protected override DeploymentOAuthShowRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var deploymentRequest = BindDeploymentRequest(input);
        return new DeploymentOAuthShowRequest
        {
            Project = deploymentRequest.Project,
            AgentId = deploymentRequest.AgentId,
            DeploymentId = deploymentRequest.DeploymentId,
            DeploymentName = deploymentRequest.DeploymentName,
            ConnectionName = input.GetString("--connection")!,
        };
    }
}

[Command("list", Description = "List all OAuth connection settings on a deployment. CodeAgent only.", ParentGroup = typeof(DeploymentOAuthGroup))]
public sealed class DeploymentOAuthListCommand :
    DeploymentOAuthCommand<DeploymentOAuthListRequest, DeploymentResult<DeploymentOAuthConnection[]>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => OAuthAddressSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => RoutedUsage();

    public override IReadOnlyList<CommandExample> GetExamples(ICommandInput input) =>
    [
        new("List OAuth connections on a deployment.",
            "msagent deployment oauth list --deployment-id <deployment-id>"),
    ];

    protected override DeploymentOAuthListRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var deploymentRequest = BindDeploymentRequest(input);
        return new DeploymentOAuthListRequest
        {
            Project = deploymentRequest.Project,
            AgentId = deploymentRequest.AgentId,
            DeploymentId = deploymentRequest.DeploymentId,
            DeploymentName = deploymentRequest.DeploymentName,
        };
    }
}

[Command("list-providers", Description = "List or show details of service providers available. CodeAgent only.", ParentGroup = typeof(DeploymentOAuthGroup))]
public sealed class DeploymentOAuthProviderListCommand :
    CommandBase<DeploymentOAuthProviderListRequest, OAuthProviderDefinition[]>
{
    public override IReadOnlyList<CommandExample> GetExamples(ICommandInput input) =>
    [
        new("List the available OAuth providers.",
            "msagent deployment oauth list-providers"),
        new("Inspect a provider's schema in an explicit subscription and tenant.",
            "msagent deployment oauth list-providers --provider-name Aadv2 --subscription <subscription-id> --tenant <tenant-id>"),
    ];

    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        ProviderFilter,
        .. ProviderCatalogueAddressSymbols,
        .. AmsRoute.AddressSymbols(withDeployment: true).Select(symbol => symbol with { RequiredFeatures = [FeatureName.AmsFeatures] }),
    ];

    private static readonly AmsRouteRules AmsRules = new("deployment oauth list-providers") { SelectingOptions = ["--environment-id", "--agent-id", "--agent-name", "--deployment-id", "--deployment-name"], LocalOptions = ["--subscription", "--tenant"], NeedsDeployment = true };

    /// <inheritdoc />
    public override IReadOnlyList<RouteMetadata> Routes => [new(typeof(DeploymentOAuthProviderListRequest), typeof(OAuthProviderDefinition[])), new(typeof(AgentDeploymentAmsOAuthRequest<DeploymentOAuthProviderListRequest>), typeof(OAuthProviderDefinition[]))];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes =>
    [
        new() { Summary = "Use the live catalogue of a subscription" },
        AmsRoute.Usage("Use the providers AMS offers for an AMS deployment", withDeployment: true),
    ];

    private static CommandOption ProviderFilter =>
        OptionCatalog.Value(
            "--provider-name",
            "OAuth service provider name from the live Azure Bot Service catalogue.");

    private static IReadOnlyList<CommandSymbol> ProviderCatalogueAddressSymbols =>
    [
        OptionCatalog.Value(
            "--subscription",
            "Target subscription id. Auto-resolved from the signed-in account when omitted."),
        OptionCatalog.Value(
            "--tenant",
            "Target tenant id. Taken from the resolved subscription when omitted."),
    ];

    protected override DeploymentOAuthProviderListRequest Bind(ICommandInput input) =>
        new()
        {
            SubscriptionId = input.GetString("--subscription"),
            TenantId = input.GetString("--tenant"),
            ServiceProviderName = input.GetString("--provider-name"),
        };

    /// <inheritdoc />
    protected override Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!AmsRoute.IsSelected(input, services, AmsRules))
        {
            return InvokeAsync<DeploymentOAuthProviderListRequest, OAuthProviderDefinition[]>(services, Bind(input), cancellationToken);
        }

        AmsRoute.Validate(input, AmsRules);
        return InvokeAsync<AgentDeploymentAmsOAuthRequest<DeploymentOAuthProviderListRequest>, OAuthProviderDefinition[]>(services, AmsRoute.Address(input, new AgentDeploymentAmsOAuthRequest<DeploymentOAuthProviderListRequest> { OAuth = Bind(input) }), cancellationToken);
    }
}

public abstract class DeploymentOAuthCommand<TRequest, TResponse> : AmsRoutedDeploymentCommand<TRequest, TResponse, AgentDeploymentAmsOAuthRequest<TRequest>>
    where TRequest : DeploymentRequest
    where TResponse : notnull
{
    protected override string CommandPath => "msagent deployment oauth";

    /// <inheritdoc />
    protected override AgentDeploymentAmsOAuthRequest<TRequest> BindAms(TRequest content) => new() { OAuth = content };

    /// <summary>The deployment selectors of both routes.</summary>
    protected static IReadOnlyList<CommandSymbol> OAuthAddressSymbols =>
    [
        .. RoutedAddressSymbols("AMS agent home environment, which selects the AMS route. Defaults to the active environment."),
    ];

    protected async Task<TMutationRequest> DispatchParametersAsync<TMutationRequest>(
        TMutationRequest request,
        CancellationToken cancellationToken)
        where TMutationRequest : DeploymentOAuthUpdateRequest
    {
        request = request with
        {
            Parameters = await DeploymentParameterInput.ReadParametersAsync(request.Parameters, cancellationToken).ConfigureAwait(false),
        };
        return request;
    }

    /// <summary>The options accepted when creating an OAuth connection.</summary>
    protected static IReadOnlyList<CommandSymbol> CreateCommandSymbols =>
    [
        .. OAuthAddressSymbols,
        ConnectionName,
        ProviderName,
        ClientId,
        ClientSecret,
        ClientSecretStdin,
        Scopes,
        Parameters,
    ];

    /// <summary>The options accepted when updating an OAuth connection.</summary>
    protected static IReadOnlyList<CommandSymbol> UpdateCommandSymbols =>
    [
        .. OAuthAddressSymbols,
        ConnectionName,
        ClientId,
        ClientSecret,
        ClientSecretStdin,
        Scopes,
        Parameters,
    ];

    /// <summary>The options accepted when deleting/retrieving an OAuth connection.</summary>
    protected static IReadOnlyList<CommandSymbol> ConnectionCommandSymbols =>
    [
        .. OAuthAddressSymbols,
        ConnectionName,
    ];

    private static CommandOption ConnectionName =>
        OptionCatalog.Value(
            "--connection",
            "OAuth connection name, unique within the bot. 2-64 characters and start with a letter or digit. Remaining characters may be letters, digits, whitespace, '.', '_' or '-'.",
            required: true);

    private static CommandOption ProviderName =>
        OptionCatalog.Value(
            "--provider-name",
            $"The OAuth service provider. Use `msagent deployment oauth list-providers` to inspect the live catalogue.",
            required: true);

    private static CommandOption ClientId =>
        OptionCatalog.Value(
            "--client-id",
            "Application id. Required when the provider catalogue marks ClientId required.");

    private static CommandOption ClientSecret =>
        OptionCatalog.Value(
            "--client-secret",
            "Write-only application secret. Required when the provider catalogue marks ClientSecret required.");

    private static CommandOption ClientSecretStdin =>
        OptionCatalog.Switch(
            "--client-secret-stdin",
            "Read the write-only application secret from piped standard input instead of --client-secret, so it stays out of the command line. Pipe exactly one line.");

    private static CommandOption Scopes =>
        OptionCatalog.Value("--scopes", "Space-separated OAuth scopes.");

    private static CommandOption Parameters =>
        OptionCatalog.Strings(
            "--parameters",
            "Provider-specific Key=Value assignments or @path to a JSON object file (null becomes Key=). " +
            "Use `msagent deployment oauth list-providers --provider-name <name>` for the live schema.") with
        {
            AllowMultipleArgumentsPerToken = true,
        };

}
