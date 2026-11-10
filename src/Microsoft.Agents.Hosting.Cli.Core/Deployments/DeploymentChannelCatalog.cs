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
    bool IsReadOnly = false,
    bool IsSelector = false,
    bool IsClearable = false,
    IReadOnlyList<string>? AllowedValues = null,
    IReadOnlyList<ChannelParameterDefinition>? ItemParameters = null)
{
    public bool CanClear => IsClearable && !IsReadOnly && !IsSelector && !IsRequired &&
        AllowedValues is null;
}

public sealed record ChannelDefinition(
    string Name,
    string ArmName,
    bool IsSupported,
    IReadOnlyList<ChannelParameterDefinition> Parameters);

public static class DeploymentChannelCatalog
{
    /// <summary>
    /// Channel parameters for API version 2023-09-15-preview, from
    /// https://learn.microsoft.com/azure/templates/microsoft.botservice/botservices/channels
    /// and https://github.com/Azure/azure-rest-api-specs/blob/main/specification/botservice/resource-manager/Microsoft.BotService/BotService/preview/2023-09-15-preview/botservice.json.
    /// Required, readonly, and clearing rules include verified service behavior and CLI policy.
    /// </summary>
    internal static IReadOnlyList<ChannelDefinition> Channels { get; } =
    [
        new("acschat", "AcsChatChannel", IsSupported: false, Parameters: []),
        new(
            "alexa",
            "AlexaChannel",
            IsSupported: false,
            Parameters:
            [
                new(DeploymentParameterType.String, "alexaSkillId", "The Alexa skill id.", IsRequired: true),
                new(DeploymentParameterType.Boolean, "isEnabled", "Whether the channel is enabled.", IsRequired: true),
            ]),
        new(
            "directline",
            "DirectLineChannel",
            IsSupported: true,
            Parameters:
            [
                new(DeploymentParameterType.String, "directLineEmbedCode", "The Direct Line embed code.",
                    IsSecret: true, IsReadOnly: true),
                new(DeploymentParameterType.String, "extensionKey1", "The primary Direct Line extension key.",
                    IsSecret: true, IsReadOnly: true),
                new(DeploymentParameterType.String, "extensionKey2", "The secondary Direct Line extension key.",
                    IsSecret: true, IsReadOnly: true),
                new(
                    DeploymentParameterType.Array,
                    "sites",
                    "The Direct Line sites:",
                    IsClearable: true,
                    ItemParameters: SiteParameters(isWebChat: false)),
            ]),
        new(
            "directlinespeech",
            "DirectLineSpeechChannel",
            IsSupported: false,
            Parameters:
            [
                new(DeploymentParameterType.String, "cognitiveServiceRegion", "The Cognitive Services region."),
                new(DeploymentParameterType.String, "cognitiveServiceResourceId", "The Cognitive Services resource id."),
                new(DeploymentParameterType.String, "cognitiveServiceSubscriptionKey", "The Cognitive Services subscription key.", IsSecret: true),
                new(DeploymentParameterType.String, "customSpeechModelId", "The custom speech model id.", IsClearable: true),
                new(DeploymentParameterType.String, "customVoiceDeploymentId", "The custom voice deployment id.", IsClearable: true),
                new(DeploymentParameterType.Boolean, "isDefaultBotForCogSvcAccount", "Whether this is the default bot for the Cognitive Services account."),
                new(DeploymentParameterType.Boolean, "isEnabled", "Whether the channel is enabled."),
            ]),
        new(
            "email",
            "EmailChannel",
            IsSupported: false,
            Parameters:
            [
                new(DeploymentParameterType.Integer, "authMethod", "The authentication method: 0 for password or 1 for Graph.",
                    DefaultValue: "0", AllowedValues: ["0", "1"]),
                new(DeploymentParameterType.String, "emailAddress", "The email address.", IsRequired: true),
                new(DeploymentParameterType.Boolean, "isEnabled", "Whether the channel is enabled.", IsRequired: true),
                new(DeploymentParameterType.String, "magicCode", "The code for setting up modern authentication.", IsSecret: true),
                new(DeploymentParameterType.String, "password", "The email account password.", IsSecret: true),
            ]),
        // Azure validates page/app credentials even when disabled and preserves omitted tokens for retained ids.
        new(
            "facebook",
            "FacebookChannel",
            IsSupported: true,
            Parameters:
            [
                new(DeploymentParameterType.String, "appId", "The Facebook application id.", IsRequired: true),
                new(DeploymentParameterType.String, "appSecret", "The Facebook application secret.", IsRequired: true, IsSecret: true),
                new(DeploymentParameterType.String, "callbackUrl", "The Facebook callback URL.", IsReadOnly: true),
                new(DeploymentParameterType.Boolean, "isEnabled", "Whether the channel is enabled."),
                new(DeploymentParameterType.Array, "pages", "The Facebook pages:", IsRequired: true, ItemParameters:
                [
                    new(DeploymentParameterType.String, "accessToken", "The Facebook page access token.", IsRequired: true, IsSecret: true),
                    new(DeploymentParameterType.String, "id", "The Facebook page id.", IsRequired: true, IsSelector: true),
                ]),
                new(DeploymentParameterType.String, "verifyToken", "The Facebook verification token.", IsSecret: true, IsReadOnly: true),
            ]),
        // Azure retains omitted credentials by generatedId; membership/order changes may change callback URLs.
        new(
            "line",
            "LineChannel",
            IsSupported: true,
            Parameters:
            [
                new(DeploymentParameterType.String, "callbackUrl", "The LINE callback URL.", IsReadOnly: true),
                new(DeploymentParameterType.Boolean, "isValidated", "Whether the channel is validated.", IsReadOnly: true),
                new(DeploymentParameterType.Array, "lineRegistrations", "The LINE channel registrations:", IsRequired: true, ItemParameters:
                [
                    new(DeploymentParameterType.String, "channelAccessToken", "The LINE channel access token.", IsRequired: true, IsSecret: true),
                    new(DeploymentParameterType.String, "channelSecret", "The LINE channel secret.", IsRequired: true, IsSecret: true),
                    new(DeploymentParameterType.String, "generatedId", "The Azure-generated registration id.", IsReadOnly: true, IsSelector: true),
                ]),
            ]),
        new("m365extensions", "M365Extensions", IsSupported: true, Parameters: []),
        // Terms acceptance cannot be revoked.
        new(
            "msteams",
            "MsTeamsChannel",
            IsSupported: true,
            Parameters:
            [
                new(DeploymentParameterType.Boolean, "acceptedTerms", "Whether the channel terms are accepted."),
                new(DeploymentParameterType.String, "callingWebhook", "The calling webhook URL."),
                new(
                    DeploymentParameterType.String,
                    "deploymentEnvironment",
                    "The channel deployment environment.",
                    DefaultValue: "CommercialDeployment",
                    AllowedValues: ["CommercialDeployment", "GCCModerateDeployment"]),
                new(DeploymentParameterType.Boolean, "enableCalling", "Whether calling is enabled.", DefaultValue: "false"),
                new(DeploymentParameterType.String, "incomingCallRoute", "The incoming call route."),
                new(DeploymentParameterType.Boolean, "isEnabled", "Whether the channel is enabled.", IsReadOnly: true),
            ]),
        new("omnichannel", "Omnichannel", IsSupported: false, Parameters: []),
        new("outlook", "OutlookChannel", IsSupported: true, Parameters: []),
        new("searchassistant", "SearchAssistant", IsSupported: false, Parameters: []),
        new(
            "skype",
            "SkypeChannel",
            IsSupported: false,
            Parameters:
            [
                new(DeploymentParameterType.String, "callingWebHook", "The Skype calling webhook URL. Can only have a value when enableCalling=true"),
                new(DeploymentParameterType.Boolean, "enableCalling", "Whether calling is enabled.", DefaultValue: "false"),
                new(DeploymentParameterType.Boolean, "enableGroups", "Whether groups are enabled. Can only be true when enableMessaging=true"),
                new(DeploymentParameterType.Boolean, "enableMediaCards", "Whether media cards are enabled. Can only be true when enableMessaging=true."),
                new(DeploymentParameterType.Boolean, "enableMessaging", "Whether messaging is enabled."),
                new(DeploymentParameterType.Boolean, "enableScreenSharing", "Whether screen sharing is enabled.", IsReadOnly: true),
                new(DeploymentParameterType.Boolean, "enableVideo", "Whether video is enabled.", IsReadOnly: true),
                new(DeploymentParameterType.String, "groupsMode", "The Skype group mode.", IsReadOnly: true),
                new(DeploymentParameterType.String, "incomingCallRoute", "The incoming call route. Can only have a value when enableCalling=true"),
                new(DeploymentParameterType.Boolean, "isEnabled", "Whether the channel is enabled.", IsReadOnly: true),
            ]),
        // Authorization URLs are transient; accepted writes do not guarantee persistence.
        // Default scopes match ARM's observed create default.
        new(
            "slack",
            "SlackChannel",
            IsSupported: true,
            Parameters:
            [
                new(DeploymentParameterType.String, "clientId", "The Slack client id.", IsRequired: true),
                new(DeploymentParameterType.String, "clientSecret", "The Slack client secret.", IsRequired: true, IsSecret: true),
                new(DeploymentParameterType.Boolean, "isEnabled", "Whether the channel is enabled."),
                new(DeploymentParameterType.Boolean, "isValidated", "Whether the channel is validated.", IsReadOnly: true),
                new(DeploymentParameterType.String, "landingPageUrl", "The Slack landing page URL."),
                new(DeploymentParameterType.String, "lastSubmissionId", "The last Slack submission id.", IsReadOnly: true),
                new(DeploymentParameterType.String, "redirectAction", "The transient Slack authorization action.", IsSecret: true, IsReadOnly: true),
                new(DeploymentParameterType.Boolean, "registerBeforeOAuthFlow", "The CLI-managed OAuth registration control; writes always send true.", IsReadOnly: true),
                new(DeploymentParameterType.String, "scopes", "The comma-separated Slack scopes.",
                    DefaultValue: "channels:history,channels:read,chat:write,dnd:read,emoji:read,files:read,files:write," +
                        "groups:history,groups:read,im:history,im:read,im:write,mpim:history,mpim:read,mpim:write," +
                        "pins:read,pins:write,reactions:read,reactions:write,team:read,usergroups:read," +
                        "users:read,users:write,users:read.email"),
                new(DeploymentParameterType.String, "signingSecret", "The Slack signing secret.", IsSecret: true),
                new(DeploymentParameterType.String, "verificationToken", "The Slack verification token.", IsSecret: true),
            ]),
        // Azure validates Twilio credentials and phone ownership even with isEnabled=false.
        // The phone must belong to accountSID; Azure retains an omitted authToken.
        new(
            "sms",
            "SmsChannel",
            IsSupported: true,
            Parameters:
            [
                new(DeploymentParameterType.String, "accountSID", "The SMS account SID.", IsRequired: true),
                new(DeploymentParameterType.String, "authToken", "The SMS authentication token.", IsRequired: true, IsSecret: true),
                new(DeploymentParameterType.Boolean, "isEnabled", "Whether the channel is enabled."),
                new(DeploymentParameterType.Boolean, "isValidated", "Whether the channel is validated.", IsReadOnly: true),
                new(DeploymentParameterType.String, "phone", "The SMS-capable Twilio phone number in E.164 format.", IsRequired: true),
            ]),
        new(
            "telegram",
            "TelegramChannel",
            IsSupported: true,
            Parameters:
            [
                new(DeploymentParameterType.String, "accessToken", "The Telegram access token.", IsRequired: true, IsSecret: true),
                new(DeploymentParameterType.Boolean, "isEnabled", "Whether the channel is enabled."),
                new(DeploymentParameterType.Boolean, "isValidated", "Whether the channel is validated.", IsReadOnly: true),
            ]),
        new(
            "webchat",
            "WebChatChannel",
            IsSupported: true,
            Parameters:
            [
                new(
                    DeploymentParameterType.Array,
                    "sites",
                    "The Web Chat sites:",
                    IsClearable: true,
                    ItemParameters: SiteParameters(isWebChat: true)),
                new(DeploymentParameterType.String, "webChatEmbedCode", "The Web Chat embed code.",
                    IsSecret: true, IsReadOnly: true),
            ]),
    ];

