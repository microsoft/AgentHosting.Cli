using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

/// <summary>
/// Preserves safe Bot Service, OAuth, and channel fields while masking secrets.
/// Declared secrets are masked even when null. Unknown nulls and booleans are preserved; malformed resources fail closed.
/// </summary>
internal static class BotServiceResponseRedactor
{
    internal const string Mask = "***";

    /// <summary>
    /// The <c>Microsoft.BotService/botServices</c> (2023-09-15-preview) response template — the single
    /// source of truth for the response schema. The set of known <c>properties</c> field names is
    /// derived from this template by <see cref="ExtractObjectKeys"/>.
    /// </summary>
    private const string BotServiceResponseTemplate = """
        {
          "id": "string",
          "type": "Microsoft.BotService/botServices",
          "apiVersion": "2023-09-15-preview",
          "name": "string",
          "etag": "string",
          "kind": "string",
          "location": "string",
          "resourceGroup": "string",
          "properties": {
            "allSettings": {
              "{customized property}": "string"
            },
            "appPasswordHint": "string",
            "cmekKeyVaultUrl": "string",
            "description": "string",
            "developerAppInsightKey": "string",
            "developerAppInsightsApiKey": "string",
            "developerAppInsightsApplicationId": "string",
            "disableLocalAuth": "bool",
            "displayName": "string",
            "endpoint": "string",
            "iconUrl": "string",
            "isCmekEnabled": "bool",
            "isStreamingSupported": "bool",
            "luisAppIds": [ "string" ],
            "luisKey": "string",
            "manifestUrl": "string",
            "msaAppId": "string",
            "msaAppMSIResourceId": "string",
            "msaAppTenantId": "string",
            "msaAppType": "string",
            "openWithHint": "string",
            "parameters": {
              "{customized property}": "string"
            },
            "publicNetworkAccess": "string",
            "publishingCredentials": "string",
            "schemaTransformationVersion": "string",
            "storageResourceId": "string",
            "tenantId": "string"
          },
          "sku": {
            "name": "string"
          },
          "tags": {
            "{customized property}": "string"
          },
          "zones": []
        }
        """;

    /// <summary>
    /// The <c>Microsoft.BotService/botServices/connections</c> response template used to derive the known
    /// OAuth resource and property fields.
    /// </summary>
    private const string OAuthConnectionResponseTemplate = """
        {
          "id": "string",
          "type": "Microsoft.BotService/botServices/connections",
          "apiVersion": "2023-09-15-preview",
          "name": "string",
          "etag": "string",
          "kind": "string",
          "location": "string",
          "resourceGroup": "string",
          "properties": {
            "clientId": "string",
            "clientSecret": "string",
            "id": "string",
            "name": "string",
            "parameters": [],
            "provisioningState": "string",
            "scopes": "string",
            "serviceProviderDisplayName": "string",
            "serviceProviderId": "string"
          },
          "sku": {
            "name": "string"
          },
          "tags": {
            "{customized property}": "string"
          },
          "zones": []
        }
        """;

    /// <summary>
    /// The <c>Microsoft.BotService/botServices/channels</c> response template used to derive the known
    /// channel envelope and outer property fields. Inner fields come from
    /// <see cref="ChannelDefinition.Parameters"/>.
    /// </summary>
    private const string ChannelResponseTemplate = """
        {
          "id": "string",
          "type": "Microsoft.BotService/botServices/channels",
          "apiVersion": "2023-09-15-preview",
          "name": "string",
          "etag": "string",
          "kind": "string",
          "location": "string",
          "resourceGroup": "string",
          "properties": {
            "etag": "string",
            "location": "string",
            "channelName": "string",
            "provisioningState": "string",
            "properties": {}
          },
          "sku": {
            "name": "string"
          },
          "tags": {
            "{customized property}": "string"
          },
          "zones": []
        }
        """;

    /// <summary>
    /// The known <c>properties</c> field names, extracted once from
    /// <see cref="BotServiceResponseTemplate"/>. A <c>properties</c> field not in this set (for example
    /// one added by a future api-version, or a read-only field not in the template) is preserved unless its name
    /// identifies a likely secret and its value is not boolean.
    /// </summary>
    private static readonly HashSet<string> BotServiceResourceSchema =
        ExtractObjectKeys(BotServiceResponseTemplate);

