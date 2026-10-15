using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

/// <summary>
/// Owns the <c>msagent deployment oauth</c> command surface — create, update, show, list, and delete of Azure Bot
/// Service OAuth connection settings (<c>Microsoft.BotService/botServices/{bot}/connections</c>).
///
/// <para>The deployment must already exist locally (a create resolves the same deterministic id); the
/// backing bot's subscription/resource-group/name are read from the persisted record, and every mutation
/// goes straight to ARM through <see cref="IDeploymentOAuthTransport"/>. The safe connection state is
/// mirrored into the deployment aggregate's <c>oauthConnections</c> collection. Well-known fields have
/// dedicated properties; secret values are persisted only as masks, and raw ARM responses are never persisted.</para>
/// </summary>
public sealed class DeploymentOAuthService
{
    private readonly IDeploymentStore _store;
    private readonly IDeploymentOAuthTransport _oauthTransport;
    private readonly IDeploymentTransport _deploymentTransport;

    public DeploymentOAuthService(
        IDeploymentStore store,
        IDeploymentOAuthTransport oauthTransport,
        IDeploymentTransport deploymentTransport)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _oauthTransport = oauthTransport ?? throw new ArgumentNullException(nameof(oauthTransport));
        _deploymentTransport = deploymentTransport ?? throw new ArgumentNullException(nameof(deploymentTransport));
    }

    public async Task<DeploymentResult<DeploymentOAuthConnection>> GetAsync(
        DeploymentOAuthShowRequest request,
        CancellationToken cancellationToken)
    {
        request = (DeploymentOAuthShowRequest)DeploymentOAuthPlan.Validate(request);

        var deployment = _store.LoadDeployment(request);

        var oauthConnection = await _oauthTransport
            .GetOAuthConnectionAsync(
                request.ConnectionName!,
                deployment.Bot.ResourceId,
                deployment.Bot.TenantId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw DeploymentException.OAuthConnectionNotFound(deployment.DeploymentId, request.ConnectionName!);

        var providers = await ListServiceProvidersAsync(deployment, cancellationToken).ConfigureAwait(false);
        var safeConnection = DeploymentOAuthParameters.Sanitize(
            oauthConnection,
            FindServiceProviderOrDefault(providers, oauthConnection.ServiceProviderId, OauthProviderKeyType.Id));

        SaveConnection(request.Project, deployment, safeConnection);
        return ToConnectionResult(DeploymentOperationStatus.Retrieved, safeConnection);
    }

    public async Task<DeploymentResult<DeploymentOAuthConnection>> CreateAsync(
        DeploymentOAuthCreateRequest request,
        CancellationToken cancellationToken)
    {
        var plan = DeploymentOAuthPlan.ValidateCreate(request);

        var deployment = _store.LoadDeployment(plan.Request);
        var providers = await ListServiceProvidersAsync(deployment, cancellationToken).ConfigureAwait(false);
        var serviceProvider = FindServiceProvider(
            providers,
            plan.Request.ServiceProviderName!,
            OauthProviderKeyType.Name);
        var upsertRequest = DeploymentOAuthPlan.BuildCreate(
            serviceProvider,
            plan,
            deployment.Bot.ResourceId,
            deployment.Bot.TenantId);

        var existingOAuthConnection = await _oauthTransport
            .GetOAuthConnectionAsync(
                plan.Request.ConnectionName!,
                deployment.Bot.ResourceId,
                deployment.Bot.TenantId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingOAuthConnection is not null)
        {
            var safeExisting = DeploymentOAuthParameters.Sanitize(
                existingOAuthConnection,
                FindServiceProviderOrDefault(
                    providers,
                    existingOAuthConnection.ServiceProviderId,
                    OauthProviderKeyType.Id));

            SaveConnection(plan.Request.Project, deployment, safeExisting);
            return ToConnectionResult(DeploymentOperationStatus.Existed, safeExisting);
        }

        var oauthConnection = DeploymentOAuthParameters.Sanitize(
            await _oauthTransport
                .UpsertOAuthConnectionAsync(upsertRequest, cancellationToken)
                .ConfigureAwait(false),
            serviceProvider);

        SaveConnection(plan.Request.Project, deployment, oauthConnection);
        return ToConnectionResult(DeploymentOperationStatus.Created, oauthConnection);
    }

    public async Task<DeploymentResult<DeploymentOAuthConnection>> UpdateAsync(
        DeploymentOAuthUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var plan = DeploymentOAuthPlan.ValidateUpdate(request);

        var deployment = _store.LoadDeployment(plan.Request);

        // Unlike create, update must read the live connection to merge omitted values safely. Use that GET as the
        // provider authority before catalog validation so stale persisted metadata cannot reject valid updates or
        // require a second validation pass when the provider ids differ.
        var existingOAuthConnection = await _oauthTransport
            .GetOAuthConnectionAsync(
                plan.Request.ConnectionName!,
                deployment.Bot.ResourceId,
                deployment.Bot.TenantId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw DeploymentException.OAuthConnectionNotFound(deployment.DeploymentId, plan.Request.ConnectionName!);

        var providers = await ListServiceProvidersAsync(deployment, cancellationToken).ConfigureAwait(false);
        var serviceProvider = FindServiceProvider(
            providers,
            existingOAuthConnection.ServiceProviderId,
            OauthProviderKeyType.Id);
        var safeExisting = DeploymentOAuthParameters.Sanitize(existingOAuthConnection, serviceProvider);

        var upsertRequest = DeploymentOAuthPlan.BuildUpdate(
            serviceProvider,
            plan,
            safeExisting,
            deployment.Bot.ResourceId,
            deployment.Bot.TenantId);

        var oauthConnection = DeploymentOAuthParameters.Sanitize(
            await _oauthTransport
                .UpsertOAuthConnectionAsync(upsertRequest, cancellationToken)
                .ConfigureAwait(false),
            serviceProvider);

        SaveConnection(plan.Request.Project, deployment, oauthConnection);
        return ToConnectionResult(DeploymentOperationStatus.Updated, oauthConnection);
    }

    public async Task<DeploymentResult<DeploymentOAuthConnection>> DeleteAsync(
        DeploymentOAuthDeleteRequest request,
        CancellationToken cancellationToken)
    {
        request = (DeploymentOAuthDeleteRequest)DeploymentOAuthPlan.Validate(request);

        var deployment = _store.LoadDeployment(request);

        _ = await _oauthTransport
            .DeleteOAuthConnectionAsync(
                request.ConnectionName!,
                deployment.Bot.ResourceId,
                deployment.Bot.TenantId,
                cancellationToken)
            .ConfigureAwait(false);

        SaveConnections(
            request.Project,
            deployment,
            deployment.OAuthConnections.Where(connection =>
                !string.Equals(
                    connection.ConnectionName,
                    request.ConnectionName,
                    StringComparison.OrdinalIgnoreCase)));

        return ToConnectionResult(
            DeploymentOperationStatus.Deleted,
            targetInstanceName: request.ConnectionName);
    }

    public async Task<DeploymentResult<DeploymentOAuthConnection[]>> ListAsync(
        DeploymentOAuthListRequest request,
        CancellationToken cancellationToken)
    {
        request = DeploymentOAuthPlan.ValidateList(request);

        var deployment = _store.LoadDeployment(request);

        var oauthConnections = await _oauthTransport
            .ListOAuthConnectionsAsync(
                deployment.Bot.ResourceId,
                deployment.Bot.TenantId,
                cancellationToken)
            .ConfigureAwait(false);
        var providers = await ListServiceProvidersAsync(deployment, cancellationToken).ConfigureAwait(false);
        var safeConnections = oauthConnections
            .Select(connection => DeploymentOAuthParameters.Sanitize(
                connection,
                FindServiceProviderOrDefault(providers, connection.ServiceProviderId, OauthProviderKeyType.Id)))
            .ToArray();

        SaveConnections(request.Project, deployment, safeConnections);

        return new DeploymentResult<DeploymentOAuthConnection[]>
        {
            Status = DeploymentOperationStatus.Listed,
            Payload = safeConnections,
            TargetName = "Connections",
        };
    }

    public async Task<OAuthProviderDefinition[]> ListProvidersAsync(
        DeploymentOAuthProviderListRequest request,
        CancellationToken cancellationToken)
    {
        request = DeploymentOAuthPlan.ValidateProviderList(request);
        var subscription = await _deploymentTransport
            .ResolveSubscriptionAsync(request.SubscriptionId, request.TenantId, cancellationToken)
            .ConfigureAwait(false);
        var serviceProviders = await _oauthTransport
            .ListServiceProvidersAsync(
                subscription.SubscriptionId,
                subscription.TenantId,
                cancellationToken)
            .ConfigureAwait(false);

        if (request.ServiceProviderName is null)
        {
            return serviceProviders
                .OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var serviceProvider = FindServiceProvider(
            serviceProviders,
            request.ServiceProviderName,
            OauthProviderKeyType.Name);

        return [serviceProvider];
    }

    private async Task<IReadOnlyList<OAuthProviderDefinition>> ListServiceProvidersAsync(
        CodeAgentDeployment deployment,
        CancellationToken cancellationToken)
    {
        var botResource = BotServiceResourceId.Parse(deployment.Bot.ResourceId, deployment.DeploymentId);
        return await _oauthTransport
            .ListServiceProvidersAsync(
                botResource.SubscriptionId,
                deployment.Bot.TenantId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static OAuthProviderDefinition FindServiceProvider(
        IReadOnlyList<OAuthProviderDefinition> serviceProviders,
        string providerKey,
        OauthProviderKeyType keyType) =>
        serviceProviders.FirstOrDefault(candidate =>
            string.Equals(
                keyType == OauthProviderKeyType.Name ? candidate.Name : candidate.Id,
                providerKey,
                StringComparison.OrdinalIgnoreCase))
        ?? throw DeploymentException.OAuthServiceProviderNotFound(providerKey);

    private static OAuthProviderDefinition? FindServiceProviderOrDefault(
        IReadOnlyList<OAuthProviderDefinition> serviceProviders,
        string providerKey,
        OauthProviderKeyType keyType) =>
        serviceProviders.FirstOrDefault(candidate =>
            string.Equals(
                keyType == OauthProviderKeyType.Name ? candidate.Name : candidate.Id,
                providerKey,
                StringComparison.OrdinalIgnoreCase));

    private void SaveConnection(
        string? project,
        CodeAgentDeployment deployment,
        DeploymentOAuthConnection oauthConnection) =>
        SaveConnections(
            project,
            deployment,
            deployment.OAuthConnections
                .Where(candidate => !string.Equals(
                    candidate.ConnectionName,
                    oauthConnection.ConnectionName,
                    StringComparison.OrdinalIgnoreCase))
                .Append(oauthConnection));

    private void SaveConnections(
        string? project,
        CodeAgentDeployment deployment,
        IEnumerable<DeploymentOAuthConnection> oauthConnections) =>
        _store.Upsert(
            project,
            deployment with
            {
                OAuthConnections = oauthConnections
                    .Select(connection => DeploymentOAuthParameters.Sanitize(
                        connection,
                        provider: null,
                        includeArmResponse: false))
                    .ToArray(),
            });

    private static DeploymentResult<DeploymentOAuthConnection> ToConnectionResult(
        DeploymentOperationStatus status,
        DeploymentOAuthConnection? connection = null,
        string? targetInstanceName = null) =>
        new()
        {
            Status = status,
            Payload = connection,
            TargetName = "Connection",
            TargetInstanceName = targetInstanceName ?? connection?.ConnectionName,
        };
}
