using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

public sealed class DeploymentChannelAdapter : DeploymentArmClient, IDeploymentChannelTransport
{
    public DeploymentChannelAdapter(
        HttpClient httpClient,
        ITokenProvider tokenProvider,
        DeploymentTargetConfig? config = null,
        string binaryName = "msagent")
        : base(httpClient, tokenProvider, config, binaryName)
    {
    }

    public async Task<DeploymentChannelRecord?> GetChannelAsync(
        string channelArmName,
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await RequestAsync(
                HttpMethod.Get,
                BuildChannelUrl(botResourceId, channelArmName),
                body: null,
                targetTenantId,
                cancellationToken).ConfigureAwait(false);

            return MapChannel(ParseObject(body));
        }
        catch (TargetRequestException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task<DeploymentChannelRecord> CreateChannelAsync(
        BotServiceChannelWriteRequest request,
        CancellationToken cancellationToken) =>
        WriteChannelAsync(request, HttpMethod.Put, cancellationToken);

    public Task<DeploymentChannelRecord> UpdateChannelAsync(
        BotServiceChannelWriteRequest request,
        CancellationToken cancellationToken) =>
        WriteChannelAsync(request, HttpMethod.Patch, cancellationToken);

    private async Task<DeploymentChannelRecord> WriteChannelAsync(
        BotServiceChannelWriteRequest request,
        HttpMethod method,
        CancellationToken cancellationToken)
    {
        var outerProperties = new Dictionary<string, object?>
        {
            ["channelName"] = request.ChannelArmName,
        };
        if (request.Parameters.Count > 0)
        {
            outerProperties["properties"] = request.Parameters;
        }

        var payload = new
        {
            location = "global",
            properties = outerProperties,
        };

        var body = await RequestAsync(
            method,
            BuildChannelUrl(request.BotResourceId, request.ChannelArmName),
            payload,
            request.TenantId,
            cancellationToken).ConfigureAwait(false);

        var root = ParseObject(body);
        var authorizationUrl = request.ChannelArmName == "SlackChannel"
            ? DeploymentChannelSlack.ExtractAuthorizationUrl(root)
            : null;
        return MapChannel(root) with
        {
            AuthorizationUrl = authorizationUrl,
        };
    }

    public async Task<bool> DeleteChannelAsync(
        string channelArmName,
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        try
        {
            await RequestAsync(
                HttpMethod.Delete,
                BuildChannelUrl(botResourceId, channelArmName),
                body: null,
                targetTenantId,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TargetRequestException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<DeploymentChannelRecord>> ListChannelsAsync(
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        var channels = new List<DeploymentChannelRecord>();
        var next = BuildChannelsUrl(botResourceId);
        while (!string.IsNullOrWhiteSpace(next))
        {
            var body = await RequestAsync(
                HttpMethod.Get,
                next,
                body: null,
                targetTenantId,
                cancellationToken).ConfigureAwait(false);
            channels.AddRange(ParseChannelList(body));
            next = ParseNextLink(body);
        }

        return channels;
    }

    private string BuildChannelUrl(string botResourceId, string channelArmName) =>
        $"{Config.ArmEndpoint}{BotServiceResourceId.ToRequestPath(botResourceId)}" +
        $"/channels/{Uri.EscapeDataString(channelArmName)}" +
        $"?api-version={Config.BotServiceApiVersion}";

    private string BuildChannelsUrl(string botResourceId) =>
        $"{Config.ArmEndpoint}{BotServiceResourceId.ToRequestPath(botResourceId)}/channels" +
        $"?api-version={Config.BotServiceApiVersion}";

    private static DeploymentChannelRecord MapChannel(JsonElement root, bool allowAllChannels = false)
    {
        var channelArmName = GetString(GetObject(root, "properties", isOptional: false)!.Value, "channelName");
        if (string.IsNullOrWhiteSpace(channelArmName))
        {
            throw InvalidResponse("The response did not contain properties.channelName");
        }

        var channelDefinition = DeploymentChannelCatalog.FindChannelDefinitionOrDefault(
            channelArmName,
            ChannelKeyType.ArmName);

        if (channelDefinition is null && !allowAllChannels)
        {
            throw DeploymentException.ChannelDefinitionNotFound(channelArmName);
        }

        var redactedResponse = BotServiceResponseRedactor.RedactChannel(root, channelDefinition);

        return new DeploymentChannelRecord
        {
            ResourceId = GetString(redactedResponse, "id")!,
            ChannelName = channelDefinition?.Name ?? channelArmName,
            Parameters = channelDefinition is { IsSupported: true }
                ? ExtractDeclaredChannelProperties(redactedResponse, channelDefinition)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            RawArmResponse = redactedResponse,
        };
    }

    private static List<DeploymentChannelRecord> ParseChannelList(string? body)
    {
        var channels = new List<DeploymentChannelRecord>();
        foreach (var element in ParseArray(body))
        {
            channels.Add(MapChannel(element, allowAllChannels: true));
        }

        return channels;
    }

    private static Dictionary<string, JsonElement> ExtractDeclaredChannelProperties(
          JsonElement channelResponse,
          ChannelDefinition channelDefinition)
    {
        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var channelProperties = GetObject(
            GetObject(channelResponse, "properties", isOptional: false)!.Value,
            "properties",
            isOptional: true);
        if (channelProperties is not { } properties)
        {
            return parameters;
        }

        return DeploymentChannelCatalog.ProjectChannelParameters(properties, channelDefinition);
    }
}