    private static readonly HashSet<string> OAuthResourceSchema =
        ExtractObjectKeys(OAuthConnectionResponseTemplate);

    private static readonly HashSet<string> ChannelResourceSchema =
        ExtractObjectKeys(ChannelResponseTemplate);

    private static readonly HashSet<string> BotServicePropertySchema =
        ExtractObjectKeys(BotServiceResponseTemplate, "properties");

    private static readonly HashSet<string> OAuthPropertySchema =
        ExtractObjectKeys(OAuthConnectionResponseTemplate, "properties");

    private static readonly HashSet<string> ChannelOuterPropertySchema =
        ExtractObjectKeys(ChannelResponseTemplate, "properties");

    private static readonly HashSet<string> OAuthSecretProperties =
        new(["clientSecret"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> KeyValueParameterSchema =
        new(["key", "value"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> KeyValueParameterSecretProperties =
        new(["value"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The subset of the template's <c>properties</c> that can carry credential material and must be
    /// masked in the raw <c>--json</c> response (for example <c>publishingCredentials</c>,
    /// <c>developerAppInsightsApiKey</c>, <c>luisKey</c>, and the free-form <c>allSettings</c>/
    /// <c>parameters</c> bags whose values live under arbitrary keys). Add a field here when a new
    /// template field carries secrets.
    /// </summary>
    private static readonly HashSet<string> BotSecretProperties =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "allSettings",
            "appPasswordHint",
            "developerAppInsightKey",
            "developerAppInsightsApiKey",
            "luisKey",
            "parameters",
            "publishingCredentials",
        };

    internal static JsonElement RedactBot(JsonElement response) =>
        RedactResource(
            response,
            BotServiceResourceSchema,
            BotServicePropertySchema,
            BotSecretProperties);

    internal static JsonElement? RedactOAuth(
        JsonElement? response,
        IReadOnlySet<string>? providerSecretNames = null)
    {
        if (response is null)
        {
            return null;
        }

        return RedactResource(
            response.Value,
            OAuthResourceSchema,
            OAuthPropertySchema,
            OAuthSecretProperties,
            nestedPropertyHandler: (propertyName, value) =>
            {
                if (!propertyName.Equals("parameters", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                RedactKeyValueParameters(value, providerSecretNames);
                return true;
            });
    }

    internal static JsonElement RedactChannel(
        JsonElement response,
        ChannelDefinition? definition) =>
        RedactResource(
            response,
            ChannelResourceSchema,
            ChannelOuterPropertySchema,
            nestedPropertyHandler: (propertyName, value) =>
            {
                if (!propertyName.Equals("properties", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                RedactChannelProperties(value, definition);
                return true;
            });

    private static JsonElement RedactResource(
        JsonElement response,
        IReadOnlySet<string> resourceSchema,
        IReadOnlySet<string> propertySchema,
        IReadOnlySet<string>? secretProperties = null,
        Func<string, JsonNode?, bool>? nestedPropertyHandler = null)
    {
        var root = JsonNode.Parse(response.GetRawText()) as JsonObject;
        if (root is null ||
            root.FirstOrDefault(property =>
                property.Key.Equals("properties", StringComparison.OrdinalIgnoreCase)).Value
                is not JsonObject properties)
        {
            using var emptyDocument = JsonDocument.Parse("{}");
            return emptyDocument.RootElement.Clone();
        }

        RedactObject(
            root,
            resourceSchema,
            nestedPropertyHandler: (propertyName, _) =>
                propertyName.Equals("properties", StringComparison.OrdinalIgnoreCase));
        RedactObject(properties, propertySchema, secretProperties, nestedPropertyHandler);

        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }

    private static void RedactKeyValueParameters(
        JsonNode? value,
        IReadOnlySet<string>? declaredSecretNames)
    {
        if (value is not JsonArray parameters)
        {
            ReplaceWithMask(value);
            return;
        }

        for (var index = 0; index < parameters.Count; index++)
        {
            if (parameters[index] is not JsonObject parameter)
            {
                parameters[index] = parameters[index] is null ? null : JsonValue.Create(Mask);
                continue;
            }

            var key = parameter.FirstOrDefault(property =>
                    property.Key.Equals("key", StringComparison.OrdinalIgnoreCase)).Value
                is JsonValue jsonValue &&
                jsonValue.TryGetValue<string>(out var text)
                    ? text
                    : null;
            var parameterValue = parameter.FirstOrDefault(property =>
                property.Key.Equals("value", StringComparison.OrdinalIgnoreCase)).Value;
            var isSecret = key is null ||
                declaredSecretNames?.Contains(key) == true ||
                key.Equals(DeploymentOAuthParameters.CommonOAuthParameters.ClientSecret, StringComparison.OrdinalIgnoreCase) ||
                (DeploymentOAuthParameters.IsSecret(key) &&
                 parameterValue is not null && !IsBoolean(parameterValue));

            RedactObject(
                parameter,
                KeyValueParameterSchema,
                isSecret
                    ? KeyValueParameterSecretProperties
                    : null);
        }
    }

    private static void RedactChannelProperties(
        JsonNode? value,
        ChannelDefinition? definition)
    {
        if (value is not JsonObject properties)
        {
            ReplaceWithMask(value);
            return;
        }

        RedactChannelFields(properties, definition?.Parameters ?? []);
    }

    private static void RedactChannelFields(
        JsonObject properties,
        IReadOnlyList<ChannelParameterDefinition> definitions)
    {
        var knownProperties = definitions
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var secretProperties = definitions
            .Where(parameter => parameter.IsSecret)
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        RedactObject(
            properties,
            knownProperties,
            secretProperties,
            nestedPropertyHandler: (propertyName, nestedValue) =>
            {
                var field = definitions.FirstOrDefault(parameter =>
                    propertyName.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));
                if (field is not { IsSecret: false, ItemParameters: { } itemParameters })
                {
                    return false;
                }

                if (nestedValue is not JsonArray items)
                {
                    ReplaceWithMask(nestedValue);
                    return true;
                }

                for (var index = 0; index < items.Count; index++)
                {
                    if (items[index] is JsonObject item)
                    {
                        RedactChannelFields(item, itemParameters);
                    }
                    else
                    {
                        items[index] = items[index] is null ? null : JsonValue.Create(Mask);
                    }
                }

                return true;
            });
    }

    private static void RedactObject(
        JsonObject value,
        IReadOnlySet<string>? knownProperties = null,
        IReadOnlySet<string>? secretProperties = null,
        Func<string, JsonNode?, bool>? nestedPropertyHandler = null)
    {
        foreach (var property in value.ToList())
        {
            if (nestedPropertyHandler?.Invoke(property.Key, property.Value) == true)
            {
                continue;
            }

            if (secretProperties?.Contains(property.Key) == true)
            {
                value[property.Key] = Mask;
                continue;
            }

            if (property.Value is not null &&
                knownProperties?.Contains(property.Key) != true &&
                DeploymentOAuthParameters.IsSecret(property.Key) &&
                !IsBoolean(property.Value))
            {
                value[property.Key] = Mask;
                continue;
            }

            RedactNestedValue(property.Value);
        }
    }

    private static void RedactNestedValue(JsonNode? value)
    {
        if (value is JsonObject nestedObject)
        {
            RedactObject(nestedObject);
        }
        else if (value is JsonArray array)
        {
            foreach (var item in array)
            {
                RedactNestedValue(item);
            }
        }
    }

    private static void ReplaceWithMask(JsonNode? value)
    {
        if (value is null)
        {
            return;
        }

        value.ReplaceWith(Mask);
    }

    private static bool IsBoolean(JsonNode value) =>
        value is JsonValue jsonValue &&
        (jsonValue.TryGetValue<bool>(out _) ||
         (jsonValue.TryGetValue<string>(out var text) && bool.TryParse(text, out _)));

    private static HashSet<string> ExtractObjectKeys(string template, params string[] path)
    {
        using var document = JsonDocument.Parse(template);
        var value = document.RootElement;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(segment, out value))
            {
                return keys;
            }
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return keys;
        }

        foreach (var property in value.EnumerateObject())
        {
            keys.Add(property.Name);
        }

        return keys;
    }
}