    // New sites receive service defaults before a separate settings write.
    private static IReadOnlyList<ChannelParameterDefinition> SiteParameters(bool isWebChat) =>
      [
      new(DeploymentParameterType.String, "appId", "The application id associated with the channel site.", IsClearable: true),
          new(DeploymentParameterType.String, "eTag", "The site entity tag.",
              IsReadOnly: true),
          new(DeploymentParameterType.Boolean, "isBlockUserUploadEnabled", "Whether user file uploads are blocked."),
          new(DeploymentParameterType.Boolean, "isDetailedLoggingEnabled",
              "Whether detailed logging is enabled. Cannot be true with isNoStorageEnabled=true."),
          new(DeploymentParameterType.Boolean, "isEnabled", "Whether the site is enabled."),
          new(DeploymentParameterType.Boolean, "isEndpointParametersEnabled", "Whether endpoint parameters are enabled."),
          new(DeploymentParameterType.Boolean, "isNoStorageEnabled", "Whether storage is disabled."),
          .. isWebChat ? [] : new ChannelParameterDefinition[]
          {
              new(DeploymentParameterType.Boolean, "isSecureSiteEnabled", "Whether enhanced authentication is enabled."),
          },
          new(DeploymentParameterType.Boolean, "isTokenEnabled", "Whether token access is enabled.",
              IsReadOnly: true),
          .. isWebChat ? new ChannelParameterDefinition[]
          {
              new(DeploymentParameterType.Boolean, "isWebchatPreviewEnabled", "Whether Web Chat preview is enabled.", DefaultValue: "false"),
          } : new ChannelParameterDefinition[]
          {
              new(DeploymentParameterType.Boolean, "isV1Enabled", "Whether Direct Line v1 is enabled.",
                  IsReadOnly: true),
              new(DeploymentParameterType.Boolean, "isV3Enabled", "Whether Direct Line v3 is enabled."),
          },
          new(DeploymentParameterType.String, "key", "The primary site key.",
              IsSecret: true, IsReadOnly: true),
          new(DeploymentParameterType.String, "key2", "The secondary site key.",
              IsSecret: true, IsReadOnly: true),
          new(DeploymentParameterType.String, "siteId", "The service-generated site identifier.",
              IsReadOnly: true),
          new(DeploymentParameterType.String, "siteName", "The channel site name.",
              IsRequired: true, IsSelector: true),
          new(DeploymentParameterType.String, "tenantId", "The tenant id associated with the channel site.", IsClearable: true),
          .. (isWebChat ? [] : new ChannelParameterDefinition[]
          {
              new(DeploymentParameterType.Array, "trustedOrigins", "Comma-separated trusted origins or a JSON array of strings.", IsClearable: true),
          }),
          ];


