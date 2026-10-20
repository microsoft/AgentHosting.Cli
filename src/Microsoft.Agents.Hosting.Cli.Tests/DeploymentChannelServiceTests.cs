using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Offline channel service tests covering request validation, ARM payloads, and persistence.
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

    [Theory]
    [InlineData("add", 3)]
    [InlineData("set", 2)]
    [InlineData("remove", 1)]
    [InlineData("replace", 1)]
    public async Task UpdateLine_FreshReadsAndPersistsOnlyTheSanitizedPatchResponse(string mode, int count)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = JsonDocument.Parse("""
                {"properties":{"properties":{"callbackUrl":"https://callback.example/one","isValidated":true,
                  "lineRegistrations":[{"generatedId":"one","channelSecret":"***","channelAccessToken":"***"},
                                       {"generatedId":"two","channelSecret":"***","channelAccessToken":"***"}]}}}
                """).RootElement.Clone(),
            UpsertRawArmResponse = JsonDocument.Parse("""
                {"properties":{"properties":{"callbackUrl":"https://callback.example/returned","isValidated":true,
                  "lineRegistrations":[{"generatedId":"returned","channelSecret":"***","channelAccessToken":"***"}]}}}
                """).RootElement.Clone(),
        };
        var request = mode switch
        {
            "add" => UpdateRequest("line") with { AddRegistration = ["channelSecret=fresh-secret", "channelAccessToken=fresh-token"] },
            "set" => UpdateRequest("line") with { SetRegistration = ["generatedId=two", "channelSecret=fresh-secret"] },
            "remove" => UpdateRequest("line") with { RemoveRegistration = ["generatedId=one"] },
            _ => UpdateRequest("line", """lineRegistrations=[{"generatedId":"two"}]"""),
        };
        var result = await new DeploymentChannelService(store, channels).UpdateChannelAsync(request, default);

        Assert.Equal(["get", "update"], channels.Operations);
        var write = Assert.Single(channels.Upserts);
        Assert.Equal(count, write.Parameters["lineRegistrations"].GetArrayLength());
        Assert.Equal("https://callback.example/one", write.Parameters["callbackUrl"].GetString());
        Assert.DoesNotContain("***", JsonSerializer.Serialize(write.Parameters));
        var saved = Assert.Single(Assert.Single(store.Records).Channels);
        Assert.Equal("returned", Assert.Single(saved.Parameters["lineRegistrations"].EnumerateArray()).GetProperty("generatedId").GetString());
        Assert.Equal(result.Payload!.Parameters["callbackUrl"].GetString(), saved.Parameters["callbackUrl"].GetString());
        Assert.Null(saved.RawArmResponse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateLine_WritesBothCredentialsOrReturnsExistingUnchanged(bool exists)
    {
        var store = new FakeStore(ProvisionedRecord());
        var response = JsonDocument.Parse("""
            {"properties":{"properties":{"lineRegistrations":[
              {"generatedId":"one","channelSecret":"***","channelAccessToken":"***"}]}}}
            """).RootElement.Clone();
        var channels = new RecordingChannelPort { IsChannelExisting = exists, GetRawArmResponse = response, UpsertRawArmResponse = response };
        var result = await new DeploymentChannelService(store, channels).CreateChannelAsync(CreateRequest("line",
            """lineRegistrations=[{"channelSecret":"synthetic-secret","channelAccessToken":"synthetic-token"}]"""), default);

        Assert.Equal(exists ? ["get"] : ["get", "create"], channels.Operations);
        Assert.Equal(exists ? DeploymentOperationStatus.Existed : DeploymentOperationStatus.Created, result.Status);
        if (!exists)
        {
            var item = Assert.Single(Assert.Single(channels.Upserts).Parameters["lineRegistrations"].EnumerateArray());
            Assert.Equal("synthetic-secret", item.GetProperty("channelSecret").GetString());
            Assert.Equal("synthetic-token", item.GetProperty("channelAccessToken").GetString());
        }
        Assert.Null(Assert.Single(Assert.Single(store.Records).Channels).RawArmResponse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateLine_InvalidInputOrFailedPatchDoesNotPersist(bool rejectWrite)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = JsonDocument.Parse("""
                {"properties":{"properties":{"lineRegistrations":[{"generatedId":"one"}]}}}
                """).RootElement.Clone(),
        };
        channels.WriteErrors[1] = new InvalidOperationException("Provider rejected the write.");
        var request = rejectWrite
            ? UpdateRequest("line") with { SetRegistration = ["generatedId=one", "channelSecret=fresh"] }
            : UpdateRequest("line", """lineRegistrations=[{"generatedId":"one","channelSecret":null}]""");
        var action = () => new DeploymentChannelService(store, channels).UpdateChannelAsync(request, default);
        if (rejectWrite)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(action);
        }
        else
        {
            await Assert.ThrowsAsync<UsageException>(action);
        }
        Assert.Equal(rejectWrite ? ["get", "update"] : [], channels.Operations);
        Assert.Equal(0, store.UpsertCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateFacebook_WritesAllParametersOrReturnsExistingUnchanged(bool exists)
    {
        var raw = JsonSerializer.SerializeToElement(new
        {
            properties = new
            {
                properties = new
                {
                    appId = "app",
                    appSecret = "synthetic-secret",
                    isEnabled = false,
                    callbackUrl = "https://callback.example",
                    verifyToken = "synthetic-verify",
                    pages = new[] { new { id = "one", accessToken = "synthetic-token" }, new { id = "two", accessToken = "other-token" } },
                },
            },
        });
        raw = BotServiceResponseRedactor.RedactChannel(raw,
            DeploymentChannelCatalog.FindChannelDefinition("facebook", ChannelKeyType.Name));
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort { IsChannelExisting = exists, GetRawArmResponse = raw, UpsertRawArmResponse = raw };
        var result = await new DeploymentChannelService(store, channels).CreateChannelAsync(
            CreateRequest("facebook", "appId=app", "isEnabled=false", "appSecret=synthetic-secret",
                """pages=[{"id":"one","accessToken":"synthetic-token"},{"id":"two","accessToken":"other-token"}]"""), default);

        Assert.Equal(exists ? ["get"] : ["get", "create"], channels.Operations);
        Assert.Equal(exists ? DeploymentOperationStatus.Existed : DeploymentOperationStatus.Created, result.Status);
        Assert.Equal(2, result.Payload!.Parameters["pages"].GetArrayLength());
        Assert.DoesNotContain("synthetic-", result.Payload.RawArmResponse!.Value.GetRawText());
        if (!exists)
        {
            var write = Assert.Single(channels.Upserts);
            Assert.Equal("synthetic-secret", write.Parameters["appSecret"].GetString());
            Assert.Equal("synthetic-token", write.Parameters["pages"][0].GetProperty("accessToken").GetString());
            Assert.False(write.Parameters["isEnabled"].GetBoolean());
        }

        var saved = Assert.Single(Assert.Single(store.Records).Channels);
        Assert.Null(saved.RawArmResponse);
        Assert.Equal("***", saved.Parameters["pages"][0].GetProperty("accessToken").GetString());
        Assert.Equal("***", saved.Parameters["verifyToken"].GetString());
    }

    [Theory]
    [InlineData("facebook", "FacebookChannel")]
    [InlineData("slack", "SlackChannel")]
    public async Task Create_OmitsUnspecifiedEnabledAndPersistsTheProviderDefault(string channelName, string armName)
    {
        var response = JsonSerializer.Serialize(new
        {
            id = "/channels/" + armName,
            properties = new { channelName = armName, properties = new { isEnabled = false } },
        });
        var handler = new StubHttpMessageHandler(request =>
            new HttpResponseMessage(request.Method == HttpMethod.Get ? HttpStatusCode.NotFound : HttpStatusCode.Created)
            {
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        var channels = new DeploymentChannelAdapter(httpClient, new StubTokenProvider { AccessToken = "arm-token", TenantId = "home" });
        var store = new FakeStore(ProvisionedRecord());
        var request = channelName == "facebook"
            ? CreateRequest(channelName, "appId=app", "appSecret=secret", """pages=[{"id":"one","accessToken":"token"}]""")
            : CreateRequest(channelName, "clientId=app", "clientSecret=secret", "signingSecret=sign", "scopes=chat:write");

        var result = await new DeploymentChannelService(store, channels).CreateChannelAsync(request, default);

        Assert.Equal(channelName == "slack" ? DeploymentOperationStatus.WriteAccepted : DeploymentOperationStatus.Created, result.Status);
        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Put }, handler.Requests.Select(call => call.Method));
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.False(body.RootElement.GetProperty("properties").GetProperty("properties").TryGetProperty("isEnabled", out _));
        Assert.False(result.Payload!.Parameters["isEnabled"].GetBoolean());
        var saved = Assert.Single(Assert.Single(store.Records).Channels);
        Assert.False(saved.Parameters["isEnabled"].GetBoolean());
        Assert.Null(saved.RawArmResponse);
    }

    [Theory]
    [InlineData("appSecret")]
    [InlineData("pages")]
    public async Task CreateFacebook_MissingRequiredInputsRejectsBeforeAzure(string omitted)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort();
        string[] fields = ["appId=app", "isEnabled=false", "appSecret=synthetic-secret",
            """pages=[{"id":"one","accessToken":"synthetic-token"}]"""];

        var error = await Assert.ThrowsAsync<UsageException>(() =>
            new DeploymentChannelService(store, channels).CreateChannelAsync(
                CreateRequest("facebook", fields.Where(field => !field.StartsWith(omitted + "=", StringComparison.Ordinal)).ToArray()), default));

        Assert.Contains(omitted, error.Message);
        Assert.Empty(channels.Operations);
        Assert.Equal(0, store.UpsertCalls);
    }

    [Theory]
    [InlineData("appSecret=")]
    [InlineData("pages=")]
    [InlineData("""pages=[{"id":"one","accessToken":null}]""")]
    [InlineData("""pages=[{"id":"one","accessToken":""}]""")]
    public async Task UpdateFacebook_ClearingCredentialsOrPagesRejectsBeforeAzure(string assignment)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort { IsChannelExisting = true };

        var error = await Assert.ThrowsAsync<UsageException>(() =>
            new DeploymentChannelService(store, channels).UpdateChannelAsync(UpdateRequest("facebook", assignment), default));

        Assert.Contains("cannot be cleared", error.Message);
        Assert.Empty(channels.Operations);
        Assert.Equal(0, store.UpsertCalls);
    }

    [Theory]
    [InlineData("scalar", 2)]
    [InlineData("add", 3)]
    [InlineData("set", 2)]
    [InlineData("remove", 1)]
    [InlineData("replace", 1)]
    public async Task UpdateFacebook_FreshReadsAndPersistsOnlyThePatchResponse(string mode, int count)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = JsonDocument.Parse("""
                {"properties":{"properties":{"appId":"app","appSecret":"***","isEnabled":false,
                  "callbackUrl":"https://callback.example","verifyToken":"***",
                  "pages":[{"id":"one","accessToken":"***"},{"id":"two","accessToken":"***"}]}}}
                """).RootElement.Clone(),
            UpsertRawArmResponse = JsonDocument.Parse("""
                {"properties":{"properties":{"appId":"returned-app","isEnabled":false,
                  "pages":[{"id":"returned","accessToken":"***"}]}}}
                """).RootElement.Clone(),
        };
        var request = mode switch
        {
            "add" => UpdateRequest("facebook") with { AddPage = ["id=three", "accessToken=fresh"] },
            "set" => UpdateRequest("facebook") with { SetPage = ["id=two", "accessToken=fresh"] },
            "remove" => UpdateRequest("facebook") with { RemovePage = ["id=one"] },
            "replace" => UpdateRequest("facebook", """pages=[{"id":"two"}]"""),
            _ => UpdateRequest("facebook", "isEnabled=false"),
        };
        var result = await new DeploymentChannelService(store, channels).UpdateChannelAsync(request, default);

        Assert.Equal(["get", "update"], channels.Operations);
        var write = Assert.Single(channels.Upserts);
        Assert.Equal(count, write.Parameters["pages"].GetArrayLength());
        Assert.Equal("https://callback.example", write.Parameters["callbackUrl"].GetString());
        Assert.DoesNotContain("appSecret", write.Parameters);
        Assert.DoesNotContain("verifyToken", write.Parameters);
        Assert.DoesNotContain("***", JsonSerializer.Serialize(write.Parameters));
        Assert.Equal("returned-app", result.Payload!.Parameters["appId"].GetString());
        var saved = Assert.Single(Assert.Single(store.Records).Channels);
        Assert.Equal("returned", Assert.Single(saved.Parameters["pages"].EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal("***", saved.Parameters["pages"][0].GetProperty("accessToken").GetString());
        Assert.Null(saved.RawArmResponse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateFacebook_UnsafeLiveStateOrFailedPatchDoesNotPersist(bool rejectWrite)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = JsonDocument.Parse(rejectWrite
                ? """{"properties":{"properties":{"pages":[{"id":"one","accessToken":"***"}]}}}"""
                : """{"properties":{"properties":{"pages":[null]}}}""").RootElement.Clone(),
        };
        if (rejectWrite)
        {
            channels.WriteErrors[1] = new InvalidOperationException("Provider rejected the write.");
        }

        var action = () => new DeploymentChannelService(store, channels).UpdateChannelAsync(
            UpdateRequest("facebook") with { SetPage = ["id=one", "accessToken=fresh"] }, default);
        if (rejectWrite)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(action);
        }
        else
        {
            var error = await Assert.ThrowsAsync<DeploymentException>(action);
            Assert.Equal(ExitCodes.Failure, error.ExitCode);
        }

        Assert.Equal(rejectWrite ? ["get", "update"] : ["get"], channels.Operations);
        Assert.Equal(0, store.UpsertCalls);
        Assert.Empty(Assert.Single(store.Records).Channels);
    }

    [Theory]
    [InlineData("sms", "add", false)]
    [InlineData("sms", "add", true)]
    [InlineData("sms", "set", false)]
    [InlineData("sms", "set", true)]
    [InlineData("sms", "remove", false)]
    [InlineData("sms", "remove", true)]
    public async Task FlatChannels_RejectSiteOperationsBeforeLoadingDeployment(string channel, string operation, bool withParameters)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort();
        var request = UpdateRequest(channel, withParameters ? ["isEnabled=false"] : []);
        request = operation switch
        {
            "add" => request with { AddSite = ["siteName=Primary"] },
            "set" => request with { SetSite = ["siteName=Primary", "isEnabled=false"] },
            _ => request with { RemoveSite = ["siteName=Primary"] },
        };

        var error = await Assert.ThrowsAsync<UsageException>(() =>
            new DeploymentChannelService(store, channels).UpdateChannelAsync(request, default));

        Assert.Equal($"Option '--{operation}-site' is not valid for '{channel}' channel update.", error.Message);
        Assert.Equal(0, store.ListCalls);
        Assert.Empty(channels.Operations);
    }

    [Fact]
    public async Task CreateMsTeams_UpsertsChannelOnBackingBot()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort();
        var service = new DeploymentChannelService(store, channels);

        var result = await service.CreateChannelAsync(CreateRequest("msteams"), default);

        var upsert = Assert.Single(channels.Upserts);
        Assert.Equal(["get", "create"], channels.Operations);
        Assert.Equal(ProvisionedRecord().Bot.ResourceId, upsert.BotResourceId);
        Assert.Equal("MsTeamsChannel", upsert.ChannelArmName);
        Assert.Equal("tenant-1", upsert.TenantId);
        Assert.Equal("CommercialDeployment", Parameters(upsert)["deploymentEnvironment"].GetString());
        Assert.False(Parameters(upsert)["enableCalling"].GetBoolean());
        Assert.DoesNotContain("isEnabled", Parameters(upsert));

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

    [Theory]
    [InlineData("m365extensions", "M365Extensions")]
    [InlineData("outlook", "OutlookChannel")]
    public async Task CreatePropertylessChannel_PersistsChannel(string channel, string armName)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort();
        var service = new DeploymentChannelService(store, channels);

        var result = await service.CreateChannelAsync(CreateRequest(channel), default);

        var upsert = Assert.Single(channels.Upserts);
        Assert.Equal(armName, upsert.ChannelArmName);
        Assert.Empty(upsert.Parameters);
        var persisted = Assert.Single(Assert.Single(store.Records).Channels);
        Assert.Equal(channel, persisted.ChannelName);
        Assert.Empty(persisted.Parameters);
        Assert.Equal(DeploymentOperationStatus.Created, result.Status);
    }

    [Theory]
    [InlineData("m365extensions")]
    [InlineData("outlook")]
    public async Task ShowPropertylessChannel_RejectsBeforeLoadingDeployment(string channel)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort { IsChannelExisting = true };
        var service = new DeploymentChannelService(store, channels);

        var ex = await Assert.ThrowsAsync<DeploymentException>(() =>
            service.GetChannelAsync(ShowRequest(channel), default));

        Assert.Equal("channel-operation-not-supported", ex.ErrorKind);
        Assert.Equal(0, store.ListCalls);
        Assert.Empty(channels.Gets);
        Assert.Empty(channels.Upserts);
    }

    [Theory]
    [InlineData("m365extensions", "isEnabled=true")]
    [InlineData("outlook", null)]
    [InlineData("outlook", "isEnabled=false")]
    public async Task UpdatePropertylessChannel_RejectsBeforeLoadingDeployment(string channel, string? parameter)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort();
        var service = new DeploymentChannelService(store, channels);

        var ex = await Assert.ThrowsAsync<DeploymentException>(() =>
            service.UpdateChannelAsync(
                UpdateRequest(channel, parameter is null ? [] : [parameter]),
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
        Assert.Equal($"The '{channel}' channel does not support 'delete' operation.", ex.Message);
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
                "acceptedTerms=true",
                "enableCalling=true",
                "callingWebhook=https://calling.example",
                "deploymentEnvironment=CommercialDeployment",
                "incomingCallRoute=https://route.example"),
            default);

        var properties = Parameters(Assert.Single(channels.Upserts));
        Assert.DoesNotContain("isEnabled", properties);
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
        // Check the whole record for leaked credentials.
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

    [Theory]
    [InlineData("lines")]
    [MemberData(nameof(DeploymentChannelCatalogTests.UnsupportedChannelNames), MemberType = typeof(DeploymentChannelCatalogTests))]
    public async Task Crud_UnsupportedChannel_RejectsBeforeLoadingDeployment(string channel)
    {
        var store = new FakeStore(ProvisionedRecord());
        var transport = new RecordingChannelPort();
        var service = new DeploymentChannelService(store, transport);
        Func<Task>[] actions =
        [
            () => service.CreateChannelAsync(CreateRequest(channel), default),
            () => service.GetChannelAsync(ShowRequest(channel), default),
            () => service.UpdateChannelAsync(UpdateRequest(channel, "isEnabled=true"), default),
            () => service.DeleteChannelAsync(DeleteRequest(channel), default),
        ];

        foreach (var action in actions)
        {
            var ex = await Assert.ThrowsAsync<UsageException>(action);
            Assert.Contains($"Channel '{channel}' is not supported.", ex.Message);
            Assert.DoesNotContain("--channel is not supported", ex.Message);
        }

        Assert.Equal(0, store.ListCalls);
        Assert.Empty(transport.Gets);
        Assert.Empty(transport.Upserts);
        Assert.Empty(transport.Deletes);
        Assert.Empty(transport.Lists);
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
        Assert.DoesNotContain("isEnabled", parameters);
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
    public async Task CreateSlack_DefaultsScopesAndOmitsOtherUnspecifiedOptionalProperties()
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort();
        var service = new DeploymentChannelService(store, channels);

        await service.CreateChannelAsync(
            CreateRequest("slack", "clientId=cid", "clientSecret=secret", "verificationToken=vtok"),
            default);

        var upsert = Assert.Single(channels.Upserts);
        var parameters = Parameters(upsert);
        Assert.Contains("channels:history", parameters["scopes"].GetString());
        Assert.False(parameters.ContainsKey("landingPageUrl"));
        Assert.False(parameters.ContainsKey("signingSecret"));
        Assert.DoesNotContain("isEnabled", parameters);
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
    public async Task SiteMutations_RejectInvalidInputBeforeStoreOrArmAccess(string channel)
    {
        const string value = "SYNTHETIC_SITE_VALUE";
        DeploymentChannelCreateRequest[] requests =
        [
            CreateRequest(channel),
            CreateRequest(channel, $$$$"""sites=[{"siteName":"Primary","key":"{{{{value}}}}"}]"""),
            CreateRequest(channel, $$$$"""sites=[{"siteName":"Primary","isEnabled":"{{{{value}}}}"}]"""),
            UpdateRequest(channel),
            UpdateRequest(channel, "sites=[]") with { AddSite = [$"siteName={value}"] },
            UpdateRequest(channel) with { AddSite = [$"siteName={value}"], RemoveSite = ["siteName=Primary"] },
            UpdateRequest(channel) with { SetSite = ["siteName=Primary", $"key={value}"] },
            UpdateRequest(channel) with { RemoveSite = ["siteName=Primary", $"appId={value}"] },
            UpdateRequest(channel) with { AddSite = ["siteName=Primary", $"siteId={value}"] },
            UpdateRequest(channel) with { SetSite = [$"siteId={value}", "isEnabled=false"] },
            UpdateRequest(channel) with { RemoveSite = [$"siteId={value}"] },
            UpdateRequest(channel, $$$$"""sites=[{"siteName":"Primary","siteId":"{{{{value}}}}"}]"""),
            UpdateRequest(channel, $$$$"""sites=[{"siteId":"{{{{value}}}}","isEnabled":false}]"""),
        ];
        foreach (var request in requests)
        {
            var store = new FakeStore(ProvisionedRecord());
            var channels = new RecordingChannelPort { IsChannelExisting = true };
            var service = new DeploymentChannelService(store, channels);

            var error = await Assert.ThrowsAsync<UsageException>(() =>
                request is DeploymentChannelUpdateRequest update
                    ? service.UpdateChannelAsync(update, default)
                    : service.CreateChannelAsync(request, default));

            Assert.Equal(ExitCodes.Usage, error.ExitCode);
            Assert.DoesNotContain(value, error.Message);
            Assert.Equal(0, store.ListCalls);
            Assert.Equal(0, store.UpsertCalls);
            Assert.Empty(channels.Operations);
            Assert.Empty(channels.Upserts);
        }
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public async Task SiteUpdate_UsesFreshGetAndPatchAndStoresWriteResponseWithoutReadback(string channel)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = JsonDocument.Parse("""
                {"properties":{"properties":{"sites":[
                  {"siteName":"Primary","siteId":"id-1","isTokenEnabled":true,"isEnabled":true,"key":"***"},
                  {"siteName":"Secondary","siteId":"id-2","isTokenEnabled":false,"isEnabled":true,"key2":"***"}
                ]}}}
                """).RootElement.Clone(),
            UpsertRawArmResponse = JsonDocument.Parse("""
                {"properties":{"properties":{"sites":[{"siteName":"ServiceResponse","siteId":"returned-id"}]}}}
                """).RootElement.Clone(),
        };
        var service = new DeploymentChannelService(store, channels);
        var result = await service.UpdateChannelAsync(
            UpdateRequest(channel) with { SetSite = ["siteName=PRIMARY", "isEnabled=false"] }, default);

        Assert.Equal(["get", "update"], channels.Operations);
        var sites = Assert.Single(channels.Upserts).Parameters["sites"];
        Assert.Equal(2, sites.GetArrayLength());
        Assert.False(sites[0].GetProperty("isEnabled").GetBoolean());
        Assert.True(sites[0].GetProperty("isTokenEnabled").GetBoolean());
        Assert.Equal("id-2", sites[1].GetProperty("siteId").GetString());
        Assert.True(sites[1].GetProperty("isEnabled").GetBoolean());
        Assert.DoesNotContain("***", sites.GetRawText());
        Assert.Equal("ServiceResponse", result.Payload!.Parameters["sites"][0].GetProperty("siteName").GetString());
        var saved = Assert.Single(Assert.Single(store.Records).Channels);
        Assert.Equal(result.Payload.Parameters["sites"].GetRawText(), saved.Parameters["sites"].GetRawText());
        Assert.Null(saved.RawArmResponse);
        Assert.Equal(1, store.UpsertCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SiteMutation_FirstWriteFailureDoesNotConfigureOrPersist(bool create)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = !create,
            GetRawArmResponse = SiteResponse("""{"siteName":"Existing","siteId":"old-id","isEnabled":true}"""),
        };
        var failure = new HttpRequestException("First PATCH failed.");
        channels.WriteErrors.Add(1, failure);
        var service = new DeploymentChannelService(store, channels);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => create
            ? service.CreateChannelAsync(CreateRequest("webchat", """sites=[{"siteName":"Primary","isEnabled":false}]"""), default)
            : service.UpdateChannelAsync(
                UpdateRequest("webchat") with { AddSite = ["siteName=Primary", "isEnabled=false"] }, default));

        Assert.Same(failure, error);
        Assert.Equal(["get", create ? "create" : "update"], channels.Operations);
        Assert.Equal(0, store.UpsertCalls);
    }

    [Theory]
    [InlineData("directline", false)]
    [InlineData("directline", true)]
    [InlineData("webchat", false)]
    [InlineData("webchat", true)]
    public async Task SiteUpdate_AddsAndConfiguresNewSitesWithTwoPatchesAndNoReadback(string channel, bool replace)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = SiteResponse("""
                {"siteName":"Existing","siteId":"old-id","isEnabled":true,"isTokenEnabled":true,"appId":"old-app"},
                {"siteName":"Retained","siteId":"retained-id","isEnabled":false,"isTokenEnabled":true}
                """),
        };
        const string primary = """
            {"siteName":"PRIMARY","siteId":"new-id","isEnabled":true,"isTokenEnabled":false,
             "isBlockUserUploadEnabled":false,"isWebchatPreviewEnabled":true,"trustedOrigins":null,
             "key":"***","key2":"***","eTag":"server-tag","unknown":"server-value"}
            """;
        const string secondary = """
            {"siteName":"Secondary","siteId":"new-id-2","isEnabled":true,"isTokenEnabled":false,
             "isBlockUserUploadEnabled":false,"isWebchatPreviewEnabled":true,"trustedOrigins":null}
            """;
        var existing = $$"""
            {"siteName":"Existing","siteId":"old-id","isEnabled":{{(replace ? "false" : "true")}},
             "isTokenEnabled":true,"appId":"old-app","key":"***"}
            """;
        channels.WriteResponses.Enqueue(SiteResponse(primary + "," + existing + "," + (replace ? secondary : """
            {"siteName":"Retained","siteId":"retained-id","isEnabled":false,"isTokenEnabled":true}
            """)));
        var finalResponse = SiteResponse("""{"siteName":"FinalResponse","siteId":"final-id","isEnabled":null}""");
        channels.WriteResponses.Enqueue(finalResponse);
        var extras = channel == "webchat"
            ? ""","isWebchatPreviewEnabled":false"""
            : ""","trustedOrigins":["https://new-site.example"]""";
        var newSite = $$"""{"siteName":"Primary","appId":"app-1","tenantId":"tenant-1","isEnabled":false,"isBlockUserUploadEnabled":true{{extras}}}""";
        var request = replace
            ? UpdateRequest(channel, $$"""sites=[{"siteName":"Existing","isEnabled":false},{{newSite}},{{newSite.Replace("Primary", "Secondary")}}]""")
            : UpdateRequest(channel) with
            {
                AddSite = ["siteName=Primary", "appId=app-1", "tenantId=tenant-1", "isEnabled=false", "isBlockUserUploadEnabled=true",
                    channel == "webchat" ? "isWebchatPreviewEnabled=false" : "trustedOrigins=https://new-site.example"],
            };
        var service = new DeploymentChannelService(store, channels);

        var result = await service.UpdateChannelAsync(request, default);

        Assert.Equal(["get", "update", "update"], channels.Operations);
        Assert.Equal(2, channels.Upserts.Count);
        var first = channels.Upserts[0].Parameters["sites"];
        foreach (var site in first.EnumerateArray().Where(site => !site.TryGetProperty("siteId", out _)))
        {
            Assert.Equal("siteName", Assert.Single(site.EnumerateObject()).Name);
        }
        var configured = channels.Upserts[1].Parameters["sites"];
        Assert.Equal(3, configured.GetArrayLength());
        foreach (var site in configured.EnumerateArray().Where(site => site.GetProperty("siteName").GetString() is "PRIMARY" or "Secondary"))
        {
            Assert.StartsWith("new-id", site.GetProperty("siteId").GetString());
            Assert.False(site.GetProperty("isEnabled").GetBoolean());
            Assert.True(site.GetProperty("isBlockUserUploadEnabled").GetBoolean());
            Assert.Equal("app-1", site.GetProperty("appId").GetString());
            Assert.Equal("tenant-1", site.GetProperty("tenantId").GetString());
            Assert.False(site.GetProperty("isTokenEnabled").GetBoolean());
            if (channel == "webchat")
            {
                Assert.False(site.GetProperty("isWebchatPreviewEnabled").GetBoolean());
            }
            else
            {
                Assert.Equal("https://new-site.example", Assert.Single(site.GetProperty("trustedOrigins").EnumerateArray()).GetString());
            }
        }

        var preserved = configured.EnumerateArray().Single(site => site.GetProperty("siteId").GetString() == "old-id");
        Assert.True(preserved.GetProperty("isTokenEnabled").GetBoolean());
        Assert.Equal(!replace, preserved.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("old-app", preserved.GetProperty("appId").GetString());
        Assert.Equal(!replace, first.EnumerateArray().Any(site => site.GetProperty("siteName").GetString() == "Retained"));
        Assert.Equal(!replace, configured.EnumerateArray().Any(site => site.GetProperty("siteName").GetString() == "Retained"));
        Assert.DoesNotContain("***", configured.GetRawText());
        Assert.DoesNotContain("eTag", configured.GetRawText());
        Assert.DoesNotContain("unknown", configured.GetRawText());
        Assert.Equal("FinalResponse", result.Payload!.Parameters["sites"][0].GetProperty("siteName").GetString());
        Assert.Equal(JsonValueKind.Null, result.Payload.Parameters["sites"][0].GetProperty("isEnabled").ValueKind);
        var saved = Assert.Single(Assert.Single(store.Records).Channels);
        Assert.Equal(result.Payload.Parameters["sites"].GetRawText(), saved.Parameters["sites"].GetRawText());
        Assert.Null(saved.RawArmResponse);
        Assert.Equal(1, store.UpsertCalls);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("directline", false)]
    [InlineData("directline", true)]
    [InlineData("webchat", false)]
    [InlineData("webchat", true)]
    public async Task SiteUpdate_NameOnlyNewSiteUsesOnePatch(string channel, bool replace)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = SiteResponse("""{"siteName":"Existing","siteId":"old-id","isEnabled":true}"""),
        };
        var request = replace
            ? UpdateRequest(channel, """sites=[{"siteName":"Primary"}]""")
            : UpdateRequest(channel) with { AddSite = ["siteName=Primary"] };
        var service = new DeploymentChannelService(store, channels);

        await service.UpdateChannelAsync(request, default);

        Assert.Equal(["get", "update"], channels.Operations);
        Assert.Single(channels.Upserts);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("cancelled")]
    [InlineData("missing-id")]
    [InlineData("missing-site")]
    [InlineData("changed-id")]
    [InlineData("unexpected-site")]
    [InlineData("duplicate-name")]
    [InlineData("duplicate-id")]
    public async Task SiteUpdate_ConfigurationFailureReturnsSuccessWithWarningAndPersistsFirstResponse(string failure)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = SiteResponse("""{"siteName":"Existing","siteId":"old-id","isEnabled":true}"""),
        };
        var firstResponse = failure switch
        {
            "missing-id" => SiteResponse("""
                {"siteName":"Primary","isEnabled":true},{"siteName":"Existing","siteId":"old-id"}
                """),
            "missing-site" => SiteResponse("""{"siteName":"Primary","siteId":"new-id","isEnabled":true}"""),
            "changed-id" => SiteResponse("""
                {"siteName":"Primary","siteId":"new-id","isEnabled":true},{"siteName":"Existing","siteId":"wrong-id"}
                """),
            "unexpected-site" => SiteResponse("""
                {"siteName":"Primary","siteId":"new-id"},{"siteName":"Unexpected","siteId":"old-id"}
                """),
            "duplicate-name" => SiteResponse("""
                {"siteName":"Primary","siteId":"new-id"},{"siteName":"PRIMARY","siteId":"old-id"}
                """),
            "duplicate-id" => SiteResponse("""
                {"siteName":"Primary","siteId":"new-id"},{"siteName":"Existing","siteId":"NEW-ID"}
                """),
            _ => SiteResponse("""
                {"siteName":"Primary","siteId":"new-id","isEnabled":true},{"siteName":"Existing","siteId":"old-id"}
                """),
        };
        channels.WriteResponses.Enqueue(firstResponse);
        if (failure is "provider" or "cancelled")
        {
            channels.WriteErrors.Add(2, failure == "provider"
                ? new HttpRequestException("Second PATCH failed.") : new OperationCanceledException());
        }
        var service = new DeploymentChannelService(store, channels);

        var result = await service.UpdateChannelAsync(
            UpdateRequest("webchat") with { AddSite = ["siteName=Primary", "isEnabled=false"] }, default);

        Assert.Equal(DeploymentOperationStatus.Updated, result.Status);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("initial write", warning);
        Assert.Contains("service defaults", warning);
        Assert.Contains("no rollback", warning);
        if (failure is not ("provider" or "cancelled"))
        {
            Assert.Contains("The channel response does not contain a usable configuration.", warning);
        }

        Assert.Equal(failure is "provider" or "cancelled" ? ["get", "update", "update"] : ["get", "update"], channels.Operations);
        var saved = Assert.Single(Assert.Single(store.Records).Channels);
        Assert.Equal(firstResponse.GetProperty("properties").GetProperty("properties").GetProperty("sites").GetRawText(),
            saved.Parameters["sites"].GetRawText());
        Assert.Equal(result.Payload!.Parameters["sites"].GetRawText(), saved.Parameters["sites"].GetRawText());
        Assert.Null(saved.RawArmResponse);
        Assert.Equal(1, store.UpsertCalls);
    }

    [Theory]
    [InlineData("directline", false)]
    [InlineData("directline", true)]
    [InlineData("webchat", false)]
    [InlineData("webchat", true)]
    public async Task SiteCreate_AcceptsUnifiedSettingsAndConfiguresAfterNameOnlyPut(string channel, bool settings)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort();
        var firstResponse = SiteResponse("""
            {"siteName":"Secondary","siteId":"id-2","isEnabled":true,"isTokenEnabled":true,"isV1Enabled":true},
            {"siteName":"PRIMARY","siteId":"id-1","isEnabled":true,"isTokenEnabled":true,"isV1Enabled":true,
             "appId":null,"tenantId":null,"isWebchatPreviewEnabled":true,"key":"***","eTag":"server-tag","unknown":true}
            """);
        channels.WriteResponses.Enqueue(firstResponse);
        var finalResponse = SiteResponse("""{"siteName":"FinalResponse","siteId":"final-id","isEnabled":null}""");
        channels.WriteResponses.Enqueue(finalResponse);
        var extras = channel == "webchat"
            ? ""","isWebchatPreviewEnabled":false"""
            : ""","isSecureSiteEnabled":true,"isV3Enabled":false,"trustedOrigins":["https://site.test"]""";
        var values = settings
            ? ""","appId":"app-1","tenantId":"tenant-1","isEnabled":false,"isBlockUserUploadEnabled":true,"isEndpointParametersEnabled":true,"isDetailedLoggingEnabled":true,"isNoStorageEnabled":false""" + extras
            : "";
        var request = CreateRequest(channel, $$"""sites=[{"siteName":"Primary"{{values}}},{"siteName":"Secondary"{{values}}}]""");
        var service = new DeploymentChannelService(store, channels);

        var result = await service.CreateChannelAsync(request, default);

        Assert.Equal(DeploymentOperationStatus.Created, result.Status);
        Assert.Empty(result.Warnings);
        Assert.Equal(settings ? ["get", "create", "update"] : ["get", "create"], channels.Operations);
        var initial = channels.Upserts[0].Parameters["sites"];
        Assert.Equal(2, initial.GetArrayLength());
        foreach (var site in initial.EnumerateArray())
        {
            Assert.Equal("siteName", Assert.Single(site.EnumerateObject()).Name);
        }

        if (settings)
        {
            var configured = channels.Upserts[1].Parameters["sites"];
            Assert.Equal(2, configured.GetArrayLength());
            foreach (var site in configured.EnumerateArray())
            {
                Assert.StartsWith("id-", site.GetProperty("siteId").GetString());
                Assert.False(site.GetProperty("isEnabled").GetBoolean());
                Assert.True(site.GetProperty("isTokenEnabled").GetBoolean());
                Assert.True(site.GetProperty("isBlockUserUploadEnabled").GetBoolean());
                Assert.True(site.GetProperty("isEndpointParametersEnabled").GetBoolean());
                Assert.True(site.GetProperty("isDetailedLoggingEnabled").GetBoolean());
                Assert.False(site.GetProperty("isNoStorageEnabled").GetBoolean());
                Assert.Equal("app-1", site.GetProperty("appId").GetString());
                Assert.Equal("tenant-1", site.GetProperty("tenantId").GetString());
                if (channel == "webchat")
                {
                    Assert.False(site.GetProperty("isWebchatPreviewEnabled").GetBoolean());
                }
                else
                {
                    Assert.True(site.GetProperty("isV1Enabled").GetBoolean());
                    Assert.True(site.GetProperty("isSecureSiteEnabled").GetBoolean());
                    Assert.False(site.GetProperty("isV3Enabled").GetBoolean());
                    Assert.Equal("https://site.test", Assert.Single(site.GetProperty("trustedOrigins").EnumerateArray()).GetString());
                }
            }

            Assert.DoesNotContain("***", configured.GetRawText());
            Assert.DoesNotContain("eTag", configured.GetRawText());
            Assert.DoesNotContain("unknown", configured.GetRawText());
        }

        Assert.Equal(settings ? "FinalResponse" : "Secondary",
            result.Payload!.Parameters["sites"][0].GetProperty("siteName").GetString());
        var saved = Assert.Single(Assert.Single(store.Records).Channels);
        Assert.Equal(result.Payload.Parameters["sites"].GetRawText(), saved.Parameters["sites"].GetRawText());
        Assert.Null(saved.RawArmResponse);
        Assert.Equal(1, store.UpsertCalls);
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public async Task SiteCreate_ExistingChannelReturnsItsParametersWithoutApplyingRequestedSites(string channel)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = SiteResponse("""{"siteName":"Existing","siteId":"id-1","isEnabled":true}"""),
        };
        var service = new DeploymentChannelService(store, channels);

        var result = await service.CreateChannelAsync(CreateRequest(channel,
            """sites=[{"siteName":"Primary","isEnabled":false},{"siteName":"Secondary","appId":"app-1"}]"""), default);

        Assert.Equal(DeploymentOperationStatus.Existed, result.Status);
        Assert.Equal(["get"], channels.Operations);
        Assert.Empty(channels.Upserts);
        Assert.Empty(result.Warnings);
        Assert.Equal("Existing", Assert.Single(result.Payload!.Parameters["sites"].EnumerateArray()).GetProperty("siteName").GetString());
        Assert.True(result.Payload.Parameters["sites"][0].GetProperty("isEnabled").GetBoolean());
        Assert.Equal(1, store.UpsertCalls);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("cancelled")]
    [InlineData("missing-id")]
    [InlineData("missing-site")]
    [InlineData("invalid-setting")]
    public async Task SiteCreate_ConfigurationFailureReturnsCreatedWithWarningAndInitialResponse(string failure)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort();
        var firstResponse = failure switch
        {
            "missing-id" => SiteResponse("""{"siteName":"Primary","isEnabled":true}"""),
            "missing-site" => SiteResponse(""),
            "invalid-setting" => SiteResponse("""{"siteName":"Primary","siteId":"id-1","isEnabled":"invalid"}"""),
            _ => SiteResponse("""{"siteName":"Primary","siteId":"id-1","isEnabled":true}"""),
        };
        channels.WriteResponses.Enqueue(firstResponse);
        if (failure is "provider" or "cancelled")
        {
            channels.WriteErrors.Add(2, failure == "provider"
                ? new HttpRequestException("Configuration PATCH failed.") : new OperationCanceledException());
        }
        var service = new DeploymentChannelService(store, channels);

        var result = await service.CreateChannelAsync(CreateRequest("webchat",
            """sites=[{"siteName":"Primary","appId":"app-1"}]"""), default);

        Assert.Equal(DeploymentOperationStatus.Created, result.Status);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("initial write", warning);
        Assert.Contains("service defaults", warning);
        if (failure is not ("provider" or "cancelled"))
        {
            Assert.Contains("The channel response does not contain a usable configuration.", warning);
        }

        Assert.Equal(failure is "provider" or "cancelled" ? ["get", "create", "update"] : ["get", "create"], channels.Operations);
        var saved = Assert.Single(Assert.Single(store.Records).Channels);
        Assert.Equal(firstResponse.GetProperty("properties").GetProperty("properties").GetProperty("sites").GetRawText(),
            saved.Parameters["sites"].GetRawText());
        Assert.Equal(result.Payload!.Parameters["sites"].GetRawText(), saved.Parameters["sites"].GetRawText());
        Assert.Equal(1, store.UpsertCalls);
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public async Task SiteUpdate_EmptyAssignmentWritesEmptyArrayAndPersistsReadback(string channel)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = SiteResponse("""{"siteName":"Primary","siteId":"id-1","appId":"app-1"}"""),
            UpsertRawArmResponse = SiteResponse(""),
        };
        var service = new DeploymentChannelService(store, channels);

        var result = await service.UpdateChannelAsync(UpdateRequest(channel, "sites="), default);

        Assert.Equal(["get", "update"], channels.Operations);
        Assert.Empty(Assert.Single(channels.Upserts).Parameters["sites"].EnumerateArray());
        Assert.Empty(result.Payload!.Parameters["sites"].EnumerateArray());
        Assert.Empty(Assert.Single(Assert.Single(store.Records).Channels).Parameters["sites"].EnumerateArray());
        Assert.Equal(1, store.UpsertCalls);
    }

    private static JsonElement SiteResponse(string sites) => JsonDocument.Parse(
        "{\"properties\":{\"properties\":{\"sites\":[" + sites + "]}}}").RootElement.Clone();

    [Fact]
    public async Task ScalarMutations_RejectSiteOperationsBeforeStoreOrArmAccess()
    {
        (DeploymentChannelUpdateRequest Request, string OptionName)[] requests =
        [
            (UpdateRequest("msteams", "enableCalling=true") with { AddSite = ["siteName=Primary"] }, "--add-site"),
            (UpdateRequest("msteams", "enableCalling=true") with { SetSite = ["siteName=Primary", "isEnabled=false"] }, "--set-site"),
            (UpdateRequest("msteams", "enableCalling=true") with { RemoveSite = ["siteName=Primary"] }, "--remove-site"),
        ];
        foreach (var (request, optionName) in requests)
        {
            var store = new FakeStore(ProvisionedRecord());
            var channels = new RecordingChannelPort();
            var service = new DeploymentChannelService(store, channels);

            var error = await Assert.ThrowsAsync<UsageException>(() =>
                service.UpdateChannelAsync(request, default));

            Assert.Equal($"Option '{optionName}' is not valid for 'msteams' channel update.", error.Message);
            Assert.Equal(0, store.ListCalls);
            Assert.Equal(0, store.UpsertCalls);
            Assert.Empty(channels.Operations);
        }
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public async Task Show_SiteChannelsStillRetainAllSitesAndSanitizedResponse(string channel)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort
        {
            IsChannelExisting = true,
            GetRawArmResponse = BotServiceResponseRedactor.RedactChannel(JsonDocument.Parse(
                """
                {"properties":{"properties":{"sites":[
                  {"siteName":"Primary","siteId":"id-1","key":"SYNTHETIC_KEY"},
                  {"siteName":"Secondary","siteId":"id-2","key2":"SYNTHETIC_KEY"}
                ]}}}
                """).RootElement.Clone(), DeploymentChannelCatalog.FindChannelDefinition(channel, ChannelKeyType.Name)),
        };
        var service = new DeploymentChannelService(store, channels);

        var result = await service.GetChannelAsync(ShowRequest(channel), default);

        Assert.Equal(2, result.Payload!.Parameters["sites"].GetArrayLength());
        var saved = Assert.Single(Assert.Single(store.Records).Channels);
        Assert.Equal("id-1", saved.Parameters["sites"][0].GetProperty("siteId").GetString());
        Assert.Equal("id-2", saved.Parameters["sites"][1].GetProperty("siteId").GetString());
        Assert.Equal("***", saved.Parameters["sites"][0].GetProperty("key").GetString());
        Assert.Equal("***", saved.Parameters["sites"][1].GetProperty("key2").GetString());
        Assert.Null(saved.RawArmResponse);
        Assert.Empty(channels.Upserts);
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
        Assert.Equal(["get", "update"], channels.Operations);
        // Existing property preserved, provided property overlaid.
        var parameters = Parameters(upsert);
        Assert.True(parameters["isEnabled"].GetBoolean());
        Assert.True(parameters["enableCalling"].GetBoolean());
        Assert.Equal("https://old.example", parameters["callingWebhook"].GetString());
    }

    [Fact]
    public async Task UpdateSlack_ReturnsWriteResponseWithoutPollingAndShowReadsFreshState()
    {
        var original = SlackSnapshot("https://old.example");
        var channels = new RecordingChannelPort { IsChannelExisting = true };
        channels.ReadResponses.Enqueue(original);
        channels.ReadResponses.Enqueue(SlackSnapshot("https://eventual.example"));
        var store = new FakeStore(ProvisionedRecord());
        var service = new DeploymentChannelService(store, channels);

        var result = await service.UpdateChannelAsync(UpdateRequest("slack", "landingPageUrl=https://new.example"), default);

        Assert.Equal(["get", "update"], channels.Operations);
        Assert.Equal(DeploymentOperationStatus.WriteAccepted, result.Status);
        Assert.Contains("does not verify authorization completion", Assert.Single(result.Warnings));
        Assert.Equal("https://new.example", result.Payload!.Parameters["landingPageUrl"].GetString());
        Assert.Equal("https://new.example", Assert.Single(Assert.Single(store.Records).Channels).Parameters["landingPageUrl"].GetString());

        var shown = await service.GetChannelAsync(ShowRequest("slack"), default);
        Assert.Equal(["get", "update", "get"], channels.Operations);
        Assert.Equal(DeploymentOperationStatus.Retrieved, shown.Status);
        Assert.Empty(shown.Warnings);
        Assert.Equal("https://eventual.example", shown.Payload!.Parameters["landingPageUrl"].GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateSlack_ReturnsTransientAuthorizationWithSingleGuidanceAndDefaultScopes(bool nonInteractive)
    {
        var authorizationUrl = new Uri("https://slack.com/oauth/v2/authorize?state=SYNTHETIC_STATE");
        var channels = new RecordingChannelPort { WriteAuthorizationUrl = authorizationUrl };
        var store = new FakeStore(ProvisionedRecord());
        var browser = new RecordingBrowser();
        var service = new DeploymentChannelService(store, channels, browser);

        var result = await service.CreateChannelAsync(
            CreateRequest("slack", "clientId=app", "clientSecret=secret", "signingSecret=sign") with { NonInteractive = nonInteractive }, default);

        Assert.Equal(["get", "create"], channels.Operations);
        Assert.Equal(DeploymentOperationStatus.WriteAccepted, result.Status);
        Assert.Equal(authorizationUrl, result.AuthorizationUrl);
        Assert.Equal(DeploymentChannelSlack.AuthorizationGuidance, Assert.Single(result.Warnings));
        Assert.Contains("channels:history", Assert.Single(channels.Upserts).Parameters["scopes"].GetString());
        Assert.Equal(nonInteractive ? [] : new[] { authorizationUrl }, browser.Urls);
        var saved = Assert.Single(Assert.Single(store.Records).Channels);
        Assert.Null(saved.AuthorizationUrl);
        Assert.Null(saved.RawArmResponse);
        Assert.DoesNotContain("SYNTHETIC_STATE", JsonSerializer.Serialize(saved));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateSlack_OpensAuthorizationUnlessExplicitlyNonInteractive(bool nonInteractive)
    {
        var uri = new Uri("https://slack.com/oauth/v2/authorize?state=SYNTHETIC_STATE");
        var channels = new RecordingChannelPort { IsChannelExisting = true, WriteAuthorizationUrl = uri };
        var browser = new RecordingBrowser();
        var service = new DeploymentChannelService(new FakeStore(ProvisionedRecord()), channels, browser);

        var result = await service.UpdateChannelAsync(
            UpdateRequest("slack", "scopes=chat:write") with { NonInteractive = nonInteractive }, default);

        Assert.Equal(["get", "update"], channels.Operations);
        Assert.Contains("does not verify authorization completion", Assert.Single(result.Warnings));
        Assert.Equal(uri, result.AuthorizationUrl);
        Assert.Equal(nonInteractive ? [] : new[] { uri }, browser.Urls);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public async Task Slack_BrowserFailurePreservesWriteAndReportsGuidanceWithoutEchoingTheError(string verb)
    {
        var uri = new Uri("https://slack.com/oauth/v2/authorize?state=SYNTHETIC_STATE");
        var channels = new RecordingChannelPort { WriteAuthorizationUrl = uri, IsChannelExisting = verb == "update" };
        var store = new FakeStore(ProvisionedRecord());
        var browser = new RecordingBrowser { Error = new Win32Exception(uri.AbsoluteUri) };
        var service = new DeploymentChannelService(store, channels, browser);

        var result = await (verb == "create"
            ? service.CreateChannelAsync(
                CreateRequest("slack", "clientId=app", "clientSecret=secret", "verificationToken=token", "scopes=chat:write"), default)
            : service.UpdateChannelAsync(UpdateRequest("slack", "scopes=chat:write"), default));

        Assert.Equal(uri, result.AuthorizationUrl);
        Assert.Equal(1, store.UpsertCalls);
        Assert.Equal(DeploymentOperationStatus.WriteAccepted, result.Status);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("browser could not be opened", warning);
        Assert.Contains(DeploymentChannelSlack.AuthorizationGuidance, warning);
        Assert.DoesNotContain("SYNTHETIC_STATE", warning);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public async Task SlackCli_CreateJsonThenPartialUpdateDebugKeepsAuthorizationActionAndDoesNotPoll(
        bool nonInteractive, bool browserAvailable, bool browserFails)
    {
        var uri = new Uri("https://slack.com/oauth/v2/authorize?state=SYNTHETIC_STATE");
        var channels = new RecordingChannelPort { WriteAuthorizationUrl = uri };
        var browser = new RecordingBrowser { Error = browserFails ? new Win32Exception(uri.AbsoluteUri) : null };
        var service = new DeploymentChannelService(new FakeStore(ProvisionedRecord()), channels, browserAvailable ? browser : null);
        var operations = new FakeOperations
        {
            OnCreateChannel = request => service.CreateChannelAsync(request, default).GetAwaiter().GetResult(),
            OnUpdateChannel = request => service.UpdateChannelAsync(request, default).GetAwaiter().GetResult(),
        };
        var cli = new Microsoft.Agents.Hosting.Cli.Hosting.AgentHostCli(operations);
        string[] options = nonInteractive ? ["--non-interactive"] : [];
        var created = await Invoke("create",
            ["--json", "--parameters", "clientId=app", "clientsecret=secret", "verificationToken=token"]);
        using var document = JsonDocument.Parse(created.Stdout);
        Assert.Equal(uri.AbsoluteUri, document.RootElement.GetProperty("authorizationUrl").GetString());
        const string instruction = DeploymentChannelSlack.AuthorizationGuidance;
        Assert.False(document.RootElement.TryGetProperty("nextAction", out _));
        Assert.Contains(instruction, Assert.Single(document.RootElement.GetProperty("warnings").EnumerateArray()).GetString());
        Assert.DoesNotContain("SYNTHETIC_STATE", document.RootElement.GetProperty("warnings").GetRawText());
        Assert.Empty(created.Stderr);

        channels.ReadResponses.Enqueue(SlackSnapshot("https://old.example"));
        channels.UpsertRawArmResponse = JsonSerializer.SerializeToElement(new
        {
            properties = new { redirectAction = "***", scopes = "chat:write" },
        });
        var updated = await Invoke("update",
            ["--debug", "--parameters", "landingPageUrl=https://new.example", "scopes=chat:write"]);

        Assert.Equal(["get", "create", "get", "update"], channels.Operations);
        Assert.Equal(nonInteractive || !browserAvailable ? 0 : 2, browser.Urls.Count);
        Assert.Contains("write accepted by Azure", updated.Stdout);
        Assert.Contains(instruction, updated.Stdout);
        Assert.Single(updated.Stdout.Split('\n'), line => line.StartsWith("Warning:", StringComparison.Ordinal));
        Assert.DoesNotContain("Next:", updated.Stdout);
        Assert.Contains("https://new.example", updated.Stdout);
        Assert.Contains("chat:write", updated.Stdout);
        Assert.Contains(uri.AbsoluteUri, updated.Stdout);
        Assert.DoesNotContain("registerBeforeOAuthFlow", updated.Stdout);
        if (browserFails)
            Assert.Contains("browser could not be opened", updated.Stdout);
        else
            Assert.DoesNotContain("browser could not be opened", updated.Stdout);
        Assert.Contains("does not verify authorization completion", updated.Stdout);
        Assert.Contains("ARM response:", updated.Stderr);
        Assert.DoesNotContain("SYNTHETIC_STATE", updated.Stderr);

        async Task<(string Stdout, string Stderr)> Invoke(string verb, string[] arguments)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await cli.InvokeAsync(
                ["deployment", "channel", verb, "--deployment-id", DepId, "--channel", "slack", .. arguments, .. options],
                stdout, stderr, color: false);
            Assert.Equal(ExitCodes.Success, exitCode);
            return (stdout.ToString(), stderr.ToString());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Slack_WriteGuidanceIsTheSameForValidMissingAndUnusableAuthorizationLinks(bool update, bool nonInteractive)
    {
        var authorizationUrl = new Uri("https://slack.com/oauth/v2/authorize?state=SYNTHETIC_STATE");
        string? guidance = null;
        foreach (var action in new object?[] { null, "", new { state = "SYNTHETIC_STATE" }, authorizationUrl.AbsoluteUri })
        {
            var properties = new Dictionary<string, object?>
            {
                ["clientId"] = "app",
                ["scopes"] = "chat:write",
                ["isEnabled"] = true,
            };
            if (action is not null)
            {
                properties["redirectAction"] = action;
            }
            var response = JsonSerializer.Serialize(new
            {
                id = "/channels/SlackChannel",
                properties = new { channelName = "SlackChannel", properties },
            });
            var handler = new StubHttpMessageHandler(request =>
                new HttpResponseMessage(request.Method == HttpMethod.Get && !update ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                {
                    Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json"),
                });
            using var httpClient = new HttpClient(handler);
            var channels = new DeploymentChannelAdapter(httpClient, new StubTokenProvider { AccessToken = "arm-token", TenantId = "home" });
            var store = new FakeStore(ProvisionedRecord());
            var browser = new RecordingBrowser();
            var service = new DeploymentChannelService(store, channels, browser);

            var result = await (update
                ? service.UpdateChannelAsync(UpdateRequest("slack", "scopes=chat:write") with { NonInteractive = nonInteractive }, default)
                : service.CreateChannelAsync(
                    CreateRequest("slack", "clientId=app", "clientSecret=secret", "signingSecret=sign", "scopes=chat:write")
                        with
                    { NonInteractive = nonInteractive }, default));

            Assert.Equal(DeploymentOperationStatus.WriteAccepted, result.Status);
            Assert.Equal(new[] { HttpMethod.Get, update ? HttpMethod.Patch : HttpMethod.Put }, handler.Requests.Select(request => request.Method));
            var warning = Assert.Single(result.Warnings);
            Assert.Equal(DeploymentChannelSlack.AuthorizationGuidance, warning);
            Assert.Contains("does not verify authorization completion", warning);
            guidance ??= warning;
            Assert.Equal(guidance, warning);
            Assert.DoesNotContain("SYNTHETIC_STATE", warning);
            var expectedUrl = (action as string) == authorizationUrl.AbsoluteUri ? authorizationUrl : null;
            Assert.Equal(expectedUrl, result.AuthorizationUrl);
            if (expectedUrl is null || nonInteractive)
            {
                Assert.Empty(browser.Urls);
            }
            else
            {
                Assert.Equal([expectedUrl], browser.Urls);
            }
            Assert.Equal(1, store.UpsertCalls);
            var saved = Assert.Single(Assert.Single(store.Records).Channels);
            Assert.Equal("chat:write", saved.Parameters["scopes"].GetString());
            Assert.Null(saved.AuthorizationUrl);
            Assert.Null(saved.RawArmResponse);
            Assert.DoesNotContain("SYNTHETIC_STATE", JsonSerializer.Serialize(result.Payload));
        }
    }

    [Theory]
    [InlineData("landingPageUrl=")]
    [InlineData("verificationToken=")]
    [InlineData("scopes=chat:write", "registerBeforeOAuthFlow=false")]
    [InlineData("IsValidated=false")]
    public async Task UpdateSlack_InvalidRequestsFailBeforeReadingOrWriting(params string[] assignments)
    {
        var store = new FakeStore(ProvisionedRecord());
        var channels = new RecordingChannelPort { IsChannelExisting = true };
        var service = new DeploymentChannelService(store, channels);
        await Assert.ThrowsAsync<UsageException>(() =>
            service.UpdateChannelAsync(UpdateRequest("slack", assignments), default));
        Assert.Empty(channels.Operations);
        Assert.Equal(0, store.UpsertCalls);
    }

    private static DeploymentChannelRecord SlackSnapshot(string landingPageUrl) => new()
    {
        ChannelName = "slack",
        ResourceId = ProvisionedRecord().Bot.ResourceId + "/channels/SlackChannel",
        Parameters = new Dictionary<string, JsonElement>
        {
            ["landingPageUrl"] = JsonSerializer.SerializeToElement(landingPageUrl),
            ["clientId"] = JsonSerializer.SerializeToElement("app"),
            ["registerBeforeOAuthFlow"] = JsonSerializer.SerializeToElement(false),
            ["IsValidated"] = JsonSerializer.SerializeToElement(false),
        },
    };

    private sealed class RecordingBrowser : IBrowserLauncher
    {
        public List<Uri> Urls { get; } = [];
        public Exception? Error { get; init; }
        public void Launch(Uri url)
        {
            Urls.Add(url);
            if (Error is { } error)
                throw error;
        }
    }

    [Fact]
    public async Task UpdateSlack_UsesFlatCodecToMergeNonClearableCatalogProperties()
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
            UpdateRequest("slack", "isEnabled=false", "landingPageUrl=https://new.example"),
            default);

        var properties = Parameters(Assert.Single(channels.Upserts));
        Assert.False(properties["isEnabled"].GetBoolean());
        Assert.Equal("https://new.example", properties["landingPageUrl"].GetString());
        Assert.True(properties["registerBeforeOAuthFlow"].GetBoolean());
    }

    [Fact]
    public async Task UpdateSlack_PreservesNonSecretsWithoutReplayingMaskedSecretsOrUnknownProperties()
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
        Assert.Equal("***", properties["scopes"].GetString());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task List_ReturnsAndPersistsOnlySupportedChannelsButPreservesFullArmResponse(bool includeSupported)
    {
        var store = new FakeStore(ProvisionedRecord());
        var unsupported = DeploymentChannelCatalog.Channels.First(channel => !channel.IsSupported);
        var channels = new RecordingChannelPort
        {
            ListChannels =
            [
                .. includeSupported
                    ? new[]
                    {
                        StoredChannel(ProvisionedRecord().Bot.ResourceId, "msteams", "MsTeamsChannel", true),
                        StoredChannel(ProvisionedRecord().Bot.ResourceId, "slack", "SlackChannel", true),
                    }
                    : Array.Empty<DeploymentChannelRecord>(),
                new()
                {
                    ResourceId = $"{ProvisionedRecord().Bot.ResourceId}/channels/{unsupported.ArmName}",
                    ChannelName = unsupported.Name,
                },
                new()
                {
                    ResourceId = $"{ProvisionedRecord().Bot.ResourceId}/channels/UnknownArmChannel",
                    ChannelName = "UnknownArmChannel",
                },
            ],
        };
        channels.ListChannels = channels.ListChannels.Select(channel => channel with
        {
            RawArmResponse = JsonSerializer.SerializeToElement(new
            {
                id = channel.ResourceId,
                properties = new { channelName = channel.ResourceId.Split('/')[^1] },
            }),
        }).ToArray();
        var service = new DeploymentChannelService(store, channels);

        var result = await service.ListChannelsAsync(new DeploymentChannelListRequest { DeploymentId = DepId }, default);

        var list = Assert.Single(channels.Lists);
        Assert.Equal(ProvisionedRecord().Bot.ResourceId, list.BotResourceId);
        Assert.Equal("tenant-1", list.TargetTenantId);

        Assert.Equal(DeploymentOperationStatus.Listed, result.Status);
        string[] expectedChannels = includeSupported ? ["msteams", "slack"] : [];
        Assert.Equal(expectedChannels, result.Payload!.Select(channel => channel.ChannelName));
        Assert.Equal(
            expectedChannels,
            Assert.Single(store.Records).Channels.Select(channel => channel.ChannelName));
        Assert.All(Assert.Single(store.Records).Channels, channel => Assert.Null(channel.RawArmResponse));
        Assert.Equal(
            channels.ListChannels.Select(channel => channel.ResourceId),
            result.ArmResponse!.Value.EnumerateArray().Select(channel => channel.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task List_UnknownId_Throws()
    {
        var service = new DeploymentChannelService(new FakeStore(), new RecordingChannelPort());
        await Assert.ThrowsAsync<DeploymentException>(() => service.ListChannelsAsync(
            new DeploymentChannelListRequest { DeploymentId = DepId },
            default));
    }

    private static IReadOnlyDictionary<string, JsonElement> Parameters(BotServiceChannelWriteRequest upsert) =>
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

        public int UpsertCalls { get; private set; }

        public IReadOnlyList<CodeAgentDeployment> List(string? project)
        {
            ListCalls++;
            return Records.ToList();
        }

        public void Upsert(string? project, CodeAgentDeployment deployment)
        {
            UpsertCalls++;
            Records.RemoveAll(record => string.Equals(record.DeploymentId, deployment.DeploymentId, StringComparison.OrdinalIgnoreCase));
            Records.Add(deployment);
        }

        public void Remove(string? project, string deploymentId) =>
            Records.RemoveAll(record => string.Equals(record.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingChannelPort : IDeploymentChannelTransport
    {
        public List<BotServiceChannelWriteRequest> Upserts { get; } = [];

        public List<string> Operations { get; } = [];

        public List<ChannelGet> Gets { get; } = [];

        public List<ChannelDelete> Deletes { get; } = [];

        public List<ChannelList> Lists { get; } = [];

        /// <summary>Controls whether <see cref="GetChannelAsync"/>/<see cref="DeleteChannelAsync"/> see an existing channel.</summary>
        public bool IsChannelExisting { get; set; }

        /// <summary>The full ARM payload the upsert record surfaces verbatim.</summary>
        public JsonElement? UpsertRawArmResponse { get; set; }

        public Queue<JsonElement> WriteResponses { get; } = new();
        public Queue<DeploymentChannelRecord?> ReadResponses { get; } = new();
        public Uri? WriteAuthorizationUrl { get; set; }

        public Dictionary<int, Exception> WriteErrors { get; } = [];

        /// <summary>The ARM response returned by reads.</summary>
        public JsonElement? GetRawArmResponse { get; set; }

        /// <summary>The channels returned by <see cref="ListChannelsAsync"/>.</summary>
        public IReadOnlyList<DeploymentChannelRecord> ListChannels { get; set; } = [];

        public Task<DeploymentChannelRecord> CreateChannelAsync(BotServiceChannelWriteRequest request, CancellationToken cancellationToken)
        {
            Operations.Add("create");
            return WriteChannelAsync(request);
        }

        public Task<DeploymentChannelRecord> UpdateChannelAsync(BotServiceChannelWriteRequest request, CancellationToken cancellationToken)
        {
            Operations.Add("update");
            return WriteChannelAsync(request);
        }

        private Task<DeploymentChannelRecord> WriteChannelAsync(BotServiceChannelWriteRequest request)
        {
            Upserts.Add(request);
            if (WriteErrors.TryGetValue(Upserts.Count, out var error))
            {
                throw error;
            }

            var parameters = Parameters(request);
            var isEnabled = parameters.TryGetValue("isEnabled", out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean()
                    : request.ChannelArmName == "MsTeamsChannel" ? true : (bool?)null;
            var record = ChannelRecord(
                request.BotResourceId,
                DeploymentChannelCatalog.FindChannelDefinition(
                    request.ChannelArmName,
                    ChannelKeyType.ArmName).Name,
                request.ChannelArmName,
                isEnabled,
                WriteResponses.Count > 0 ? WriteResponses.Dequeue() : UpsertRawArmResponse);
            if (request.ChannelArmName == "SlackChannel")
            {
                var saved = SafeParameters(GetRawArmResponse);
                foreach (var pair in request.Parameters)
                    saved[pair.Key] = pair.Value;
                record = record with
                {
                    Parameters = DeploymentChannelCatalog.ProjectChannelParameters(
                        JsonSerializer.SerializeToElement(saved), DeploymentChannelCatalog.FindChannelDefinition("slack", ChannelKeyType.Name)),
                    AuthorizationUrl = WriteAuthorizationUrl,
                };
            }
            return Task.FromResult(record);
        }

        public Task<DeploymentChannelRecord?> GetChannelAsync(
            string channelName,
            string botResourceId,
            string? targetTenantId,
            CancellationToken cancellationToken)
        {
            Operations.Add("get");
            Gets.Add(new ChannelGet(channelName, botResourceId, targetTenantId));
            if (ReadResponses.Count > 0)
                return Task.FromResult(ReadResponses.Dequeue());
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
