using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class BotServiceResponseRedactionTests
{
    [Fact]
    public void Bot_PreservesSchemaAndUnknownFieldsAndMasksSecrets()
    {
        var result = BotServiceResponseRedactor.RedactBot(Json(
            """
            {
              "id": "/bots/bot-1",
              "type": "Microsoft.BotService/botServices",
              "apiVersion": "2023-09-15-preview",
              "name": "bot-1",
              "etag": "etag-1",
              "kind": "azurebot",
              "location": "global",
              "sku": { "name": "F0" },
              "tags": { "environment": "test" },
              "systemData": { "createdBy": "operator" },
              "properties": {
                "displayName": "Bot One",
                "endpoint": "https://example.test/messages",
                "msaAppId": "app-1",
                "publishingCredentials": { "secret": "value" },
                "futureProperty": "unknown-value",
                "luisKey": null
              }
            }
            """));

        Assert.Equal("/bots/bot-1", result.GetProperty("id").GetString());
        Assert.Equal("Bot One", result.GetProperty("properties").GetProperty("displayName").GetString());
        Assert.Equal(
            BotServiceResponseRedactor.Mask,
            result.GetProperty("properties").GetProperty("publishingCredentials").GetString());
        Assert.Equal(
            "unknown-value",
            result.GetProperty("properties").GetProperty("futureProperty").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("properties").GetProperty("luisKey").ValueKind);
    }

    [Fact]
    public void OAuth_PreservesSchemaAndUnknownFieldsAndMasksSecrets()
    {
        IReadOnlySet<string> providerSecrets =
            new HashSet<string>(["ProviderPrivateValue"], StringComparer.OrdinalIgnoreCase);
        var result = BotServiceResponseRedactor.RedactOAuth(
            Json(
                """
                {
                  "id": "/connections/connection-1",
                  "type": "Microsoft.BotService/botServices/connections",
                  "apiVersion": "2023-09-15-preview",
                  "name": "connection-1",
                  "etag": "etag-1",
                  "kind": "azurebot",
                  "location": "global",
                  "sku": { "name": "F0" },
                  "tags": { "environment": "test" },
                  "systemData": { "createdBy": "operator" },
                  "properties": {
                    "clientId": "client-1",
                    "clientSecret": "direct-secret",
                    "id": "connection-id",
                    "name": "connection-1",
                    "provisioningState": "Succeeded",
                    "scopes": "openid profile",
                    "serviceProviderDisplayName": "Provider",
                    "serviceProviderId": "provider-1",
                    "futureProperty": "unknown-value",
                    "parameters": [
                      { "key": "TenantId", "value": "common" },
                      { "key": "ProviderPrivateValue", "value": "declared-secret" },
                      { "key": "ApiKey", "value": "heuristic-secret" },
                      { "key": "ApiKey10", "value": true },
                      { "key": "PasswordSetting", "value": "false" },
                      { "key": "ProviderPrivateValue", "value": "true" },
                      { "value": true },
                      { "key": "OptionalValue", "value": null }
                    ]
                  }
                }
                """),
            providerSecrets);

        var properties = result!.Value.GetProperty("properties");
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("clientSecret").GetString());
        Assert.Equal("unknown-value", properties.GetProperty("futureProperty").GetString());
        var parameters = properties.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(
            [
                "TenantId",
                "ProviderPrivateValue",
                "ApiKey",
                "ApiKey10",
                "PasswordSetting",
                "ProviderPrivateValue",
                null,
                "OptionalValue",
            ],
            parameters.Select(parameter =>
                parameter.TryGetProperty("key", out var key) ? key.GetString() : null));
        Assert.Equal(BotServiceResponseRedactor.Mask, parameters[1].GetProperty("value").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, parameters[2].GetProperty("value").GetString());
        Assert.True(parameters[3].GetProperty("value").GetBoolean());
        Assert.Equal("false", parameters[4].GetProperty("value").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, parameters[5].GetProperty("value").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, parameters[6].GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, parameters[7].GetProperty("value").ValueKind);
    }

    [Fact]
    public void OAuth_PreservesDeclaredClientSecret_WhenValueIsNull()
    {
        var result = BotServiceResponseRedactor.RedactOAuth(Json(
            """
            {
              "name": "connection-1",
              "properties": {
                "clientSecret": null
              }
            }
            """));

        Assert.Equal(
            JsonValueKind.Null,
            result!.Value.GetProperty("properties").GetProperty("clientSecret").ValueKind);
    }

    [Fact]
    public void Channel_PreservesSchemaAndUnknownFieldsAndMasksSecrets()
    {
        var result = BotServiceResponseRedactor.RedactChannel(
            Json(
                """
                {
                  "id": "/channels/DirectLineChannel",
                  "type": "Microsoft.BotService/botServices/channels",
                  "apiVersion": "2023-09-15-preview",
                  "name": "bot-1/DirectLineChannel",
                  "etag": "etag-1",
                  "kind": "azurebot",
                  "location": "global",
                  "sku": { "name": "F0" },
                  "tags": { "environment": "test" },
                  "systemData": { "createdBy": "operator" },
                  "futureEnvelopeProperty": "unknown-value",
                  "properties": {
                    "etag": "outer-etag",
                    "location": "global",
                    "channelName": "DirectLineChannel",
                    "provisioningState": "Succeeded",
                    "futureOuterProperty": "unknown-value",
                    "properties": {
                      "isEnabled": true,
                      "sites": [{
                        "siteName": "Default Site",
                        "isEnabled": true,
                        "trustedOrigins": ["https://example.test"],
                        "key": "site-secret",
                        "futureSiteProperty": "unknown-value",
                        "nullableProperty": null
                      }]
                    }
                  }
                }
                """),
            Channel("directline"));

        Assert.Equal("unknown-value", result.GetProperty("futureEnvelopeProperty").GetString());
        Assert.Equal("operator", result.GetProperty("systemData").GetProperty("createdBy").GetString());
        var outer = result.GetProperty("properties");
        Assert.Equal("Succeeded", outer.GetProperty("provisioningState").GetString());
        Assert.Equal("outer-etag", outer.GetProperty("etag").GetString());
        Assert.Equal("global", outer.GetProperty("location").GetString());
        Assert.Equal("unknown-value", outer.GetProperty("futureOuterProperty").GetString());
        var channelProperties = outer.GetProperty("properties");
        Assert.True(channelProperties.GetProperty("isEnabled").GetBoolean());
        var site = channelProperties.GetProperty("sites")[0];
        Assert.Equal("Default Site", site.GetProperty("siteName").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, site.GetProperty("key").GetString());
        Assert.Equal("unknown-value", site.GetProperty("futureSiteProperty").GetString());
        Assert.Equal(JsonValueKind.Null, site.GetProperty("nullableProperty").ValueKind);
    }

    [Fact]
    public void Channel_AcceptsPropertylessM365Extensions()
    {
        var result = BotServiceResponseRedactor.RedactChannel(
            Json(
                """
                {
                  "id": "/channels/M365Extensions",
                  "name": "bot-1/M365Extensions",
                  "properties": {
                    "channelName": "M365Extensions",
                    "provisioningState": "Succeeded"
                  }
                }
                """),
            Channel("m365extensions"));

        Assert.Equal("/channels/M365Extensions", result.GetProperty("id").GetString());
        Assert.Equal(
            "Succeeded",
            result.GetProperty("properties").GetProperty("provisioningState").GetString());
        Assert.False(result.GetProperty("properties").TryGetProperty("properties", out _));
    }

    [Fact]
    public void Bot_PreservesUnifiedEnvelopeFields()
    {
        var result = BotServiceResponseRedactor.RedactBot(Json(
            """
            {
              "id": "/bots/bot-1",
              "resourceGroup": "rg-one",
              "zones": ["1"],
              "systemData": { "createdBy": "operator" },
              "futureEnvelopeProperty": "unknown-value",
              "properties": {
                "displayName": "Bot One",
                "publishingCredentials": { "secret": "value" },
                "futureProperty": "unknown-value",
                "luisKey": null
              }
            }
            """));

        Assert.Equal("/bots/bot-1", result.GetProperty("id").GetString());
        Assert.Equal("rg-one", result.GetProperty("resourceGroup").GetString());
        Assert.Equal("1", result.GetProperty("zones")[0].GetString());
        Assert.Equal("operator", result.GetProperty("systemData").GetProperty("createdBy").GetString());
        Assert.Equal("unknown-value", result.GetProperty("futureEnvelopeProperty").GetString());
        var properties = result.GetProperty("properties");
        Assert.Equal("Bot One", properties.GetProperty("displayName").GetString());
        Assert.Equal(
            BotServiceResponseRedactor.Mask,
            properties.GetProperty("publishingCredentials").GetString());
        Assert.Equal("unknown-value", properties.GetProperty("futureProperty").GetString());
        Assert.Equal(JsonValueKind.Null, properties.GetProperty("luisKey").ValueKind);
    }

    [Fact]
    public void OAuth_UsesSharedKeyValueArrayTraversal()
    {
        IReadOnlySet<string> providerSecrets =
            new HashSet<string>(["ProviderPrivateValue"], StringComparer.OrdinalIgnoreCase);
        var result = BotServiceResponseRedactor.RedactOAuth(
            Json(
                """
                {
                  "name": "connection-1",
                  "properties": {
                    "clientId": "client-1",
                    "clientSecret": "direct-secret",
                    "futureProperty": "unknown-value",
                    "parameters": [
                      { "key": "TenantId", "value": "common", "futureEntryProperty": "unknown-value" },
                      { "key": "ProviderPrivateValue", "value": "declared-secret" },
                      { "key": "ApiKey", "value": "heuristic-secret" },
                      { "key": "OptionalValue", "value": null }
                    ]
                  }
                }
                """),
            providerSecrets);

        var properties = result!.Value.GetProperty("properties");
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("clientSecret").GetString());
        Assert.Equal("unknown-value", properties.GetProperty("futureProperty").GetString());
        var parameters = properties.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal("common", parameters[0].GetProperty("value").GetString());
        Assert.Equal("unknown-value", parameters[0].GetProperty("futureEntryProperty").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, parameters[1].GetProperty("value").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, parameters[2].GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, parameters[3].GetProperty("value").ValueKind);
    }

    [Fact]
    public void Channel_UsesSharedObjectArrayTraversal()
    {
        var result = BotServiceResponseRedactor.RedactChannel(
            Json(
                """
                {
                  "id": "/channels/DirectLineChannel",
                  "futureEnvelopeProperty": "unknown-value",
                  "properties": {
                    "etag": "outer-etag",
                    "location": "global",
                    "channelName": "DirectLineChannel",
                    "futureOuterProperty": "unknown-value",
                    "properties": {
                      "sites": [{
                        "siteName": "Default Site",
                        "isEnabled": true,
                        "trustedOrigins": ["https://example.test"],
                        "key": "site-secret",
                        "futureSiteProperty": "unknown-value",
                        "nullableProperty": null
                      }]
                    }
                  }
                }
                """),
            Channel("directline"));

        Assert.Equal("unknown-value", result.GetProperty("futureEnvelopeProperty").GetString());
        var outer = result.GetProperty("properties");
        Assert.Equal("outer-etag", outer.GetProperty("etag").GetString());
        Assert.Equal("global", outer.GetProperty("location").GetString());
        Assert.Equal("unknown-value", outer.GetProperty("futureOuterProperty").GetString());
        var site = outer.GetProperty("properties").GetProperty("sites")[0];
        Assert.Equal("Default Site", site.GetProperty("siteName").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, site.GetProperty("key").GetString());
        Assert.Equal("unknown-value", site.GetProperty("futureSiteProperty").GetString());
        Assert.Equal(JsonValueKind.Null, site.GetProperty("nullableProperty").ValueKind);
    }

    [Fact]
    public void UnknownProperties_AreMaskedOnlyForNonBooleanSecretNames()
    {
        const string unknownProperties = """
            "futureProperty": "unknown-value",
            "unknownSecret": "secret-value",
            "unknownPassword": "password-value",
            "unknownKey": "key-value",
            "unknownKey1": "key1-value",
            "unknownKey2": "key2-value",
            "unknownKey10": "key10-value",
            "unknownToken": "token-value",
            "unknownToken20": "token20-value",
            "unknownCredential": "credential-value",
            "unknownCredential3": "credential3-value",
            "booleanSecret": true,
            "booleanPassword": false,
            "stringSecret": "true",
            "stringKey": "FALSE",
            "nullableSecret": null
            """;

        var bot = BotServiceResponseRedactor.RedactBot(Json(
            $$"""
            {
              "name": "bot-1",
              "properties": {
                "displayName": "Bot One",
                {{unknownProperties}}
              }
            }
            """));
        var oauth = BotServiceResponseRedactor.RedactOAuth(Json(
            $$"""
            {
              "name": "connection-1",
              "properties": {
                "clientId": "client-1",
                {{unknownProperties}}
              }
            }
            """))!.Value;
        var channel = BotServiceResponseRedactor.RedactChannel(
            Json(
                $$"""
                {
                  "name": "bot-1/SlackChannel",
                  "properties": {
                    "channelName": "SlackChannel",
                    "properties": {
                      "clientId": "client-1",
                      {{unknownProperties}}
                    }
                  }
                }
                """),
            Channel("slack"));

        AssertUnknownFallback(bot.GetProperty("properties"));
        AssertUnknownFallback(oauth.GetProperty("properties"));
        AssertUnknownFallback(channel.GetProperty("properties").GetProperty("properties"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"properties":"malformed"}""")]
    public void MalformedRequiredStructuresReturnEmptyObject(string json)
    {
        var source = Json(json);

        Assert.Empty(BotServiceResponseRedactor.RedactBot(source).EnumerateObject());
        Assert.Empty(BotServiceResponseRedactor.RedactChannel(source, Channel("slack")).EnumerateObject());
        Assert.Empty(BotServiceResponseRedactor.RedactOAuth(source)!.Value.EnumerateObject());
    }

    [Fact]
    public void Bot_MasksKnownSecretsAndPreservesUnknownProperties()
    {
        var result = BotServiceResponseRedactor.RedactBot(Json(
            """
            {
              "id": "/bots/bot-1",
              "name": "bot-1",
              "properties": {
                "displayName": "Bot One",
                "publishingCredentials": { "secret": "value" },
                "futureProperty": "unknown-value",
                "luisKey": null
              }
            }
            """));

        var properties = result.GetProperty("properties");
        Assert.Equal("Bot One", properties.GetProperty("displayName").GetString());
        Assert.Equal(
            BotServiceResponseRedactor.Mask,
            properties.GetProperty("publishingCredentials").GetString());
        Assert.Equal("unknown-value", properties.GetProperty("futureProperty").GetString());
        Assert.Equal(JsonValueKind.Null, properties.GetProperty("luisKey").ValueKind);
    }

    [Fact]
    public void OAuth_PreservesSecretEntriesAndMasksTheirValues()
    {
        IReadOnlySet<string> providerSecrets =
            new HashSet<string>(["ProviderPrivateValue"], StringComparer.OrdinalIgnoreCase);
        var result = BotServiceResponseRedactor.RedactOAuth(
            Json(
                """
                {
                  "name": "connection-1",
                  "properties": {
                    "clientId": "client-1",
                    "clientSecret": "direct-secret",
                    "futureProperty": "unknown-value",
                    "nullableProperty": null,
                    "parameters": [
                      { "key": "TenantId", "value": "common" },
                      { "key": "ProviderPrivateValue", "value": "declared-secret" },
                      { "key": "ApiKey", "value": "heuristic-secret" },
                      { "key": "OptionalValue", "value": null },
                      { "key": "TenantId", "value": "common", "futureEntryProperty": "unknown-value" }
                    ]
                  }
                }
                """),
            providerSecrets);

        var properties = result!.Value.GetProperty("properties");
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("clientSecret").GetString());
        Assert.Equal("unknown-value", properties.GetProperty("futureProperty").GetString());
        Assert.Equal(JsonValueKind.Null, properties.GetProperty("nullableProperty").ValueKind);
        var parameters = properties.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(5, parameters.Length);
        Assert.Equal(BotServiceResponseRedactor.Mask, parameters[1].GetProperty("value").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, parameters[2].GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, parameters[3].GetProperty("value").ValueKind);
        Assert.Equal("unknown-value", parameters[4].GetProperty("futureEntryProperty").GetString());
    }

    [Fact]
    public void Channel_PreservesOuterSchemaAndUnknownFields()
    {
        var result = BotServiceResponseRedactor.RedactChannel(
            Json(
                """
                {
                  "id": "/channels/SlackChannel",
                  "name": "bot-1/SlackChannel",
                  "properties": {
                    "etag": "outer-etag",
                    "location": "global",
                    "channelName": "SlackChannel",
                    "provisioningState": "Succeeded",
                    "futureOuterProperty": "unknown-value",
                    "properties": {
                      "clientId": "client-1",
                      "clientSecret": "secret-1",
                      "landingPageUrl": null,
                      "futureInnerProperty": "unknown-value"
                    }
                  }
                }
                """),
            Channel("slack"));

        var outer = result.GetProperty("properties");
        Assert.Equal("outer-etag", outer.GetProperty("etag").GetString());
        Assert.Equal("global", outer.GetProperty("location").GetString());
        Assert.Equal("SlackChannel", outer.GetProperty("channelName").GetString());
        Assert.Equal("Succeeded", outer.GetProperty("provisioningState").GetString());
        Assert.Equal("unknown-value", outer.GetProperty("futureOuterProperty").GetString());
        var properties = outer.GetProperty("properties");
        Assert.Equal("client-1", properties.GetProperty("clientId").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("clientSecret").GetString());
        Assert.Equal(JsonValueKind.Null, properties.GetProperty("landingPageUrl").ValueKind);
        Assert.Equal("unknown-value", properties.GetProperty("futureInnerProperty").GetString());
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static ChannelDefinition Channel(string name) =>
        DeploymentChannelCatalog.FindChannelDefinition(name, ChannelKeyType.Name);

    private static void AssertUnknownFallback(JsonElement properties)
    {
        Assert.Equal("unknown-value", properties.GetProperty("futureProperty").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("unknownSecret").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("unknownPassword").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("unknownKey").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("unknownKey1").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("unknownKey2").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("unknownKey10").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("unknownToken").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("unknownToken20").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("unknownCredential").GetString());
        Assert.Equal(
            BotServiceResponseRedactor.Mask,
            properties.GetProperty("unknownCredential3").GetString());
        Assert.True(properties.GetProperty("booleanSecret").GetBoolean());
        Assert.False(properties.GetProperty("booleanPassword").GetBoolean());
        Assert.Equal("true", properties.GetProperty("stringSecret").GetString());
        Assert.Equal("FALSE", properties.GetProperty("stringKey").GetString());
        Assert.Equal(JsonValueKind.Null, properties.GetProperty("nullableSecret").ValueKind);
    }
}
