using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

/// <summary>
/// Manages Azure Bot Service channels on a persisted deployment's backing bot — the live side of
/// <c>msagent deployment channel {create|update|delete|show|list}</c>, each mirroring the equivalent
/// <c>az bot &lt;channel&gt;</c> ARM call. It resolves the deployment from the same local <c>.ah</c> store the
/// create path writes (only to read the backing bot's subscription/resource-group/name), then issues the
/// channel call through <see cref="IDeploymentChannelTransport"/> so the core stays offline-testable. Requests
/// are validated by <see cref="DeploymentChannelPlan"/> before any ARM call. Channels have no ARM PATCH,
/// so <c>update</c> is a GET-merge-PUT: it reads the existing channel, overlays the provided settings, and
/// upserts the full resource.
/// </summary>
public sealed class DeploymentChannelService
{
    private readonly IDeploymentStore _store;
    private readonly IDeploymentChannelTransport _channelTransport;

    public DeploymentChannelService(IDeploymentStore store, IDeploymentChannelTransport channelTransport)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _channelTransport = channelTransport ?? throw new ArgumentNullException(nameof(channelTransport));
    }

    public async Task<DeploymentResult<DeploymentChannelRecord>> GetChannelAsync(
        DeploymentChannelShowRequest request,
        CancellationToken cancellationToken)
    {
        var plan = DeploymentChannelPlan.Validate(request);
        var deployment = _store.LoadDeployment(plan);
        var channelArmName = DeploymentChannelCatalog.FindChannelArmName(plan.ChannelName);

        var channelRecord = await _channelTransport
            .GetChannelAsync(
                channelArmName,
                deployment.Bot.ResourceId,
                deployment.Bot.TenantId,
                cancellationToken)
            .ConfigureAwait(false)
                ?? throw DeploymentException.ChannelNotFound(deployment.DeploymentId, plan.ChannelName);

        SaveChannel(plan.Project, deployment, channelRecord, channelRecord.ChannelName);
        return ToChannelResult(DeploymentOperationStatus.Retrieved, channelRecord);
    }

    public async Task<DeploymentResult<DeploymentChannelRecord>> CreateChannelAsync(
        DeploymentChannelCreateRequest request,
        CancellationToken cancellationToken)
    {
        var plan = DeploymentChannelPlan.ValidateCreate(request);
        var deployment = _store.LoadDeployment(plan.Request);

        var existingChannel = await _channelTransport
            .GetChannelAsync(
                plan.ChannelDefinition.ArmName,
                deployment.Bot.ResourceId,
                deployment.Bot.TenantId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existingChannel is not null)
        {
            SaveChannel(plan.Request.Project, deployment, existingChannel, existingChannel.ChannelName);
            return ToChannelResult(DeploymentOperationStatus.Existed, existingChannel);
        }

        var channelRecord = await _channelTransport.UpsertChannelAsync(
            DeploymentChannelPlan.BuildCreate(
                plan,
                deployment.Bot.ResourceId,
                deployment.Bot.TenantId),
            cancellationToken).ConfigureAwait(false);

        SaveChannel(plan.Request.Project, deployment, channelRecord, channelRecord.ChannelName);
        return ToChannelResult(DeploymentOperationStatus.Created, channelRecord);
    }

    public async Task<DeploymentResult<DeploymentChannelRecord>> UpdateChannelAsync(
        DeploymentChannelUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var plan = DeploymentChannelPlan.ValidateUpdate(request);
        var deployment = _store.LoadDeployment(plan.Request);

        var existingChannel = await _channelTransport.GetChannelAsync(
            plan.ChannelDefinition.ArmName,
            deployment.Bot.ResourceId,
            deployment.Bot.TenantId,
            cancellationToken)
            .ConfigureAwait(false)
            ?? throw DeploymentException.ChannelNotFound(deployment.DeploymentId, plan.Request.ChannelName);

        var channelRecord = await _channelTransport.UpsertChannelAsync(
            DeploymentChannelPlan.BuildUpdate(
                plan,
                existingChannel,
                deployment.Bot.ResourceId,
                deployment.Bot.TenantId),
            cancellationToken).ConfigureAwait(false);

        SaveChannel(plan.Request.Project, deployment, channelRecord, channelRecord.ChannelName);
        return ToChannelResult(DeploymentOperationStatus.Updated, channelRecord);
    }

    public async Task<DeploymentResult<DeploymentChannelRecord>> DeleteChannelAsync(
        DeploymentChannelDeleteRequest request,
        CancellationToken cancellationToken)
    {
        var plan = DeploymentChannelPlan.ValidateDelete(request);
        var deployment = _store.LoadDeployment(plan);
        var channelArmName = DeploymentChannelCatalog.FindChannelArmName(plan.ChannelName);

        _ = await _channelTransport
            .DeleteChannelAsync(
                channelArmName,
                deployment.Bot.ResourceId,
                deployment.Bot.TenantId,
                cancellationToken)
            .ConfigureAwait(false);

        SaveChannel(plan.Project, deployment, channel: null, plan.ChannelName);
        return ToChannelResult(
            DeploymentOperationStatus.Deleted,
            targetInstanceName: plan.ChannelName);
    }

    public async Task<DeploymentResult<DeploymentChannelRecord[]>> ListChannelsAsync(
        DeploymentChannelListRequest request,
        CancellationToken cancellationToken)
    {
        request = DeploymentChannelPlan.ValidateList(request);
        var deployment = _store.LoadDeployment(request);

        var channels = await _channelTransport
            .ListChannelsAsync(
                deployment.Bot.ResourceId,
                deployment.Bot.TenantId,
                cancellationToken)
            .ConfigureAwait(false);

        var channelRecords = channels.ToArray();

        SaveChannels(request.Project, deployment, channelRecords);
        return new DeploymentResult<DeploymentChannelRecord[]>
        {
            Status = DeploymentOperationStatus.Listed,
            Payload = channelRecords,
            TargetName = "Channels",
        };
    }

    /// <summary>
    /// Persists the created/updated channel into (or removes the deleted channel from) the deployment
    /// record's <see cref="CodeAgentDeployment.Channels"/> array, keyed by friendly channel name.
    /// </summary>
    private void SaveChannel(
        string? project,
        CodeAgentDeployment deployment,
        DeploymentChannelRecord? channel,
        string channelName)
    {
        var channels = deployment.Channels
            .Where(candidate => !string.Equals(
                candidate.ChannelName,
                channelName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (channel is not null)
        {
            channels.Add(channel with { RawArmResponse = null });
        }

        _store.Upsert(project, deployment with { Channels = channels });
    }

    private void SaveChannels(
        string? project,
        CodeAgentDeployment deployment,
        IEnumerable<DeploymentChannelRecord> channels) =>
        _store.Upsert(
            project,
            deployment with
            {
                Channels = [.. channels.Select(channelRecord => channelRecord with { RawArmResponse = null })],
            });

    private static DeploymentResult<DeploymentChannelRecord> ToChannelResult(
        DeploymentOperationStatus status,
        DeploymentChannelRecord? channel = null,
        string? targetInstanceName = null) =>
        new()
        {
            Status = status,
            Payload = channel,
            TargetName = "Channel",
            TargetInstanceName = targetInstanceName ?? channel?.ChannelName,
        };
}
