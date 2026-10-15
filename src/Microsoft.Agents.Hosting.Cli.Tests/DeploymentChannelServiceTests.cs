using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Offline unit tests for <see cref="DeploymentChannelService"/> — the generic
/// <c>ah deployment channel {create|update|delete|show|list}</c> surface that mirrors the equivalent
/// <c>az bot &lt;channel&gt;</c> ARM call. They drive the service through in-memory fakes (no network, no
/// MSAL, no <c>az</c>): a store seeded with records and a recording channel port. They assert the ARM
/// channel call is addressed at the backing bot with the right ARM channel discriminator and inner
/// <c>properties</c>, the GET-merge-PUT update, and the clean failures for unknown ids, deployments with no
/// backing bot, and channels that are not configured.
/// </summary>
public sealed class DeploymentChannelServiceTests
{
    private const string DepId = "11111111-1111-1111-1111-111111111111";
    private const string AgentId = "22222222-2222-2222-2222-222222222222";

    private static CodeAgentDeployment ProvisionedRecord() => new()
    {
        DeploymentId = DepId,
        AgentId = AgentId,
        DeploymentName = "dev",
        DeploymentType = DeploymentType.Dev,
        Bot = new DeploymentBotResource
        {
            Name = "dev",
            EntraId = "app-1",
            EntraIdType = "AppReg",
            TenantId = "tenant-1",
            ResourceId = "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev",
            Sku = "F0",
            Location = "global",
            DisplayName = "dev",
        },
    };

    private static DeploymentChannelCreateRequest CreateRequest(string channel, params string[] parameters) =>
        new() { DeploymentId = DepId, ChannelName = channel, Parameters = parameters };

    private static DeploymentChannelUpdateRequest UpdateRequest(string channel, params string[] parameters) =>
        new() { DeploymentId = DepId, ChannelName = channel, Parameters = parameters };

    private static DeploymentChannelShowRequest ShowRequest(string channel) =>
        new() { DeploymentId = DepId, ChannelName = channel };

    private static DeploymentChannelDeleteRequest DeleteRequest(string channel) =>
        new() { DeploymentId = DepId, ChannelName = channel };

    [Fact]
    public async Task CreateMsTeams_UpsertsChannelOnBackingBot()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort();
        var service = new DeploymentChannelService(store, channels);

        var result = await service.CreateChannelAsync(CreateRequest("msteams"), default);

        var upsert = Assert.Single(channels.Upserts);
        Assert.Equal(ProvisionedRecord().Bot.ResourceId, upsert.BotResourceId);
        Assert.Equal("MsTeamsChannel", upsert.ChannelArmName);
        Assert.Equal("tenant-1", upsert.TenantId);
        Assert.Equal("CommercialDeployment", Parameters(upsert)["deploymentEnvironment"].GetString());
        Assert.True(Parameters(upsert)["isEnabled"].GetBoolean());

