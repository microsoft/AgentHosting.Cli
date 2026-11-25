using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsChannelAdapterTests
{
    private const string Audience = "https://api.powerplatform.com/";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Home = "22222222-2222-2222-2222-222222222222";
    private const string Agent = "44444444-4444-4444-4444-444444444444";
    private const string Deployment = "77777777-7777-7777-7777-777777777777";
    private const string DeploymentRoute = $"https://api.powerplatform.com/agenthosting/environments/{Home}/agents/{Agent}/deployments/{Deployment}";
    private const string ChannelId = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/ams-bots/providers/Microsoft.BotService/botServices/ams-11111111-22222222-88888888-77777777/channels/MsTeamsChannel";
    private const string TeamsBody = $$$"""{"id":"{{{ChannelId}}}","channelType":"msteams","provisioningState":"Succeeded","properties":{"acceptedTerms":true,"isEnabled":true}}""";
    private const string TokenBody = """{"token":"secret-token-value","conversationId":"conversation-1","expiresInSeconds":3600,"siteName":"Default Site","endpoint":"https://directline.botframework.com"}""";
    private static readonly string[] KnownRefusalKinds = ["deployment-not-found", "channel-not-found", "invalid-channel-request", "channel-settings-refused", "deployment-has-no-bot", "agent-bot-service-conflict", "directline-unavailable", "channel-operation-failed", "deployment-deleting"];

    [Fact]
    public async Task ListTypes_ReadsTheTypeRouteWithTheReadPermissionAndParsesTheCatalog()
    {
        var handler = Handler(HttpStatusCode.OK, """
            {"value":[{"channelType":"directline","displayName":"Direct Line","operations":["create","update","token"],"parameters":[
              {"name":"sites","type":"sites","required":false,"readOnly":false,"clearable":true,"itemParameters":[
                {"name":"siteName","type":"string","required":true,"isSelector":true},
                {"name":"isEnabled","type":"boolean","defaultValue":true}]},
              {"name":"deploymentEnvironment","type":"string","allowedValues":["CommercialDeployment","GCCModerateDeployment"],"defaultValue":"CommercialDeployment","readOnly":true},
              {"name":"accessToken","type":"string","required":true,"isSecret":true}]}]}
            """);
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsRead };

        var types = await Adapter(handler, tokens).ListTypesAsync(DeploymentRequest(), default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal($"{DeploymentRoute}/channelTypes?api-version=1", sent.Uri.AbsoluteUri);
        Assert.True(sent.HasAuthorization);
        Assert.Empty(sent.Body);
        Assert.Equal([Audience], tokens.Resources);
        Assert.Equal([Tenant], tokens.Tenants);
        var type = Assert.Single(types);
        Assert.Equal("directline", type.ChannelType);
        Assert.Equal("Direct Line", type.DisplayName);
        Assert.Equal(["create", "update", "token"], type.Operations);
        Assert.Equal(["sites", "deploymentEnvironment", "accessToken"], type.Parameters.Select(parameter => parameter.Name));
        var sites = type.Parameters[0];
        Assert.True(sites.Clearable);
        Assert.False(sites.Required);
        Assert.Null(sites.AllowedValues);
        Assert.Null(sites.DefaultValue);
        Assert.Equal(["siteName", "isEnabled"], sites.ItemParameters!.Select(parameter => parameter.Name));
        Assert.True(sites.ItemParameters![0].Required);
        Assert.True(sites.ItemParameters![0].IsSelector);
        Assert.False(sites.ItemParameters![1].IsSelector);
        Assert.False(sites.IsSecret);
        Assert.True(sites.ItemParameters![1].DefaultValue!.Value.GetBoolean());
        var environment = type.Parameters[1];
        Assert.True(environment.ReadOnly);
        Assert.Equal(["CommercialDeployment", "GCCModerateDeployment"], environment.AllowedValues);
        Assert.Equal("CommercialDeployment", environment.DefaultValue!.Value.GetString());
        Assert.Null(environment.ItemParameters);
        Assert.False(environment.IsSecret);
        var accessToken = type.Parameters[2];
        Assert.True(accessToken.IsSecret);
        Assert.True(accessToken.Required);
        Assert.False(accessToken.IsSelector);
    }

    [Fact]
    public async Task List_ReadsTheChannelCollectionWithTheReadPermission()
    {
        var handler = Handler(HttpStatusCode.OK, $$$"""{"value":[{{{TeamsBody}}},{"id":"/bots/ams/channels/WebChatChannel","channelType":"webchat","properties":{"sites":[{"siteName":"Default Site"}]}}]}""");

        var channels = await Adapter(handler, new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsRead }).ListAsync(DeploymentRequest(), default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal($"{DeploymentRoute}/channels?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Equal(["msteams", "webchat"], channels.Select(channel => channel.ChannelName));
        Assert.Equal("Default Site", channels[1].Parameters["sites"][0].GetProperty("siteName").GetString());
    }

    [Fact]
    public async Task Get_ReadsOneChannelWithTheReadPermission()
    {
        var handler = Handler(HttpStatusCode.OK, TeamsBody);

        var channel = await Adapter(handler, new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsRead }).GetAsync(ChannelRequest("msteams"), default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal($"{DeploymentRoute}/channels/msteams?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Equal(ChannelId, channel.ResourceId);
        Assert.Equal("msteams", channel.ChannelName);
        Assert.Equal(["acceptedTerms", "isEnabled"], channel.Parameters.Keys);
        Assert.True(channel.Parameters["acceptedTerms"].GetBoolean());
        Assert.Null(channel.RawArmResponse);
    }

    [Theory]
    [InlineData(HttpStatusCode.Created, true)]
    [InlineData(HttpStatusCode.OK, false)]
    public async Task Create_PostsTheTypedPropertiesAndReportsWhetherAmsCreatedTheChannel(HttpStatusCode status, bool isCreated)
    {
        var handler = Handler(status, TeamsBody);
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate };

        var result = await Adapter(handler, tokens).CreateAsync(new AmsChannelCreateTransportRequest { Deployment = DeploymentRequest(), ChannelType = "msteams", Properties = Properties("""{"acceptedTerms":true,"deploymentEnvironment":"CommercialDeployment"}""") }, default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal($"{DeploymentRoute}/channels?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Equal("application/json", sent.ContentType);
        AssertBody("""{"channelType":"msteams","properties":{"acceptedTerms":true,"deploymentEnvironment":"CommercialDeployment"}}""", sent);
        Assert.Equal(isCreated, result.IsCreated);
        Assert.Equal("msteams", result.Channel.ChannelName);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Create_PostsTheListsInsideThePropertiesAndReturnsTheWarnings()
    {
        var handler = Handler(HttpStatusCode.Created, """{"id":"/bots/ams/channels/DirectLineChannel","channelType":"directline","properties":{"sites":[]},"warnings":["The settings of site 'web' were not applied."]}""");

        var result = await Adapter(handler, new FakePpapiTokenProvider()).CreateAsync(new AmsChannelCreateTransportRequest { Deployment = DeploymentRequest(), ChannelType = "directline", Properties = Properties("""{"sites":[{"siteName":"web","isEnabled":false}]}""") }, default);

        AssertBody("""{"channelType":"directline","properties":{"sites":[{"siteName":"web","isEnabled":false}]}}""", Assert.Single(handler.Requests));
        Assert.Equal(["The settings of site 'web' were not applied."], result.Warnings);
    }

    [Fact]
    public async Task Update_PatchesThePropertiesIncludingClearedValues()
    {
        var handler = Handler(HttpStatusCode.OK, TeamsBody);

        var result = await Adapter(handler, new FakePpapiTokenProvider()).UpdateAsync(new AmsChannelUpdateTransportRequest { Deployment = DeploymentRequest(), ChannelType = "msteams", Properties = Properties("""{"enableCalling":true,"callingWebhook":null}""") }, default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, sent.Method);
        Assert.Equal($"{DeploymentRoute}/channels/msteams?api-version=1", sent.Uri.AbsoluteUri);
        AssertBody("""{"properties":{"enableCalling":true,"callingWebhook":null}}""", sent);
        Assert.False(result.IsCreated);
    }

    [Fact]
    public async Task Update_PatchesOneItemChangeWithoutProperties()
    {
        var handler = Handler(HttpStatusCode.OK, """{"id":"/bots/ams/channels/FacebookChannel","channelType":"facebook","properties":{}}""");

        await Adapter(handler, new FakePpapiTokenProvider()).UpdateAsync(new AmsChannelUpdateTransportRequest { Deployment = DeploymentRequest(), ChannelType = "facebook", ItemChange = new AmsChannelItemChange("remove", [Json("""{"id":"page-1"}""")]) }, default);

        AssertBody("""{"itemChange":{"operation":"remove","items":[{"id":"page-1"}]}}""", Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task Delete_SendsOneBodylessDeleteWithTheUpdatePermission()
    {
        var handler = new CapturingPackageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await Adapter(handler, new FakePpapiTokenProvider()).DeleteAsync(ChannelRequest("msteams"), default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, sent.Method);
        Assert.Equal($"{DeploymentRoute}/channels/msteams?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Empty(sent.Body);
        Assert.Null(sent.ContentType);
    }

    [Fact]
    public async Task Token_PostsTheSiteAndUserAndParsesTheToken()
    {
        var handler = Handler(HttpStatusCode.OK, TokenBody);

        var token = await Adapter(handler, new FakePpapiTokenProvider()).CreateDirectLineTokenAsync(new AmsDirectLineTokenTransportRequest { Deployment = DeploymentRequest(), SiteName = "Default Site", UserId = "dl_user" }, default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal($"{DeploymentRoute}/channels/directline/token?api-version=1", sent.Uri.AbsoluteUri);
        AssertBody("""{"siteName":"Default Site","userId":"dl_user"}""", sent);
        Assert.Equal("secret-token-value", token.Token);
        Assert.Equal("conversation-1", token.ConversationId);
        Assert.Equal(3600, token.ExpiresInSeconds);
        Assert.Equal("Default Site", token.SiteName);
        Assert.Equal("https://directline.botframework.com", token.Endpoint);
    }

    [Fact]
    public async Task Token_WithoutSiteOrUserPostsAnEmptyObject()
    {
        var handler = Handler(HttpStatusCode.OK, TokenBody);

        await Adapter(handler, new FakePpapiTokenProvider()).CreateDirectLineTokenAsync(new AmsDirectLineTokenTransportRequest { Deployment = DeploymentRequest() }, default);

        AssertBody("{}", Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task Webhook_PostsWithoutABodyAndParsesTheWebhookValues()
    {
        var handler = Handler(HttpStatusCode.OK, """{"callbackUrl":"https://facebook.botframework.com/api/v1/bots/ams-bot","verifyToken":"secret-token-value"}""");

        var webhook = await Adapter(handler, new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate }).GetWebhookAsync(ChannelRequest("facebook"), default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal($"{DeploymentRoute}/channels/facebook/webhook?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Empty(sent.Body);
        Assert.Null(sent.ContentType);
        Assert.Equal("https://facebook.botframework.com/api/v1/bots/ams-bot", webhook.CallbackUrl);
        Assert.Equal("secret-token-value", webhook.VerifyToken);
    }

    [Theory]
    [InlineData("types", PowerPlatformApiPermissions.AppsUpdate)]
    [InlineData("list", PowerPlatformApiPermissions.AppsUpdate)]
    [InlineData("get", PowerPlatformApiPermissions.AppsUpdate)]
    [InlineData("create", PowerPlatformApiPermissions.AppsRead)]
    [InlineData("update", PowerPlatformApiPermissions.AppsRead)]
    [InlineData("delete", PowerPlatformApiPermissions.AppsRead)]
    [InlineData("token", PowerPlatformApiPermissions.AppsRead)]
    [InlineData("webhook", PowerPlatformApiPermissions.AppsRead)]
    public async Task EveryCall_RequiresItsPermissionBeforeSending(string operation, string grantedScope)
    {
        var handler = Handler(HttpStatusCode.OK, TeamsBody);

        await Assert.ThrowsAsync<TokenClaimException>(() => Invoke(Adapter(handler, new FakePpapiTokenProvider { Scopes = grantedScope }), operation));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("get", HttpStatusCode.NotFound, "DeploymentNotFound", "deployment-not-found", ExitCodes.Failure)]
    [InlineData("create", HttpStatusCode.NotFound, "deploymentnotfound", "deployment-not-found", ExitCodes.Failure)]
    [InlineData("get", HttpStatusCode.NotFound, "ChannelNotFound", "channel-not-found", ExitCodes.Usage)]
    [InlineData("list", HttpStatusCode.Conflict, "DeploymentBotServiceRequired", "deployment-has-no-bot", ExitCodes.Failure)]
    [InlineData("update", HttpStatusCode.Conflict, "AgentBotServiceConflict", "agent-bot-service-conflict", ExitCodes.Failure)]
    [InlineData("token", HttpStatusCode.NotImplemented, "DirectLineRegionNotSupported", "directline-unavailable", ExitCodes.Failure)]
    [InlineData("create", HttpStatusCode.ServiceUnavailable, "AgentBotServiceChannelFailed", "channel-operation-failed", ExitCodes.Failure)]
    [InlineData("delete", HttpStatusCode.BadGateway, "AgentBotServiceChannelFailed", "channel-operation-failed", ExitCodes.Failure)]
    [InlineData("update", HttpStatusCode.BadRequest, "ChannelSettingsRefused", "channel-settings-refused", ExitCodes.Usage)]
    [InlineData("token", HttpStatusCode.Conflict, "DeploymentDeleting", "deployment-deleting", ExitCodes.Failure)]
    [InlineData("webhook", HttpStatusCode.NotFound, "ChannelNotFound", "channel-not-found", ExitCodes.Usage)]
    public async Task KnownRefusals_AreExplainedWithCorrelationAndNoServiceDetail(string operation, HttpStatusCode status, string code, string errorKind, int exitCode)
    {
        var response = FakeHttpResponse.Json(status, $$$"""{"error":{"code":"{{{code}}}","message":"private-response-detail"}}""");
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-channel-id");
        var handler = new CapturingPackageHandler(_ => response);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, ReadAndUpdateTokens()), operation));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(errorKind, exception.ErrorKind);
        Assert.Equal(exitCode, exception.ExitCode);
        Assert.Equal(ErrorCodes.Deployment.AgentDeployServiceRejected, exception.ErrorCode);
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("serviceRequestId=service-channel-id", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", exception.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChannelNotFound_NamesTheRequestedChannel()
    {
        var handler = Handler(HttpStatusCode.NotFound, """{"error":{"code":"ChannelNotFound","message":"private-response-detail"}}""");

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Adapter(handler, ReadAndUpdateTokens()).GetAsync(ChannelRequest("webchat"), default));

        Assert.Contains("no 'webchat' channel", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChannelSettingsRefused_NamesTheChannelAndPointsToTheCredentials()
    {
        var handler = Handler(HttpStatusCode.BadRequest, """{"error":{"code":"ChannelSettingsRefused","message":"private-response-detail"}}""");

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Adapter(handler, ReadAndUpdateTokens()).CreateAsync(new AmsChannelCreateTransportRequest { Deployment = DeploymentRequest(), ChannelType = "telegram", Properties = Properties("""{"accessToken":"fake-telegram-token"}""") }, default));

        Assert.Equal("channel-settings-refused", exception.ErrorKind);
        Assert.Equal(ExitCodes.Usage, exception.ExitCode);
        Assert.StartsWith("Azure Bot Service refused the credentials or settings of the 'telegram' channel, so AMS did not change it.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--parameters", exception.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-telegram-token", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://slack.com/oauth/v2/authorize?client_id=1&state=a", "https://slack.com/oauth/v2/authorize?client_id=1&state=a")]
    [InlineData("http://slack.com/oauth/v2/authorize?client_id=1", null)]
    [InlineData("https://slack.example.test/oauth/v2/authorize?client_id=1", null)]
    [InlineData("https://slack.com/oauth/v2/authorize?client_id=1#fragment", null)]
    [InlineData(null, null)]
    public async Task Writes_KeepOnlyAUsableSlackAuthorizationUrl(string? authorizationUrl, string? expected)
    {
        var body = authorizationUrl is null
            ? """{"id":"/bots/ams/channels/SlackChannel","channelType":"slack","properties":{"clientId":"slack-client"}}"""
            : $$$"""{"id":"/bots/ams/channels/SlackChannel","channelType":"slack","properties":{"clientId":"slack-client"},"authorizationUrl":"{{{authorizationUrl}}}"}""";
        var adapter = Adapter(Handler(HttpStatusCode.Created, body), ReadAndUpdateTokens());
        var updater = Adapter(Handler(HttpStatusCode.OK, body), ReadAndUpdateTokens());

        var created = await adapter.CreateAsync(new AmsChannelCreateTransportRequest { Deployment = DeploymentRequest(), ChannelType = "slack", Properties = Properties("""{"clientId":"slack-client"}""") }, default);
        var updated = await updater.UpdateAsync(new AmsChannelUpdateTransportRequest { Deployment = DeploymentRequest(), ChannelType = "slack", Properties = Properties("""{"isEnabled":true}""") }, default);

        Assert.Equal(expected, created.Channel.AuthorizationUrl?.AbsoluteUri);
        Assert.Equal(expected, updated.Channel.AuthorizationUrl?.AbsoluteUri);
        Assert.False(created.Channel.Parameters.ContainsKey("authorizationUrl"));
    }

    [Theory]
    [InlineData("""{"error":{"code":"InvalidChannelRequest","message":"The channel type 'slack' is not supported. Use one of: directline, msteams, webchat."}}""", "AMS refused the channel request: The channel type 'slack' is not supported. Use one of: directline, msteams, webchat.")]
    [InlineData("""{"error":{"code":"InvalidChannelRequest"}}""", "AMS refused the channel request: it does not satisfy the AMS channel catalog.")]
    public async Task InvalidChannelRequest_ShowsTheAmsValidationMessageAsAUsageError(string body, string message)
    {
        var handler = Handler(HttpStatusCode.BadRequest, body);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, ReadAndUpdateTokens()), "create"));

        Assert.Equal("invalid-channel-request", exception.ErrorKind);
        Assert.Equal(ExitCodes.Usage, exception.ExitCode);
        Assert.StartsWith(message, exception.Message, StringComparison.Ordinal);
        Assert.Contains("msagent deployment channel types", exception.Remediation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"error":{"code":"InvalidChannelRequest","message":"The 'directline' channel has no item in 'sites' with siteName 'Default Site'."}}""", "The 'directline' channel has no item in 'sites' with siteName 'Default Site'.")]
    [InlineData("""{"error":{"code":"InvalidChannelRequest","message":"The 'directline' channel has no enabled site that accepts Direct Line 3.0 clients, so it cannot issue a token."}}""", "The 'directline' channel has no enabled site that accepts Direct Line 3.0 clients, so it cannot issue a token.")]
    [InlineData("""{"error":{"code":"InvalidChannelRequest","message":"The site 'Portal' of the 'directline' channel cannot issue tokens, because it is disabled or does not accept Direct Line 3.0 clients."}}""", "The site 'Portal' of the 'directline' channel cannot issue tokens, because it is disabled or does not accept Direct Line 3.0 clients.")]
    [InlineData("""{"error":{"code":"InvalidChannelRequest","message":"The 'directline' channel has several sites that can issue tokens: 'Default Site', 'Portal'. Name one in 'siteName'."}}""", "The 'directline' channel has several sites that can issue tokens: 'Default Site', 'Portal'. Name one in 'siteName'.")]
    [InlineData("""{"error":{"code":"invalidchannelrequest","message":"The site '\u001b[31mPortal' cannot issue tokens."}}""", "The site '\uFFFD[31mPortal' cannot issue tokens.")]
    [InlineData("""{"error":{"code":"InvalidChannelRequest"}}""", "no site of the deployment's Direct Line channel can issue one.")]
    public async Task TokenSiteRefusal_KeepsTheAmsReasonAndSaysHowToGetAUsableSite(string body, string reason)
    {
        var response = FakeHttpResponse.Json(HttpStatusCode.BadRequest, body);
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-token-id");
        var handler = new CapturingPackageHandler(_ => response);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, ReadAndUpdateTokens()), "token"));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("invalid-channel-request", exception.ErrorKind);
        Assert.Equal(ExitCodes.Usage, exception.ExitCode);
        Assert.StartsWith($"AMS could not issue a Direct Line token: {reason}", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("serviceRequestId=service-token-id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("a new bot can list no site until its Direct Line channel is first updated", exception.Remediation, StringComparison.Ordinal);
        Assert.Contains("`msagent deployment channel show --channel directline`", exception.Remediation, StringComparison.Ordinal);
        Assert.Contains("pass a usable one with --site", exception.Remediation, StringComparison.Ordinal);
        Assert.Contains("`msagent deployment channel update --channel directline --add-site siteName=<name> isV3Enabled=true`", exception.Remediation, StringComparison.Ordinal);
        Assert.Contains("`msagent deployment channel update --channel directline --set-site siteName=<name> isEnabled=true isV3Enabled=true`", exception.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("msagent deployment channel types", exception.Remediation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("create", HttpStatusCode.GatewayTimeout, "AgentManagementServiceDataPlaneCallFailed")]
    [InlineData("get", HttpStatusCode.Conflict, "ChannelNotFound")]
    [InlineData("update", HttpStatusCode.BadRequest, "SomethingElse")]
    [InlineData("list", HttpStatusCode.NotImplemented, "NotImplemented")]
    [InlineData("token", HttpStatusCode.BadGateway, "SomethingElse")]
    [InlineData("delete", HttpStatusCode.NotFound, "EnvironmentNotFound")]
    public async Task OtherRefusals_KeepTheGenericMapping(string operation, HttpStatusCode status, string code)
    {
        var handler = Handler(status, $$$"""{"error":{"code":"{{{code}}}","message":"private-response-detail"}}""");

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, ReadAndUpdateTokens()), operation));

        var sent = Assert.Single(handler.Requests);
        Assert.DoesNotContain(exception.ErrorKind, KnownRefusalKinds);
        Assert.Contains(sent.Method.Method, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> MalformedSuccessResponses => new()
    {
        { "types", "{}" },
        { "types", """{"value":{}}""" },
        { "types", """{"value":[{"channelType":"directline","displayName":"Direct Line","parameters":[]}]}""" },
        { "types", """{"value":[{"channelType":"directline","displayName":"Direct Line","operations":["create"],"parameters":[{"name":"sites"}]}]}""" },
        { "types", """{"value":[{"channelType":"directline","displayName":"Direct Line","operations":[""],"parameters":[]}]}""" },
        { "list", """{"value":[{"id":"x","channelType":"msteams"}]}""" },
        { "get", "not json" },
        { "get", """{"channelType":"msteams","properties":{}}""" },
        { "get", """{"id":"x","channelType":"msteams","properties":[]}""" },
        { "get", """{"id":"x","channelType":"msteams","properties":{"a":1,"a":2}}""" },
        { "create", """{"id":"x","channelType":"msteams","properties":{},"warnings":"one"}""" },
        { "update", """{"id":"x","properties":{}}""" },
        { "token", """{"token":"secret-token-value","conversationId":"c","expiresInSeconds":3600,"siteName":"s"}""" },
        { "token", """{"token":"secret-token-value","conversationId":"c","expiresInSeconds":0,"siteName":"s","endpoint":"e"}""" },
        { "token", """{"token":"secret-token-value","conversationId":"c","expiresInSeconds":"3600","siteName":"s","endpoint":"e"}""" },
        { "token", """{"token":"secret-token-value","conversationId":"c","expiresInSeconds":9999999999,"siteName":"s","endpoint":"e"}""" },
        { "webhook", """{"verifyToken":"secret-token-value"}""" },
        { "webhook", """{"callbackUrl":"https://facebook.botframework.com/api/v1/bots/ams-bot"}""" },
        { "webhook", """{"callbackUrl":5,"verifyToken":"secret-token-value"}""" },
        { "webhook", """{"callbackUrl":"https://facebook.botframework.com/api/v1/bots/ams-bot","verifyToken":["secret-token-value"]}""" },
    };

    [Theory]
    [MemberData(nameof(MalformedSuccessResponses))]
    public async Task MalformedSuccess_IsRejectedWithoutEchoingTheBody(string operation, string body)
    {
        var handler = Handler(HttpStatusCode.OK, body);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, ReadAndUpdateTokens()), operation));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("invalid-ams-response", exception.ErrorKind);
        Assert.Equal(ExitCodes.Failure, exception.ExitCode);
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token-value", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("create", true)]
    [InlineData("update", true)]
    [InlineData("delete", true)]
    [InlineData("get", false)]
    [InlineData("token", false)]
    [InlineData("webhook", false)]
    public async Task TransportFailure_IsIndeterminateOnlyForChannelWrites(string operation, bool indeterminate)
    {
        var handler = new CapturingPackageHandler(_ => throw new HttpRequestException("private-transport-detail"));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, ReadAndUpdateTokens()), operation));

        Assert.Single(handler.Requests);
        Assert.Equal("deploy-transport-failed", exception.ErrorKind);
        Assert.Equal(indeterminate, exception.Message.Contains("outcome may be indeterminate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("private-transport-detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirect_IsRejectedWithoutASecondCredentialBearingRequest()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Redirect(HttpStatusCode.Redirect, "https://elsewhere.example/channels"));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, ReadAndUpdateTokens()), "token"));

        Assert.Single(handler.Requests);
        Assert.Equal("redirect-rejected", exception.ErrorKind);
        Assert.Contains("api.powerplatform.com", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryOperation_RequiresARequest()
    {
        var adapter = Adapter(Handler(HttpStatusCode.OK, "{}"), ReadAndUpdateTokens());

        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.ListTypesAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.ListAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.GetAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.CreateAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.UpdateAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.DeleteAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.CreateDirectLineTokenAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.GetWebhookAsync(null!, default));
    }

    private static Task Invoke(AmsChannelAdapter adapter, string operation) => operation switch
    {
        "types" => adapter.ListTypesAsync(DeploymentRequest(), default),
        "list" => adapter.ListAsync(DeploymentRequest(), default),
        "get" => adapter.GetAsync(ChannelRequest("msteams"), default),
        "create" => adapter.CreateAsync(new AmsChannelCreateTransportRequest { Deployment = DeploymentRequest(), ChannelType = "msteams", Properties = Properties("""{"acceptedTerms":true}""") }, default),
        "update" => adapter.UpdateAsync(new AmsChannelUpdateTransportRequest { Deployment = DeploymentRequest(), ChannelType = "msteams", Properties = Properties("""{"acceptedTerms":true}""") }, default),
        "delete" => adapter.DeleteAsync(ChannelRequest("msteams"), default),
        "token" => adapter.CreateDirectLineTokenAsync(new AmsDirectLineTokenTransportRequest { Deployment = DeploymentRequest() }, default),
        "webhook" => adapter.GetWebhookAsync(ChannelRequest("facebook"), default),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static void AssertBody(string expected, CapturedPackageRequest sent) => Assert.True(JsonElement.DeepEquals(Json(expected), Json(Encoding.UTF8.GetString(sent.Body))), Encoding.UTF8.GetString(sent.Body));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Dictionary<string, JsonElement> Properties(string json) => Json(json).EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());

    private static CapturingPackageHandler Handler(HttpStatusCode status, string body) => new(_ => FakeHttpResponse.Json(status, body));

    private static AmsChannelAdapter Adapter(CapturingPackageHandler handler, FakePpapiTokenProvider tokens) => new(new HttpClient(handler), tokens);

    private static FakePpapiTokenProvider ReadAndUpdateTokens() => new() { Scopes = $"{PowerPlatformApiPermissions.AppsRead} {PowerPlatformApiPermissions.AppsUpdate}" };

    private static AmsDeploymentReadTransportRequest DeploymentRequest() => new()
    {
        ServiceTarget = new AmsServiceTarget { Endpoint = new Uri("https://api.powerplatform.com"), Audience = Audience, RouteForm = AmsRouteForm.TenantHost, TenantId = Tenant, EnvironmentId = Home },
        AgentId = Agent,
        DeploymentId = Deployment,
    };

    private static AmsChannelTransportRequest ChannelRequest(string channelType) => new() { Deployment = DeploymentRequest(), ChannelType = channelType };
}
