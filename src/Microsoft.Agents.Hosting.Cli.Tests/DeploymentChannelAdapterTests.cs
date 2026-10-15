using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentChannelAdapterTests
{
    private static readonly string[] TrustedOrigins =
        ["https://a.example", "https://b.example"];

    private static DeploymentChannelAdapter Adapter(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), new StubTokenProvider { AccessToken = "arm-token", TenantId = "home" });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task GetSlack_RedactsCatalogSecretsAndPreservesUnknownProperties()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """
            {
              "id":"/channels/SlackChannel",
              "name":"dev/SlackChannel",
              "properties":{
                "channelName":"SlackChannel",
                "provisioningState":"Succeeded",
                "properties":{
                  "isEnabled":true,
                  "clientId":"client-1",
                  "clientSecret":"secret-1",
                  "verificationToken":"token-1",
                  "signingSecret":"signing-1",
                  "landingPageUrl":null,
                  "unexpected":"must-not-render"
                }
              }
            }
            """));

        var channel = await Adapter(handler).GetChannelAsync(
            "SlackChannel",
            BotResourceId,
            targetTenantId: null,
            default);

        Assert.Equal("slack", channel!.ChannelName);
        Assert.True(channel.Parameters["isEnabled"].GetBoolean());
        Assert.Equal("/channels/SlackChannel", channel.ResourceId);
        Assert.Equal("client-1", channel.Parameters["clientId"].GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, channel.Parameters["clientSecret"].GetString());
        var properties = channel.RawArmResponse!.Value
            .GetProperty("properties")
            .GetProperty("properties");
        Assert.True(properties.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("client-1", properties.GetProperty("clientId").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("clientSecret").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("verificationToken").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("signingSecret").GetString());
        Assert.Equal(JsonValueKind.Null, properties.GetProperty("landingPageUrl").ValueKind);
        Assert.Equal("must-not-render", properties.GetProperty("unexpected").GetString());
    }

    [Fact]
    public async Task UpsertSlack_SendsSecretButRedactsEchoedResponse()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """
            {"id":"/channels/SlackChannel","properties":{"channelName":"SlackChannel","properties":{
              "isEnabled":true,
              "clientId":"client-1",
              "clientSecret":"secret-1",
              "verificationToken":"token-1"
            }}}
            """));
        var adapter = Adapter(handler);

        var channel = await adapter.UpsertChannelAsync(
            new BotServiceChannelUpsertRequest
            {
                ChannelArmName = "SlackChannel",
                BotResourceId = BotResourceId,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["isEnabled"] = Value(true),
                    ["clientId"] = Value("client-1"),
                    ["clientSecret"] = Value("secret-1"),
                    ["verificationToken"] = Value("token-1"),
                },
            },
            default);

        using var request = JsonDocument.Parse(handler.Requests.Single().Body!);
        Assert.Equal(
            "secret-1",
            request.RootElement.GetProperty("properties").GetProperty("properties").GetProperty("clientSecret").GetString());
        Assert.Equal(
            "***",
            channel.RawArmResponse!.Value.GetProperty("properties").GetProperty("properties")
                .GetProperty("clientSecret").GetString());
        Assert.Equal("slack", channel.ChannelName);
        Assert.True(channel.Parameters["isEnabled"].GetBoolean());
    }

    [Fact]
    public async Task UpsertM365Extensions_OmitsInnerProperties()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """{"id":"/channels/M365Extensions","properties":{"channelName":"M365Extensions","provisioningState":"Succeeded"}}"""));

        var channel = await Adapter(handler).UpsertChannelAsync(
            Upsert("M365Extensions", new Dictionary<string, JsonElement>()),
            default);

        using var request = JsonDocument.Parse(handler.Requests.Single().Body!);
        var properties = request.RootElement.GetProperty("properties");
        Assert.Equal("M365Extensions", properties.GetProperty("channelName").GetString());
        Assert.False(properties.TryGetProperty("properties", out _));
        Assert.Equal("m365extensions", channel.ChannelName);
        Assert.Empty(channel.Parameters);
    }

    [Fact]
    public async Task UpsertDirectLine_WritesPreShapedSingleSite()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """{"id":"/channels/DirectLineChannel","properties":{"channelName":"DirectLineChannel","properties":{"isEnabled":true}}}"""));

        await Adapter(handler).UpsertChannelAsync(
            Upsert(
                "DirectLineChannel",
                new Dictionary<string, JsonElement>
                {
                    ["sites"] = Value(
                        new[]
                        {
                            new
                            {
                                isEnabled = true,
                                siteName = "primary",
                                isV1Enabled = false,
                                trustedOrigins = TrustedOrigins,
                            },
                        }),
                }),
            default);

        using var request = JsonDocument.Parse(handler.Requests.Single().Body!);
        var properties = request.RootElement.GetProperty("properties").GetProperty("properties");
        var site = properties.GetProperty("sites")[0];
        Assert.True(site.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("primary", site.GetProperty("siteName").GetString());
        Assert.False(site.GetProperty("isV1Enabled").GetBoolean());
        Assert.Equal(2, site.GetProperty("trustedOrigins").GetArrayLength());
    }

    [Fact]
    public async Task UpsertSlack_ShapesCompleteValidatedParameterSet()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """{"id":"/channels/SlackChannel","properties":{"channelName":"SlackChannel","properties":{"isEnabled":false}}}"""));

        await Adapter(handler).UpsertChannelAsync(
            Upsert(
                "SlackChannel",
                new Dictionary<string, JsonElement>
                {
                    ["isEnabled"] = Value(false),
                    ["clientId"] = Value("existing-client"),
                    ["clientSecret"] = Value("new-secret"),
                    ["landingPageUrl"] = Value("https://old.example"),
                }),
            default);

        using var request = JsonDocument.Parse(handler.Requests.Single().Body!);
        var properties = request.RootElement.GetProperty("properties").GetProperty("properties");
        Assert.False(properties.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("existing-client", properties.GetProperty("clientId").GetString());
        Assert.Equal("new-secret", properties.GetProperty("clientSecret").GetString());
        Assert.Equal("https://old.example", properties.GetProperty("landingPageUrl").GetString());
        Assert.False(properties.TryGetProperty("unexpected", out _));
    }

    [Fact]
    public async Task UpsertDirectLine_PreservesPreShapedSingleSite()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """{"id":"/channels/DirectLineChannel","properties":{"channelName":"DirectLineChannel","properties":{"isEnabled":true}}}"""));

        await Adapter(handler).UpsertChannelAsync(
            Upsert(
                "DirectLineChannel",
                new Dictionary<string, JsonElement>
                {
                    ["sites"] = Value(new[] { new { siteName = "beta", isV3Enabled = false } }),
                }),
            default);

        using var request = JsonDocument.Parse(handler.Requests.Single().Body!);
        var sites = request.RootElement.GetProperty("properties").GetProperty("properties").GetProperty("sites");
        var site = Assert.Single(sites.EnumerateArray());
        Assert.Equal("beta", site.GetProperty("siteName").GetString());
        Assert.False(site.GetProperty("isV3Enabled").GetBoolean());
    }

    [Fact]
    public async Task GetDirectLine_PreservesUnknownArmSiteProperties()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """
            {"id":"/channels/DirectLineChannel","properties":{"channelName":"DirectLineChannel","properties":{
              "sites":[{
                "siteName":"Default Site",
                "isEnabled":true,
                "trustedOrigins":["https://example.test"],
                "key":"must-not-render",
                "unexpected":{"nested":"must-not-render"}
              }]
            }}}
            """));

        var channel = await Adapter(handler).GetChannelAsync(
            "DirectLineChannel",
            BotResourceId,
            targetTenantId: null,
            default);

        var site = channel!.RawArmResponse!.Value
            .GetProperty("properties")
            .GetProperty("properties")
            .GetProperty("sites")[0];
        var persistedSite = Assert.Single(channel.Parameters["sites"].EnumerateArray());
        Assert.Equal("Default Site", persistedSite.GetProperty("siteName").GetString());
        Assert.True(persistedSite.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("https://example.test", persistedSite.GetProperty("trustedOrigins")[0].GetString());
        Assert.False(persistedSite.TryGetProperty("key", out _));
        Assert.False(persistedSite.TryGetProperty("unexpected", out _));
        Assert.Equal("Default Site", site.GetProperty("siteName").GetString());
        Assert.True(site.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("https://example.test", site.GetProperty("trustedOrigins")[0].GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, site.GetProperty("key").GetString());
        Assert.Equal(
            "must-not-render",
            site.GetProperty("unexpected").GetProperty("nested").GetString());
    }

    [Fact]
    public async Task GetDirectLine_MultipleSites_ProjectsAllSites()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """
            {"id":"/channels/DirectLineChannel","properties":{"channelName":"DirectLineChannel","properties":{
              "sites":[
                {"siteName":"Primary","isEnabled":true},
                {"siteName":"Secondary","isEnabled":false}
              ]
            }}}
            """));

        var channel = await Adapter(handler).GetChannelAsync(
            "DirectLineChannel",
            BotResourceId,
            targetTenantId: null,
            default);

        var persistedSites = channel!.Parameters["sites"];
        Assert.Equal(2, persistedSites.GetArrayLength());
        Assert.Equal("Primary", persistedSites[0].GetProperty("siteName").GetString());
        Assert.True(persistedSites[0].GetProperty("isEnabled").GetBoolean());
        Assert.Equal("Secondary", persistedSites[1].GetProperty("siteName").GetString());
        Assert.False(persistedSites[1].GetProperty("isEnabled").GetBoolean());
        Assert.Equal(
            2,
            channel.RawArmResponse!.Value
                .GetProperty("properties")
                .GetProperty("properties")
                .GetProperty("sites")
                .GetArrayLength());
    }

    [Fact]
    public async Task GetDirectLine_MasksMalformedSitesPayload()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """
            {"id":"/channels/DirectLineChannel","properties":{"channelName":"DirectLineChannel","properties":{
              "sites":{"key":"must-not-render"}
            }}}
            """));

        var channel = await Adapter(handler).GetChannelAsync(
            "DirectLineChannel",
            BotResourceId,
            targetTenantId: null,
            default);

        var properties = channel!.RawArmResponse!.Value
            .GetProperty("properties")
            .GetProperty("properties");
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("sites").GetString());
    }

    [Fact]
    public async Task List_RedactsEachChannelResource()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """
            {"value":[
              {"id":"/channels/SlackChannel","name":"dev/SlackChannel","properties":{"channelName":"SlackChannel","properties":{
                "isEnabled":true,"clientId":"client-1","clientSecret":"secret-1"
              }}},
              {"id":"/channels/MsTeamsChannel","name":"dev/MsTeamsChannel","properties":{"channelName":"MsTeamsChannel","properties":{
                "isEnabled":false,"enableCalling":true,"unknown":"must-not-render"
              }}}
            ]}
            """));

        var channels = await Adapter(handler).ListChannelsAsync(
            BotResourceId,
            targetTenantId: null,
            default);

        var slack = channels[0].RawArmResponse!.Value;
        var teams = channels[1].RawArmResponse!.Value;
        Assert.Equal(["slack", "msteams"], channels.Select(channel => channel.ChannelName));
        Assert.True(channels[0].Parameters["isEnabled"].GetBoolean());
        Assert.False(channels[1].Parameters["isEnabled"].GetBoolean());
        Assert.Equal(
            BotServiceResponseRedactor.Mask,
            slack.GetProperty("properties").GetProperty("properties").GetProperty("clientSecret").GetString());
        Assert.True(
            teams.GetProperty("properties").GetProperty("properties").GetProperty("enableCalling").GetBoolean());
        Assert.Equal(
            "must-not-render",
            teams.GetProperty("properties").GetProperty("properties").GetProperty("unknown").GetString());
        Assert.Equal(2, channels.Count);
    }

    [Fact]
    public async Task List_MapsPropertylessM365ExtensionsChannel()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """
            {"value":[{
              "id":"/channels/M365Extensions",
              "name":"dev/M365Extensions",
              "properties":{
                "channelName":"M365Extensions",
                "provisioningState":"Succeeded"
              }
            }]}
            """));

        var channel = Assert.Single(await Adapter(handler).ListChannelsAsync(
            BotResourceId,
            targetTenantId: null,
            default));

        Assert.Equal("m365extensions", channel.ChannelName);
        Assert.Empty(channel.Parameters);
        Assert.Equal(
            "Succeeded",
            channel.RawArmResponse!.Value.GetProperty("properties").GetProperty("provisioningState").GetString());
    }

    [Fact]
    public async Task List_MalformedCollection_ThrowsInvalidResponse()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """
            {"secret":"must-not-render"}
            """));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler).ListChannelsAsync(
                BotResourceId,
                targetTenantId: null,
                default));

        Assert.Equal(ProvisioningErrorKind.InvalidResponse, ex.Kind);
        Assert.Contains("'value' must be a JSON array", ex.Message);
    }

    [Fact]
    public async Task Upsert_ResponseWithoutChannelArmName_DoesNotFallBackToRequest()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """{"properties":{"properties":{"isEnabled":true}}}"""));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler).UpsertChannelAsync(
                Upsert("SlackChannel", new Dictionary<string, JsonElement>()),
                default));

        Assert.Equal(ProvisioningErrorKind.InvalidResponse, ex.Kind);
        Assert.Contains("properties.channelName", ex.Message);
    }

    [Fact]
    public async Task List_UnknownChannelArmName_DoesNotUseItAsCanonicalName()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """{"value":[{"properties":{"channelName":"UnknownArmChannel"}}]}"""));

        var ex = await Assert.ThrowsAsync<DeploymentException>(() =>
            Adapter(handler).ListChannelsAsync(
                BotResourceId,
                targetTenantId: null,
                default));

        Assert.Equal("channel-definition-not-found", ex.ErrorKind);
        Assert.Contains("UnknownArmChannel", ex.Message);
    }

    [Fact]
    public async Task Get_MalformedChannel_ThrowsInvalidResponse()
    {
        const string body = """{"properties":null}""";
        var handler = new StubHttpMessageHandler(_ => Json(body));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler).GetChannelAsync(
                "MsTeamsChannel",
                BotResourceId,
                targetTenantId: null,
                default));

        Assert.Equal(ProvisioningErrorKind.InvalidResponse, ex.Kind);
        Assert.Contains("'properties' must be a JSON object", ex.Message);
    }

    private const string BotResourceId =
        "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev";

    private static BotServiceChannelUpsertRequest Upsert(
        string channelArmName,
        IReadOnlyDictionary<string, JsonElement> parameters) =>
        new()
        {
            ChannelArmName = channelArmName,
            BotResourceId = BotResourceId,
            Parameters = parameters,
        };

    private static JsonElement Value<T>(T value) =>
        JsonSerializer.SerializeToElement(value);
}
