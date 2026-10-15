using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment;
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

    public override IReadOnlyList<UsageRoute> UsageRoutes => DeploymentUsage("--connection", "--provider-name");

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
            Scopes = input.GetString("--scopes"),
            Parameters = input.GetStrings("--parameters"),
        };
    }

}

[Command("update", Description = "Update an OAuth connection setting on a deployment. CodeAgent only.", ParentGroup = typeof(DeploymentOAuthGroup))]
public sealed class DeploymentOAuthUpdateCommand :
    DeploymentOAuthCommand<DeploymentOAuthUpdateRequest, DeploymentResult<DeploymentOAuthConnection>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => UpdateCommandSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => DeploymentUsage("--connection");

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
            Scopes = input.GetString("--scopes"),
            Parameters = input.GetStrings("--parameters"),
        };
    }

}

[Command("delete", Description = "Delete an OAuth connection setting on a deployment. CodeAgent only.", ParentGroup = typeof(DeploymentOAuthGroup))]
public sealed class DeploymentOAuthDeleteCommand :
    DeploymentOAuthCommand<DeploymentOAuthDeleteRequest, DeploymentResult<DeploymentOAuthConnection>>
{
    public override IReadOnlyList<CommandSymbol> Symbols => ConnectionCommandSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => DeploymentUsage("--connection");

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

    public override IReadOnlyList<UsageRoute> UsageRoutes => DeploymentUsage("--connection");

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
    public override IReadOnlyList<CommandSymbol> Symbols => DeploymentAddressSymbols;

    public override IReadOnlyList<UsageRoute> UsageRoutes => DeploymentUsage();

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
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        ProviderFilter,
        .. ProviderCatalogueAddressSymbols,
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

    protected override Task<object> DispatchAsync(
        ICommandInput input,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        InvokeAsync<DeploymentOAuthProviderListRequest, OAuthProviderDefinition[]>(
            services,
            Bind(input),
            cancellationToken);
}

public abstract class DeploymentOAuthCommand<TRequest, TResponse> : CodeAgentDeploymentCommand<TRequest, TResponse>
    where TResponse : notnull
{
    protected override string CommandPath => "msagent deployment oauth";

    /// <summary>The options accepted when creating an OAuth connection.</summary>
    protected static IReadOnlyList<CommandSymbol> CreateCommandSymbols =>
    [
        .. DeploymentAddressSymbols,
        ConnectionName,
        ProviderName,
        ClientId,
        ClientSecret,
        Scopes,
        Parameters,
    ];

    /// <summary>The options accepted when updating an OAuth connection.</summary>
    protected static IReadOnlyList<CommandSymbol> UpdateCommandSymbols =>
    [
        .. DeploymentAddressSymbols,
        ConnectionName,
        ClientId,
        ClientSecret,
        Scopes,
        Parameters,
    ];

    /// <summary>The options accepted when deleting/retrieving an OAuth connection.</summary>
    protected static IReadOnlyList<CommandSymbol> ConnectionCommandSymbols =>
    [
        .. DeploymentAddressSymbols,
        ConnectionName,
    ];

    private static CommandOption ConnectionName =>
        OptionCatalog.Value(
            "--connection",
            "OAuth connection name, unique within the bot. 2-64 characters, starting with a letter or digit, from whitespace, letters, digits, '.', '_' and '-'.",
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

    private static CommandOption Scopes =>
        OptionCatalog.Value("--scopes", "Space-separated OAuth scopes.");

    private static CommandOption Parameters =>
        OptionCatalog.Strings(
            "--parameters",
            "Provider-specific Key=Value assignments. " +
            "Use `msagent deployment oauth list-providers --provider-name <name>` for the live schema.") with
        {
            AllowMultipleArgumentsPerToken = true,
        };

}