        Assert.Equal(DeploymentOperationStatus.Created, result.Status);
        Assert.Equal("Channel", result.TargetName);
        Assert.Equal("msteams", result.TargetInstanceName);
        Assert.NotNull(result.Payload);
        Assert.Equal("msteams", result.Payload!.ChannelName);
        Assert.True(result.Payload.Parameters["isEnabled"].GetBoolean());
        Assert.Contains("channels/MsTeamsChannel", result.Payload.ResourceId);
    }

    [Fact]
    public async Task CreateMsTeams_ExistingChannel_ReturnsExistingWithoutUpsert()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = JsonDocument.Parse(
                "{\"id\":\"/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev/channels/MsTeamsChannel\",\"properties\":{\"channelName\":\"MsTeamsChannel\",\"properties\":{\"isEnabled\":true,\"enableCalling\":true}}}")
                .RootElement.Clone(),
        };
        var service = new DeploymentChannelService(store, channels);

        var result = await service.CreateChannelAsync(CreateRequest("msteams"), default);

        Assert.Single(channels.Gets);
        Assert.Empty(channels.Upserts);
        Assert.Equal(DeploymentOperationStatus.Existed, result.Status);
        Assert.Equal("msteams", result.Payload!.ChannelName);
        Assert.True(result.Payload.Parameters["enableCalling"].GetBoolean());
        Assert.Equal("msteams", Assert.Single(Assert.Single(store.Records).Channels).ChannelName);
    }

    [Fact]
    public async Task CreateM365Extensions_PersistsPropertylessChannel()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort();
        var service = new DeploymentChannelService(store, channels);

        var result = await service.CreateChannelAsync(CreateRequest("m365extensions"), default);

        var upsert = Assert.Single(channels.Upserts);
        Assert.Equal("M365Extensions", upsert.ChannelArmName);
        Assert.Empty(upsert.Parameters);
        var persisted = Assert.Single(Assert.Single(store.Records).Channels);
        Assert.Equal("m365extensions", persisted.ChannelName);
        Assert.Empty(persisted.Parameters);
        Assert.Equal(DeploymentOperationStatus.Created, result.Status);
    }

    [Fact]
    public async Task ShowM365Extensions_ReadsPropertylessChannel()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort { IsChannelExisting = true };
        var service = new DeploymentChannelService(store, channels);

        var result = await service.GetChannelAsync(ShowRequest("m365extensions"), default);

        Assert.Equal(DeploymentOperationStatus.Retrieved, result.Status);
        Assert.Single(channels.Gets);
        Assert.Equal("m365extensions", result.Payload!.ChannelName);
        Assert.Empty(result.Payload.Parameters);
    }

    [Fact]
    public async Task UpdateM365Extensions_RejectsBeforeLoadingDeployment()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort();
        var service = new DeploymentChannelService(store, channels);

        var ex = await Assert.ThrowsAsync<DeploymentException>(() =>
            service.UpdateChannelAsync(
                UpdateRequest("m365extensions", "isEnabled=true"),
                default));

        Assert.Equal("channel-operation-not-supported", ex.ErrorKind);
        Assert.Equal(0, store.ListCalls);
        Assert.Empty(channels.Gets);
        Assert.Empty(channels.Upserts);
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public async Task Delete_AutomaticallyCreatedChannel_RejectsBeforeLoadingDeployment(string channel)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort { IsChannelExisting = true };
        var service = new DeploymentChannelService(store, channels);

        var ex = await Assert.ThrowsAsync<DeploymentException>(() =>
            service.DeleteChannelAsync(DeleteRequest(channel), default));

        Assert.Equal("channel-operation-not-supported", ex.ErrorKind);
        Assert.Equal($"{channel} does not support delete.", ex.Message);
        Assert.Equal(0, store.ListCalls);
        Assert.Empty(channels.Deletes);
    }

    [Fact]
    public async Task CreateSlack_ExistingChannel_RequiresCreateParametersBeforeGet()
    {
        var channels = new RecordingChannelPort { IsChannelExisting = true };
        var service = new DeploymentChannelService(new FakeStore(ProvisionedRecord()), channels);

        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            service.CreateChannelAsync(CreateRequest("slack"), default));

        Assert.Contains("clientId", ex.Message);
        Assert.Empty(channels.Gets);
        Assert.Empty(channels.Upserts);
    }

    [Fact]
    public async Task CreateSlack_ExistingChannel_WithValidParametersReturnsExisting()
    {
        var channels = new RecordingChannelPort { IsChannelExisting = true };
        var service = new DeploymentChannelService(new FakeStore(ProvisionedRecord()), channels);

        var result = await service.CreateChannelAsync(
            CreateRequest(
                "slack",
                "clientId=client-1",
                "clientSecret=secret-1",
                "verificationToken=token-1"),
            default);

        Assert.Equal(DeploymentOperationStatus.Existed, result.Status);
        Assert.Single(channels.Gets);
        Assert.Empty(channels.Upserts);
    }

    [Theory]
    [InlineData("msteams", "invalid-assignment")]
    [InlineData("msteams", "enableCalling=true", "ENABLECALLING=false")]
    [InlineData("msteams", "unknown=value")]
    [InlineData("msteams", "enableCalling=not-a-boolean")]
    [InlineData("slack", "clientId=client-1", "verificationToken=token-1")]
    public async Task Create_InvalidParameters_ThrowsBeforeGet(
        string channel,
        params string[] parameters)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort { IsChannelExisting = true };
        var service = new DeploymentChannelService(store, channels);

        await Assert.ThrowsAsync<UsageException>(() =>
            service.CreateChannelAsync(CreateRequest(channel, parameters), default));

        Assert.Equal(0, store.ListCalls);
        Assert.Empty(channels.Gets);
        Assert.Empty(channels.Upserts);
    }

    [Fact]
    public async Task Create_ExistingChannel_InvalidSuppliedParameter_ThrowsBeforeGet()
    {
        var channels = new RecordingChannelPort { IsChannelExisting = true };
        var service = new DeploymentChannelService(new FakeStore(ProvisionedRecord()), channels);

        await Assert.ThrowsAsync<UsageException>(() => service.CreateChannelAsync(
            CreateRequest("msteams", "enableCalling=not-a-boolean"),
            default));

        Assert.Empty(channels.Gets);
        Assert.Empty(channels.Upserts);
    }

    [Fact]
    public async Task CreateMsTeams_AppliesEverySuppliedFlatCatalogProperty()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort();
        var service = new DeploymentChannelService(store, channels);

        await service.CreateChannelAsync(
            CreateRequest(
                "msteams",
                "isEnabled=false",
                "acceptedTerms=true",
                "enableCalling=true",
                "callingWebhook=https://calling.example",
                "deploymentEnvironment=CommercialDeployment",
                "incomingCallRoute=https://route.example"),
            default);

        var properties = Parameters(Assert.Single(channels.Upserts));
        Assert.False(properties["isEnabled"].GetBoolean());
        Assert.True(properties["acceptedTerms"].GetBoolean());
        Assert.True(properties["enableCalling"].GetBoolean());
        Assert.Equal("https://calling.example", properties["callingWebhook"].GetString());
        Assert.Equal("CommercialDeployment", properties["deploymentEnvironment"].GetString());
        Assert.Equal("https://route.example", properties["incomingCallRoute"].GetString());
    }

    [Fact]
    public async Task CreateMsTeams_CarriesEntireArmResponse()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            UpsertRawArmResponse = JsonDocument.Parse(
                "{\"id\":\"/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev/channels/MsTeamsChannel\",\"properties\":{\"channelName\":\"MsTeamsChannel\",\"provisioningState\":\"Succeeded\",\"properties\":{\"isEnabled\":true,\"acceptedTerms\":true}}}")
                .RootElement.Clone(),
        };
        var service = new DeploymentChannelService(store, channels);

        var result = await service.CreateChannelAsync(CreateRequest("msteams"), default);

        Assert.NotNull(result.Payload!.RawArmResponse);
        var arm = result.Payload.RawArmResponse!.Value;
        Assert.True(arm.GetProperty("properties").GetProperty("properties").GetProperty("acceptedTerms").GetBoolean());
        Assert.True(result.Payload.Parameters["acceptedTerms"].GetBoolean());
        Assert.True(result.Payload.Parameters["isEnabled"].GetBoolean());
    }

    [Fact]
    public async Task CreateMsTeams_UnknownId_Throws()
    {
        var service = new DeploymentChannelService(new FakeStore(), new RecordingChannelPort());
        await Assert.ThrowsAsync<DeploymentException>(() => service.CreateChannelAsync(CreateRequest("msteams"), default));
    }

    [Fact]
    public async Task CreateMsTeams_PersistsChannelToDeploymentRecord()
    {
        var store = new FakeStore(ProvisionedRecord());
        var service = new DeploymentChannelService(store, new RecordingChannelPort());

        await service.CreateChannelAsync(CreateRequest("msteams"), default);

        var persisted = Assert.Single(store.Records).Channels;
        var channel = Assert.Single(persisted);
        Assert.Equal("msteams", channel.ChannelName);
        Assert.True(channel.Parameters["isEnabled"].GetBoolean());
        Assert.Contains("channels/MsTeamsChannel", channel.ResourceId);
    }

    [Fact]
    public async Task CreateSlack_DoesNotPersistSecretsInDeploymentRecord()
    {
        var store = new FakeStore(ProvisionedRecord());
        var service = new DeploymentChannelService(store, new RecordingChannelPort());

        var request = CreateRequest(
            "slack",
            "clientId=client-1",
            "clientSecret=super-secret",
            "verificationToken=verify-token",
            "signingSecret=signing-secret");

        await service.CreateChannelAsync(request, default);

        var channel = Assert.Single(Assert.Single(store.Records).Channels);
        Assert.Equal("slack", channel.ChannelName);
        // DeploymentChannelRecord has no property that can carry a secret; assert the serialized form
        // contains none of the supplied credentials.
        var json = JsonSerializer.Serialize(channel);
        Assert.DoesNotContain("super-secret", json);
        Assert.DoesNotContain("verify-token", json);
        Assert.DoesNotContain("signing-secret", json);
    }

    [Fact]
    public async Task CreateMsTeams_ReplacesExistingChannelEntry_NoDuplicate()
    {
        var seeded = ProvisionedRecord() with
        {
            Channels =
            [
                StoredChannel(
                    ProvisionedRecord().Bot.ResourceId,
                    "msteams",
                    "MsTeamsChannel",
                    false),
            ],
        };
        var store = new FakeStore(seeded);
        var service = new DeploymentChannelService(store, new RecordingChannelPort());

        await service.CreateChannelAsync(CreateRequest("msteams"), default);

        var channel = Assert.Single(Assert.Single(store.Records).Channels);
        Assert.Equal("msteams", channel.ChannelName);
        Assert.True(channel.Parameters["isEnabled"].GetBoolean());
    }

    [Fact]
    public async Task DeleteMsTeams_RemovesChannelFromDeploymentRecord()
    {
        var seeded = ProvisionedRecord() with
        {
            Channels =
            [
                StoredChannel(ProvisionedRecord().Bot.ResourceId, "msteams", "MsTeamsChannel", true),
                StoredChannel(ProvisionedRecord().Bot.ResourceId, "slack", "SlackChannel", true),
            ],
        };
        var store = new FakeStore(seeded);
        var service = new DeploymentChannelService(store, new RecordingChannelPort());

        await service.DeleteChannelAsync(DeleteRequest("msteams"), default);

        var remaining = Assert.Single(store.Records).Channels;
        Assert.Equal("slack", Assert.Single(remaining).ChannelName);
    }

    [Fact]
    public async Task Create_InvalidChannel_ThrowsUsage()
    {
        var service = new DeploymentChannelService(new FakeStore(ProvisionedRecord()), new RecordingChannelPort());
        await Assert.ThrowsAsync<UsageException>(() => service.CreateChannelAsync(CreateRequest("nope"), default));
    }

    [Fact]
    public async Task Create_UnsupportedButAllowedChannel_ThrowsUsage()
    {
        var service = new DeploymentChannelService(new FakeStore(ProvisionedRecord()), new RecordingChannelPort());
        // "email" (EmailChannel) is an allowed ARM channel but not supported by this CLI.
        await Assert.ThrowsAsync<UsageException>(() => service.CreateChannelAsync(CreateRequest("email"), default));
    }

    [Fact]
    public async Task Create_ByAgentAndName_ResolvesDeterministicId()
    {
        var deterministicId = DeterministicDeploymentId.Compute(AgentId, "dev1").ToString();
        var record = ProvisionedRecord() with { DeploymentId = deterministicId, DeploymentName = "dev1" };
        var channels = new RecordingChannelPort();
        var service = new DeploymentChannelService(new FakeStore(record), channels);

        var result = await service.CreateChannelAsync(
            new DeploymentChannelCreateRequest { AgentId = AgentId, DeploymentName = "dev1", ChannelName = "msteams" },
            default);

        Assert.Single(channels.Upserts);
        Assert.Equal("msteams", result.TargetInstanceName);
    }

    [Fact]
    public async Task CreateSlack_UpsertsChannelWithCredentials()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort();
        var service = new DeploymentChannelService(store, channels);

        var result = await service.CreateChannelAsync(
            CreateRequest(
                "slack",
                "clientId=cid",
                "clientSecret=secret",
                "verificationToken=vtok",
                "signingSecret=sign",
                "scopes=chat:write",
                "landingPageUrl=https://landing.example"),
            default);

        var upsert = Assert.Single(channels.Upserts);
        Assert.Equal("SlackChannel", upsert.ChannelArmName);
        var parameters = Parameters(upsert);
        Assert.True(parameters["isEnabled"].GetBoolean());
        Assert.Equal("cid", parameters["clientId"].GetString());
        Assert.Equal("secret", parameters["clientSecret"].GetString());
        Assert.Equal("vtok", parameters["verificationToken"].GetString());
        Assert.Equal("sign", parameters["signingSecret"].GetString());
        Assert.Equal("chat:write", parameters["scopes"].GetString());
        Assert.Equal("https://landing.example", parameters["landingPageUrl"].GetString());
        Assert.True(parameters["registerBeforeOAuthFlow"].GetBoolean());

        Assert.Equal("slack", result.Payload!.ChannelName);
    }

    [Fact]
    public async Task CreateSlack_OmitsOptionalPropertiesWhenNotSupplied()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort();
        var service = new DeploymentChannelService(store, channels);

        await service.CreateChannelAsync(
            CreateRequest("slack", "clientId=cid", "clientSecret=secret", "verificationToken=vtok"),
            default);

        var upsert = Assert.Single(channels.Upserts);
        var parameters = Parameters(upsert);
        Assert.False(parameters.ContainsKey("scopes"));
        Assert.False(parameters.ContainsKey("landingPageUrl"));
        Assert.False(parameters.ContainsKey("signingSecret"));
        Assert.True(parameters["isEnabled"].GetBoolean());
        Assert.True(parameters["registerBeforeOAuthFlow"].GetBoolean());
    }

    [Fact]
    public async Task CreateSlack_MissingRequiredCredential_Throws()
    {
        var store = new FakeStore(ProvisionedRecord());
        var service = new DeploymentChannelService(store, new RecordingChannelPort());

        await Assert.ThrowsAsync<UsageException>(() => service.CreateChannelAsync(
            CreateRequest("slack", "clientId=cid", "verificationToken=vtok"),
            default));
    }

    [Fact]
    public async Task Create_OptionNotValidForChannel_Throws()
    {
        var store = new FakeStore(ProvisionedRecord());
        var service = new DeploymentChannelService(store, new RecordingChannelPort());

        // clientId is a Slack property, not valid for msteams.
        await Assert.ThrowsAsync<UsageException>(() => service.CreateChannelAsync(
            CreateRequest("msteams", "clientId=cid"),
            default));
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public async Task Create_NestedChannel_UpsertsSingleSite(string channel)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort();
        var service = new DeploymentChannelService(store, channels);

        await service.CreateChannelAsync(CreateRequest(channel, "siteName=Primary"), default);

        var sites = Assert.Single(channels.Upserts).Parameters["sites"];
        var site = Assert.Single(sites.EnumerateArray());
        Assert.Equal("Primary", site.GetProperty("siteName").GetString());
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public async Task Update_NestedChannel_OverridesSitesArrayWithSingleSite(string channel)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = JsonDocument.Parse(
                """
                {"properties":{"properties":{"sites":[
                  {"siteName":"Primary","isEnabled":true},
                  {"siteName":"Secondary","isEnabled":true}
                ]}}}
                """).RootElement.Clone(),
        };
        var service = new DeploymentChannelService(store, channels);
        var parameters = new[] { "isEnabled=false" };

        await service.UpdateChannelAsync(UpdateRequest(channel, parameters), default);

        var sites = Assert.Single(channels.Upserts).Parameters["sites"];
        var site = Assert.Single(sites.EnumerateArray());
        Assert.Equal("Primary", site.GetProperty("siteName").GetString());
        Assert.False(site.GetProperty("isEnabled").GetBoolean());
    }

    [Fact]
    public async Task Update_MergesExistingChannelAndUpserts()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = JsonDocument.Parse(
                "{\"properties\":{\"channelName\":\"MsTeamsChannel\",\"properties\":{\"isEnabled\":true,\"callingWebhook\":\"https://old.example\"}}}")
                .RootElement.Clone(),
        };
        var service = new DeploymentChannelService(store, channels);

        await service.UpdateChannelAsync(
            UpdateRequest("msteams", "enableCalling=true"),
            default);

        var upsert = Assert.Single(channels.Upserts);
        Assert.Single(channels.Gets);
        // Existing property preserved, provided property overlaid.
        var parameters = Parameters(upsert);
        Assert.True(parameters["isEnabled"].GetBoolean());
        Assert.True(parameters["enableCalling"].GetBoolean());
        Assert.Equal("https://old.example", parameters["callingWebhook"].GetString());
    }

    [Fact]
    public async Task UpdateSlack_UsesFlatCodecToMergeAndClearCatalogProperties()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = JsonDocument.Parse(
                "{\"properties\":{\"channelName\":\"SlackChannel\",\"properties\":{\"isEnabled\":true,\"clientId\":\"existing-client\",\"landingPageUrl\":\"https://old.example\"}}}")
                .RootElement.Clone(),
        };
        var service = new DeploymentChannelService(store, channels);

        await service.UpdateChannelAsync(
            UpdateRequest("slack", "isEnabled=false", "landingPageUrl="),
            default);

        var properties = Parameters(Assert.Single(channels.Upserts));
        Assert.False(properties["isEnabled"].GetBoolean());
        Assert.Equal(JsonValueKind.Null, properties["landingPageUrl"].ValueKind);
    }

    [Fact]
    public async Task UpdateSlack_DoesNotReplayRedactedNullOrUnknownProperties()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = JsonDocument.Parse(
                """
                {"properties":{"channelName":"SlackChannel","properties":{
                  "isEnabled":true,
                  "clientId":"existing-client",
                  "clientSecret":"***",
                  "verificationToken":null,
                  "signingSecret":"***",
                  "scopes":"***",
                  "landingPageUrl":"https://old.example",
                  "unexpected":"***"
                }}}
                """)
                .RootElement.Clone(),
        };
        var service = new DeploymentChannelService(store, channels);

        await service.UpdateChannelAsync(
            UpdateRequest("slack", "isEnabled=false"),
            default);

        var properties = Parameters(Assert.Single(channels.Upserts));
        Assert.False(properties["isEnabled"].GetBoolean());
        Assert.DoesNotContain("clientSecret", properties);
        Assert.DoesNotContain("verificationToken", properties);
        Assert.DoesNotContain("signingSecret", properties);
        Assert.DoesNotContain("scopes", properties);
        Assert.DoesNotContain("unexpected", properties);
    }

    [Fact]
    public async Task UpdateSlack_SendsOnlySecretsSuppliedInCurrentInvocation()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = JsonDocument.Parse(
                """
                {"properties":{"channelName":"SlackChannel","properties":{
                  "isEnabled":true,
                  "clientId":"existing-client",
                  "clientSecret":"***",
                  "verificationToken":"***",
                  "signingSecret":"***"
                }}}
                """)
                .RootElement.Clone(),
        };
        var service = new DeploymentChannelService(store, channels);

        await service.UpdateChannelAsync(
            UpdateRequest("slack", "clientSecret=new-secret"),
            default);

        var properties = Parameters(Assert.Single(channels.Upserts));
        Assert.Equal("new-secret", properties["clientSecret"].GetString());
        Assert.DoesNotContain("verificationToken", properties);
        Assert.DoesNotContain("signingSecret", properties);
    }

    [Fact]
    public async Task Update_NoSettings_Throws()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort { IsChannelExisting = true };
        var service = new DeploymentChannelService(
            store,
            channels);

        await Assert.ThrowsAsync<UsageException>(() => service.UpdateChannelAsync(UpdateRequest("msteams"), default));
        Assert.Empty(channels.Gets);
        Assert.Empty(channels.Upserts);
    }

    [Fact]
    public async Task Update_ChannelNotConfigured_Throws()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort { IsChannelExisting = false };
        var service = new DeploymentChannelService(store, channels);

        await Assert.ThrowsAsync<DeploymentException>(() => service.UpdateChannelAsync(
            UpdateRequest("msteams", "enableCalling=true"),
            default));
        Assert.Empty(channels.Upserts);
    }

    [Fact]
    public async Task Show_ReadsChannelOnBackingBot()
    {
        var seeded = ProvisionedRecord() with
        {
            Channels =
            [
                StoredChannel(ProvisionedRecord().Bot.ResourceId, "msteams", "MsTeamsChannel", true),
                StoredChannel(ProvisionedRecord().Bot.ResourceId, "SLACK", "SlackChannel", false),
            ],
        };
        var store = new FakeStore(seeded);
        var raw = JsonDocument.Parse(
            "{\"properties\":{\"channelName\":\"SlackChannel\",\"properties\":{\"isEnabled\":true,\"clientId\":\"client-1\",\"clientSecret\":\"***\"}}}")
            .RootElement.Clone();
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = raw,
        };
        var service = new DeploymentChannelService(store, channels);

        var result = await service.GetChannelAsync(
            ShowRequest("slack"),
            default);

        var reference = Assert.Single(channels.Gets);
        Assert.Equal(ProvisionedRecord().Bot.ResourceId, reference.BotResourceId);
        Assert.Equal("tenant-1", reference.TargetTenantId);
        Assert.Equal("SlackChannel", reference.ChannelName);

        Assert.Equal(DeploymentOperationStatus.Retrieved, result.Status);
        Assert.Equal("slack", result.Payload!.ChannelName);
        Assert.Equal(raw.GetRawText(), result.Payload.RawArmResponse!.Value.GetRawText());

        var persisted = Assert.Single(store.Records).Channels;
        Assert.Equal(2, persisted.Count);
        Assert.Contains(persisted, channel => channel.ChannelName == "msteams");
        var slack = Assert.Single(
            persisted,
            channel => channel.ChannelName.Equals("slack", StringComparison.OrdinalIgnoreCase));
        Assert.True(slack.Parameters["isEnabled"].GetBoolean());
        Assert.Equal("client-1", slack.Parameters["clientId"].GetString());
        Assert.Equal("***", slack.Parameters["clientSecret"].GetString());
        Assert.Null(slack.RawArmResponse);
    }

    [Fact]
    public async Task Show_ChannelNotConfigured_Throws()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort { IsChannelExisting = false };
        var service = new DeploymentChannelService(store, channels);

        await Assert.ThrowsAsync<DeploymentException>(() => service.GetChannelAsync(
            ShowRequest("msteams"),
            default));
    }

    [Fact]
    public void ShowRequest_DoesNotExposeParameters()
    {
        Assert.Null(typeof(DeploymentChannelShowRequest).GetProperty(nameof(DeploymentChannelUpdateRequest.Parameters)));
    }

    [Fact]
    public async Task Delete_RemovesChannelOnBackingBot()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort { IsChannelExisting = true };
        var service = new DeploymentChannelService(store, channels);

        var result = await service.DeleteChannelAsync(DeleteRequest("msteams"), default);

        var reference = Assert.Single(channels.Deletes);
        Assert.Equal("MsTeamsChannel", reference.ChannelName);
        Assert.Equal(ProvisionedRecord().Bot.ResourceId, reference.BotResourceId);
        Assert.Equal("tenant-1", reference.TargetTenantId);
        Assert.Equal(DeploymentOperationStatus.Deleted, result.Status);
        Assert.Null(result.Payload);
        Assert.Equal("msteams", result.TargetInstanceName);
    }

    [Fact]
    public async Task Delete_AlreadyAbsent_ReportsNotDeleted()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort { IsChannelExisting = false };
        var service = new DeploymentChannelService(store, channels);

        var result = await service.DeleteChannelAsync(DeleteRequest("msteams"), default);

        Assert.Single(channels.Deletes);
        Assert.Equal(DeploymentOperationStatus.Deleted, result.Status);
        Assert.Null(result.Payload);
    }

    [Fact]
    public async Task List_ReturnsAndPersistsChannelRecords()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            ListChannels =
            [
                StoredChannel(ProvisionedRecord().Bot.ResourceId, "msteams", "MsTeamsChannel", true),
                StoredChannel(ProvisionedRecord().Bot.ResourceId, "slack", "SlackChannel", true),
            ],
        };
        var service = new DeploymentChannelService(store, channels);

        var result = await service.ListChannelsAsync(new DeploymentChannelListRequest { DeploymentId = DepId }, default);

        var list = Assert.Single(channels.Lists);
        Assert.Equal(ProvisionedRecord().Bot.ResourceId, list.BotResourceId);
        Assert.Equal("tenant-1", list.TargetTenantId);

        Assert.Equal(DeploymentOperationStatus.Listed, result.Status);
        Assert.Equal(2, result.Payload!.Length);
        Assert.Equal(["msteams", "slack"], result.Payload.Select(channel => channel.ChannelName));
        Assert.Equal(
            ["msteams", "slack"],
            Assert.Single(store.Records).Channels.Select(channel => channel.ChannelName));
    }

    [Fact]
    public async Task List_UnknownId_Throws()
    {
        var service = new DeploymentChannelService(new FakeStore(), new RecordingChannelPort());
        await Assert.ThrowsAsync<DeploymentException>(() => service.ListChannelsAsync(
            new DeploymentChannelListRequest { DeploymentId = DepId },
            default));
    }

    private static IReadOnlyDictionary<string, JsonElement> Parameters(BotServiceChannelUpsertRequest upsert) =>
        upsert.Parameters;

    private static DeploymentChannelRecord StoredChannel(
        string botResourceId,
        string channelName,
        string channelArmName,
        bool isEnabled) =>
        new()
        {
            ChannelName = channelName,
            ResourceId = $"{botResourceId}/channels/{channelArmName}",
            Parameters = new Dictionary<string, JsonElement>
            {
                ["isEnabled"] = JsonSerializer.SerializeToElement(isEnabled),
            },
        };

    private sealed class FakeStore : IDeploymentStore
    {
        public FakeStore(params CodeAgentDeployment[] seed) => Records = seed.ToList();

        public List<CodeAgentDeployment> Records { get; }

        public int ListCalls { get; private set; }

        public IReadOnlyList<CodeAgentDeployment> List(string? project)
        {
            ListCalls++;
            return Records.ToList();
        }

        public void Upsert(string? project, CodeAgentDeployment deployment)
        {
            Records.RemoveAll(record => string.Equals(record.DeploymentId, deployment.DeploymentId, StringComparison.OrdinalIgnoreCase));
            Records.Add(deployment);
        }

        public void Remove(string? project, string deploymentId) =>
            Records.RemoveAll(record => string.Equals(record.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingChannelPort : IDeploymentChannelTransport
    {
        public List<BotServiceChannelUpsertRequest> Upserts { get; } = [];

        public List<ChannelGet> Gets { get; } = [];

        public List<ChannelDelete> Deletes { get; } = [];

        public List<ChannelList> Lists { get; } = [];

        /// <summary>Controls whether <see cref="GetChannelAsync"/>/<see cref="DeleteChannelAsync"/> see an existing channel.</summary>
        public bool IsChannelExisting { get; set; }

        /// <summary>The full ARM payload the upsert record surfaces verbatim.</summary>
        public JsonElement? UpsertRawArmResponse { get; set; }

        /// <summary>The full ARM payload the get record surfaces verbatim (drives the update GET-merge-PUT).</summary>
        public JsonElement? GetRawArmResponse { get; set; }

        /// <summary>The channels returned by <see cref="ListChannelsAsync"/>.</summary>
        public IReadOnlyList<DeploymentChannelRecord> ListChannels { get; set; } = [];

        public Task<DeploymentChannelRecord> UpsertChannelAsync(BotServiceChannelUpsertRequest request, CancellationToken cancellationToken)
        {
            Upserts.Add(request);
            var parameters = Parameters(request);
            var isEnabled = parameters.TryGetValue("isEnabled", out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean()
                    : (bool?)null;
            return Task.FromResult(ChannelRecord(
                request.BotResourceId,
                DeploymentChannelCatalog.FindChannelDefinition(
                    request.ChannelArmName,
                    ChannelKeyType.ArmName).Name,
                request.ChannelArmName,
                isEnabled,
                UpsertRawArmResponse));
        }

        public Task<DeploymentChannelRecord?> GetChannelAsync(
            string channelName,
            string botResourceId,
            string? targetTenantId,
            CancellationToken cancellationToken)
        {
            Gets.Add(new ChannelGet(channelName, botResourceId, targetTenantId));
            return Task.FromResult<DeploymentChannelRecord?>(IsChannelExisting
                ? ChannelRecord(
                    botResourceId,
                    DeploymentChannelCatalog.FindChannelDefinition(
                        channelName,
                        ChannelKeyType.ArmName).Name,
                    channelName,
                    isEnabled: true,
                    GetRawArmResponse)
                : null);
        }

        public Task<bool> DeleteChannelAsync(
            string channelName,
            string botResourceId,
            string? targetTenantId,
            CancellationToken cancellationToken)
        {
            Deletes.Add(new ChannelDelete(channelName, botResourceId, targetTenantId));
            return Task.FromResult(IsChannelExisting);
        }

        public Task<IReadOnlyList<DeploymentChannelRecord>> ListChannelsAsync(
            string botResourceId,
            string? targetTenantId,
            CancellationToken cancellationToken)
        {
            Lists.Add(new ChannelList(botResourceId, targetTenantId));
            return Task.FromResult(ListChannels);
        }

        private static DeploymentChannelRecord ChannelRecord(
            string botResourceId,
            string channelName,
            string channelArmName,
            bool? isEnabled,
            JsonElement? raw)
        {
            var parameters = SafeParameters(raw);
            var channel = DeploymentChannelCatalog.FindChannelDefinition(
                channelName,
                ChannelKeyType.Name);
            if (isEnabled is { } enabled &&
                channel.Parameters.Any(parameter =>
                    parameter.Name.Equals("isEnabled", StringComparison.OrdinalIgnoreCase)))
            {
                parameters["isEnabled"] = JsonSerializer.SerializeToElement(enabled);
            }

            return new()
            {
                ChannelName = channelName,
                ResourceId = $"{botResourceId}/channels/{channelArmName}",
                Parameters = parameters,
                RawArmResponse = raw,
            };
        }

        private static Dictionary<string, JsonElement> SafeParameters(JsonElement? raw)
        {
            if (raw is not { ValueKind: JsonValueKind.Object } root ||
                !root.TryGetProperty("properties", out var outer) ||
                outer.ValueKind != JsonValueKind.Object ||
                !outer.TryGetProperty("properties", out var inner) ||
                inner.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            }

            return inner.EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.Clone(),
                    StringComparer.Ordinal);
        }

        public sealed record ChannelGet(
            string ChannelName,
            string BotResourceId,
            string? TargetTenantId);

        public sealed record ChannelDelete(
            string ChannelName,
            string BotResourceId,
            string? TargetTenantId);

        public sealed record ChannelList(
            string BotResourceId,
            string? TargetTenantId);
    }
}
