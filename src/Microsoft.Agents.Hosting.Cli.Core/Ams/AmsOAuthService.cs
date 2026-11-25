using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Manages the OAuth connections of the Azure Bot Service instance AMS bound to a code-agent deployment, validating the schema-free content with the local-mode rules before any call.</summary>
public sealed class AmsOAuthService :
    ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthCreateRequest>, DeploymentResult<DeploymentOAuthConnection>>,
    ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthUpdateRequest>, DeploymentResult<DeploymentOAuthConnection>>,
    ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthDeleteRequest>, DeploymentResult<DeploymentOAuthConnection>>,
    ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthShowRequest>, DeploymentResult<DeploymentOAuthConnection>>,
    ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthListRequest>, DeploymentResult<DeploymentOAuthConnection[]>>,
    ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthProviderListRequest>, OAuthProviderDefinition[]>
{
    private readonly AmsAddressResolver _addresses;
    private readonly IAmsOAuthTransport _oauth;
    private readonly IStandardInput _standardInput;

    /// <summary>Creates the service over the AMS OAuth transport and the standard input a piped client secret is read from.</summary>
    public AmsOAuthService(AmsAddressResolver addresses, IAmsOAuthTransport oauth, IStandardInput standardInput)
    {
        _addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));
        _oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
        _standardInput = standardInput ?? throw new ArgumentNullException(nameof(standardInput));
    }

    /// <summary>Creates an OAuth connection with the named provider, or reports the existing connection unchanged.</summary>
    public async Task<DeploymentResult<DeploymentOAuthConnection>> CreateConnectionAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthCreateRequest> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = DeploymentOAuthPlan.ValidateCreateContent(request.OAuth);
        var connectionName = RequireAddressableName(plan.Request.ConnectionName);
        var deployment = await ResolveAsync(request, "deployment oauth create", cancellationToken).ConfigureAwait(false);
        plan = await DeploymentOAuthPlan.ReadClientSecretAsync(plan, _standardInput, cancellationToken).ConfigureAwait(false);
        var created = await _oauth.CreateAsync(new AmsOAuthCreateTransportRequest { Deployment = deployment, ConnectionName = connectionName, ServiceProviderName = plan.Request.ServiceProviderName, Settings = Settings(plan) }, cancellationToken).ConfigureAwait(false);
        return DeploymentOAuthService.ToConnectionResult(created.IsCreated ? DeploymentOperationStatus.Created : DeploymentOperationStatus.Existed, created.Connection);
    }

    /// <summary>Sends the changed settings, which AMS applies to the connection it reads just before writing.</summary>
    public async Task<DeploymentResult<DeploymentOAuthConnection>> UpdateConnectionAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthUpdateRequest> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = DeploymentOAuthPlan.ValidateUpdateContent(request.OAuth);
        var connectionName = RequireAddressableName(plan.Request.ConnectionName);
        var deployment = await ResolveAsync(request, "deployment oauth update", cancellationToken).ConfigureAwait(false);
        plan = await DeploymentOAuthPlan.ReadClientSecretAsync(plan, _standardInput, cancellationToken).ConfigureAwait(false);
        return DeploymentOAuthService.ToConnectionResult(DeploymentOperationStatus.Updated, await _oauth.UpdateAsync(new AmsOAuthUpdateTransportRequest { Deployment = deployment, ConnectionName = connectionName, Settings = Settings(plan) }, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Deletes an OAuth connection; an absent connection counts as deleted.</summary>
    public async Task<DeploymentResult<DeploymentOAuthConnection>> DeleteConnectionAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthDeleteRequest> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await ResolveConnectionAsync(request, request.OAuth, "deployment oauth delete", cancellationToken).ConfigureAwait(false);
        await _oauth.DeleteAsync(connection, cancellationToken).ConfigureAwait(false);
        return DeploymentOAuthService.ToConnectionResult(DeploymentOperationStatus.Deleted, targetInstanceName: connection.ConnectionName);
    }

    /// <summary>Reads one OAuth connection as AMS projects it from the bot, without its client secret.</summary>
    public async Task<DeploymentResult<DeploymentOAuthConnection>> GetConnectionAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthShowRequest> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await ResolveConnectionAsync(request, request.OAuth, "deployment oauth show", cancellationToken).ConfigureAwait(false);
        return DeploymentOAuthService.ToConnectionResult(DeploymentOperationStatus.Retrieved, await _oauth.GetAsync(connection, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Lists the OAuth connections of the bot.</summary>
    public async Task<DeploymentResult<DeploymentOAuthConnection[]>> ListConnectionsAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthListRequest> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deployment = await ResolveAsync(request, "deployment oauth list", cancellationToken).ConfigureAwait(false);
        return DeploymentOAuthService.ToConnectionListResult([.. await _oauth.ListAsync(deployment, cancellationToken).ConfigureAwait(false)]);
    }

    /// <summary>Lists the OAuth providers AMS can create connections for, or the one the request names.</summary>
    public async Task<OAuthProviderDefinition[]> ListProvidersAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthProviderListRequest> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var serviceProviderName = DeploymentRequestValidator.Optional("--provider-name", request.OAuth.ServiceProviderName);
        var deployment = await ResolveAsync(request, "deployment oauth list-providers", cancellationToken).ConfigureAwait(false);
        return DeploymentOAuthService.SelectProviders(await _oauth.ListProvidersAsync(deployment, cancellationToken).ConfigureAwait(false), serviceProviderName);
    }

    Task<DeploymentResult<DeploymentOAuthConnection>> ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthCreateRequest>, DeploymentResult<DeploymentOAuthConnection>>.HandleAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthCreateRequest> request, CancellationToken cancellationToken) => CreateConnectionAsync(request, cancellationToken);

    Task<DeploymentResult<DeploymentOAuthConnection>> ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthUpdateRequest>, DeploymentResult<DeploymentOAuthConnection>>.HandleAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthUpdateRequest> request, CancellationToken cancellationToken) => UpdateConnectionAsync(request, cancellationToken);

    Task<DeploymentResult<DeploymentOAuthConnection>> ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthDeleteRequest>, DeploymentResult<DeploymentOAuthConnection>>.HandleAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthDeleteRequest> request, CancellationToken cancellationToken) => DeleteConnectionAsync(request, cancellationToken);

    Task<DeploymentResult<DeploymentOAuthConnection>> ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthShowRequest>, DeploymentResult<DeploymentOAuthConnection>>.HandleAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthShowRequest> request, CancellationToken cancellationToken) => GetConnectionAsync(request, cancellationToken);

    Task<DeploymentResult<DeploymentOAuthConnection[]>> ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthListRequest>, DeploymentResult<DeploymentOAuthConnection[]>>.HandleAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthListRequest> request, CancellationToken cancellationToken) => ListConnectionsAsync(request, cancellationToken);

    Task<OAuthProviderDefinition[]> ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthProviderListRequest>, OAuthProviderDefinition[]>.HandleAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthProviderListRequest> request, CancellationToken cancellationToken) => ListProvidersAsync(request, cancellationToken);

    private static string RequireAddressableName(string connectionName) => connectionName.EndsWith('.') ? throw new UsageException("--connection must not end with a period for an AMS deployment, because AMS addresses the connection in the request path, where a final period can be lost.") : connectionName;

    private static AmsOAuthSettings Settings<TRequest>(DeploymentOAuthOperationPlan<TRequest> plan) where TRequest : DeploymentOAuthUpdateRequest => new(plan.Request.ClientId, plan.Request.ClientSecret, plan.Request.Scopes, plan.ParsedParameters);

    private async Task<AmsOAuthConnectionTransportRequest> ResolveConnectionAsync(AgentDeploymentAmsRequest request, DeploymentOAuthRequest content, string operation, CancellationToken cancellationToken)
    {
        var connectionName = RequireAddressableName(DeploymentOAuthPlan.ValidateContent(content).ConnectionName);
        return new AmsOAuthConnectionTransportRequest { Deployment = await ResolveAsync(request, operation, cancellationToken).ConfigureAwait(false), ConnectionName = connectionName };
    }

    private Task<AmsDeploymentReadTransportRequest> ResolveAsync(AgentDeploymentAmsRequest request, string operation, CancellationToken cancellationToken) => _addresses.ResolveBotDeploymentAsync(request, operation, cancellationToken);
}
