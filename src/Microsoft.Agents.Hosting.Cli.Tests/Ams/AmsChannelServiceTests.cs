using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;
using Microsoft.Extensions.DependencyInjection;
using static Microsoft.Agents.Hosting.Cli.Tests.Ams.AmsBotServiceFixtures;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsChannelServiceTests
{
    [Fact]
    public async Task Create_SendsTheTypedTeamsPropertiesWithTheCreateDefaultsThroughTheHomeControlPlane()
    {
        var channels = new RecordingChannelTransport();

        var result = await Service(channels).CreateChannelAsync(Request(new DeploymentChannelCreateRequest { ChannelName = " MsTeams ", Parameters = ["acceptedTerms=true"] }), default);

        var sent = Assert.IsType<AmsChannelCreateTransportRequest>(Assert.Single(channels.Requests));
        AssertDeployment(sent.Deployment);
        Assert.Equal("msteams", sent.ChannelType);
        Assert.Equal(["acceptedTerms", "deploymentEnvironment", "enableCalling"], sent.Properties.Keys.Order(StringComparer.Ordinal));
        Assert.True(sent.Properties["acceptedTerms"].GetBoolean());
        Assert.Equal("CommercialDeployment", sent.Properties["deploymentEnvironment"].GetString());
        Assert.False(sent.Properties["enableCalling"].GetBoolean());
        Assert.Equal(DeploymentOperationStatus.Created, result.Status);
        Assert.Equal("Channel", result.TargetName);
        Assert.Equal("msteams", result.TargetInstanceName);
        Assert.Same(channels.Channel, result.Payload);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Create_SendsTheCompleteSiteListInsideThePropertiesAndReportsAnExistingChannelWithItsWarnings()
    {
        var channels = new RecordingChannelTransport { WriteResult = new AmsChannelWriteResult { Channel = Channel("directline"), IsCreated = false, Warnings = ["site settings were not applied"] } };

        var result = await Service(channels).CreateChannelAsync(Request(new DeploymentChannelCreateRequest { ChannelName = "directline", Parameters = ["""sites=[{"siteName":"web","isEnabled":false}]"""] }), default);

        var sent = Assert.IsType<AmsChannelCreateTransportRequest>(Assert.Single(channels.Requests));
        Assert.Equal("directline", sent.ChannelType);
        AssertJson("""{"sites":[{"siteName":"web","isEnabled":false}]}""", sent.Properties);
        Assert.Equal(DeploymentOperationStatus.Existed, result.Status);
        Assert.Equal(["site settings were not applied"], result.Warnings);
    }

    public static TheoryData<DeploymentChannelCreateRequest, string> ListCreates => new()
    {
        { new DeploymentChannelCreateRequest { ChannelName = "facebook", Parameters = ["appId=app-1", "appSecret=fake-app-secret", """pages=[{"ID":" page-1 ","accessToken":"fake-page-token"}]"""] }, """{"appId":"app-1","appSecret":"fake-app-secret","pages":[{"id":"page-1","accessToken":"fake-page-token"}]}""" },
        { new DeploymentChannelCreateRequest { ChannelName = "line", Parameters = ["""lineRegistrations=[{"channelSecret":"fake-line-secret","channelAccessToken":"fake-line-token"}]"""] }, """{"lineRegistrations":[{"channelSecret":"fake-line-secret","channelAccessToken":"fake-line-token"}]}""" },
    };

    [Theory]
    [MemberData(nameof(ListCreates))]
    public async Task Create_SendsFacebookPagesAndLineRegistrationsInsideTheProperties(DeploymentChannelCreateRequest content, string properties)
    {
        var channels = new RecordingChannelTransport();

        await Service(channels).CreateChannelAsync(Request(content), default);

        var sent = Assert.IsType<AmsChannelCreateTransportRequest>(Assert.Single(channels.Requests));
        Assert.Equal(content.ChannelName, sent.ChannelType);
        AssertJson(properties, sent.Properties);
    }

    [Fact]
    public async Task Update_SendsOnlyTheChangedTeamsProperties()
    {
        var channels = new RecordingChannelTransport { WriteResult = new AmsChannelWriteResult { Channel = Channel("msteams"), Warnings = ["warning"] } };

        var result = await Service(channels).UpdateChannelAsync(Request(new DeploymentChannelUpdateRequest { ChannelName = "msteams", Parameters = ["enableCalling=true"] }), default);

        var sent = Assert.IsType<AmsChannelUpdateTransportRequest>(Assert.Single(channels.Requests));
        AssertDeployment(sent.Deployment);
        Assert.Equal("msteams", sent.ChannelType);
        Assert.Null(sent.ItemChange);
        Assert.True(Assert.Single(sent.Properties!, property => property.Key == "enableCalling").Value.GetBoolean());
        Assert.Single(sent.Properties!);
        Assert.Equal(DeploymentOperationStatus.Updated, result.Status);
        Assert.Equal(["warning"], result.Warnings);
    }

    public static TheoryData<DeploymentChannelUpdateRequest, string, string> ItemOperations => new()
    {
        { new DeploymentChannelUpdateRequest { ChannelName = "directline", AddSite = ["siteName=web", "isEnabled=true"] }, "add", """{"siteName":"web","isEnabled":true}""" },
        { new DeploymentChannelUpdateRequest { ChannelName = "webchat", SetSite = ["siteName=web", "appId="] }, "set", """{"siteName":"web","appId":null}""" },
        { new DeploymentChannelUpdateRequest { ChannelName = "directline", RemoveSite = ["siteName=web"] }, "remove", """{"siteName":"web"}""" },
        { new DeploymentChannelUpdateRequest { ChannelName = "facebook", AddPage = ["id=page-2", "accessToken=fake-page-token"] }, "add", """{"id":"page-2","accessToken":"fake-page-token"}""" },
        { new DeploymentChannelUpdateRequest { ChannelName = "facebook", SetPage = ["id=page-2", "accessToken=fake-page-token"] }, "set", """{"id":"page-2","accessToken":"fake-page-token"}""" },
        { new DeploymentChannelUpdateRequest { ChannelName = "facebook", RemovePage = ["id=page-2"] }, "remove", """{"id":"page-2"}""" },
        { new DeploymentChannelUpdateRequest { ChannelName = "line", AddRegistration = ["channelSecret=fake-line-secret", "channelAccessToken=fake-line-token"] }, "add", """{"channelSecret":"fake-line-secret","channelAccessToken":"fake-line-token"}""" },
        { new DeploymentChannelUpdateRequest { ChannelName = "line", SetRegistration = ["generatedId=registration-1", "channelSecret=fake-line-secret"] }, "set", """{"generatedId":"registration-1","channelSecret":"fake-line-secret"}""" },
        { new DeploymentChannelUpdateRequest { ChannelName = "line", RemoveRegistration = ["generatedId=registration-1"] }, "remove", """{"generatedId":"registration-1"}""" },
    };

    [Theory]
    [MemberData(nameof(ItemOperations))]
    public async Task Update_SendsOneItemChangeForAmsToApplyToTheLiveList(DeploymentChannelUpdateRequest content, string operation, string item)
    {
        var channels = new RecordingChannelTransport();

        await Service(channels).UpdateChannelAsync(Request(content), default);

        var sent = Assert.IsType<AmsChannelUpdateTransportRequest>(Assert.Single(channels.Requests));
        Assert.Equal(content.ChannelName, sent.ChannelType);
        Assert.Null(sent.Properties);
        Assert.Equal(operation, sent.ItemChange!.Operation);
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(item).RootElement, Assert.Single(sent.ItemChange.Items)));
    }

    public static TheoryData<DeploymentChannelUpdateRequest, string> PropertyUpdates => new()
    {
        { new DeploymentChannelUpdateRequest { ChannelName = "directline", Parameters = ["""sites=[{"siteName":"web"},{"siteName":"new","isEnabled":false}]"""] }, """{"sites":[{"siteName":"web"},{"siteName":"new","isEnabled":false}]}""" },
        { new DeploymentChannelUpdateRequest { ChannelName = "directline", Parameters = ["sites=[]"] }, """{"sites":[]}""" },
        { new DeploymentChannelUpdateRequest { ChannelName = "webchat", Parameters = ["sites="] }, """{"sites":[]}""" },
        { new DeploymentChannelUpdateRequest { ChannelName = "facebook", Parameters = ["isEnabled=false"] }, """{"isEnabled":false}""" },
        { new DeploymentChannelUpdateRequest { ChannelName = "facebook", Parameters = ["appId=app-2", """pages=[{"id":"page-1"},{"id":"page-2","accessToken":"fake-page-token"}]"""] }, """{"appId":"app-2","pages":[{"id":"page-1"},{"id":"page-2","accessToken":"fake-page-token"}]}""" },
        { new DeploymentChannelUpdateRequest { ChannelName = "line", Parameters = ["""lineRegistrations=[{"generatedId":"registration-1"},{"channelSecret":"fake-line-secret","channelAccessToken":"fake-line-token"}]"""] }, """{"lineRegistrations":[{"generatedId":"registration-1"},{"channelSecret":"fake-line-secret","channelAccessToken":"fake-line-token"}]}""" },
    };

    [Theory]
    [MemberData(nameof(PropertyUpdates))]
    public async Task Update_SendsScalarsAndWholeListsAsProperties(DeploymentChannelUpdateRequest content, string properties)
    {
        var channels = new RecordingChannelTransport();

        await Service(channels).UpdateChannelAsync(Request(content), default);

        var sent = Assert.IsType<AmsChannelUpdateTransportRequest>(Assert.Single(channels.Requests));
        Assert.Equal(content.ChannelName, sent.ChannelType);
        Assert.Null(sent.ItemChange);
        AssertJson(properties, sent.Properties!);
    }

    [Theory]
    [InlineData("facebook")]
    [InlineData("line")]
    public async Task Update_RefusesAnItemOperationWithParametersBeforeAnyCall(string channel)
    {
        var channels = new RecordingChannelTransport();
        var content = channel == "facebook"
            ? new DeploymentChannelUpdateRequest { ChannelName = channel, Parameters = ["isEnabled=true"], RemovePage = ["id=one"] }
            : new DeploymentChannelUpdateRequest { ChannelName = channel, AddRegistration = ["channelSecret=fake-line-secret", "channelAccessToken=fake-line-token"], RemoveRegistration = ["generatedId=one"] };

        var exception = await Assert.ThrowsAsync<UsageException>(() => Service(channels).UpdateChannelAsync(Request(content), default));

        Assert.StartsWith("Specify only one of --parameters", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-line-secret", exception.Message, StringComparison.Ordinal);
        Assert.Empty(channels.Requests);
    }

    [Fact]
    public async Task ContentRules_MatchLocalModeAndRefuseBeforeAnyCall()
    {
        var channels = new RecordingChannelTransport();
        var content = new DeploymentChannelCreateRequest { ChannelName = "msteams", DeploymentId = Deployment, Parameters = ["notAParameter=1"] };

        var local = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(content));
        var ams = await Assert.ThrowsAsync<UsageException>(() => Service(channels).CreateChannelAsync(Request(content), default));

        Assert.Equal(local.Message, ams.Message);
        Assert.Empty(channels.Requests);
    }

    [Fact]
    public async Task ContentRules_IgnoreTheLocalDeploymentAddressThatTheCommandAlsoBinds()
    {
        var channels = new RecordingChannelTransport();
        var content = new DeploymentChannelShowRequest { ChannelName = "webchat", AgentId = Agent, DeploymentId = Deployment };

        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateShow(content));
        var result = await Service(channels).GetChannelAsync(Request(content), default);

        var sent = Assert.IsType<AmsChannelTransportRequest>(Assert.Single(channels.Requests));
        AssertDeployment(sent.Deployment);
        Assert.Equal("webchat", sent.ChannelType);
        Assert.Equal(DeploymentOperationStatus.Retrieved, result.Status);
        Assert.Same(channels.Channel, result.Payload);
    }

    [Fact]
    public async Task Delete_DeletesTheNamedChannelAndRefusesAChannelThatCannotBeDeleted()
    {
        var channels = new RecordingChannelTransport();

        var result = await Service(channels).DeleteChannelAsync(Request(new DeploymentChannelDeleteRequest { ChannelName = "MsTeams" }), default);
        var refused = await Assert.ThrowsAsync<DeploymentException>(() => Service(channels).DeleteChannelAsync(Request(new DeploymentChannelDeleteRequest { ChannelName = "directline" }), default));

        var sent = Assert.IsType<AmsChannelTransportRequest>(Assert.Single(channels.Requests));
        Assert.Equal("msteams", sent.ChannelType);
        Assert.Equal(DeploymentOperationStatus.Deleted, result.Status);
        Assert.Null(result.Payload);
        Assert.Equal("msteams", result.TargetInstanceName);
        Assert.Equal("channel-operation-not-supported", refused.ErrorKind);
    }

    [Fact]
    public async Task ListAndTypes_ReturnWhatAmsAnswered()
    {
        var channels = new RecordingChannelTransport();
        var service = Service(channels);

        var list = await service.ListChannelsAsync(Request(new DeploymentChannelListRequest()), default);
        var types = await service.ListChannelTypesAsync(new AgentDeploymentAmsChannelTypesRequest { AgentId = Agent, DeploymentId = Deployment, EnvironmentId = HomeEnvironment }, default);

        Assert.All(channels.Requests, request => AssertDeployment(Assert.IsType<AmsDeploymentReadTransportRequest>(request)));
        Assert.Equal(2, channels.Requests.Count);
        Assert.Equal(DeploymentOperationStatus.Listed, list.Status);
        Assert.Equal("Channels", list.TargetName);
        Assert.Equal([channels.Channel], list.Payload!);
        Assert.Equal(DeploymentOperationStatus.Listed, types.Status);
        Assert.Equal("ChannelTypes", types.TargetName);
        Assert.Equal(channels.Types, types.Payload);
    }

    [Fact]
    public async Task Token_SendsTheTrimmedSiteAndUserAndReturnsTheToken()
    {
        var channels = new RecordingChannelTransport();

        var result = await Service(channels).CreateTokenAsync(new AgentDeploymentAmsChannelTokenRequest { AgentId = Agent, DeploymentId = Deployment, EnvironmentId = HomeEnvironment, ChannelName = "DirectLine", SiteName = " Default Site ", UserId = " dl_user " }, default);

        var sent = Assert.IsType<AmsDirectLineTokenTransportRequest>(Assert.Single(channels.Requests));
        AssertDeployment(sent.Deployment);
        Assert.Equal("Default Site", sent.SiteName);
        Assert.Equal("dl_user", sent.UserId);
        Assert.Equal(DeploymentOperationStatus.Created, result.Status);
        Assert.Equal("Token", result.TargetName);
        Assert.Equal("Default Site", result.TargetInstanceName);
        Assert.Same(channels.Token, result.Payload);
    }

    [Fact]
    public async Task Token_WithoutSiteOrUser_LetsAmsChooseThem()
    {
        var channels = new RecordingChannelTransport();

        await Service(channels).CreateTokenAsync(new AgentDeploymentAmsChannelTokenRequest { AgentId = Agent, DeploymentId = Deployment, EnvironmentId = HomeEnvironment, ChannelName = "directline" }, default);

        var sent = Assert.IsType<AmsDirectLineTokenTransportRequest>(Assert.Single(channels.Requests));
        Assert.Null(sent.SiteName);
        Assert.Null(sent.UserId);
    }

    public static TheoryData<AgentDeploymentAmsChannelTokenRequest, string> InvalidTokenRequests => new()
    {
        { new AgentDeploymentAmsChannelTokenRequest { ChannelName = "webchat" }, "The 'webchat' channel does not support 'token' operation." },
        { new AgentDeploymentAmsChannelTokenRequest { ChannelName = "carrier-pigeon" }, "Channel 'carrier-pigeon' is not supported." },
        { new AgentDeploymentAmsChannelTokenRequest { ChannelName = null }, "--channel" },
        { new AgentDeploymentAmsChannelTokenRequest { ChannelName = "directline", SiteName = " " }, "--site cannot be empty." },
        { new AgentDeploymentAmsChannelTokenRequest { ChannelName = "directline", UserId = "" }, "--user-id cannot be empty." },
        { new AgentDeploymentAmsChannelTokenRequest { ChannelName = "directline", UserId = "user-1" }, "--user-id must start with dl_ and name a user, for example dl_tester." },
        { new AgentDeploymentAmsChannelTokenRequest { ChannelName = "directline", UserId = " dl_ " }, "--user-id must start with dl_ and name a user, for example dl_tester." },
        { new AgentDeploymentAmsChannelTokenRequest { ChannelName = "directline", UserId = "DL_user" }, "--user-id must start with dl_ and name a user, for example dl_tester." },
    };

    [Theory]
    [MemberData(nameof(InvalidTokenRequests))]
    public async Task Token_InvalidRequestsAreRefusedBeforeAnyCall(AgentDeploymentAmsChannelTokenRequest request, string message)
    {
        var channels = new RecordingChannelTransport();

        var exception = await Assert.ThrowsAnyAsync<AgentHostingException>(() => Service(channels).CreateTokenAsync(request with { AgentId = Agent, DeploymentId = Deployment, EnvironmentId = HomeEnvironment }, default));

        Assert.Equal(ExitCodes.Usage, exception.ExitCode);
        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
        Assert.Empty(channels.Requests);
    }

    [Fact]
    public async Task Webhook_ReadsTheWebhookValuesOfTheNamedChannel()
    {
        var channels = new RecordingChannelTransport();

        var result = await Service(channels).GetWebhookAsync(new AgentDeploymentAmsChannelWebhookRequest { AgentId = Agent, DeploymentId = Deployment, EnvironmentId = HomeEnvironment, ChannelName = " Facebook " }, default);

        var sent = Assert.IsType<AmsChannelTransportRequest>(Assert.Single(channels.Requests));
        AssertDeployment(sent.Deployment);
        Assert.Equal("facebook", sent.ChannelType);
        Assert.Equal(["webhook"], channels.Operations);
        Assert.Equal(DeploymentOperationStatus.Retrieved, result.Status);
        Assert.Equal("Webhook", result.TargetName);
        Assert.Equal("facebook", result.TargetInstanceName);
        Assert.Same(channels.Webhook, result.Payload);
    }

    public static TheoryData<string?, string> InvalidWebhookChannels => new()
    {
        { "line", "The 'line' channel does not support 'webhook' operation." },
        { "directline", "The 'directline' channel does not support 'webhook' operation." },
        { "carrier-pigeon", "Channel 'carrier-pigeon' is not supported." },
        { null, "--channel" },
    };

    [Theory]
    [MemberData(nameof(InvalidWebhookChannels))]
    public async Task Webhook_InvalidChannelsAreRefusedBeforeAnyCall(string? channelName, string message)
    {
        var channels = new RecordingChannelTransport();

        var exception = await Assert.ThrowsAnyAsync<AgentHostingException>(() => Service(channels).GetWebhookAsync(new AgentDeploymentAmsChannelWebhookRequest { AgentId = Agent, DeploymentId = Deployment, EnvironmentId = HomeEnvironment, ChannelName = channelName }, default));

        Assert.Equal(ExitCodes.Usage, exception.ExitCode);
        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
        Assert.Empty(channels.Requests);
    }

    [Theory]
    [InlineData(null, Deployment, HomeEnvironment, "--agent-id or --agent-name is required for `msagent deployment channel show`.")]
    [InlineData(Agent, "not-a-guid", HomeEnvironment, "--deployment-id must be a non-empty GUID.")]
    [InlineData(Agent, "00000000-0000-0000-0000-000000000000", HomeEnvironment, "--deployment-id must be a non-empty GUID.")]
    [InlineData(Agent, Deployment, " ", "--environment-id requires a non-empty environment id.")]
    public async Task Address_InvalidIdsAreRefusedBeforeAnyCall(string? agentId, string deploymentId, string environmentId, string message)
    {
        var channels = new RecordingChannelTransport();

        var exception = await Assert.ThrowsAsync<UsageException>(() => Service(channels).GetChannelAsync(Request(new DeploymentChannelShowRequest { ChannelName = "webchat" }) with { AgentId = agentId, DeploymentId = deploymentId, EnvironmentId = environmentId }, default));

        Assert.Equal(message, exception.Message);
        Assert.Empty(channels.Requests);
    }

    [Fact]
    public async Task Address_UsesTheCanonicalIdsOfTheRequest()
    {
        var channels = new RecordingChannelTransport();

        await Service(channels).GetChannelAsync(Request(new DeploymentChannelShowRequest { ChannelName = "webchat" }) with { AgentId = $" {Agent.ToUpperInvariant()} ", DeploymentId = Deployment.ToUpperInvariant() }, default);

        AssertDeployment(Assert.IsType<AmsChannelTransportRequest>(Assert.Single(channels.Requests)).Deployment);
    }

    [Fact]
    public async Task Address_ByNames_ResolvesThroughTheListsAndRefusesARecordThatWasNeverDeployed()
    {
        var channels = new RecordingChannelTransport();
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()] };
        var deployed = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()] };
        var notDeployed = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(state: AmsDeploymentResult.NotDeployedState)] };
        var byNames = Request(new DeploymentChannelShowRequest { ChannelName = "webchat" }) with { AgentId = null, AgentName = "contoso agent", DeploymentId = null, DeploymentName = "DEV-PROOF" };

        await Service(channels, Resolver(agents, deployed)).GetChannelAsync(byNames, default);
        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Service(channels, Resolver(agents, notDeployed)).GetChannelAsync(byNames, default));

        AssertDeployment(Assert.IsType<AmsChannelTransportRequest>(Assert.Single(channels.Requests)).Deployment);
        Assert.Equal(["list"], deployed.Calls);
        Assert.Equal("deployment-not-deployed", exception.ErrorKind);
    }

    [Fact]
    public async Task SlackWrites_OpenTheAuthorizationUrlWithTheAmsGuidance()
    {
        var url = new Uri("https://slack.com/oauth/v2/authorize?client_id=slack-client&state=a");
        var browser = new RecordingBrowserLauncher();
        var channels = new RecordingChannelTransport { WriteResult = new AmsChannelWriteResult { Channel = Channel("slack") with { AuthorizationUrl = url }, IsCreated = true } };
        var service = new AmsChannelService(Resolver(), channels, browser);

        var created = await service.CreateChannelAsync(Request(new DeploymentChannelCreateRequest { ChannelName = "slack", Parameters = ["clientId=slack-client", "clientSecret=fake-slack-secret", "signingSecret=fake-slack-signing"] }), default);
        var updated = await service.UpdateChannelAsync(Request(new DeploymentChannelUpdateRequest { ChannelName = "slack", Parameters = ["isEnabled=true"] }), default);

        Assert.Equal(DeploymentOperationStatus.WriteAccepted, created.Status);
        Assert.Equal(DeploymentOperationStatus.WriteAccepted, updated.Status);
        Assert.Equal(url, created.AuthorizationUrl);
        Assert.Equal(url, updated.AuthorizationUrl);
        Assert.Equal([url, url], browser.Launched);
        Assert.Equal([DeploymentChannelSlack.AmsAuthorizationGuidance], created.Warnings);
        Assert.Equal([DeploymentChannelSlack.AmsAuthorizationGuidance], updated.Warnings);
        Assert.DoesNotContain("Azure portal", DeploymentChannelSlack.AmsAuthorizationGuidance, StringComparison.Ordinal);
        Assert.Contains("msagent deployment channel update --channel slack", DeploymentChannelSlack.AmsAuthorizationGuidance, StringComparison.Ordinal);
        var sent = Assert.IsType<AmsChannelCreateTransportRequest>(channels.Requests[0]);
        Assert.False(sent.Properties!.ContainsKey("registerBeforeOAuthFlow"));
        Assert.Equal("fake-slack-secret", sent.Properties["clientSecret"].GetString());
    }

    [Fact]
    public async Task SlackWrites_NonInteractiveExistingOrBrowserFailure_OnlyGuide()
    {
        var url = new Uri("https://slack.com/oauth/v2/authorize?client_id=slack-client&state=a");
        var quietBrowser = new RecordingBrowserLauncher();
        var failingBrowser = new RecordingBrowserLauncher { Throws = new InvalidOperationException("no browser") };
        var accepted = new RecordingChannelTransport { WriteResult = new AmsChannelWriteResult { Channel = Channel("slack") with { AuthorizationUrl = url }, IsCreated = true } };
        var existing = new RecordingChannelTransport { WriteResult = new AmsChannelWriteResult { Channel = Channel("slack"), IsCreated = false } };
        var request = Request(new DeploymentChannelCreateRequest { ChannelName = "slack", Parameters = ["clientId=slack-client", "clientSecret=fake-slack-secret", "verificationToken=fake-slack-verification"] });

        var nonInteractive = await new AmsChannelService(Resolver(), accepted, quietBrowser).CreateChannelAsync(request with { Channel = request.Channel with { NonInteractive = true } }, default);
        var existed = await new AmsChannelService(Resolver(), existing, quietBrowser).CreateChannelAsync(request, default);
        var failed = await new AmsChannelService(Resolver(), accepted, failingBrowser).CreateChannelAsync(request, default);

        Assert.Empty(quietBrowser.Launched);
        Assert.Equal(DeploymentOperationStatus.WriteAccepted, nonInteractive.Status);
        Assert.Equal(url, nonInteractive.AuthorizationUrl);
        Assert.Equal([DeploymentChannelSlack.AmsAuthorizationGuidance], nonInteractive.Warnings);
        Assert.Equal(DeploymentOperationStatus.Existed, existed.Status);
        Assert.Null(existed.AuthorizationUrl);
        Assert.Equal([DeploymentChannelSlack.AmsAuthorizationGuidance], existed.Warnings);
        Assert.Equal(["The browser could not be opened. " + DeploymentChannelSlack.AmsAuthorizationGuidance], failed.Warnings);
    }

    [Fact]
    public void Constructor_RequiresEveryDependency()
    {
        Assert.Throws<ArgumentNullException>(() => new AmsChannelService(null!, new RecordingChannelTransport()));
        Assert.Throws<ArgumentNullException>(() => new AmsChannelService(Resolver(), null!));
    }

    [Fact]
    public async Task Handlers_DispatchEveryRouteToItsOperation()
    {
        var channels = new RecordingChannelTransport();
        var service = Service(channels);

        await ((ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelCreateRequest>, DeploymentResult<DeploymentChannelRecord>>)service).HandleAsync(Request(new DeploymentChannelCreateRequest { ChannelName = "msteams" }), default);
        await ((ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelUpdateRequest>, DeploymentResult<DeploymentChannelRecord>>)service).HandleAsync(Request(new DeploymentChannelUpdateRequest { ChannelName = "msteams", Parameters = ["acceptedTerms=true"] }), default);
        await ((ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelDeleteRequest>, DeploymentResult<DeploymentChannelRecord>>)service).HandleAsync(Request(new DeploymentChannelDeleteRequest { ChannelName = "msteams" }), default);
        await ((ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelShowRequest>, DeploymentResult<DeploymentChannelRecord>>)service).HandleAsync(Request(new DeploymentChannelShowRequest { ChannelName = "msteams" }), default);
        await ((ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelListRequest>, DeploymentResult<DeploymentChannelRecord[]>>)service).HandleAsync(Request(new DeploymentChannelListRequest()), default);
        await ((ICommandHandler<AgentDeploymentAmsChannelTypesRequest, DeploymentResult<DeploymentChannelType[]>>)service).HandleAsync(new AgentDeploymentAmsChannelTypesRequest { AgentId = Agent, DeploymentId = Deployment, EnvironmentId = HomeEnvironment }, default);
        await ((ICommandHandler<AgentDeploymentAmsChannelTokenRequest, DeploymentResult<DeploymentChannelToken>>)service).HandleAsync(new AgentDeploymentAmsChannelTokenRequest { AgentId = Agent, DeploymentId = Deployment, EnvironmentId = HomeEnvironment, ChannelName = "directline" }, default);
        await ((ICommandHandler<AgentDeploymentAmsChannelWebhookRequest, DeploymentResult<DeploymentChannelWebhook>>)service).HandleAsync(new AgentDeploymentAmsChannelWebhookRequest { AgentId = Agent, DeploymentId = Deployment, EnvironmentId = HomeEnvironment, ChannelName = "facebook" }, default);

        Assert.Equal(
            [typeof(AmsChannelCreateTransportRequest), typeof(AmsChannelUpdateTransportRequest), typeof(AmsChannelTransportRequest), typeof(AmsChannelTransportRequest), typeof(AmsDeploymentReadTransportRequest), typeof(AmsDeploymentReadTransportRequest), typeof(AmsDirectLineTokenTransportRequest), typeof(AmsChannelTransportRequest)],
            channels.Requests.Select(request => request.GetType()));
        Assert.Equal(["create", "update", "delete", "get", "list", "types", "token", "webhook"], channels.Operations);
    }

    [Fact]
    public void Composition_ServesEveryAmsChannelRouteFromTheOneChannelService()
    {
        var services = new ServiceCollection();
        services.AddAgentHostAuthentication(new AgentHostAuthOptions { ClientId = "c47b0a43-21ff-45f7-9280-6561e31342a5", ConfigDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) });
        services.AddSingleton(new AmsEndpointProfile());
        services.AddSingleton<IEnvironmentClient>(new FakeEnvironmentClient());
        services.AddSingleton<IClock>(new FixedClock(EnvironmentFixtures.SelectedAt));
        services.AddSingleton<IAmsChannelTransport>(new RecordingChannelTransport());
        services.AddSingleton<IAmsAgentReadTransport>(new PendingAmsAgentReadTransport());
        services.AddSingleton<IAmsDeploymentTransport>(new PendingAmsDeploymentTransport());
        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<AmsChannelService>();
        Type[] routes =
        [
            typeof(ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelCreateRequest>, DeploymentResult<DeploymentChannelRecord>>),
            typeof(ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelUpdateRequest>, DeploymentResult<DeploymentChannelRecord>>),
            typeof(ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelDeleteRequest>, DeploymentResult<DeploymentChannelRecord>>),
            typeof(ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelShowRequest>, DeploymentResult<DeploymentChannelRecord>>),
            typeof(ICommandHandler<AgentDeploymentAmsChannelRequest<DeploymentChannelListRequest>, DeploymentResult<DeploymentChannelRecord[]>>),
            typeof(ICommandHandler<AgentDeploymentAmsChannelTypesRequest, DeploymentResult<DeploymentChannelType[]>>),
            typeof(ICommandHandler<AgentDeploymentAmsChannelTokenRequest, DeploymentResult<DeploymentChannelToken>>),
            typeof(ICommandHandler<AgentDeploymentAmsChannelWebhookRequest, DeploymentResult<DeploymentChannelWebhook>>),
        ];

        Assert.All(routes, route => Assert.Same(service, provider.GetRequiredService(route)));
    }

    [Fact]
    public async Task Composition_InjectsTheRegisteredBrowserIntoTheChannelService()
    {
        var url = new Uri("https://slack.com/oauth/v2/authorize?client_id=slack-client&state=a");
        var browser = new RecordingBrowserLauncher();
        var services = new ServiceCollection();
        services.AddAgentHostAuthentication(new AgentHostAuthOptions { ClientId = "c47b0a43-21ff-45f7-9280-6561e31342a5", ConfigDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) });
        services.AddSingleton(new AmsEndpointProfile());
        services.AddSingleton<IEnvironmentClient>(new FakeEnvironmentClient());
        services.AddSingleton<IClock>(new FixedClock(EnvironmentFixtures.SelectedAt));
        services.AddSingleton<IAmsChannelTransport>(new RecordingChannelTransport { WriteResult = new AmsChannelWriteResult { Channel = Channel("slack") with { AuthorizationUrl = url }, IsCreated = true } });
        services.AddSingleton<IAmsAgentReadTransport>(new PendingAmsAgentReadTransport());
        services.AddSingleton<IAmsDeploymentTransport>(new PendingAmsDeploymentTransport());
        services.AddSingleton<IBrowserLauncher>(browser);
        services.AddSingleton(Resolver());
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<AmsChannelService>().CreateChannelAsync(Request(new DeploymentChannelCreateRequest { ChannelName = "slack", Parameters = ["clientId=slack-client", "clientSecret=fake-slack-secret", "signingSecret=fake-slack-signing"] }), default);

        Assert.Equal([url], browser.Launched);
    }

    private static AgentDeploymentAmsChannelRequest<TChannelRequest> Request<TChannelRequest>(TChannelRequest content)
        where TChannelRequest : DeploymentRequest => new() { AgentId = Agent, DeploymentId = Deployment, EnvironmentId = HomeEnvironment, Channel = content };

    private static DeploymentChannelRecord Channel(string channelType) => new() { ResourceId = $"/bots/ams/channels/{channelType}", ChannelName = channelType };

    private static AmsChannelService Service(IAmsChannelTransport channels, AmsAddressResolver? addresses = null) => new(addresses ?? Resolver(), channels);

    private static void AssertJson(string expected, IReadOnlyDictionary<string, JsonElement> actual) => Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(expected).RootElement, JsonSerializer.SerializeToElement(actual)), JsonSerializer.Serialize(actual));

    private sealed class RecordingChannelTransport : IAmsChannelTransport
    {
        public List<object> Requests { get; } = [];
        public List<string> Operations { get; } = [];
        public DeploymentChannelRecord Channel { get; } = AmsChannelServiceTests.Channel("msteams");
        public DeploymentChannelType[] Types { get; } = [new() { ChannelType = "msteams", DisplayName = "Microsoft Teams", Operations = ["create"], Parameters = [] }];
        public DeploymentChannelToken Token { get; } = new() { Token = "token", ConversationId = "conversation", ExpiresInSeconds = 3600, SiteName = "Default Site", Endpoint = "https://directline.botframework.com" };
        public DeploymentChannelWebhook Webhook { get; } = new() { CallbackUrl = "https://facebook.botframework.com/api/v1/bots/ams-bot", VerifyToken = "fake-verify-token" };
        public AmsChannelWriteResult? WriteResult { get; init; }

        public Task<IReadOnlyList<DeploymentChannelType>> ListTypesAsync(AmsDeploymentReadTransportRequest request, CancellationToken cancellationToken) => Record<IReadOnlyList<DeploymentChannelType>>("types", request, Types);

        public Task<IReadOnlyList<DeploymentChannelRecord>> ListAsync(AmsDeploymentReadTransportRequest request, CancellationToken cancellationToken) => Record<IReadOnlyList<DeploymentChannelRecord>>("list", request, [Channel]);

        public Task<DeploymentChannelRecord> GetAsync(AmsChannelTransportRequest request, CancellationToken cancellationToken) => Record("get", request, Channel);

        public Task<AmsChannelWriteResult> CreateAsync(AmsChannelCreateTransportRequest request, CancellationToken cancellationToken) => Record("create", request, WriteResult ?? new AmsChannelWriteResult { Channel = Channel, IsCreated = true });

        public Task<AmsChannelWriteResult> UpdateAsync(AmsChannelUpdateTransportRequest request, CancellationToken cancellationToken) => Record("update", request, WriteResult ?? new AmsChannelWriteResult { Channel = Channel });

        public Task DeleteAsync(AmsChannelTransportRequest request, CancellationToken cancellationToken) => Record("delete", request, true);

        public Task<DeploymentChannelToken> CreateDirectLineTokenAsync(AmsDirectLineTokenTransportRequest request, CancellationToken cancellationToken) => Record("token", request, Token);

        public Task<DeploymentChannelWebhook> GetWebhookAsync(AmsChannelTransportRequest request, CancellationToken cancellationToken) => Record("webhook", request, Webhook);

        private Task<T> Record<T>(string operation, object request, T result)
        {
            Operations.Add(operation);
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}
