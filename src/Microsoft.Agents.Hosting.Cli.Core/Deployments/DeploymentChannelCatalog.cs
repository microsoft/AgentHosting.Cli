using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

public enum ChannelKeyType
{
    Name,
    ArmName,
}

public sealed record ChannelParameterDefinition(
    DeploymentParameterType Type,
    string Name,
    string Description,
    string? DefaultValue = null,
    bool IsRequired = false,
    bool IsSecret = false,
    bool IsReadOnly = false);

public sealed record ChannelDefinition(
    string Name,
    string ArmName,
    bool IsSupported,
    IReadOnlyList<ChannelParameterDefinition> Parameters,
    bool IsNestedParameters = false);

public static class DeploymentChannelCatalog
{
    internal const string TrustedOriginsParameterName = "trustedOrigins";

    internal static IReadOnlyList<ChannelDefinition> Channels { get; } =
    [
        Supported(
            "directline",
            "DirectLineChannel",
            isNestedParameters: true,
            new(DeploymentParameterType.String, "appId", "The application id associated with the channel site."),
            new(DeploymentParameterType.Boolean, "isBlockUserUploadEnabled", "Whether user file uploads are blocked."),
            new(DeploymentParameterType.Boolean, "isDetailedLoggingEnabled", "Whether detailed logging is enabled."),
            new(DeploymentParameterType.Boolean, "isEnabled", "Whether the channel is enabled.", DefaultValue: "true"),
            new(DeploymentParameterType.Boolean, "isEndpointParametersEnabled", "Whether endpoint parameters are enabled."),
            new(DeploymentParameterType.Boolean, "isNoStorageEnabled", "Whether storage is disabled."),
            new(DeploymentParameterType.Boolean, "isSecureSiteEnabled", "Whether enhanced authentication is enabled."),
            new(DeploymentParameterType.Boolean, "isTokenEnabled", "Whether token access is enabled."),
            new(DeploymentParameterType.Boolean, "isV1Enabled", "Whether Direct Line v1 is enabled.", IsReadOnly: true),
            new(DeploymentParameterType.Boolean, "isV3Enabled", "Whether Direct Line v3 is enabled."),
            new(
                DeploymentParameterType.String,
                "siteName",
                "The channel site name.",
                IsRequired: true),
            new(DeploymentParameterType.String, "tenantId", "The tenant id associated with the channel site."),
            new(DeploymentParameterType.String, TrustedOriginsParameterName, "Comma-separated trusted origins.")),
        Supported(
            "msteams",
            "MsTeamsChannel",
            isNestedParameters: false,
            new(DeploymentParameterType.Boolean, "acceptedTerms", "Whether the channel terms are accepted."),
            new(DeploymentParameterType.String, "callingWebhook", "The calling webhook URL."),
            new(
                DeploymentParameterType.String,
                "deploymentEnvironment",
                "The channel deployment environment.",
                DefaultValue: "CommercialDeployment"),
            new(DeploymentParameterType.Boolean, "enableCalling", "Whether calling is enabled."),
            new(DeploymentParameterType.String, "incomingCallRoute", "The incoming call route."),
            new(DeploymentParameterType.Boolean, "isEnabled", "Whether the channel is enabled.", DefaultValue: "true")),
        Supported(
            "m365extensions",
            "M365Extensions",
            isNestedParameters: false),
        Supported(
            "slack",
            "SlackChannel",
            isNestedParameters: false,
            new(DeploymentParameterType.String, "clientId", "The Slack client id.", IsRequired: true),
            new(DeploymentParameterType.String, "clientSecret", "The Slack client secret.", IsRequired: true, IsSecret: true),
            new(DeploymentParameterType.Boolean, "isEnabled", "Whether the channel is enabled.", DefaultValue: "true"),
            new(DeploymentParameterType.String, "landingPageUrl", "The Slack landing page URL."),
            new(DeploymentParameterType.Boolean, "registerBeforeOAuthFlow", "Whether to register before OAuth.", DefaultValue: "true"),
            new(DeploymentParameterType.String, "scopes", "The Slack scopes."),
            new(DeploymentParameterType.String, "signingSecret", "The Slack signing secret.", IsSecret: true),
            new(DeploymentParameterType.String, "verificationToken", "The Slack verification token.", IsRequired: true, IsSecret: true)),
        Supported(
            "webchat",
            "WebChatChannel",
            isNestedParameters: true,
            new(DeploymentParameterType.String, "appId", "The application id associated with the channel site."),
            new(DeploymentParameterType.Boolean, "isBlockUserUploadEnabled", "Whether user file uploads are blocked."),
            new(DeploymentParameterType.Boolean, "isDetailedLoggingEnabled", "Whether detailed logging is enabled."),
            new(DeploymentParameterType.Boolean, "isEnabled", "Whether the channel is enabled.", DefaultValue: "true"),
            new(DeploymentParameterType.Boolean, "isEndpointParametersEnabled", "Whether endpoint parameters are enabled."),
            new(DeploymentParameterType.Boolean, "isNoStorageEnabled", "Whether storage is disabled."),
            new(DeploymentParameterType.Boolean, "isTokenEnabled", "Whether token access is enabled."),
            new(DeploymentParameterType.Boolean, "isWebchatPreviewEnabled", "Whether Web Chat preview is enabled."),
            new(
                DeploymentParameterType.String,
                "siteName",
                "The channel site name.",
                IsRequired: true),
            new(DeploymentParameterType.String, "tenantId", "The tenant id associated with the channel site.")),
        Recognized("acschat", "AcsChatChannel"),
        Recognized("alexa", "AlexaChannel"),
        Recognized("directlinespeech", "DirectLineSpeechChannel"),
        Recognized("email", "EmailChannel"),
        Recognized("facebook", "FacebookChannel"),
        Recognized("kik", "KikChannel"),
        Recognized("line", "LineChannel"),
        Recognized("omnichannel", "Omnichannel"),
        Recognized("outlook", "OutlookChannel"),
        Recognized("searchassistant", "SearchAssistant"),
        Recognized("skype", "SkypeChannel"),
        Recognized("sms", "SmsChannel"),
        Recognized("telegram", "TelegramChannel"),
        Recognized("telephony", "TelephonyChannel"),
    ];

