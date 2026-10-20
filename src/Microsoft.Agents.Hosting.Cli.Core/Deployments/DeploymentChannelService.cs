using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

/// <summary>
/// Manages channels on a deployment's backing bot using <see cref="DeploymentChannelPlan"/> validation.
/// Updates merge live settings before PATCH; new site settings require a follow-up write.
/// Write responses are persisted without a post-write read.
/// </summary>
public sealed class DeploymentChannelService
{
    private readonly IDeploymentStore _store;
    private readonly IDeploymentChannelTransport _channelTransport;
    private readonly IBrowserLauncher? _browser;

    public DeploymentChannelService(
        IDeploymentStore store,
        IDeploymentChannelTransport channelTransport,
        IBrowserLauncher? browser = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _channelTransport = channelTransport ?? throw new ArgumentNullException(nameof(channelTransport));
        _browser = browser;
    }

    public async Task<DeploymentResult<DeploymentChannelRecord>> GetChannelAsync(
        DeploymentChannelShowRequest request,
        CancellationToken cancellationToken)
    {
        var plan = DeploymentChannelPlan.ValidateShow(request);
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

        var write = DeploymentChannelPlan.BuildCreate(
            plan,
            deployment.Bot.ResourceId,
            deployment.Bot.TenantId);
        var (channelRecord, warnings) = await WriteChannelAsync(
            plan.ChannelDefinition, write, isCreate: true, cancellationToken).ConfigureAwait(false);

        SaveChannel(plan.Request.Project, deployment, channelRecord, channelRecord.ChannelName);
        return OpenAuthorization(
            ToChannelResult(plan.ChannelDefinition.Name == "slack" ? DeploymentOperationStatus.WriteAccepted : DeploymentOperationStatus.Created,
                channelRecord, warnings: warnings),
            plan.Request.NonInteractive);
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

        var write = DeploymentChannelPlan.BuildUpdate(
            plan,
            existingChannel,
            deployment.Bot.ResourceId,
            deployment.Bot.TenantId);
        var (channelRecord, warnings) = await WriteChannelAsync(
            plan.ChannelDefinition, write, isCreate: false, cancellationToken).ConfigureAwait(false);

        SaveChannel(plan.Request.Project, deployment, channelRecord, channelRecord.ChannelName);
        return OpenAuthorization(
            ToChannelResult(plan.ChannelDefinition.Name == "slack" ? DeploymentOperationStatus.WriteAccepted : DeploymentOperationStatus.Updated,
                channelRecord, warnings: warnings),
            plan.Request.NonInteractive);
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
        var supportedChannels = channelRecords
            .Where(channel => DeploymentChannelCatalog.SupportedChannelNames.Contains(
                channel.ChannelName, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        SaveChannels(request.Project, deployment, supportedChannels);
        return new DeploymentResult<DeploymentChannelRecord[]>
        {
            Status = DeploymentOperationStatus.Listed,
            Payload = supportedChannels,
            ArmResponse = JsonSerializer.SerializeToElement(
                channelRecords.Where(channel => channel.RawArmResponse is not null)
                    .Select(channel => channel.RawArmResponse!.Value)
                    .ToArray()),
            TargetName = "Channels",
        };
    }

    private async Task<(DeploymentChannelRecord Channel, IReadOnlyList<string> Warnings)> WriteChannelAsync(
        ChannelDefinition channelDefinition,
        BotServiceChannelWriteRequest writeRequest,
        bool isCreate,
        CancellationToken cancellationToken)
    {
        var hasSites = channelDefinition.Parameters.Any(field => field.Name == "sites" && field.ItemParameters is not null);
        var initialRequest = hasSites
            ? writeRequest with { Parameters = DeploymentChannelSites.BuildInitialWrite(channelDefinition, writeRequest.Parameters) }
            : writeRequest;
        var response = await (isCreate
            ? _channelTransport.CreateChannelAsync(initialRequest, cancellationToken)
            : _channelTransport.UpdateChannelAsync(initialRequest, cancellationToken)).ConfigureAwait(false);

        if (hasSites)
        {
            try
            {
                var configuration = DeploymentChannelSites.BuildNewSiteConfiguration(channelDefinition, writeRequest.Parameters, response);
                if (configuration is not null)
                {
                    response = await _channelTransport.UpdateChannelAsync(
                        writeRequest with { Parameters = configuration }, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is AgentHostingException or HttpRequestException or OperationCanceledException)
            {
                return (response,
                [
                    $"The initial write for the {channelDefinition.Name} channel succeeded, but configuring the new sites did not complete. " +
                    $"Sites may remain at service defaults; no rollback was attempted. {ex.Message}",
                ]);
            }
        }

        return (response, []);
    }

    private DeploymentResult<DeploymentChannelRecord> OpenAuthorization(
        DeploymentResult<DeploymentChannelRecord> result,
        bool nonInteractive)
    {
        if (result.Payload?.ChannelName != "slack")
        {
            return result;
        }

        var guidance = DeploymentChannelSlack.AuthorizationGuidance;
        if (result.AuthorizationUrl is { } url && !nonInteractive && _browser is not null)
        {
            try
            {
                _browser.Launch(url);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                guidance = $"The browser could not be opened. {guidance}";
            }
        }

        return result with { Warnings = [.. result.Warnings, guidance] };
    }

    /// <summary>
    /// Saves or removes a channel in the deployment record, keyed by friendly channel name.
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
            channels.Add(channel with { RawArmResponse = null, AuthorizationUrl = null });
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
                Channels = [.. channels.Select(channelRecord => channelRecord with { RawArmResponse = null, AuthorizationUrl = null })],
            });

    private static DeploymentResult<DeploymentChannelRecord> ToChannelResult(
        DeploymentOperationStatus status,
        DeploymentChannelRecord? channel = null,
        string? targetInstanceName = null,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Status = status,
            Payload = channel,
            TargetName = "Channel",
            TargetInstanceName = targetInstanceName ?? channel?.ChannelName,
            Warnings = warnings ?? [],
            AuthorizationUrl = channel?.AuthorizationUrl,
        };
}
