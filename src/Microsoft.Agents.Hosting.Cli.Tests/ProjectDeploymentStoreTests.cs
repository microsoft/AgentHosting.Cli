using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ProjectDeploymentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ah-project-store", Guid.NewGuid().ToString("N"));
    private readonly ProjectDeploymentStore _store;

    public ProjectDeploymentStoreTests()
    {
        Directory.CreateDirectory(_root);
        var locator = new AgentConfigLocator();
        _store = new ProjectDeploymentStore(new JsonAgentConfigStore(locator, new StoreTestClock()), locator);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class StoreTestClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static CodeAgentDeployment Record(string deploymentId = "deployment-1", string agentId = "agent-1", string name = "dev1", DeploymentType type = DeploymentType.Dev, bool isDefault = false) =>
        new()
        {
            DeploymentId = deploymentId,
            AgentId = agentId,
            DeploymentName = name,
            DeploymentType = type,
            IsDefault = isDefault,
            Bot = new DeploymentBotResource
            {
                ResourceId = $"/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/{name}",
                Name = name,
                TenantId = "tenant-1",
                Sku = "F0",
                Location = "global",
                DisplayName = "Dev One",
                EntraId = "app-1",
                EntraIdType = "AppReg",
                Description = "the first",
                Endpoint = "https://example.invalid/api/messages",
            },
        };

    [Fact]
    public void UpsertPreservesCreationTimeAcrossUpdates()
    {
        _store.Upsert(_root, Record());
        var created = Registry().List(_root).Single().CreatedTime;

        _store.Upsert(_root, Record() with { Bot = Record().Bot with { DisplayName = "Renamed" } });

        Assert.Equal(created, Registry().List(_root).Single().CreatedTime);
        Assert.NotEqual(default, Registry().List(_root).Single().CreatedTime);
    }

    [Fact]
    public void UpsertPreservesGovernanceFieldsAcrossUpdates()
    {
        _store.Upsert(_root, Record());
        var stored = Registry().List(_root).Single();
        Registry().Update(_root, stored with
        {
            IsQuarantined = true,
            QuarantinedReason = "under review",
            IsEnabled = false,
            LastDeployedTime = new DateTimeOffset(2025, 5, 5, 0, 0, 0, TimeSpan.Zero),
        });

        _store.Upsert(_root, Record() with { Bot = Record().Bot with { DisplayName = "Renamed" } });

        var after = Registry().List(_root).Single();

        Assert.True(after.IsQuarantined);
        Assert.Equal("under review", after.QuarantinedReason);
        Assert.False(after.IsEnabled);
        Assert.Equal(new DateTimeOffset(2025, 5, 5, 0, 0, 0, TimeSpan.Zero), after.LastDeployedTime);
        Assert.Equal("Renamed", after.DisplayName);
    }

    // The interface type is load-bearing: JsonAgentConfigStore also exposes a managed-agent List/Update
    // pair, and binding to the concrete type would resolve these calls to the wrong overload.
#pragma warning disable CA1859
    private static IAgentDeploymentRegistry Registry() => new JsonAgentConfigStore(new AgentConfigLocator(), new StoreTestClock());
#pragma warning restore CA1859

    [Fact]
    public void AnEmptyProjectListsNothing()
    {
        Assert.Empty(_store.List(_root));
    }

    [Fact]
    public void UpsertThenListRoundTripsEveryField()
    {
        _store.Upsert(_root, Record());

        var stored = Assert.Single(_store.List(_root));

        Assert.Equal("deployment-1", stored.DeploymentId);
        Assert.Equal("agent-1", stored.AgentId);
        Assert.Equal("dev1", stored.DeploymentName);
        Assert.Equal(DeploymentType.Dev, stored.DeploymentType);
        Assert.Equal("app-1", stored.Bot.EntraId);
        Assert.Equal("AppReg", stored.Bot.EntraIdType);
        Assert.Equal("tenant-1", stored.Bot.TenantId);
        Assert.Equal("F0", stored.Bot.Sku);
        Assert.Equal("global", stored.Bot.Location);
        Assert.Equal("Dev One", stored.Bot.DisplayName);
        Assert.Equal("the first", stored.Bot.Description);
        Assert.Equal("https://example.invalid/api/messages", stored.Bot.Endpoint);
        Assert.Contains("/botServices/dev1", stored.Bot.ResourceId);
    }

    [Fact]
    public void UpsertWritesToTheProjectConfig()
    {
        _store.Upsert(_root, Record());

        Assert.True(File.Exists(Path.Combine(_root, AgentConfigLocator.ConfigDirectoryName, AgentConfigLocator.ConfigFileName)));
    }

    [Fact]
    public void UpsertReplacesRatherThanDuplicates()
    {
        _store.Upsert(_root, Record());
        _store.Upsert(_root, Record() with { Bot = Record().Bot with { Sku = "S1" } });

        Assert.Equal("S1", Assert.Single(_store.List(_root)).Bot.Sku);
    }

    [Fact]
    public void RemoveDeletesTheRecord()
    {
        _store.Upsert(_root, Record());

        _store.Remove(_root, "deployment-1");

        Assert.Empty(_store.List(_root));
    }

    [Fact]
    public void RemovingAnAbsentRecordIsSilent()
    {
        _store.Remove(_root, "never-existed");

        Assert.Empty(_store.List(_root));
    }

    [Fact]
    public void EachStageRoundTrips()
    {
        _store.Upsert(_root, Record("d-dev", name: "a", type: DeploymentType.Dev));
        _store.Upsert(_root, Record("d-test", name: "b", type: DeploymentType.Test));
        _store.Upsert(_root, Record("d-prod", name: "c", type: DeploymentType.Prod));

        var stored = _store.List(_root);

        Assert.Equal(DeploymentType.Dev, stored.Single(record => record.DeploymentId == "d-dev").DeploymentType);
        Assert.Equal(DeploymentType.Test, stored.Single(record => record.DeploymentId == "d-test").DeploymentType);
        Assert.Equal(DeploymentType.Prod, stored.Single(record => record.DeploymentId == "d-prod").DeploymentType);
    }

    [Fact]
    public void OnlyOneDeploymentPerAgentStaysDefault()
    {
        _store.Upsert(_root, Record("d-1", name: "a", isDefault: true));
        _store.Upsert(_root, Record("d-2", name: "b", isDefault: true));

        var stored = _store.List(_root);

        Assert.False(stored.Single(record => record.DeploymentId == "d-1").IsDefault);
        Assert.True(stored.Single(record => record.DeploymentId == "d-2").IsDefault);
    }

    [Theory]
    [InlineData("must-not-persist")]
    [InlineData("***")]
    [InlineData(null)]
    public void OAuthConnectionsRoundTripThroughTheProjectStore(string? clientSecret)
    {
        var deployment = Record() with
        {
            OAuthConnections =
            [
                new DeploymentOAuthConnection
                {
                    ConnectionName = "graph",
                    ServiceProviderId = "provider-id",
                    ServiceProviderDisplayName = "Azure Active Directory v2",
                    ClientId = "client-id",
                    Scopes = ["User.Read", "offline_access"],
                    Parameters = new Dictionary<string, string?>
                    {
                        ["ClientId"] = "client-id",
                        ["ClientSecret"] = clientSecret,
                        ["Scopes"] = "User.Read",
                        ["TenantId"] = "common",
                        ["ProviderPrivateValue"] = BotServiceResponseRedactor.Mask,
                        ["refreshToken"] = null,
                        ["OptionalValue"] = null,
                        ["tokenEndpoint"] = "https://example.test/token",
                    },
                    ResourceId = "/connections/graph",
                },
            ],
        };

        _store.Upsert(_root, deployment);

        var connection = Assert.Single(Assert.Single(_store.List(_root)).OAuthConnections);
        Assert.Equal("graph", connection.ConnectionName);
        Assert.Equal("provider-id", connection.ServiceProviderId);
        Assert.Equal("Azure Active Directory v2", connection.ServiceProviderDisplayName);
        Assert.Equal("client-id", connection.ClientId);
        Assert.Equal(["User.Read", "offline_access"], connection.Scopes);
        Assert.Equal("common", connection.Parameters["TenantId"]);
        Assert.DoesNotContain("ClientId", connection.Parameters.Keys);
        Assert.DoesNotContain("ClientSecret", connection.Parameters.Keys);
        Assert.DoesNotContain("ProviderPrivateValue", connection.Parameters.Keys);
        Assert.DoesNotContain("refreshToken", connection.Parameters.Keys);
        Assert.Null(connection.Parameters["OptionalValue"]);
        Assert.Equal("https://example.test/token", connection.Parameters["tokenEndpoint"]);
        Assert.DoesNotContain("Scopes", connection.Parameters.Keys);

        var json = File.ReadAllText(Path.Combine(
            _root,
            AgentConfigLocator.ConfigDirectoryName,
            AgentConfigLocator.ConfigFileName));
        Assert.Contains("\"oauthConnections\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"oAuthConnections\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientSecret", json);
        Assert.DoesNotContain("ProviderPrivateValue", json);
        Assert.DoesNotContain("refreshToken", json);
        Assert.DoesNotContain(BotServiceResponseRedactor.Mask, json);
    }

    [Fact]
    public void SavingLegacyConfigRemovesMaskedChannelAndOAuthProperties()
    {
        var legacy = ProjectDeploymentStore.ToAgentDeploymentRecord(Record(), "etag") with
        {
            Channels =
            [
                new DeploymentChannelRecord
                {
                    ChannelName = "telegram",
                    ResourceId = "/channels/TelegramChannel",
                    Parameters = new Dictionary<string, JsonElement>
                    {
                        ["accessToken"] = JsonSerializer.SerializeToElement(BotServiceResponseRedactor.Mask),
                        ["isEnabled"] = JsonSerializer.SerializeToElement(false),
                    },
                },
            ],
            OAuthConnections =
            [
                new DeploymentOAuthConnection
                {
                    ConnectionName = "graph",
                    ServiceProviderId = "provider",
                    ServiceProviderDisplayName = "Provider",
                    ResourceId = "/connections/graph",
                    Parameters = new Dictionary<string, string?>
                    {
                        ["ProviderPrivateValue"] = BotServiceResponseRedactor.Mask,
                        ["TenantId"] = "common",
                    },
                },
            ],
        };
        Registry().Add(_root, legacy);

        var loaded = Assert.Single(_store.List(_root));
        Assert.DoesNotContain("accessToken", Assert.Single(loaded.Channels).Parameters);
        Assert.DoesNotContain("ProviderPrivateValue", Assert.Single(loaded.OAuthConnections).Parameters);
        _store.Upsert(_root, loaded);

        var json = File.ReadAllText(Path.Combine(_root, AgentConfigLocator.ConfigDirectoryName, AgentConfigLocator.ConfigFileName));
        Assert.DoesNotContain("accessToken", json);
        Assert.DoesNotContain("ProviderPrivateValue", json);
        Assert.DoesNotContain(BotServiceResponseRedactor.Mask, json);
        Assert.False(Assert.Single(Assert.Single(_store.List(_root)).Channels).Parameters["isEnabled"].GetBoolean());
        Assert.Equal("common", Assert.Single(Assert.Single(_store.List(_root)).OAuthConnections).Parameters["TenantId"]);
    }

    [Fact]
    public void OAuthClientIdParameter_IsNotHoistedOnRead()
    {
        var record = ProjectDeploymentStore.ToAgentDeploymentRecord(Record(), "1") with
        {
            OAuthConnections =
            [
                new DeploymentOAuthConnection
                {
                    ConnectionName = "graph",
                    ServiceProviderId = "provider-id",
                    ServiceProviderDisplayName = "Azure Active Directory v2",
                    ClientId = null,
                    Parameters = new Dictionary<string, string?>
                    {
                        ["ClientId"] = "parameter-client-id",
                        ["TenantId"] = "common",
                    },
                    ResourceId = "/connections/graph",
                },
            ],
        };

        var connection = Assert.Single(ProjectDeploymentStore.ToDeployment(record).OAuthConnections);

        Assert.Null(connection.ClientId);
        Assert.Equal("common", connection.Parameters["TenantId"]);
        Assert.DoesNotContain("ClientId", connection.Parameters.Keys);
    }

    [Fact]
    public void MissingOAuthConnectionsLoadsAsEmpty()
    {
        var record = ProjectDeploymentStore.ToAgentDeploymentRecord(Record(), "1") with
        {
            OAuthConnections = null!,
        };

        var deployment = ProjectDeploymentStore.ToDeployment(record);

        Assert.Empty(deployment.OAuthConnections);
    }

    [Fact]
    public void McsDeploymentsAreNotListedAsCodeAgentRecords()
    {
        var locator = new AgentConfigLocator();
        var registry = (IAgentDeploymentRegistry)new JsonAgentConfigStore(locator, new StoreTestClock());
        ((IManagedAgentRegistry)new JsonAgentConfigStore(locator, new StoreTestClock())).Add(_root, new ManagedAgentRecord
        {
            AgentId = "mcs-agent",
            DisplayName = "MCS",
            AgentType = AgentTypes.McsAgent,
            AgentLocation = AgentConfigLocator.CurrentDirectoryLocation,
            TenantId = "tenant-1",
            EnvironmentId = "env-1",
            Origin = "AgentHostCLI",
            EntityTag = "1",
            AgentState = EntityStates.Saved,
        });
        registry.Add(_root, new AgentDeploymentRecord
        {
            DeploymentId = "mcs-slot",
            AgentId = "mcs-agent",
            AgentType = AgentTypes.McsAgent,
            DeploymentType = DeploymentStages.Dev,
            DeploymentName = "mcs-dev",
            TenantId = "tenant-1",
            HomeEnvironmentId = "env-1",
            EnvironmentId = "env-1",
            EntityTag = "1",
        });

        _store.Upsert(_root, Record());

        Assert.Equal("deployment-1", Assert.Single(_store.List(_root)).DeploymentId);
        Assert.Equal(2, registry.List(_root).Count);
    }

    [Fact]
    public void TheStoredStageTextMatchesTheSharedVocabulary()
    {
        Assert.Equal(DeploymentStages.Dev, ProjectDeploymentStore.Stage(DeploymentType.Dev));
        Assert.Equal(DeploymentStages.Test, ProjectDeploymentStore.Stage(DeploymentType.Test));
        Assert.Equal(DeploymentStages.Prod, ProjectDeploymentStore.Stage(DeploymentType.Prod));
    }

    [Theory]
    [InlineData("dev", DeploymentType.Dev)]
    [InlineData("TEST", DeploymentType.Test)]
    [InlineData("Prod", DeploymentType.Prod)]
    [InlineData(null, DeploymentType.Dev)]
    [InlineData("nonsense", DeploymentType.Dev)]
    public void StageTextParsesCaseInsensitively(string? stage, DeploymentType expected)
    {
        Assert.Equal(expected, ProjectDeploymentStore.ParseStage(stage));
    }

    [Fact]
    public void ANonDefaultRecordOmitsTheFlagOnDisk()
    {
        _store.Upsert(_root, Record());

        Assert.DoesNotContain("isDefault", File.ReadAllText(Path.Combine(_root, AgentConfigLocator.ConfigDirectoryName, AgentConfigLocator.ConfigFileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void ChannelsAddUpdateAndDeleteRoundTripThroughTheStore()
    {
        _store.Upsert(_root, Record());

        // Add a channel (the channel service persists via Upsert with the channel list set).
        var withChannel = Assert.Single(_store.List(_root)) with
        {
            Channels =
            [
                new DeploymentChannelRecord
                {
                    ChannelName = "msteams",
                    ResourceId = "/subscriptions/sub-1/.../channels/MsTeamsChannel",
                    Parameters = new Dictionary<string, JsonElement>
                    {
                        ["isEnabled"] = JsonSerializer.SerializeToElement(true),
                        ["enableCalling"] = JsonSerializer.SerializeToElement(true),
                    },
                    RawArmResponse = JsonSerializer.SerializeToElement(new
                    {
                        id = "/subscriptions/sub-1/.../channels/MsTeamsChannel",
                        properties = new { channelName = "MsTeamsChannel" },
                    }),
                },
            ],
        };
        _store.Upsert(_root, withChannel);

        var afterAdd = Assert.Single(_store.List(_root));
        var channel = Assert.Single(afterAdd.Channels);
        Assert.Equal("msteams", channel.ChannelName);
        Assert.True(channel.Parameters["isEnabled"].GetBoolean());
        Assert.Contains("channels/MsTeamsChannel", channel.ResourceId);
        Assert.True(channel.Parameters["enableCalling"].GetBoolean());
        Assert.Null(channel.RawArmResponse);
        Assert.DoesNotContain(
            "rawArmResponse",
            File.ReadAllText(Path.Combine(_root, AgentConfigLocator.ConfigDirectoryName, AgentConfigLocator.ConfigFileName)),
            StringComparison.OrdinalIgnoreCase);

        // Update the channel's state.
        _store.Upsert(_root, afterAdd with
        {
            Channels =
            [
                channel with
                {
                    Parameters = new Dictionary<string, JsonElement>(channel.Parameters)
                    {
                        ["isEnabled"] = JsonSerializer.SerializeToElement(false),
                    },
                },
            ],
        });

        var afterUpdate = Assert.Single(Assert.Single(_store.List(_root)).Channels);
        Assert.False(afterUpdate.Parameters["isEnabled"].GetBoolean());
        Assert.Contains("channels/MsTeamsChannel", afterUpdate.ResourceId);

        // Delete the channel.
        _store.Upsert(_root, Assert.Single(_store.List(_root)) with { Channels = [] });

        Assert.Empty(Assert.Single(_store.List(_root)).Channels);
    }

    [Theory]
    [InlineData("SLACK")]
    [InlineData("SlackChannel")]
    public void ChannelSecretsAreOmittedAtBothStoreBoundaries(string channelName)
    {
        var unsafeChannel = new DeploymentChannelRecord
        {
            ChannelName = channelName,
            ResourceId = "/subscriptions/sub-1/.../channels/SlackChannel",
            Parameters = new Dictionary<string, JsonElement>
            {
                ["CLIENTID"] = JsonSerializer.SerializeToElement("client-id"),
                ["clientSecret"] = JsonSerializer.SerializeToElement("client-secret"),
                ["verificationToken"] = JsonSerializer.SerializeToElement("verification-token"),
                ["signingSecret"] = JsonSerializer.SerializeToElement<string?>(null),
                ["redirectAction"] = JsonSerializer.SerializeToElement("https://slack.com/oauth/v2/authorize?state=SYNTHETIC_STATE"),
                ["futureProperty"] = JsonSerializer.SerializeToElement("unknown"),
            },
            RawArmResponse = JsonSerializer.SerializeToElement(new
            {
                properties = new
                {
                    channelName = "SlackChannel",
                    properties = new { clientSecret = "client-secret", redirectAction = "https://slack.com/oauth/v2/authorize?state=SYNTHETIC_STATE" },
                },
            }),
            AuthorizationUrl = new Uri("https://slack.com/oauth/v2/authorize?state=SYNTHETIC_STATE"),
        };

        var inMemoryChannel = DeploymentChannelCatalog.Sanitize(unsafeChannel);
        Assert.NotNull(inMemoryChannel);
        Assert.Equal(BotServiceResponseRedactor.Mask, inMemoryChannel.Parameters["clientSecret"].GetString());
        Assert.Equal(
            BotServiceResponseRedactor.Mask,
            inMemoryChannel.RawArmResponse!.Value
                .GetProperty("properties")
                .GetProperty("properties")
                .GetProperty("clientSecret")
                .GetString());

        var storedRecord = ProjectDeploymentStore.ToAgentDeploymentRecord(
            Record() with { Channels = [unsafeChannel] },
            "etag");
        var storedChannel = Assert.Single(storedRecord.Channels!);

        var untrustedRecord = ProjectDeploymentStore.ToAgentDeploymentRecord(Record(), "etag") with
        {
            Channels = [unsafeChannel],
        };
        var loadedChannel = Assert.Single(ProjectDeploymentStore.ToDeployment(untrustedRecord).Channels);

        foreach (var channel in new[] { storedChannel, loadedChannel })
        {
            Assert.Equal("slack", channel.ChannelName);
            Assert.Equal("client-id", channel.Parameters["clientId"].GetString());
            Assert.DoesNotContain("clientSecret", channel.Parameters.Keys);
            Assert.DoesNotContain("verificationToken", channel.Parameters.Keys);
            Assert.DoesNotContain("signingSecret", channel.Parameters.Keys);
            Assert.DoesNotContain("futureProperty", channel.Parameters.Keys);
            Assert.Null(channel.RawArmResponse);
            Assert.Null(channel.AuthorizationUrl);
            Assert.DoesNotContain("SYNTHETIC_STATE", JsonSerializer.Serialize(channel));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmsTokenIsOmittedOnDiskAndWhenLoadingUntrustedRecords(bool nullToken)
    {
        var unsafeChannel = new DeploymentChannelRecord
        {
            ChannelName = "SmsChannel",
            ResourceId = "/channels/SmsChannel",
            Parameters = new Dictionary<string, JsonElement>
            {
                ["accountSID"] = JsonSerializer.SerializeToElement("synthetic-sid"),
                ["authToken"] = JsonSerializer.SerializeToElement(nullToken ? null : "synthetic-auth-token"),
                ["phone"] = JsonSerializer.SerializeToElement("+12025550123"),
                ["isEnabled"] = JsonSerializer.SerializeToElement(false),
                ["isValidated"] = JsonSerializer.SerializeToElement(true),
                ["unknown"] = JsonSerializer.SerializeToElement("ignore"),
            },
            RawArmResponse = JsonSerializer.SerializeToElement(new { authToken = "synthetic-auth-token" }),
        };
        _store.Upsert(_root, Record() with { Channels = [unsafeChannel] });
        var config = File.ReadAllText(Path.Combine(_root, ".config", "agent.config.json"));
        Assert.DoesNotContain("authToken", config);
        Assert.DoesNotContain(BotServiceResponseRedactor.Mask, config);
        var untrusted = ProjectDeploymentStore.ToAgentDeploymentRecord(Record(), "etag") with { Channels = [unsafeChannel] };
        foreach (var channel in new[] { Assert.Single(Assert.Single(_store.List(_root)).Channels),
            Assert.Single(ProjectDeploymentStore.ToDeployment(untrusted).Channels) })
        {
            Assert.Equal("sms", channel.ChannelName);
            Assert.Equal("synthetic-sid", channel.Parameters["accountSID"].GetString());
            Assert.Equal("+12025550123", channel.Parameters["phone"].GetString());
            Assert.DoesNotContain("authToken", channel.Parameters.Keys);
            Assert.False(channel.Parameters["isEnabled"].GetBoolean());
            Assert.True(channel.Parameters["isValidated"].GetBoolean());
            Assert.DoesNotContain("unknown", channel.Parameters);
            Assert.Null(channel.RawArmResponse);
        }
    }

    [Fact]
    public void LineCredentialsAreOmittedOnDiskAndWhenLoadingUntrustedRecords()
    {
        var channel = new DeploymentChannelRecord
        {
            ChannelName = "LineChannel",
            ResourceId = "/channels/LineChannel",
            Parameters = new Dictionary<string, JsonElement>
            {
                ["lineRegistrations"] = JsonDocument.Parse("""
                    [{"generatedId":"one","channelSecret":"synthetic-secret","channelAccessToken":"synthetic-token","unknown":"ignore"},
                     {"generatedId":"two","channelSecret":null,"channelAccessToken":null}]
                    """).RootElement.Clone(),
                ["callbackUrl"] = JsonSerializer.SerializeToElement("https://callback.example/one"),
                ["isValidated"] = JsonSerializer.SerializeToElement(true),
            },
        };
        _store.Upsert(_root, Record() with { Channels = [channel] });
        var config = File.ReadAllText(Path.Combine(_root, ".config", "agent.config.json"));
        Assert.DoesNotContain("synthetic-", config);
        Assert.DoesNotContain("channelSecret", config);
        Assert.DoesNotContain("channelAccessToken", config);
        Assert.DoesNotContain(BotServiceResponseRedactor.Mask, config);
        var untrusted = ProjectDeploymentStore.ToAgentDeploymentRecord(Record(), "etag") with { Channels = [channel] };
        foreach (var stored in new[] { Assert.Single(Assert.Single(_store.List(_root)).Channels),
            Assert.Single(ProjectDeploymentStore.ToDeployment(untrusted).Channels) })
        {
            Assert.Equal("line", stored.ChannelName);
            Assert.Equal("https://callback.example/one", stored.Parameters["callbackUrl"].GetString());
            Assert.True(stored.Parameters["isValidated"].GetBoolean());
            var items = stored.Parameters["lineRegistrations"];
            Assert.Equal("one", items[0].GetProperty("generatedId").GetString());
            Assert.Equal("two", items[1].GetProperty("generatedId").GetString());
            foreach (var item in items.EnumerateArray())
            {
                Assert.False(item.TryGetProperty("channelSecret", out _));
                Assert.False(item.TryGetProperty("channelAccessToken", out _));
                Assert.False(item.TryGetProperty("unknown", out _));
            }
        }
    }

    [Theory]
    [MemberData(nameof(DeploymentChannelCatalogTests.UnsupportedChannelNames), MemberType = typeof(DeploymentChannelCatalogTests))]
    public void UnsupportedChannelsAreFilteredAtBothStoreBoundaries(string channelName)
    {
        var unsafeChannel = new DeploymentChannelRecord
        {
            ResourceId = $"/channels/{channelName}",
            ChannelName = channelName,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["futureField"] = JsonSerializer.SerializeToElement("SYNTHETIC_SECRET"),
            },
            RawArmResponse = JsonSerializer.SerializeToElement(new { futureField = "SYNTHETIC_RAW_SECRET" }),
        };
        var supportedChannel = new DeploymentChannelRecord
        {
            ResourceId = "/channels/MsTeamsChannel",
            ChannelName = "msteams",
            Parameters = new Dictionary<string, JsonElement>
            {
                ["isEnabled"] = JsonSerializer.SerializeToElement(true),
            },
        };
        var record = Record() with { Channels = [supportedChannel, unsafeChannel] };
        var storedRecord = ProjectDeploymentStore.ToAgentDeploymentRecord(record, "etag");
        var untrustedRecord = ProjectDeploymentStore.ToAgentDeploymentRecord(Record(), "etag") with
        {
            Channels = [supportedChannel, unsafeChannel],
        };

        _store.Upsert(_root, record);
        var roundTripped = Assert.Single(Assert.Single(_store.List(_root)).Channels);
        var loaded = Assert.Single(ProjectDeploymentStore.ToDeployment(untrustedRecord).Channels);
        var stored = Assert.Single(storedRecord.Channels!);

        foreach (var channel in new[] { roundTripped, loaded, stored })
        {
            Assert.Equal(supportedChannel.ResourceId, channel.ResourceId);
            Assert.Equal(supportedChannel.ChannelName, channel.ChannelName);
            Assert.True(channel.Parameters["isEnabled"].GetBoolean());
            Assert.Null(channel.RawArmResponse);
        }

        var config = File.ReadAllText(Path.Combine(
            _root, AgentConfigLocator.ConfigDirectoryName, AgentConfigLocator.ConfigFileName));
        Assert.DoesNotContain(channelName, config);
        Assert.DoesNotContain("SYNTHETIC_", config);
        Assert.DoesNotContain("futureField", config);
        Assert.DoesNotContain("rawArmResponse", config, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isSupported", config, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("directline", "DirectLineChannel")]
    [InlineData("webchat", "WebChatChannel")]
    public void ChannelWithMultipleSitesRoundTripsThroughTheStore(string channelName, string armName)
    {
        string[] origins = ["https://example.test"];
        var deployment = Record() with
        {
            Channels =
            [
                new DeploymentChannelRecord
                {
                    ChannelName = channelName,
                    ResourceId = $"/subscriptions/sub-1/.../channels/{armName}",
                    Parameters = new Dictionary<string, JsonElement>
                    {
                        ["EXTENSIONKEY1"] = JsonSerializer.SerializeToElement("SYNTHETIC_EXTENSION_SECRET"),
                        ["extensionKey2"] = JsonSerializer.SerializeToElement<string?>(null),
                        ["directLineEmbedCode"] = JsonSerializer.SerializeToElement(BotServiceResponseRedactor.Mask),
                        ["WEBCHATEMBEDCODE"] = JsonSerializer.SerializeToElement("SYNTHETIC_EMBED_SECRET"),
                        ["isEnabled"] = JsonSerializer.SerializeToElement(true),
                        ["sites"] = JsonSerializer.SerializeToElement(new[]
                        {
                            new
                            {
                                siteId = "one", siteName = "Primary", isEnabled = true,
                                key = (string?)"SYNTHETIC_SITE_SECRET",
                                key1LastUpdated = "2026-10-13T11:25:35Z", key2LastUpdated = "2026-10-14T11:25:35Z",
                                futureProperty = "unknown", isWebChatSpeechEnabled = (bool?)null,
                                isSecureSiteEnabled = true, isV1Enabled = true, isV3Enabled = true,
                                trustedOrigins = origins,
                            },
                            new
                            {
                                siteId = "two", siteName = "Secondary", isEnabled = false,
                                key = (string?)null,
                                key1LastUpdated = "2026-10-13T11:25:35Z", key2LastUpdated = "2026-10-14T11:25:35Z",
                                futureProperty = "unknown", isWebChatSpeechEnabled = (bool?)null,
                                isSecureSiteEnabled = true, isV1Enabled = true, isV3Enabled = true,
                                trustedOrigins = origins,
                            },
                        }),
                    },
                },
            ],
        };

        _store.Upsert(_root, deployment);

        var channel = Assert.Single(Assert.Single(_store.List(_root)).Channels);
        Assert.False(channel.Parameters.ContainsKey("isEnabled"));
        Assert.False(channel.Parameters.ContainsKey("webChatEmbedCode"));
        Assert.False(channel.Parameters.ContainsKey("directLineEmbedCode"));
        Assert.False(channel.Parameters.ContainsKey("extensionKey1"));
        Assert.False(channel.Parameters.ContainsKey("extensionKey2"));
        var sites = channel.Parameters["sites"];
        Assert.Equal(2, sites.GetArrayLength());
        Assert.Equal("Primary", sites[0].GetProperty("siteName").GetString());
        Assert.True(sites[0].GetProperty("isEnabled").GetBoolean());
        Assert.False(sites[0].TryGetProperty("futureProperty", out _));
        Assert.Equal("Secondary", sites[1].GetProperty("siteName").GetString());
        Assert.False(sites[1].GetProperty("isEnabled").GetBoolean());
        Assert.False(sites[1].TryGetProperty("futureProperty", out _));
        Assert.Equal("one", sites[0].GetProperty("siteId").GetString());
        Assert.Equal("two", sites[1].GetProperty("siteId").GetString());
        Assert.False(sites[0].TryGetProperty("key", out _));
        Assert.False(sites[1].TryGetProperty("key", out _));
        foreach (var site in sites.EnumerateArray())
        {
            Assert.False(site.TryGetProperty("key1LastUpdated", out _));
            Assert.False(site.TryGetProperty("key2LastUpdated", out _));
            Assert.False(site.TryGetProperty("isWebChatSpeechEnabled", out _));
            if (channelName == "webchat")
            {
                foreach (var field in new[] { "isSecureSiteEnabled", "isV1Enabled", "isV3Enabled", "trustedOrigins" })
                {
                    Assert.False(site.TryGetProperty(field, out _));
                }
            }
        }

        var config = File.ReadAllText(Path.Combine(_root, AgentConfigLocator.ConfigDirectoryName, AgentConfigLocator.ConfigFileName));
        Assert.DoesNotContain("SYNTHETIC_", config);
        Assert.DoesNotContain(BotServiceResponseRedactor.Mask, config);
        Assert.DoesNotContain("key1LastUpdated", config);
        Assert.DoesNotContain("key2LastUpdated", config);
        Assert.DoesNotContain("isWebChatSpeechEnabled", config);
    }

    [Fact]
    public void FacebookPagesAndReadonlyOutputsRoundTripWithoutCredentials()
    {
        var record = Record() with
        {
            Channels =
            [
                new DeploymentChannelRecord
                {
                    ChannelName = "FacebookChannel",
                    ResourceId = "/subscriptions/sub-1/.../channels/FacebookChannel",
                    Parameters = new Dictionary<string, JsonElement>
                    {
                        ["appId"] = JsonSerializer.SerializeToElement("app"),
                        ["appSecret"] = JsonSerializer.SerializeToElement("SYNTHETIC_SECRET"),
                        ["verifyToken"] = JsonSerializer.SerializeToElement("SYNTHETIC_VERIFY"),
                        ["callbackUrl"] = JsonSerializer.SerializeToElement("https://callback.example"),
                        ["pages"] = JsonDocument.Parse("""
                            [{"id":"one","accessToken":"SYNTHETIC_TOKEN","unknown":"ignore"},
                             {"id":"two","accessToken":null}]
                            """).RootElement.Clone(),
                        ["unknown"] = JsonSerializer.SerializeToElement("ignore"),
                    },
                },
            ],
        };
        _store.Upsert(_root, record);
        var persisted = Assert.Single(Assert.Single(_store.List(_root)).Channels);
        var untrusted = ProjectDeploymentStore.ToAgentDeploymentRecord(Record(), "etag") with
        {
            Channels = record.Channels,
        };
        var loaded = Assert.Single(ProjectDeploymentStore.ToDeployment(untrusted).Channels);
        foreach (var channel in new[] { persisted, loaded })
        {
            Assert.Equal("facebook", channel.ChannelName);
            Assert.Equal("https://callback.example", channel.Parameters["callbackUrl"].GetString());
            Assert.DoesNotContain("appSecret", channel.Parameters.Keys);
            Assert.DoesNotContain("verifyToken", channel.Parameters.Keys);
            Assert.Equal(2, channel.Parameters["pages"].GetArrayLength());
            foreach (var page in channel.Parameters["pages"].EnumerateArray())
            {
                Assert.False(page.TryGetProperty("accessToken", out _));
                Assert.False(page.TryGetProperty("unknown", out _));
            }

            Assert.DoesNotContain("unknown", channel.Parameters);
            Assert.Null(channel.RawArmResponse);
        }

        var config = File.ReadAllText(Path.Combine(_root, AgentConfigLocator.ConfigDirectoryName, AgentConfigLocator.ConfigFileName));
        Assert.DoesNotContain("SYNTHETIC_", config);
        Assert.DoesNotContain(BotServiceResponseRedactor.Mask, config);
    }

    [Fact]
    public void ADeploymentWithoutChannelsOmitsTheArrayOnDisk()
    {
        _store.Upsert(_root, Record());

        Assert.DoesNotContain("channels", File.ReadAllText(Path.Combine(_root, AgentConfigLocator.ConfigDirectoryName, AgentConfigLocator.ConfigFileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void ADeploymentWithoutOAuthConnectionsOmitsTheArrayOnDisk()
    {
        _store.Upsert(_root, Record());

        Assert.DoesNotContain(
            "oauthConnections",
            File.ReadAllText(Path.Combine(_root, AgentConfigLocator.ConfigDirectoryName, AgentConfigLocator.ConfigFileName)),
            StringComparison.Ordinal);
    }
}
