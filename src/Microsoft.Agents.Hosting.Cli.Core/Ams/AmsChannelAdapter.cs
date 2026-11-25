using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using static Microsoft.Agents.Hosting.Cli.Core.Ams.AmsJson;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Manages the channels of the Azure Bot Service instance AMS bound to a code-agent deployment, through the AMS control plane.</summary>
public sealed class AmsChannelAdapter : IAmsChannelTransport
{
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);

    private readonly AmsRequestSender _sender;

    /// <summary>Creates the adapter over a client whose handler must not follow redirects.</summary>
    public AmsChannelAdapter(HttpClient httpClient, ITokenProvider tokens, PpapiTokenClaimsValidator? claimsValidator = null, IProfileResourceTokenProvider? profileTokens = null) => _sender = new AmsRequestSender(httpClient, tokens, claimsValidator, profileTokens);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeploymentChannelType>> ListTypesAsync(AmsDeploymentReadTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await SendAsync(HttpMethod.Get, AmsRoutes.ChannelTypes(request.ServiceTarget, request.AgentId, request.DeploymentId), request, PowerPlatformApiPermissions.AppsRead, "Channel type list", channelType: null, content: null, cancellationToken).ConfigureAwait(false);
        return Parse(response, "Channel type list", "a valid channel type list", root => GetRequiredArray(root, "value").Select(ParseType).ToArray());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeploymentChannelRecord>> ListAsync(AmsDeploymentReadTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await SendAsync(HttpMethod.Get, AmsRoutes.Channels(request.ServiceTarget, request.AgentId, request.DeploymentId), request, PowerPlatformApiPermissions.AppsRead, "Channel list", channelType: null, content: null, cancellationToken).ConfigureAwait(false);
        return Parse(response, "Channel list", "a valid channel list", root => GetRequiredArray(root, "value").Select(ParseChannel).ToArray());
    }

    /// <inheritdoc />
    public async Task<DeploymentChannelRecord> GetAsync(AmsChannelTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deployment = request.Deployment;
        var response = await SendAsync(HttpMethod.Get, AmsRoutes.Channel(deployment.ServiceTarget, deployment.AgentId, deployment.DeploymentId, request.ChannelType), deployment, PowerPlatformApiPermissions.AppsRead, "Channel read", request.ChannelType, content: null, cancellationToken).ConfigureAwait(false);
        return Parse(response, "Channel read", "a valid channel", ParseChannel);
    }

    /// <inheritdoc />
    public async Task<AmsChannelWriteResult> CreateAsync(AmsChannelCreateTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deployment = request.Deployment;
        using var content = JsonContent.Create(new ChannelCreateWireRequest { ChannelType = request.ChannelType, Properties = request.Properties }, options: RequestJson);
        var response = await SendAsync(HttpMethod.Post, AmsRoutes.Channels(deployment.ServiceTarget, deployment.AgentId, deployment.DeploymentId), deployment, PowerPlatformApiPermissions.AppsUpdate, "Channel create", request.ChannelType, content, cancellationToken, outcomeMayBeIndeterminate: true).ConfigureAwait(false);
        return ParseWrite(response, "Channel create", isCreated: response.StatusCode == HttpStatusCode.Created);
    }

    /// <inheritdoc />
    public async Task<AmsChannelWriteResult> UpdateAsync(AmsChannelUpdateTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deployment = request.Deployment;
        using var content = JsonContent.Create(new ChannelUpdateWireRequest { Properties = request.Properties, ItemChange = request.ItemChange }, options: RequestJson);
        var response = await SendAsync(HttpMethod.Patch, AmsRoutes.Channel(deployment.ServiceTarget, deployment.AgentId, deployment.DeploymentId, request.ChannelType), deployment, PowerPlatformApiPermissions.AppsUpdate, "Channel update", request.ChannelType, content, cancellationToken, outcomeMayBeIndeterminate: true).ConfigureAwait(false);
        return ParseWrite(response, "Channel update", isCreated: false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(AmsChannelTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deployment = request.Deployment;
        await SendAsync(HttpMethod.Delete, AmsRoutes.Channel(deployment.ServiceTarget, deployment.AgentId, deployment.DeploymentId, request.ChannelType), deployment, PowerPlatformApiPermissions.AppsUpdate, "Channel delete", request.ChannelType, content: null, cancellationToken, outcomeMayBeIndeterminate: true).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DeploymentChannelToken> CreateDirectLineTokenAsync(AmsDirectLineTokenTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deployment = request.Deployment;
        using var content = JsonContent.Create(new DirectLineTokenWireRequest { SiteName = request.SiteName, UserId = request.UserId }, options: RequestJson);
        var response = await SendAsync(HttpMethod.Post, AmsRoutes.DirectLineToken(deployment.ServiceTarget, deployment.AgentId, deployment.DeploymentId), deployment, PowerPlatformApiPermissions.AppsUpdate, "Direct Line token request", "directline", content, cancellationToken, issuesToken: true).ConfigureAwait(false);
        return Parse(response, "Direct Line token request", "a valid token", root => new DeploymentChannelToken
        {
            Token = GetRequiredString(root, "token"),
            ConversationId = GetRequiredString(root, "conversationId"),
            ExpiresInSeconds = GetRequiredInt64(root, "expiresInSeconds") is var seconds and > 0 and <= int.MaxValue ? (int)seconds : throw new FormatException("expiresInSeconds is invalid."),
            SiteName = GetRequiredString(root, "siteName"),
            Endpoint = GetRequiredString(root, "endpoint"),
        });
    }

    /// <inheritdoc />
    public async Task<DeploymentChannelWebhook> GetWebhookAsync(AmsChannelTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deployment = request.Deployment;
        var response = await SendAsync(HttpMethod.Post, AmsRoutes.ChannelWebhook(deployment.ServiceTarget, deployment.AgentId, deployment.DeploymentId, request.ChannelType), deployment, PowerPlatformApiPermissions.AppsUpdate, "Channel webhook request", request.ChannelType, content: null, cancellationToken).ConfigureAwait(false);
        return Parse(response, "Channel webhook request", "valid webhook values", root => new DeploymentChannelWebhook { CallbackUrl = GetRequiredString(root, "callbackUrl"), VerifyToken = GetRequiredString(root, "verifyToken") });
    }

    private Task<AmsResponse> SendAsync(HttpMethod method, Uri uri, AmsDeploymentReadTransportRequest deployment, string permission, string phase, string? channelType, HttpContent? content, CancellationToken cancellationToken, bool outcomeMayBeIndeterminate = false, bool issuesToken = false) =>
        _sender.SendAsync(AmsBotServiceCall.Create(method, uri, deployment, permission, phase, content, (status, code, serviceMessage) => Refusal(status, code, serviceMessage, channelType, issuesToken), outcomeMayBeIndeterminate), cancellationToken);

    private static (string Message, int ExitCode, string ErrorKind, string Remediation)? Refusal(int status, string? code, string? serviceMessage, string? channelType, bool issuesToken)
    {
        bool Is(string expected) => AmsBotServiceCall.IsCode(code, expected);
        return status switch
        {
            (int)HttpStatusCode.NotFound when Is("ChannelNotFound") => ($"The Azure Bot Service instance of the deployment has no '{channelType}' channel.", ExitCodes.Usage, "channel-not-found", "Create the channel with `msagent deployment channel create`, or list the channels AMS manages with `msagent deployment channel list`."),
            (int)HttpStatusCode.BadRequest when issuesToken && Is("InvalidChannelRequest") => ($"AMS could not issue a Direct Line token: {AmsRefusals.Echo(serviceMessage, "no site of the deployment's Direct Line channel can issue one.")}", ExitCodes.Usage, "invalid-channel-request", "A token needs a site of the deployment's Direct Line channel that is enabled and accepts Direct Line 3.0 clients, and a new bot can list no site until its Direct Line channel is first updated. With the same agent and deployment options, list the sites with `msagent deployment channel show --channel directline`, then pass a usable one with --site, add one with `msagent deployment channel update --channel directline --add-site siteName=<name> isV3Enabled=true`, or turn one on with `msagent deployment channel update --channel directline --set-site siteName=<name> isEnabled=true isV3Enabled=true`."),
            (int)HttpStatusCode.BadRequest when Is("InvalidChannelRequest") => ($"AMS refused the channel request: {serviceMessage ?? "it does not satisfy the AMS channel catalog."}", ExitCodes.Usage, "invalid-channel-request", "Correct the request as the message describes. `msagent deployment channel types` lists the channel types, operations and parameters AMS accepts."),
            (int)HttpStatusCode.BadRequest when Is("ChannelSettingsRefused") => ($"Azure Bot Service refused the credentials or settings of the '{channelType}' channel, so AMS did not change it.", ExitCodes.Usage, "channel-settings-refused", "Check the credentials and settings in --parameters against the third-party console, then retry. `msagent deployment channel types` lists the parameters of each channel type."),
            (int)HttpStatusCode.NotImplemented when Is("DirectLineRegionNotSupported") => ("AMS cannot issue a Direct Line token for the deployment's bot, because the bot is outside the 'global' location or this cloud has no Direct Line endpoint, so no token was issued.", ExitCodes.Failure, "directline-unavailable", "AMS issues Direct Line tokens only for bots in the 'global' location, in clouds where it has a Direct Line endpoint; ask the AMS team about the deployment's bot."),
            (int)HttpStatusCode.ServiceUnavailable when Is("AgentBotServiceChannelFailed") => ("Azure Bot Service or Direct Line was temporarily unavailable, so AMS could not complete the channel operation.", ExitCodes.Failure, "channel-operation-failed", "Retry the command. A change may still have reached the channel, so check it with `msagent deployment channel show` before you change it again."),
            (int)HttpStatusCode.BadGateway when Is("AgentBotServiceChannelFailed") => ("Azure Bot Service or Direct Line refused the channel operation AMS sent.", ExitCodes.Failure, "channel-operation-failed", "Check the channel with `msagent deployment channel show`, then retry; if it fails again, ask the AMS team to investigate with the correlation identifiers."),
            _ => null,
        };
    }

    private static AmsChannelWriteResult ParseWrite(AmsResponse response, string phase, bool isCreated) => Parse(response, phase, "a valid channel", root => new AmsChannelWriteResult { Channel = ParseChannel(root) with { AuthorizationUrl = DeploymentChannelSlack.ParseAuthorizationUrl(GetOptionalString(root, "authorizationUrl")) }, IsCreated = isCreated, Warnings = GetOptionalStrings(root, "warnings") ?? [] });

    private static T Parse<T>(AmsResponse response, string phase, string subject, Func<JsonElement, T> read) => AmsBotServiceCall.Read(response, phase, subject, "channel", read);

    private static DeploymentChannelRecord ParseChannel(JsonElement root) => new()
    {
        ResourceId = GetRequiredString(root, "id"),
        ChannelName = GetRequiredString(root, "channelType"),
        Parameters = GetRequiredProperty(root, "properties") is { ValueKind: JsonValueKind.Object } properties
            ? properties.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal)
            : throw new FormatException("properties is invalid."),
    };

    private static DeploymentChannelType ParseType(JsonElement root) => new()
    {
        ChannelType = GetRequiredString(root, "channelType"),
        DisplayName = GetRequiredString(root, "displayName"),
        Operations = GetOptionalStrings(root, "operations") ?? throw new FormatException("operations is missing."),
        Parameters = GetRequiredArray(root, "parameters").Select(ParseParameter).ToArray(),
    };

    private static DeploymentChannelTypeParameter ParseParameter(JsonElement root) => new()
    {
        Name = GetRequiredString(root, "name"),
        Type = GetRequiredString(root, "type"),
        Required = GetOptionalBoolean(root, "required") ?? false,
        ReadOnly = GetOptionalBoolean(root, "readOnly") ?? false,
        Clearable = GetOptionalBoolean(root, "clearable") ?? false,
        IsSecret = GetOptionalBoolean(root, "isSecret") ?? false,
        IsSelector = GetOptionalBoolean(root, "isSelector") ?? false,
        AllowedValues = GetOptionalStrings(root, "allowedValues"),
        DefaultValue = GetOptionalProperty(root, "defaultValue")?.Clone(),
        ItemParameters = GetOptionalArray(root, "itemParameters")?.Select(ParseParameter).ToArray(),
    };

    private sealed record ChannelCreateWireRequest
    {
        public required string ChannelType { get; init; }
        public required IReadOnlyDictionary<string, JsonElement> Properties { get; init; }
    }

    private sealed record ChannelUpdateWireRequest
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyDictionary<string, JsonElement>? Properties { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AmsChannelItemChange? ItemChange { get; init; }
    }

    private sealed record DirectLineTokenWireRequest
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SiteName { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UserId { get; init; }
    }
}
