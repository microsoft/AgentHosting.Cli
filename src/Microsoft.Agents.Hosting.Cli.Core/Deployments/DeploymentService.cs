using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

/// <summary>
/// Owns the full deployment lifecycle for the <c>msagent deployment</c> command surface — create, show,
/// update, and delete — plus the shared input validation those verbs need.
///
/// <para>All external deployment operations go through <see cref="IDeploymentTransport"/>, so the core
/// stays offline-testable and the current direct-ARM implementation can later be replaced by AMS without
/// changing this service. The deployment id is deterministic
/// (<see cref="DeterministicDeploymentId"/>), so a re-create with the same agent id and deployment name
/// resolves the same record and adopts the existing bot instead of provisioning a duplicate. Because the
/// id needs no Azure inventory, create loads that record first and reuses the coordinates pinned in it,
/// so an idempotent retry does not depend on live subscription/resource-group resolution.</para>
/// </summary>
public sealed class DeploymentService
{
    private readonly IDeploymentStore _store;
    private readonly IDeploymentTransport _deploymentTransport;

    public DeploymentService(
        IDeploymentStore store,
        IDeploymentTransport deploymentTransport)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _deploymentTransport = deploymentTransport ?? throw new ArgumentNullException(nameof(deploymentTransport));
    }

    public async Task<DeploymentResult<CodeAgentDeployment>> GetAsync(
        DeploymentShowRequest request,
        CancellationToken cancellationToken)
    {
        request = (DeploymentShowRequest)DeploymentRequestValidator.Validate(request);

        var deployments = _store.List(request.Project);
        var (deploymentId, botResourceId, tenantId) = await ResolveBotTargetAsync(deployments, request, cancellationToken).ConfigureAwait(false);
        var existingDeployment = deployments.FirstOrDefault(deployment =>
            string.Equals(deployment.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase));

        try
        {
            var botDetails = await _deploymentTransport
                .GetBotAsync(botResourceId, tenantId, cancellationToken)
                .ConfigureAwait(false);

            // The bot exists in Azure. Merge with local record.
            var deployment = existingDeployment is not null
                ? existingDeployment.WithSnapshot(botDetails)
                : CodeAgentDeployment.FromSnapshot(botDetails, deploymentId, request.AgentId!);

            UpsertLocalRecord(request.Project, deployment);

            return ToDeploymentResult(DeploymentOperationStatus.Retrieved, deployment);
        }
        catch (TargetRequestException exception) when (exception.Status == 404)
        {
            // The bot does not exist in Azure. Remove the local record if it exists.
            DeleteLocalRecord(request.Project, existingDeployment);

            throw DeploymentException.NotFound(deploymentId, exception);
        }
    }

    public async Task<DeploymentResult<CodeAgentDeployment>> CreateAsync(
        DeploymentCreateRequest request,
        CancellationToken cancellationToken)
    {
        request = DeploymentRequestValidator.ValidateCreate(request);

        var agentId = request.AgentId;
        var deploymentName = request.DeploymentName;

        // The deployment id is derived from the agent id and deployment name alone (no Azure inventory), so load
        // and reconcile the existing record first. When a record exists, reuse the subscription/resource
        // group/tenant already pinned in its bot resource id instead of resolving them from live Azure
        // inventory — an identical retry then stays unambiguous even if the account has since gained
        // extra subscriptions or resource groups (which would otherwise make auto-resolution throw).
        var deploymentId = DeterministicDeploymentId.Compute(agentId, deploymentName).ToString();
        var deployments = _store.List(request.Project);
        var existingDeployment = deployments.FirstOrDefault(deployment =>
            string.Equals(deployment.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase));

        string subscriptionId;
        string resourceGroup;
        string tenantId;

        if (existingDeployment is not null)
        {
            var botResource = BotServiceResourceId.Parse(existingDeployment.Bot.ResourceId, existingDeployment.DeploymentId);
            subscriptionId = botResource.SubscriptionId;
            resourceGroup = botResource.ResourceGroup;
            tenantId = existingDeployment.Bot.TenantId;
        }
        else
        {
            var subscription = await _deploymentTransport
                .ResolveSubscriptionAsync(request.SubscriptionId, request.TenantId, cancellationToken)
                .ConfigureAwait(false);
            subscriptionId = subscription.SubscriptionId;
            tenantId = subscription.TenantId;
            resourceGroup = (await _deploymentTransport
                .ResolveResourceGroupAsync(subscriptionId, request.ResourceGroup, tenantId, cancellationToken)
                .ConfigureAwait(false)).Name;
        }

        // Carry the resolved coordinates back onto the request so the provisioning PUT targets the
        // correct subscription/resource group/tenant even when --subscription/--tenant were omitted.
        request = request with
        {
            ResourceGroup = resourceGroup,
            SubscriptionId = subscriptionId,
            TenantId = tenantId
        };

        CodeAgentDeployment deployment;
        var availability = await _deploymentTransport
            .CheckBotNameAvailabilityAsync(deploymentName, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (!availability.IsValid)
        {
            // The deployment name is unavailable, so a bot with this name already exists. Read it instead.
            var botResourceId = BotServiceResourceId.Build(subscriptionId, resourceGroup, deploymentName);
            try
            {
                var botDetails = await _deploymentTransport
                    .GetBotAsync(botResourceId, tenantId, cancellationToken)
                    .ConfigureAwait(false);
                deployment = existingDeployment is not null
                    ? existingDeployment.WithSnapshot(botDetails)
                    : CodeAgentDeployment.FromSnapshot(
                        botDetails,
                        deploymentId,
                        agentId,
                        request.IsDefault ?? false,
                        ParseDeploymentType(request.DeploymentType!));
            }
            catch (TargetRequestException exception)
            {
                throw DeploymentException.ReadFailed(deploymentId, exception);
            }
        }
        else
        {
            var botDetails = await _deploymentTransport
                .CreateBotAsync(request, cancellationToken)
                .ConfigureAwait(false);
            deployment = CodeAgentDeployment.FromSnapshot(
                botDetails,
                deploymentId,
                agentId,
                request.IsDefault ?? false,
                ParseDeploymentType(request.DeploymentType!));
        }

        UpsertLocalRecord(request.Project, deployment);

        return ToDeploymentResult(
            availability.IsValid
                ? DeploymentOperationStatus.Created
                : DeploymentOperationStatus.Existed,
            deployment);
    }

    public async Task<DeploymentResult<CodeAgentDeployment>> UpdateAsync(
        DeploymentUpdateRequest request,
        CancellationToken cancellationToken)
    {
        request = DeploymentRequestValidator.ValidateUpdate(request);

        // --deployment-type and --default are local-only fields.
        // Only --sku/--display-name/--description/--endpoint are pushed to the Azure Bot Service resource.
        // When no bot field changes, update the local record in place without any ARM call.
        var mutatesBot = request.Sku is not null
            || request.DisplayName is not null
            || request.Description is not null
            || request.Endpoint is not null;

        CodeAgentDeployment? existingDeployment;
        CodeAgentDeployment deployment;
        var deployments = _store.List(request.Project);
        DeploymentType? deploymentType = !string.IsNullOrWhiteSpace(request.DeploymentType)
            ? ParseDeploymentType(request.DeploymentType)
            : null;

        string deploymentId;
        string botResourceId;
        string tenantId;

        if (!mutatesBot)
        {
            deploymentId = request.DeploymentId
                ?? DeterministicDeploymentId.Compute(request.AgentId!, request.DeploymentName!).ToString();
            existingDeployment = deployments.FirstOrDefault(deployment =>
                string.Equals(deployment.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase));

            if (existingDeployment is null)
            {
                throw DeploymentException.NotFound(deploymentId);
            }

            deployment = existingDeployment.WithMetadata(request.IsDefault, deploymentType);
        }
        else
        {
            (deploymentId, botResourceId, tenantId) = await ResolveBotTargetAsync(deployments, request, cancellationToken).ConfigureAwait(false);
            existingDeployment = deployments.FirstOrDefault(candidate =>
                string.Equals(candidate.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase));
            var botResource = BotServiceResourceId.Parse(botResourceId, deploymentId);

            request = request with
            {
                DeploymentName = botResource.BotName,
                ResourceGroup = botResource.ResourceGroup,
                SubscriptionId = botResource.SubscriptionId,
                TenantId = tenantId
            };

            try
            {
                var botDetails = await _deploymentTransport
                    .UpdateBotAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                deployment = existingDeployment is not null
                    ? existingDeployment
                        .WithSnapshot(botDetails)
                        .WithMetadata(request.IsDefault, deploymentType)
                    : CodeAgentDeployment.FromSnapshot(
                        botDetails,
                        deploymentId,
                        request.AgentId!,
                        request.IsDefault ?? false,
                        deploymentType ?? DeploymentType.Dev);
            }
            catch (TargetRequestException exception) when (exception.Status == 404)
            {
                // Remove the local record if it exists.
                DeleteLocalRecord(request.Project, existingDeployment);

                throw DeploymentException.NotFound(deploymentId, exception);
            }
        }

        UpsertLocalRecord(request.Project, deployment);

        return ToDeploymentResult(
            DeploymentOperationStatus.Updated,
            deployment);
    }

    public async Task<DeploymentResult<CodeAgentDeployment>> DeleteAsync(
        DeploymentRequest request,
        CancellationToken cancellationToken)
    {
        request = DeploymentRequestValidator.Validate(request);

        var deployments = _store.List(request.Project);

        string deploymentId;
        string botResourceId;
        string tenantId;
        try
        {
            (deploymentId, botResourceId, tenantId) = await ResolveBotTargetAsync(deployments, request, cancellationToken).ConfigureAwait(false);
        }
        catch (DeploymentException exception) when (exception.ErrorKind == "deployment-not-found")
        {
            // Idempotent delete: no local record and no bot to target — the deployment is already
            // gone, so report success without an error.
            return ToDeploymentResult(DeploymentOperationStatus.Deleted);
        }

        var deployment = deployments.FirstOrDefault(candidate =>
            string.Equals(candidate.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase));

        await _deploymentTransport
            .DeleteBotAsync(botResourceId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        DeleteLocalRecord(request.Project, deployment);

        return ToDeploymentResult(
            DeploymentOperationStatus.Deleted,
            targetInstanceName: deployment?.DeploymentName);
    }

    private static DeploymentResult<CodeAgentDeployment> ToDeploymentResult(
        DeploymentOperationStatus status,
        CodeAgentDeployment? deployment = null,
        string? targetInstanceName = null) =>
        new()
        {
            Status = status,
            Payload = deployment,
            TargetName = "Deployment",
            TargetInstanceName = targetInstanceName ?? deployment?.DeploymentName,
        };

    // Deployment CRUD does not own channels, so preserve the channel projection when rewriting a row.
    private void UpsertLocalRecord(string? project, CodeAgentDeployment deployment)
    {
        var existing = _store.List(project).FirstOrDefault(candidate =>
            string.Equals(candidate.DeploymentId, deployment.DeploymentId, StringComparison.OrdinalIgnoreCase));
        _store.Upsert(project, existing is null ? deployment : deployment with { Channels = existing.Channels });
    }

    private void DeleteLocalRecord(string? project, CodeAgentDeployment? deployment)
    {
        if (deployment is not null)
        {
            _store.Remove(project, deployment.DeploymentId);
        }
    }

    private async Task<(string DeploymentId, string BotResourceId, string TenantId)> ResolveBotTargetAsync(
        IReadOnlyList<CodeAgentDeployment> deployments,
        DeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var deploymentName = request.DeploymentName;
        var agentId = request.AgentId;
        var hasAttachedAgent = !string.IsNullOrWhiteSpace(agentId) && !string.IsNullOrWhiteSpace(deploymentName);
        var deploymentId = request.DeploymentId
            ?? DeterministicDeploymentId.Compute(request.AgentId!, request.DeploymentName!).ToString();

        var deployment = deployments.FirstOrDefault(candidate =>
            string.Equals(candidate.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase));

        string? botResourceId = null;
        string? tenantId = null;
        if (deployment is not null)
        {
            botResourceId = deployment.Bot.ResourceId;
            tenantId = deployment.Bot.TenantId;
        }
        else if (hasAttachedAgent)
        {
            // No usable local coordinates — resolve the subscription/tenant from the signed-in account
            // (honouring explicit --subscription/--tenant so a multi-subscription or guest-tenant account
            // can be disambiguated) and the resource group (honouring an explicit --resource-group, else
            // auto-resolved when the subscription has exactly one) so we can read the bot directly by deployment name.
            var subscription = await _deploymentTransport
                .ResolveSubscriptionAsync(request.SubscriptionId, request.TenantId, cancellationToken)
                .ConfigureAwait(false);
            var subscriptionId = subscription.SubscriptionId;
            tenantId = subscription.TenantId;
            var resourceGroup = (await _deploymentTransport
                .ResolveResourceGroupAsync(subscriptionId, request.ResourceGroup, tenantId, cancellationToken)
                .ConfigureAwait(false)).Name;
            botResourceId = BotServiceResourceId.Build(subscriptionId, resourceGroup, deploymentName!);
        }

        if (string.IsNullOrWhiteSpace(botResourceId))
        {
            throw DeploymentException.NotFound(deploymentId);
        }

        return (deploymentId!, botResourceId!, tenantId!);
    }

    private static DeploymentType ParseDeploymentType(string value) =>
        Enum.Parse<DeploymentType>(value, ignoreCase: true);
}
