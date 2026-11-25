using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Manages the channels of the Azure Bot Service instance AMS bound to a code-agent deployment, validating channel content with the local-mode rules before any call.</summary>
public sealed class AmsChannelService :
    ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelCreateRequest>, DeploymentResult<DeploymentChannelRecord>>,
    ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelUpdateRequest>, DeploymentResult<DeploymentChannelRecord>>,
    ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelDeleteRequest>, DeploymentResult<DeploymentChannelRecord>>,
    ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelShowRequest>, DeploymentResult<DeploymentChannelRecord>>,
    ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelListRequest>, DeploymentResult<DeploymentChannelRecord[]>>,
    ICommandHandler<AgentDeploymentAmsChannelTypesRequest, DeploymentResult<DeploymentChannelType[]>>,
    ICommandHandler<AgentDeploymentAmsChannelTokenRequest, DeploymentResult<DeploymentChannelToken>>,
    ICommandHandler<AgentDeploymentAmsChannelWebhookRequest, DeploymentResult<DeploymentChannelWebhook>>
{
    private const string DirectLineUserIdPrefix = "dl_";

    private readonly AmsAddressResolver _addresses;
    private readonly IAmsChannelTransport _channels;
    private readonly IBrowserLauncher? _browser;

    /// <summary>Creates the service over the AMS channel transport, with the browser that opens Slack's authorization URL when one is available.</summary>
    public AmsChannelService(AmsAddressResolver addresses, IAmsChannelTransport channels, IBrowserLauncher? browser = null)
    {
        _addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        _browser = browser;
    }

    /// <summary>Creates a channel with typed properties that carry its complete lists, or reports the existing channel unchanged; a Slack write opens its authorization URL as local mode does.</summary>
    public async Task<DeploymentResult<DeploymentChannelRecord>> CreateChannelAsync(AgentDeploymentAmsChannelRequest<DeploymentChannelCreateRequest> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = DeploymentChannelPlan.ValidateCreateContent(request.Channel);
        var deployment = await ResolveAsync(request, "deployment channel create", cancellationToken).ConfigureAwait(false);
        var written = await _channels.CreateAsync(new AmsChannelCreateTransportRequest { Deployment = deployment, ChannelType = plan.ChannelDefinition.Name, Properties = plan.SiteMutation is { } sites ? DeploymentChannelSites.BuildCreate(sites) : plan.NormalizedParameters }, cancellationToken).ConfigureAwait(false);
        var status = written.IsCreated ? DeploymentChannelSlack.WriteStatus(plan.ChannelDefinition.Name, DeploymentOperationStatus.Created) : DeploymentOperationStatus.Existed;
        return OpenAuthorization(DeploymentChannelService.ToChannelResult(status, written.Channel, warnings: written.Warnings), request.Channel.NonInteractive);
    }

    /// <summary>Sends typed properties, whole lists included, or one site, page or registration change, which AMS applies to the channel it reads just before writing; a Slack update answers a new authorization URL.</summary>
    public async Task<DeploymentResult<DeploymentChannelRecord>> UpdateChannelAsync(AgentDeploymentAmsChannelRequest<DeploymentChannelUpdateRequest> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = DeploymentChannelPlan.ValidateUpdateContent(request.Channel);
        var deployment = await ResolveAsync(request, "deployment channel update", cancellationToken).ConfigureAwait(false);
        var (properties, itemChange) = ToUpdateWire(plan);
        var written = await _channels.UpdateAsync(new AmsChannelUpdateTransportRequest { Deployment = deployment, ChannelType = plan.ChannelDefinition.Name, Properties = properties, ItemChange = itemChange }, cancellationToken).ConfigureAwait(false);
        return OpenAuthorization(DeploymentChannelService.ToChannelResult(DeploymentChannelSlack.WriteStatus(plan.ChannelDefinition.Name, DeploymentOperationStatus.Updated), written.Channel, warnings: written.Warnings), request.Channel.NonInteractive);
    }

    /// <summary>Deletes a channel that supports deletion; an absent channel counts as deleted.</summary>
    public async Task<DeploymentResult<DeploymentChannelRecord>> DeleteChannelAsync(AgentDeploymentAmsChannelRequest<DeploymentChannelDeleteRequest> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channel = DeploymentChannelPlan.ValidateDeleteContent(request.Channel);
        var deployment = await ResolveAsync(request, "deployment channel delete", cancellationToken).ConfigureAwait(false);
        await _channels.DeleteAsync(new AmsChannelTransportRequest { Deployment = deployment, ChannelType = channel.ChannelName }, cancellationToken).ConfigureAwait(false);
        return DeploymentChannelService.ToChannelResult(DeploymentOperationStatus.Deleted, targetInstanceName: channel.ChannelName);
    }

    /// <summary>Reads one channel as AMS projects it from the bot.</summary>
    public async Task<DeploymentResult<DeploymentChannelRecord>> GetChannelAsync(AgentDeploymentAmsChannelRequest<DeploymentChannelShowRequest> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channel = DeploymentChannelPlan.ValidateShowContent(request.Channel);
        var deployment = await ResolveAsync(request, "deployment channel show", cancellationToken).ConfigureAwait(false);
        return DeploymentChannelService.ToChannelResult(DeploymentOperationStatus.Retrieved, await _channels.GetAsync(new AmsChannelTransportRequest { Deployment = deployment, ChannelType = channel.ChannelName }, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Lists the channels AMS manages on the bot.</summary>
    public async Task<DeploymentResult<DeploymentChannelRecord[]>> ListChannelsAsync(AgentDeploymentAmsChannelRequest<DeploymentChannelListRequest> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deployment = await ResolveAsync(request, "deployment channel list", cancellationToken).ConfigureAwait(false);
        return DeploymentChannelService.ToChannelListResult([.. await _channels.ListAsync(deployment, cancellationToken).ConfigureAwait(false)]);
    }

    /// <summary>Lists the channel types AMS can create and edit, with their operations and parameters.</summary>
    public async Task<DeploymentResult<DeploymentChannelType[]>> ListChannelTypesAsync(AgentDeploymentAmsChannelTypesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deployment = await ResolveAsync(request, "deployment channel types", cancellationToken).ConfigureAwait(false);
        return new() { Status = DeploymentOperationStatus.Listed, Payload = [.. await _channels.ListTypesAsync(deployment, cancellationToken).ConfigureAwait(false)], TargetName = "ChannelTypes" };
    }

    /// <summary>Asks AMS for a Direct Line token for one conversation; the site key never leaves AMS.</summary>
    public async Task<DeploymentResult<DeploymentChannelToken>> CreateTokenAsync(AgentDeploymentAmsChannelTokenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateOperation(request.ChannelName, "token");
        var siteName = DeploymentRequestValidator.Optional("--site", request.SiteName);
        var userId = ValidateUserId(request.UserId);
        var deployment = await ResolveAsync(request, "deployment channel token", cancellationToken).ConfigureAwait(false);
        var token = await _channels.CreateDirectLineTokenAsync(new AmsDirectLineTokenTransportRequest { Deployment = deployment, SiteName = siteName, UserId = userId }, cancellationToken).ConfigureAwait(false);
        return new() { Status = DeploymentOperationStatus.Created, Payload = token, TargetName = "Token", TargetInstanceName = token.SiteName };
    }

    /// <summary>Asks AMS for the callback URL and verify token that the channel's webhook settings in the third-party console need.</summary>
    public async Task<DeploymentResult<DeploymentChannelWebhook>> GetWebhookAsync(AgentDeploymentAmsChannelWebhookRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channelName = ValidateOperation(request.ChannelName, "webhook");
        var deployment = await ResolveAsync(request, "deployment channel webhook", cancellationToken).ConfigureAwait(false);
        return new() { Status = DeploymentOperationStatus.Retrieved, Payload = await _channels.GetWebhookAsync(new AmsChannelTransportRequest { Deployment = deployment, ChannelType = channelName }, cancellationToken).ConfigureAwait(false), TargetName = "Webhook", TargetInstanceName = channelName };
    }

    Task<DeploymentResult<DeploymentChannelRecord>> ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelCreateRequest>, DeploymentResult<DeploymentChannelRecord>>.HandleAsync(AgentDeploymentAmsChannelRequest<DeploymentChannelCreateRequest> request, CancellationToken cancellationToken) => CreateChannelAsync(request, cancellationToken);

    Task<DeploymentResult<DeploymentChannelRecord>> ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelUpdateRequest>, DeploymentResult<DeploymentChannelRecord>>.HandleAsync(AgentDeploymentAmsChannelRequest<DeploymentChannelUpdateRequest> request, CancellationToken cancellationToken) => UpdateChannelAsync(request, cancellationToken);

    Task<DeploymentResult<DeploymentChannelRecord>> ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelDeleteRequest>, DeploymentResult<DeploymentChannelRecord>>.HandleAsync(AgentDeploymentAmsChannelRequest<DeploymentChannelDeleteRequest> request, CancellationToken cancellationToken) => DeleteChannelAsync(request, cancellationToken);

    Task<DeploymentResult<DeploymentChannelRecord>> ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelShowRequest>, DeploymentResult<DeploymentChannelRecord>>.HandleAsync(AgentDeploymentAmsChannelRequest<DeploymentChannelShowRequest> request, CancellationToken cancellationToken) => GetChannelAsync(request, cancellationToken);

    Task<DeploymentResult<DeploymentChannelRecord[]>> ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelListRequest>, DeploymentResult<DeploymentChannelRecord[]>>.HandleAsync(AgentDeploymentAmsChannelRequest<DeploymentChannelListRequest> request, CancellationToken cancellationToken) => ListChannelsAsync(request, cancellationToken);

    Task<DeploymentResult<DeploymentChannelType[]>> ICommandHandler<AgentDeploymentAmsChannelTypesRequest, DeploymentResult<DeploymentChannelType[]>>.HandleAsync(AgentDeploymentAmsChannelTypesRequest request, CancellationToken cancellationToken) => ListChannelTypesAsync(request, cancellationToken);

    Task<DeploymentResult<DeploymentChannelToken>> ICommandHandler<AgentDeploymentAmsChannelTokenRequest, DeploymentResult<DeploymentChannelToken>>.HandleAsync(AgentDeploymentAmsChannelTokenRequest request, CancellationToken cancellationToken) => CreateTokenAsync(request, cancellationToken);

    Task<DeploymentResult<DeploymentChannelWebhook>> ICommandHandler<AgentDeploymentAmsChannelWebhookRequest, DeploymentResult<DeploymentChannelWebhook>>.HandleAsync(AgentDeploymentAmsChannelWebhookRequest request, CancellationToken cancellationToken) => GetWebhookAsync(request, cancellationToken);

    private static string ValidateOperation(string? channelName, string operation)
    {
        var validatedName = DeploymentChannelCatalog.ValidateChannelName(channelName);
        return DeploymentChannelCatalog.SupportsOperation(DeploymentChannelCatalog.FindChannelDefinition(validatedName, ChannelKeyType.Name), operation) ? validatedName : throw DeploymentException.ChannelOperationNotSupported(validatedName, operation);
    }

    private static string? ValidateUserId(string? value) => DeploymentRequestValidator.Optional("--user-id", value) switch
    {
        null => null,
        var userId when userId.Length > DirectLineUserIdPrefix.Length && userId.StartsWith(DirectLineUserIdPrefix, StringComparison.Ordinal) => userId,
        _ => throw new UsageException($"--user-id must start with {DirectLineUserIdPrefix} and name a user, for example {DirectLineUserIdPrefix}tester."),
    };

    private static (IReadOnlyDictionary<string, JsonElement>? Properties, AmsChannelItemChange? ItemChange) ToUpdateWire(DeploymentChannelOperationPlan<DeploymentChannelUpdateRequest> plan) => plan switch
    {
        { SiteMutation: { Operation: ChannelCollectionOperation.Replace } sites } => (DeploymentChannelSites.BuildCreate(sites), null),
        { SiteMutation: { } sites } => (null, ToItemChange(sites.Operation, sites.Items)),
        { PageMutation: { } page } => (null, ToItemChange(page.Operation, [page.Item])),
        { RegistrationMutation: { } registration } => (null, ToItemChange(registration.Operation, [registration.Item])),
        _ => (plan.NormalizedParameters, null),
    };

    private static AmsChannelItemChange ToItemChange(ChannelCollectionOperation operation, IReadOnlyList<JsonElement> items) => new(operation.ToString().ToLowerInvariant(), items);

    private Task<AmsDeploymentReadTransportRequest> ResolveAsync(AgentDeploymentAmsRequest request, string operation, CancellationToken cancellationToken) => _addresses.ResolveBotDeploymentAsync(request, operation, cancellationToken);

    private DeploymentResult<DeploymentChannelRecord> OpenAuthorization(DeploymentResult<DeploymentChannelRecord> result, bool nonInteractive) => DeploymentChannelSlack.OpenAuthorization(result, nonInteractive, _browser, DeploymentChannelSlack.AmsAuthorizationGuidance);
}