    public static IReadOnlyList<string> SupportedChannelNames { get; } =
        Channels.Where(channel => channel.IsSupported)
            .Select(channel => channel.Name)
            .OrderBy(channelName => channelName, StringComparer.Ordinal)
            .ToArray();

    public static bool SupportsOperation(ChannelDefinition channel, string operation) =>
        channel.IsSupported && operation switch
        {
            "create" or "list" => true,
            "show" or "update" => channel.Parameters.Count > 0,
            "delete" => channel.Name is not ("directline" or "webchat"),
            _ => false,
        };

    public static string ValidateChannelName(string? name) =>
        DeploymentRequestValidator.RequiredEnum(
            string.IsNullOrWhiteSpace(name) ? "--channel" : $"Channel '{name.Trim()}'",
            name,
            SupportedChannelNames);

    internal static string FindChannelArmName(string name) =>
        FindChannelDefinition(name, ChannelKeyType.Name).ArmName;

    public static ChannelDefinition FindChannelDefinition(
        string key,
        ChannelKeyType keyType) =>
        FindChannelDefinitionOrDefault(key, keyType)
            ?? throw DeploymentException.ChannelDefinitionNotFound(key);

    internal static ChannelDefinition? FindChannelDefinitionOrDefault(
        string key,
        ChannelKeyType keyType) =>
        Channels.FirstOrDefault(candidate =>
            string.Equals(
                keyType == ChannelKeyType.Name ? candidate.Name : candidate.ArmName,
                key.Trim(),
                StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Produces the safe canonical channel shape defined by the catalog. Undeclared parameters are removed,
    /// declared secrets are masked, and the raw ARM response is retained only when requested and redacted.
    /// </summary>
    internal static DeploymentChannelRecord? Sanitize(
        DeploymentChannelRecord channel,
        bool includeArmResponse = true,
        bool omitSecrets = false)
    {
        var definition = FindChannelDefinitionOrDefault(channel.ChannelName, ChannelKeyType.Name)
            ?? FindChannelDefinitionOrDefault(channel.ChannelName, ChannelKeyType.ArmName);
        if (definition is not { IsSupported: true })
        {
            return null;
        }

        var parameters = channel.Parameters
            ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var sanitized = ProjectChannelParameters(JsonSerializer.SerializeToElement(parameters), definition, omitSecrets);

        return channel with
        {
            ChannelName = definition.Name,
            Parameters = sanitized,
            RawArmResponse = includeArmResponse && channel.RawArmResponse is { } rawArmResponse
                ? BotServiceResponseRedactor.RedactChannel(rawArmResponse, definition)
                : null,
            AuthorizationUrl = includeArmResponse ? channel.AuthorizationUrl : null,
        };
    }

    internal static Dictionary<string, JsonElement> ProjectChannelParameters(
        JsonElement properties, ChannelDefinition definition, bool omitSecrets = false) =>
        ProjectParameters(properties, definition.Name == "slack"
            ? [.. definition.Parameters.Where(field => !field.IsReadOnly)]
            : definition.Parameters, omitSecrets);

    internal static Dictionary<string, JsonElement> ProjectParameters(
        JsonElement properties,
        IReadOnlyList<ChannelParameterDefinition> definitions,
        bool omitSecrets = false)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (omitSecrets && definition.IsSecret)
            {
                continue;
            }

            var value = properties.EnumerateObject().FirstOrDefault(property =>
                property.Name.Equals(definition.Name, StringComparison.OrdinalIgnoreCase)).Value;
            if (value.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }

            if (definition.IsSecret)
            {
                result[definition.Name] = JsonSerializer.SerializeToElement(BotServiceResponseRedactor.Mask);
            }
            else if (definition.ItemParameters is { } itemParameters)
            {
                if (value.ValueKind == JsonValueKind.Array)
                {
                    result[definition.Name] = JsonSerializer.SerializeToElement(
                        value.EnumerateArray()
                            .Select(item => item.ValueKind == JsonValueKind.Object
                                ? JsonSerializer.SerializeToElement(ProjectParameters(item, itemParameters, omitSecrets))
                                : item.Clone()));
                }
            }
            else
            {
                result[definition.Name] = value.Clone();
            }
        }

        return result;
    }
}