    public static IReadOnlyList<string> SupportedChannelNames { get; } =
        Channels.Where(channel => channel.IsSupported)
            .Select(channel => channel.Name)
            .OrderBy(channelName => channelName, StringComparer.Ordinal)
            .ToArray();

    private static ChannelDefinition Supported(
        string name,
        string armName,
        bool isNestedParameters,
        params ChannelParameterDefinition[] parameters) =>
        new(name, armName, IsSupported: true, parameters, isNestedParameters);

    private static ChannelDefinition Recognized(string name, string armName) =>
        new(name, armName, IsSupported: false, []);

    internal static string FindChannelArmName(string name) =>
        FindChannelDefinition(name, ChannelKeyType.Name).ArmName;

    public static ChannelDefinition FindChannelDefinition(
        string key,
        ChannelKeyType keyType) =>
        Channels.FirstOrDefault(candidate =>
            string.Equals(
                keyType == ChannelKeyType.Name ? candidate.Name : candidate.ArmName,
                key.Trim(),
                StringComparison.OrdinalIgnoreCase))
            ?? throw DeploymentException.ChannelDefinitionNotFound(key);

    /// <summary>
    /// Produces the safe canonical channel shape defined by the catalog. Undeclared parameters are removed,
    /// declared secrets are masked, and the raw ARM response is retained only when requested and redacted.
    /// </summary>
    internal static DeploymentChannelRecord Sanitize(
        DeploymentChannelRecord channel,
        bool includeArmResponse = true)
    {
        var definition = FindChannelDefinition(channel.ChannelName, ChannelKeyType.Name);
        var parameters = channel.Parameters
            ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var sanitized = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (!definition.IsNestedParameters)
        {
            foreach (var parameter in definition.Parameters)
            {
                if (TryGetParameter(parameters, parameter.Name, out var value) &&
                    value.ValueKind != JsonValueKind.Undefined)
                {
                    sanitized[parameter.Name] = SanitizeValue(value, parameter.IsSecret);
                }
            }
        }
        else if (TryGetParameter(parameters, "sites", out var sites) &&
            sites.ValueKind == JsonValueKind.Array)
        {
            var sanitizedSites = new List<Dictionary<string, JsonElement>>();
            foreach (var site in sites.EnumerateArray().Where(site => site.ValueKind == JsonValueKind.Object))
            {
                var sanitizedSite = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var parameter in definition.Parameters)
                {
                    var property = site.EnumerateObject().FirstOrDefault(candidate =>
                        candidate.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));
                    if (property.Value.ValueKind != JsonValueKind.Undefined)
                    {
                        sanitizedSite[parameter.Name] = SanitizeValue(property.Value, parameter.IsSecret);
                    }
                }

                sanitizedSites.Add(sanitizedSite);
            }

            sanitized["sites"] = JsonSerializer.SerializeToElement(sanitizedSites);
        }

        return channel with
        {
            ChannelName = definition.Name,
            Parameters = sanitized,
            RawArmResponse = includeArmResponse && channel.RawArmResponse is { } rawArmResponse
                ? BotServiceResponseRedactor.RedactChannel(rawArmResponse, definition)
                : null,
        };
    }

    /// <summary>
    /// Finds a parameter without treating ARM or persisted-property casing as significant; for example,
    /// a stored key named <c>CLIENTSECRET</c> matches the catalog parameter <c>clientSecret</c>.
    /// </summary>
    private static bool TryGetParameter(
        IReadOnlyDictionary<string, JsonElement> parameters,
        string name,
        out JsonElement value)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = parameter.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Clones a safe value for the sanitized record, masking a declared secret while preserving an explicit
    /// JSON <c>null</c>. For example, <c>"secret-1"</c> becomes <c>"***"</c>, a secret <c>null</c>
    /// remains <c>null</c>, and a non-secret value such as <c>true</c> is copied unchanged.
    /// </summary>
    private static JsonElement SanitizeValue(JsonElement value, bool isSecret) =>
        isSecret && value.ValueKind != JsonValueKind.Null
            ? JsonSerializer.SerializeToElement(BotServiceResponseRedactor.Mask)
            : value.Clone();
}
