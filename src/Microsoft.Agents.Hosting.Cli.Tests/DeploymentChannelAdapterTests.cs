using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentChannelAdapterTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("\"SYNTHETIC_SECRET\"")]
    public void SlackAuthorizationUrl_NonObjectResponseHasNoUsableUrl(string response)
    {
        using var document = JsonDocument.Parse(response);

        Assert.Null(DeploymentChannelSlack.ExtractAuthorizationUrl(document.RootElement));
    }

    [Theory]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("update", false)]
    [InlineData("update", true)]
    [InlineData("get", false)]
    [InlineData("get", true)]
    [InlineData("list", false)]
    [InlineData("list", true)]
    public async Task Sms_UsesTypedFieldsAndMasksReturnedOrNullToken(string operation, bool nullToken)
    {
        var response = JsonSerializer.Serialize(new
        {
            properties = new
            {
                channelName = "SmsChannel",
                properties = new
                {
                    accountSID = "synthetic-sid",
                    phone = "+12025550123",
                    isEnabled = false,
                    isValidated = true,
                    authToken = nullToken ? null : "synthetic-auth-token",
                    unknown = "ignore",
                },
            },
        });
        var handler = new StubHttpMessageHandler(_ => Json(operation == "list" ? "{\"value\":[" + response + "]}" : response));
        var adapter = Adapter(handler);
        var write = Upsert("SmsChannel", new Dictionary<string, JsonElement>
        {
            ["accountSID"] = Value("synthetic-sid"),
            ["authToken"] = Value("synthetic-auth-token"),
            ["phone"] = Value("+12025550123"),
            ["isEnabled"] = Value(false),
        });
        var channel = operation switch
        {
            "create" => await adapter.CreateChannelAsync(write, default),
            "update" => await adapter.UpdateChannelAsync(write, default),
            "list" => Assert.Single(await adapter.ListChannelsAsync(BotResourceId, null, default)),
            _ => await adapter.GetChannelAsync("SmsChannel", BotResourceId, null, default),
        };
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(operation == "create" ? HttpMethod.Put : operation == "update" ? HttpMethod.Patch : HttpMethod.Get, sent.Method);
        if (operation is "create" or "update")
        {
            using var body = JsonDocument.Parse(sent.Body!);
            Assert.Equal("SmsChannel", body.RootElement.GetProperty("properties").GetProperty("channelName").GetString());
            Assert.Equal(JsonSerializer.SerializeToElement(write.Parameters).GetRawText(),
                body.RootElement.GetProperty("properties").GetProperty("properties").GetRawText());
        }

        Assert.NotNull(channel);
        Assert.Equal("sms", channel.ChannelName);
        Assert.Equal("synthetic-sid", channel.Parameters["accountSID"].GetString());
        Assert.Equal("+12025550123", channel.Parameters["phone"].GetString());
        Assert.False(channel.Parameters["isEnabled"].GetBoolean());
        Assert.True(channel.Parameters["isValidated"].GetBoolean());
        Assert.Equal("***", channel.Parameters["authToken"].GetString());
        Assert.DoesNotContain("unknown", channel.Parameters);
        Assert.DoesNotContain("synthetic-auth-token", channel.RawArmResponse!.Value.GetRawText());
        Assert.Equal("***", channel.RawArmResponse.Value.GetProperty("properties").GetProperty("properties").GetProperty("authToken").GetString());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("get")]
    [InlineData("list")]
    public async Task Line_UsesTypedCollectionsAndRedactsNestedCredentials(string operation)
    {
        const string response = """
            {"properties":{"channelName":"LineChannel","properties":{
              "callbackUrl":"https://callback.example/one","isValidated":true,
              "lineRegistrations":[{"generatedId":"one","channelSecret":"synthetic-secret","channelAccessToken":"synthetic-token","unknown":"ignore"},
                                   {"generatedId":"two","channelSecret":null,"channelAccessToken":null}]
            }}}
            """;
        var handler = new StubHttpMessageHandler(_ => Json(operation == "list" ? "{\"value\":[" + response + "]}" : response));
        var adapter = Adapter(handler);
        var write = Upsert("LineChannel", new Dictionary<string, JsonElement>
        {
            ["lineRegistrations"] = JsonDocument.Parse(
                """[{"channelSecret":"synthetic-secret","channelAccessToken":"synthetic-token"}]""").RootElement.Clone(),
        });
        var channel = operation switch
        {
            "create" => await adapter.CreateChannelAsync(write, default),
            "update" => await adapter.UpdateChannelAsync(write, default),
            "list" => Assert.Single(await adapter.ListChannelsAsync(BotResourceId, null, default)),
            _ => await adapter.GetChannelAsync("LineChannel", BotResourceId, null, default),
        };
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(operation == "create" ? HttpMethod.Put : operation == "update" ? HttpMethod.Patch : HttpMethod.Get, sent.Method);
        if (operation is "create" or "update")
        {
            using var body = JsonDocument.Parse(sent.Body!);
            Assert.Equal("LineChannel", body.RootElement.GetProperty("properties").GetProperty("channelName").GetString());
            Assert.Equal(JsonSerializer.SerializeToElement(write.Parameters).GetRawText(),
                body.RootElement.GetProperty("properties").GetProperty("properties").GetRawText());
        }

        Assert.NotNull(channel);
        Assert.Equal("line", channel.ChannelName);
        Assert.Equal("https://callback.example/one", channel.Parameters["callbackUrl"].GetString());
        Assert.True(channel.Parameters["isValidated"].GetBoolean());
        foreach (var item in channel.Parameters["lineRegistrations"].EnumerateArray())
        {
            Assert.Equal("***", item.GetProperty("channelSecret").GetString());
            Assert.Equal("***", item.GetProperty("channelAccessToken").GetString());
            Assert.False(item.TryGetProperty("unknown", out _));
        }
        Assert.DoesNotContain("synthetic-", channel.RawArmResponse!.Value.GetRawText());
    }

    private static readonly string[] TrustedOrigins =
        ["https://a.example", "https://b.example"];

    private static DeploymentChannelAdapter Adapter(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), new StubTokenProvider { AccessToken = "arm-token", TenantId = "home" });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("get")]
    public async Task Facebook_UsesHierarchicalWirePayloadAndRedactsAllCredentials(string operation)
    {
        var handler = new StubHttpMessageHandler(_ => Json("""
            {"properties":{"channelName":"FacebookChannel","properties":{
              "appId":"app","isEnabled":false,"appSecret":"synthetic-secret",
              "verifyToken":"synthetic-verify","callbackUrl":"https://callback.example",
              "pages":[{"id":"one","accessToken":"synthetic-token","unknown":"ignore"},
                       {"id":"two","accessToken":null}],"unknown":"ignore"
            }}}
            """));
        var adapter = Adapter(handler);
        var write = Upsert("FacebookChannel", new Dictionary<string, JsonElement>
        {
            ["appId"] = Value("app"),
            ["appSecret"] = Value("synthetic-secret"),
            ["isEnabled"] = Value(false),
            ["pages"] = JsonDocument.Parse("""[{"id":"one","accessToken":"synthetic-token"},{"id":"two","accessToken":"other-token"}]""")
                .RootElement.Clone(),
        });
        var channel = operation switch
        {
            "create" => await adapter.CreateChannelAsync(write, default),
            "update" => await adapter.UpdateChannelAsync(write, default),
            _ => await adapter.GetChannelAsync("FacebookChannel", BotResourceId, null, default),
        };
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(operation == "create" ? HttpMethod.Put : operation == "update" ? HttpMethod.Patch : HttpMethod.Get, sent.Method);
        Assert.Equal($"https://management.azure.com{BotResourceId}/channels/FacebookChannel?api-version=2023-09-15-preview",
            sent.Uri.ToString());
        if (operation != "get")
        {
            using var body = JsonDocument.Parse(sent.Body!);
            Assert.Equal(JsonSerializer.SerializeToElement(write.Parameters).GetRawText(),
                body.RootElement.GetProperty("properties").GetProperty("properties").GetRawText());
        }

        Assert.NotNull(channel);
        Assert.Equal(6, channel.Parameters.Count);
        Assert.Equal("https://callback.example", channel.Parameters["callbackUrl"].GetString());
        Assert.Equal("***", channel.Parameters["appSecret"].GetString());
        Assert.Equal("***", channel.Parameters["verifyToken"].GetString());
        Assert.Equal(2, channel.Parameters["pages"].GetArrayLength());
        foreach (var page in channel.Parameters["pages"].EnumerateArray())
        {
            Assert.Equal("***", page.GetProperty("accessToken").GetString());
            Assert.False(page.TryGetProperty("unknown", out _));
        }

        Assert.DoesNotContain("synthetic-", channel.RawArmResponse!.Value.GetRawText());
        Assert.DoesNotContain("unknown", channel.Parameters);
    }

    [Fact]
    public async Task GetSlack_MasksAuthorizationStateWithoutExposingAnAction()
    {
        var handler = new StubHttpMessageHandler(_ => Json(JsonSerializer.Serialize(new
        {
            id = "/channels/SlackChannel",
            properties = new
            {
                channelName = "SlackChannel",
                properties = new { redirectAction = SlackAuthorizationAction(), IsValidated = false, lastSubmissionId = "submission", registerBeforeOAuthFlow = false },
            },
        })));
        var channel = await Adapter(handler).GetChannelAsync("SlackChannel", BotResourceId, null, default);
        Assert.Null(channel!.AuthorizationUrl);
        Assert.Empty(channel.Parameters);
        var diagnostics = channel.RawArmResponse!.Value.GetProperty("properties").GetProperty("properties");
        Assert.Equal("***", diagnostics.GetProperty("redirectAction").GetString());
        Assert.False(diagnostics.GetProperty("IsValidated").GetBoolean());
        Assert.Equal("submission", diagnostics.GetProperty("lastSubmissionId").GetString());
        Assert.False(diagnostics.GetProperty("registerBeforeOAuthFlow").GetBoolean());
        Assert.DoesNotContain("SYNTHETIC_STATE", channel.RawArmResponse!.Value.GetRawText());
    }

    [Theory]
    [InlineData(false, "https://slack.botframework.com")]
    [InlineData(true, "https://slack.botframework.com")]
    [InlineData(false, "https://europe.slack.botframework.com")]
    [InlineData(true, "https://india.slack.botframework.com")]
    [InlineData(false, "https://slack.botframework.com/api/OAuth")]
    [InlineData(true, "https://slack.botframework.com/api/OAuth")]
    public async Task WriteSlack_ExtractsAzureAuthorizationActionBeforeRedaction(bool update, string callback)
    {
        var action = SlackAuthorizationAction(callback);
        var handler = new StubHttpMessageHandler(_ => Json(JsonSerializer.Serialize(new
        {
            id = "/channels/SlackChannel",
            properties = new { channelName = "SlackChannel", properties = new { redirectAction = action } },
        })));
        var write = Upsert("SlackChannel", new Dictionary<string, JsonElement>
        {
            ["clientId"] = Value("app"),
            ["scopes"] = Value("chat:write"),
        });
        var adapter = Adapter(handler);
        var channel = update
            ? await adapter.UpdateChannelAsync(write, default)
            : await adapter.CreateChannelAsync(write, default);

        Assert.Equal(action, channel.AuthorizationUrl!.AbsoluteUri);
        Assert.DoesNotContain("SYNTHETIC_STATE", channel.RawArmResponse!.Value.GetRawText());
        Assert.DoesNotContain("SYNTHETIC_STATE", JsonSerializer.Serialize(channel));
        Assert.DoesNotContain("redirectAction", channel.Parameters);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WriteSlack_PreservesProviderAuthorizationUrlWithoutInterpretingOAuthParameters(bool update, bool hasQuery)
    {
        var action = hasQuery
            ? SlackAuthorizationAction("https://regional.slack.botframework.com/api/OAuth")
                .Replace("client_id=app", "client_id=provider-app")
                .Replace("scope=chat%3Awrite", "scope=chat%3Awrite%2Cchat%3Awrite.public")
            : "https://slack.com/oauth/v2/authorize";
        var handler = new StubHttpMessageHandler(_ => Json(JsonSerializer.Serialize(new
        {
            properties = new { channelName = "SlackChannel", properties = new { redirectAction = action } },
        })));
        var write = Upsert("SlackChannel", new Dictionary<string, JsonElement>
        {
            ["clientId"] = Value("app"),
            ["scopes"] = Value("chat:write"),
        });
        var adapter = Adapter(handler);

        var channel = update
            ? await adapter.UpdateChannelAsync(write, default)
            : await adapter.CreateChannelAsync(write, default);

        Assert.Equal(action, channel.AuthorizationUrl!.AbsoluteUri);
        Assert.DoesNotContain(action, channel.RawArmResponse!.Value.GetRawText());
        Assert.DoesNotContain("SYNTHETIC_STATE", JsonSerializer.Serialize(channel));
    }

    [Theory]
    [InlineData("scheme")]
    [InlineData("host")]
    [InlineData("userinfo")]
    [InlineData("port")]
    [InlineData("path")]
    [InlineData("fragment")]
    [InlineData("relative")]
    [InlineData("malformed")]
    public async Task WriteSlack_RejectsUnexpectedAuthorizationActionsWithoutFailingTheWriteOrExposingThem(string problem)
    {
        var action = SlackAuthorizationAction();
        action = problem switch
        {
            "scheme" => action.Replace("https://slack.com", "http://slack.com"),
            "host" => action.Replace("https://slack.com", "https://slack.com.example.test"),
            "userinfo" => action.Replace("https://slack.com", "https://user@slack.com"),
            "port" => action.Replace("https://slack.com", "https://slack.com:444"),
            "path" => action.Replace("/oauth/v2/authorize", "/unexpected"),
            "fragment" => action + "#SYNTHETIC_STATE",
            "relative" => "/oauth/v2/authorize",
            _ => "not-a-url",
        };
        var handler = new StubHttpMessageHandler(_ => Json(JsonSerializer.Serialize(new
        {
            id = "/channels/SlackChannel",
            properties = new { channelName = "SlackChannel", properties = new { redirectAction = action } },
        })));
        var channel = await Adapter(handler).CreateChannelAsync(
            Upsert("SlackChannel", new Dictionary<string, JsonElement>
            {
                ["clientId"] = Value("app"),
                ["scopes"] = Value("chat:write"),
            }), default);

        Assert.Equal("slack", channel.ChannelName);
        Assert.Null(channel.AuthorizationUrl);
        Assert.Equal("***", channel.RawArmResponse!.Value.GetProperty("properties").GetProperty("properties").GetProperty("redirectAction").GetString());
        Assert.DoesNotContain("SYNTHETIC_STATE", JsonSerializer.Serialize(channel));
    }

    private static string SlackAuthorizationAction(string callback = "https://slack.botframework.com") =>
        "https://slack.com/oauth/v2/authorize?client_id=app&scope=chat%3Awrite&redirect_uri=" +
        Uri.EscapeDataString(callback) + "&state=SYNTHETIC_STATE";

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteSlack_UsesExplicitMethodAndRedactsEchoedSecret(bool update)
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

        var write = new BotServiceChannelWriteRequest
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
        };
        var channel = update
            ? await adapter.UpdateChannelAsync(write, default)
            : await adapter.CreateChannelAsync(write, default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(update ? HttpMethod.Patch : HttpMethod.Put, sent.Method);
        Assert.Equal(
            $"https://management.azure.com{BotResourceId}/channels/SlackChannel?api-version=2023-09-15-preview",
            sent.Uri.ToString());
        using var request = JsonDocument.Parse(sent.Body!);
        Assert.Equal("global", request.RootElement.GetProperty("location").GetString());
        Assert.Equal("SlackChannel", request.RootElement.GetProperty("properties").GetProperty("channelName").GetString());
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

    [Theory]
    [InlineData("M365Extensions", "m365extensions")]
    [InlineData("OutlookChannel", "outlook")]
    public async Task CreatePropertylessChannel_OmitsInnerProperties(string armName, string channelName)
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            $$"""
            {
              "id":"/channels/{{armName}}",
              "properties":{"channelName":"{{armName}}","provisioningState":"Succeeded"}
            }
            """));

        var channel = await Adapter(handler).CreateChannelAsync(
            Upsert(armName, new Dictionary<string, JsonElement>()),
            default);

        using var request = JsonDocument.Parse(handler.Requests.Single().Body!);
        var properties = request.RootElement.GetProperty("properties");
        Assert.Equal(armName, properties.GetProperty("channelName").GetString());
        Assert.False(properties.TryGetProperty("properties", out _));
        Assert.Equal(channelName, channel.ChannelName);
        Assert.Empty(channel.Parameters);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteDirectLine_PreservesPreShapedSiteForBothMethods(bool update)
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """{"id":"/channels/DirectLineChannel","properties":{"channelName":"DirectLineChannel","properties":{"isEnabled":true}}}"""));

        var write = Upsert(
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
                });
        var adapter = Adapter(handler);
        if (update)
        {
            await adapter.UpdateChannelAsync(write, default);
        }
        else
        {
            await adapter.CreateChannelAsync(write, default);
        }

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(update ? HttpMethod.Patch : HttpMethod.Put, sent.Method);
        using var request = JsonDocument.Parse(sent.Body!);
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

        await Adapter(handler).UpdateChannelAsync(
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

        await Adapter(handler).UpdateChannelAsync(
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
        Assert.Equal(BotServiceResponseRedactor.Mask, persistedSite.GetProperty("key").GetString());
        Assert.False(persistedSite.TryGetProperty("unexpected", out _));
        Assert.Equal("Default Site", site.GetProperty("siteName").GetString());
        Assert.True(site.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("https://example.test", site.GetProperty("trustedOrigins")[0].GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, site.GetProperty("key").GetString());
        Assert.Equal(
            "must-not-render",
            site.GetProperty("unexpected").GetProperty("nested").GetString());
    }

    [Theory]
    [InlineData("DirectLineChannel")]
    [InlineData("WebChatChannel")]
    public async Task GetSiteChannel_MultipleSites_ProjectsAllSites(string armName)
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            $$$$"""
            {"id":"/channels/{{{{armName}}}}","properties":{"channelName":"{{{{armName}}}}","properties":{
              "sites":[
                {"siteName":"Primary","isEnabled":true},
                {"siteName":"Secondary","isEnabled":false}
              ]
            }}}
            """));

        var channel = await Adapter(handler).GetChannelAsync(
            armName,
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

    [Theory]
    [InlineData("DirectLineEmbedCode")]
    [InlineData("directLineEmbedCode")]
    [InlineData("DIRECTLINEEMBEDCODE")]
    public async Task GetDirectLine_ProjectsAndMasksResponseOnlyFieldsFromTheSameCatalog(string embedCodeProperty)
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """
            {"id":"/channels/DirectLineChannel","properties":{"channelName":"DirectLineChannel","properties":{
              "DirectLineEmbedCode":"embed-with-credentials",
              "extensionKey1":"secret-one","extensionKey2":null,"isEnabled":true,
              "futureChannelField":"debug-only",
              "sites":[
                {"siteId":"one","siteName":"Primary","key":null,"key2":"secret-two",
                 "key1LastUpdated":"2026-10-13T11:25:35Z","key2LastUpdated":"2026-10-14T11:25:35Z",
                 "isTokenEnabled":false,"isWebchatPreviewEnabled":false,"isWebChatSpeechEnabled":true,
                 "futureSecret":"hidden"},
                {"siteId":"two","siteName":"Secondary","key":"secret-three","key2":null}
              ]
            }}}
            """.Replace("DirectLineEmbedCode", embedCodeProperty, StringComparison.Ordinal)));

        var channel = await Adapter(handler).GetChannelAsync(
            "DirectLineChannel", BotResourceId, targetTenantId: null, default);
        var parameters = channel!.Parameters;
        foreach (var name in new[] { "directLineEmbedCode", "extensionKey1", "extensionKey2" })
        {
            Assert.Equal(BotServiceResponseRedactor.Mask, parameters[name].GetString());
        }

        Assert.False(parameters.ContainsKey("isEnabled"));
        Assert.False(parameters.ContainsKey("futureChannelField"));
        var sites = parameters["sites"];
        Assert.Equal(2, sites.GetArrayLength());
        Assert.Equal("one", sites[0].GetProperty("siteId").GetString());
        Assert.Equal("two", sites[1].GetProperty("siteId").GetString());
        Assert.False(sites[0].TryGetProperty("key1LastUpdated", out _));
        Assert.False(sites[0].TryGetProperty("key2LastUpdated", out _));
        Assert.False(sites[0].GetProperty("isTokenEnabled").GetBoolean());
        Assert.False(sites[0].TryGetProperty("isWebchatPreviewEnabled", out _));
        Assert.False(sites[0].TryGetProperty("isWebChatSpeechEnabled", out _));
        foreach (var site in sites.EnumerateArray())
        {
            Assert.Equal(BotServiceResponseRedactor.Mask, site.GetProperty("key").GetString());
            Assert.Equal(BotServiceResponseRedactor.Mask, site.GetProperty("key2").GetString());
        }

        var raw = channel.RawArmResponse!.Value.GetProperty("properties").GetProperty("properties");
        Assert.Equal(BotServiceResponseRedactor.Mask, raw.GetProperty(embedCodeProperty).GetString());
        Assert.False(parameters.ContainsKey("DirectLineEmbedCode"));
        Assert.Equal("debug-only", raw.GetProperty("futureChannelField").GetString());
        Assert.True(raw.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("2026-10-13T11:25:35Z", raw.GetProperty("sites")[0].GetProperty("key1LastUpdated").GetString());
        Assert.Equal("2026-10-14T11:25:35Z", raw.GetProperty("sites")[0].GetProperty("key2LastUpdated").GetString());
        Assert.False(raw.GetProperty("sites")[0].GetProperty("isWebchatPreviewEnabled").GetBoolean());
        Assert.True(raw.GetProperty("sites")[0].GetProperty("isWebChatSpeechEnabled").GetBoolean());
        Assert.Equal(BotServiceResponseRedactor.Mask, raw.GetProperty("sites")[0].GetProperty("futureSecret").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("embed-with-credentials")]
    public async Task GetWebChat_ProjectsAndMasksResponseOnlyFieldsFromTheSameCatalog(string? embedCode)
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            $$$$"""
            {"id":"/channels/WebChatChannel","properties":{"channelName":"WebChatChannel","properties":{
              "webChatEmbedCode":{{{{JsonSerializer.Serialize(embedCode)}}}},"isEnabled":false,
              "futureChannelField":"debug-only",
              "sites":[
                {"siteId":"one","siteName":"Primary","isEnabled":true,"key":null,"key2":"secret-two",
                 "key1LastUpdated":"2026-10-13T11:25:35Z","key2LastUpdated":"2026-10-14T11:25:35Z",
                 "isTokenEnabled":false,"isV1Enabled":true,"eTag":"site-etag",
                 "isWebchatPreviewEnabled":true,"isWebChatSpeechEnabled":null,"futureSecret":"hidden",
                 "isSecureSiteEnabled":true,"isV3Enabled":true,"trustedOrigins":["https://example.test"]},
                {"siteId":"two","siteName":"Secondary","key":"secret-three","key2":null}
              ]
            }}}
            """));

        var channel = await Adapter(handler).GetChannelAsync(
            "WebChatChannel", BotResourceId, targetTenantId: null, default);
        var parameters = channel!.Parameters;
        Assert.Equal(BotServiceResponseRedactor.Mask, parameters["webChatEmbedCode"].GetString());
        Assert.False(parameters.ContainsKey("isEnabled"));
        Assert.False(parameters.ContainsKey("futureChannelField"));
        var sites = parameters["sites"];
        Assert.Equal(2, sites.GetArrayLength());
        Assert.Equal("one", sites[0].GetProperty("siteId").GetString());
        Assert.Equal("two", sites[1].GetProperty("siteId").GetString());
        Assert.False(sites[0].TryGetProperty("key1LastUpdated", out _));
        Assert.False(sites[0].TryGetProperty("key2LastUpdated", out _));
        Assert.Equal("site-etag", sites[0].GetProperty("eTag").GetString());
        Assert.True(sites[0].GetProperty("isEnabled").GetBoolean());
        Assert.False(sites[0].GetProperty("isTokenEnabled").GetBoolean());
        Assert.True(sites[0].GetProperty("isWebchatPreviewEnabled").GetBoolean());
        foreach (var field in new[]
        {
            "isWebChatSpeechEnabled", "isSecureSiteEnabled", "isV1Enabled", "isV3Enabled", "trustedOrigins",
        })
        {
            Assert.False(sites[0].TryGetProperty(field, out _));
        }
        foreach (var site in sites.EnumerateArray())
        {
            Assert.Equal(BotServiceResponseRedactor.Mask, site.GetProperty("key").GetString());
            Assert.Equal(BotServiceResponseRedactor.Mask, site.GetProperty("key2").GetString());
            Assert.False(site.TryGetProperty("futureSecret", out _));
        }

        var raw = channel.RawArmResponse!.Value.GetProperty("properties").GetProperty("properties");
        Assert.Equal(BotServiceResponseRedactor.Mask, raw.GetProperty("webChatEmbedCode").GetString());
        Assert.Equal("debug-only", raw.GetProperty("futureChannelField").GetString());
        Assert.False(raw.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("2026-10-13T11:25:35Z", raw.GetProperty("sites")[0].GetProperty("key1LastUpdated").GetString());
        Assert.Equal("2026-10-14T11:25:35Z", raw.GetProperty("sites")[0].GetProperty("key2LastUpdated").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, raw.GetProperty("sites")[0].GetProperty("futureSecret").GetString());
        Assert.Equal(JsonValueKind.Null, raw.GetProperty("sites")[0].GetProperty("isWebChatSpeechEnabled").ValueKind);
        Assert.True(raw.GetProperty("sites")[0].GetProperty("isV1Enabled").GetBoolean());
        Assert.True(raw.GetProperty("sites")[0].TryGetProperty("trustedOrigins", out _));
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

    [Theory]
    [InlineData("M365Extensions", "m365extensions")]
    [InlineData("OutlookChannel", "outlook")]
    public async Task List_MapsPropertylessChannel(string armName, string channelName)
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            $$"""
            {"value":[{
              "id":"/channels/{{armName}}",
              "name":"dev/{{armName}}",
              "properties":{
                "channelName":"{{armName}}",
                "provisioningState":"Succeeded"
              }
            }]}
            """));

        var channel = Assert.Single(await Adapter(handler).ListChannelsAsync(
            BotResourceId,
            targetTenantId: null,
            default));

        Assert.Equal(channelName, channel.ChannelName);
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
    public async Task Update_ResponseWithoutChannelArmName_DoesNotFallBackToRequest()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """{"properties":{"properties":{"isEnabled":true}}}"""));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler).UpdateChannelAsync(
                Upsert("SlackChannel", new Dictionary<string, JsonElement>()),
                default));

        Assert.Equal(ProvisioningErrorKind.InvalidResponse, ex.Kind);
        Assert.Contains("properties.channelName", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Update_ProviderFailure_IsSurfacedWithoutPutFallback(HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("""{"error":{"code":"ChannelUpdateFailed","message":"Update rejected."}}"""),
        });

        var error = await Assert.ThrowsAsync<TargetRequestException>(() =>
            Adapter(handler).UpdateChannelAsync(
                Upsert("DirectLineChannel", new Dictionary<string, JsonElement>
                {
                    ["sites"] = Value(new[] { new { siteName = "primary", isEnabled = false } }),
                }),
                default));

        Assert.Equal((int)status, error.Status);
        Assert.Equal(HttpMethod.Patch, Assert.Single(handler.Requests).Method);
    }

    [Theory]
    [MemberData(nameof(DeploymentChannelCatalogTests.UnsupportedArmNamesAndNames), MemberType = typeof(DeploymentChannelCatalogTests))]
    public async Task List_UnsupportedChannels_RetainRedactedDebugPropertiesAndContinuePaging(string armName, string channelName)
    {
        var requests = 0;
        var handler = new StubHttpMessageHandler(_ => Json(requests++ == 0
            ? $$$$"""
                {
                  "value":[
                    {"id":"/channels/SlackChannel","properties":{"channelName":"SlackChannel","properties":{
                      "clientId":"client-1","clientSecret":"SYNTHETIC_KNOWN_SECRET"
                    }}},
                    {"id":"/channels/{{{{armName}}}}","extra":{"token":"SYNTHETIC_OUTER_SECRET"},"properties":{
                      "channelName":"{{{{armName}}}}","unknownPassword":"SYNTHETIC_METADATA_SECRET","properties":{
                        "description":"visible",
                        "entries":[{"accessToken":"SYNTHETIC_INNER_SECRET","label":"visible entry"}]
                      }
                    }}
                  ],
                  "nextLink":"https://management.azure.com/next"
                }
                """
            : """{"value":[{"id":"/channels/MsTeamsChannel","properties":{"channelName":"MsTeamsChannel"}}]}"""));

        var channels = await Adapter(handler).ListChannelsAsync(BotResourceId, targetTenantId: null, default);

        Assert.Equal(2, requests);
        Assert.Equal(["slack", channelName, "msteams"], channels.Select(channel => channel.ChannelName));
        var unsupported = channels[1];
        Assert.Equal($"/channels/{armName}", unsupported.ResourceId);
        Assert.Empty(unsupported.Parameters);
        var response = unsupported.RawArmResponse!.Value;
        var properties = response.GetProperty("properties").GetProperty("properties");
        Assert.Equal("visible", properties.GetProperty("description").GetString());
        Assert.Equal("visible entry", properties.GetProperty("entries")[0].GetProperty("label").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, properties.GetProperty("entries")[0].GetProperty("accessToken").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, channels[0].Parameters["clientSecret"].GetString());
        var result = new Core.Operations.DeploymentResult<DeploymentChannelRecord[]>
        {
            Status = Core.Operations.DeploymentOperationStatus.Listed,
            Payload = channels.ToArray(),
        };
        Assert.DoesNotContain("SYNTHETIC_", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("""{"value":[{"properties":{"channelName":"UnknownArmChannel"}}]}""", null)]
    [InlineData("""{"value":[{"id":"","properties":{"channelName":"UnknownArmChannel"}}]}""", "")]
    public async Task List_UnsupportedChannel_DoesNotRequireResourceId(string body, string? resourceId)
    {
        var handler = new StubHttpMessageHandler(_ => Json(body));

        var channel = Assert.Single(await Adapter(handler).ListChannelsAsync(
            BotResourceId, targetTenantId: null, default));

        Assert.Equal(resourceId, channel.ResourceId);
        Assert.Equal("UnknownArmChannel", channel.ChannelName);
        Assert.NotNull(channel.RawArmResponse);
    }

    [Theory]
    [InlineData("""{"value":[{"id":"/channels/UnknownArmChannel","properties":{}}]}""")]
    [InlineData("""{"value":[{"id":"/channels/UnknownArmChannel","properties":{"channelName":42}}]}""")]
    public async Task List_MalformedUnsupportedChannel_ThrowsInvalidResponse(string body)
    {
        var handler = new StubHttpMessageHandler(_ => Json(body));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler).ListChannelsAsync(BotResourceId, targetTenantId: null, default));

        Assert.Equal(ProvisioningErrorKind.InvalidResponse, ex.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleChannelResponse_UnknownArmName_RemainsStrict(bool upsert)
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """{"id":"/channels/UnknownArmChannel","properties":{"channelName":"UnknownArmChannel"}}"""));
        var adapter = Adapter(handler);

        Func<Task> action = upsert
            ? () => adapter.UpdateChannelAsync(Upsert("SlackChannel", new Dictionary<string, JsonElement>()), default)
            : () => adapter.GetChannelAsync("SlackChannel", BotResourceId, targetTenantId: null, default);
        var ex = await Assert.ThrowsAsync<DeploymentException>(action);

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

    private static BotServiceChannelWriteRequest Upsert(
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
