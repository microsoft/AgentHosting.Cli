using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentOAuthServiceTests
{
    private const string DeploymentId = "11111111-1111-1111-1111-111111111111";
    private const string AgentId = "44444444-4444-4444-4444-444444444444";

    private static DeploymentOAuthService Service(FakeStore store, FakeOAuthPort port) =>
        new(
            store,
            port,
            new CompositeDeploymentTransport(subscriptionResolver: new FakeSubscriptionResolver()));

    private static JsonElement Arm(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static CodeAgentDeployment ProvisionedRecord(string id = DeploymentId, string name = "dev1") =>
        new()
        {
            DeploymentId = id,
            AgentId = AgentId,
            DeploymentName = name,
            DeploymentType = DeploymentType.Dev,
            Bot = new DeploymentBotResource
            {
                ResourceId = $"/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/{name}",
                Name = name,
                TenantId = "tenant-1",
                Sku = "F0",
                Location = "global",
                DisplayName = name,
                EntraId = "app-1",
                EntraIdType = "AppReg",
            },
        };

    private static CodeAgentDeployment MetadataRecord(string id = DeploymentId, string name = "dev1") =>
        ProvisionedRecord(id, name) with { Bot = ProvisionedRecord(id, name).Bot with { ResourceId = string.Empty } };

    private static DeploymentOAuthCreateRequest CreateRequest(
        string connection = "aad-conn",
        string provider = "Aadv2",
        string? clientId = "client-1",
        string? clientSecret = "secret-1",
        params string[] parameters) =>
        new()
        {
            DeploymentId = DeploymentId,
            ConnectionName = connection,
            ServiceProviderName = provider,
            ClientId = clientId,
            ClientSecret = clientSecret,
            Parameters = parameters,
        };

    [Fact]
    public async Task CreateAsync_UnknownDeployment_ThrowsNotFound()
    {
        var store = new FakeStore();
        var port = new FakeOAuthPort();
        var service = Service(store, port);

        var ex = await Assert.ThrowsAsync<DeploymentException>(() =>
            service.CreateAsync(CreateRequest(), CancellationToken.None));
        Assert.Equal("deployment-not-found", ex.ErrorKind);
    }

    [Fact]
    public async Task CreateAsync_NoBackingBot_ThrowsNoBackingBot()
    {
        var store = new FakeStore(MetadataRecord());
        var port = new FakeOAuthPort();
        var service = Service(store, port);

        await Assert.ThrowsAsync<DeploymentException>(() =>
            service.CreateAsync(CreateRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_ProviderNotInCatalogue_Throws()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort { Providers = [] };
        var service = Service(store, port);

        await Assert.ThrowsAsync<DeploymentException>(() =>
            service.CreateAsync(CreateRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_ExistingConnection_ReturnsExistedWithoutPut()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort
        {
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "aad-conn",
                ServiceProviderId = "provider-id-Fic",
                ClientId = "existing-client",
                Scopes = "existing scope",
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["UniqueIdentifier"] = "existing-value",
                },
                ResourceId = "existing-resource",
            },
        };
        var service = Service(store, port);

        var result = await service.CreateAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(DeploymentOperationStatus.Existed, result.Status);
        var payload = Assert.IsType<DeploymentOAuthConnection>(result.Payload);
        Assert.Equal("provider-id-Fic", payload.ServiceProviderId);
        Assert.Equal("existing-client", payload.ClientId);
        Assert.Equal(["existing", "scope"], payload.Scopes);
        Assert.Equal("existing-value", payload.Parameters["UniqueIdentifier"]);
        Assert.Equal("existing-resource", payload.ResourceId);
        Assert.Empty(port.Upserts);
        Assert.Equal(1, port.ProviderListCallCount);
        Assert.Equal(
            "existing-client",
            Assert.Single(Assert.Single(store.Records).OAuthConnections).ClientId);
    }

    [Fact]
    public async Task CreateAsync_UsesCanonicalValidatedDeploymentIdForLookup()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort
        {
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "aad-conn",
                ServiceProviderId = "provider-id-Aadv2",
                ClientId = "existing-client",
            },
        };

        var report = await Service(store, port).CreateAsync(
            CreateRequest() with { DeploymentId = Guid.Parse(DeploymentId).ToString("B") },
            CancellationToken.None);

        Assert.Equal(DeploymentOperationStatus.Existed, report.Status);
    }

    [Fact]
    public async Task CreateAsync_ExistingConnectionReadFails_PropagatesWithoutPut()
    {
        var store = new FakeStore(ProvisionedRecord());
        var failure = new TargetRequestException(
            "ARM failed",
            ExitCodes.Failure,
            TargetErrorKind.RequestFailed,
            500);
        var port = new FakeOAuthPort { GetThrows = failure };
        var service = Service(store, port);

        var actual = await Assert.ThrowsAsync<TargetRequestException>(() =>
            service.CreateAsync(CreateRequest(), CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.Empty(port.Upserts);
        Assert.Equal(1, port.ProviderListCallCount);
    }

    [Fact]
    public async Task CreateAsync_MissingCatalogueRequiredParameter_ThrowsBeforePut()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort();
        var service = Service(store, port);

        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            service.CreateAsync(CreateRequest(clientId: null), CancellationToken.None));

        Assert.Contains("--client-id", ex.Message);
        Assert.Empty(port.Upserts);
        Assert.Equal(0, port.GetCallCount);
    }

    [Fact]
    public async Task CreateAsync_UnknownProviderParameter_ThrowsBeforePut()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort();
        var service = Service(store, port);

        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            service.CreateAsync(CreateRequest(parameters: ["DoesNotExist=value"]), CancellationToken.None));

        Assert.Contains("DoesNotExist", ex.Message);
        Assert.Empty(port.Upserts);
        Assert.Equal(0, port.GetCallCount);
    }

    [Fact]
    public async Task CreateAsync_CommonOAuthOptionNotDeclaredByProvider_ThrowsBeforePut()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort();
        var service = Service(store, port);

        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            service.CreateAsync(
                CreateRequest(
                    provider: "Aadv2WithFic",
                    clientSecret: "must-not-be-silently-dropped",
                    parameters: ["UniqueIdentifier=uid-1"]),
                CancellationToken.None));

        Assert.Contains("--client-secret", ex.Message);
        Assert.Empty(port.Upserts);
        Assert.Equal(0, port.GetCallCount);
    }

    [Fact]
    public async Task CreateAsync_HappyPath_UpsertsAndPersistsWithoutSecret()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort();
        var service = Service(store, port);

        var report = await service.CreateAsync(
            CreateRequest(parameters: ["TokenExchangeUrl=api://exchange"]),
            CancellationToken.None);

        Assert.Equal(DeploymentOperationStatus.Created, report.Status);
        var upsert = Assert.Single(port.Upserts);
        Assert.Equal("aad-conn", upsert.ConnectionName);
        Assert.Equal("client-1", upsert.ClientId);
        Assert.Equal("secret-1", upsert.ClientSecret);
        Assert.Equal("provider-id-Aadv2", upsert.ServiceProviderId);
        Assert.Equal("common", upsert.Parameters.Single(parameter => parameter.Key == "TenantId").Value);
        Assert.Equal("api://exchange", upsert.Parameters.Single(parameter => parameter.Key == "TokenExchangeUrl").Value);

        // The bot's coordinates flow from the record's backing bot resource id.
        Assert.Equal(
            "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev1",
            upsert.BotResourceId);

        // The connection descriptor is mirrored into the record, but never an unmasked secret.
        var stored = Assert.Single(store.Records);
        var connection = Assert.Single(stored.OAuthConnections);
        Assert.Equal("aad-conn", connection.ConnectionName);
        Assert.Equal("provider-id-Aadv2", connection.ServiceProviderId);
    }

    [Fact]
    public async Task CreateAsync_SecureTypedParameter_IsMaskedInResultStoreAndRawArmResponse()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort
        {
            Providers =
            [
                new OAuthProviderDefinition
                {
                    Name = "Aad",
                    Id = "provider-id-certificate",
                    DisplayName = "Custom certificate provider",
                    Parameters =
                    [
                        new() { Name = "ClientId", DisplayName = "Client id", Type = "string", IsRequired = true },
                        new() { Name = "Certificate", DisplayName = "Certificate", Type = "securestring", IsRequired = true },
                    ],
                },
            ],
            UpsertRawArmResponse = Arm(
                """
                {
                  "properties": {
                    "parameters": [
                      { "key": "Certificate", "value": "must-not-render" }
                    ]
                  }
                }
                """),
        };

        var report = await Service(store, port).CreateAsync(
            CreateRequest(
                provider: "Aad",
                clientSecret: null,
                parameters: ["Certificate=must-not-persist"]),
            CancellationToken.None);

        Assert.Equal("***", report.Payload!.Parameters["Certificate"]);
        Assert.Equal(
            "***",
            Assert.Single(Assert.Single(store.Records).OAuthConnections).Parameters["Certificate"]);
        var rawParameter = Assert.Single(
            report.ArmResponse!.Value.GetProperty("properties").GetProperty("parameters").EnumerateArray());
        Assert.Equal("Certificate", rawParameter.GetProperty("key").GetString());
        Assert.Equal("***", rawParameter.GetProperty("value").GetString());
    }

    [Fact]
    public async Task CreateAsync_AppliesCatalogueScopesDefault()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort
        {
            Providers =
            [
                new OAuthProviderDefinition
                {
                    Name = "Aadv2",
                    Id = "provider-id-Aadv2",
                    DisplayName = "Azure Active Directory v2",
                    Parameters =
                    [
                        new() { Name = "ClientId", DisplayName = "Client id", Type = "string", IsRequired = true },
                        new() { Name = "ClientSecret", DisplayName = "Client secret", Type = "securestring", IsRequired = true },
                        new() { Name = "Scopes", DisplayName = "Scopes", Type = "string", DefaultValue = "openid profile" },
                    ],
                },
            ],
        };
        var service = Service(store, port);

        await service.CreateAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("openid profile", Assert.Single(port.Upserts).Scopes);
    }

    [Fact]
    public async Task ShowAsync_Missing_ThrowsNotFound()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort();
        var service = Service(store, port);

        await Assert.ThrowsAsync<DeploymentException>(() =>
            service.GetAsync(
                new DeploymentOAuthShowRequest { DeploymentId = DeploymentId, ConnectionName = "aad-conn" },
                CancellationToken.None));
    }

    [Fact]
    public async Task UpdateAsync_OnlyChangesProvidedValuesAndPreservesProvider()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort
        {
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "aad-conn",
                ServiceProviderId = "provider-id-Aadv2",
                ClientId = "client-1",
                Scopes = "openid profile",
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["TenantId"] = "common",
                    ["TokenExchangeUrl"] = "api://old",
                    ["serverGenerated"] = "keep",
                },
            },
        };
        var service = Service(store, port);

        var report = await service.UpdateAsync(
            new DeploymentOAuthUpdateRequest
            {
                DeploymentId = DeploymentId,
                ConnectionName = "aad-conn",
                Parameters = ["TokenExchangeUrl=api://new"],
            },
            CancellationToken.None);

        Assert.Equal(DeploymentOperationStatus.Updated, report.Status);
        var upsert = Assert.Single(port.Upserts);
        Assert.Equal("provider-id-Aadv2", upsert.ServiceProviderId);
        Assert.Equal("client-1", upsert.ClientId);
        Assert.Null(upsert.ClientSecret);
        Assert.Equal("openid profile", upsert.Scopes);
        Assert.Equal("common", upsert.Parameters.Single(p => p.Key == "TenantId").Value);
        Assert.Equal("api://new", upsert.Parameters.Single(p => p.Key == "TokenExchangeUrl").Value);
        Assert.DoesNotContain(upsert.Parameters, parameter => parameter.Key == "serverGenerated");
    }

    [Fact]
    public async Task UpdateAsync_OmittedScopes_PreservesCaseDistinctExistingScopes()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort
        {
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "aad-conn",
                ServiceProviderId = "provider-id-Aadv2",
                ClientId = "client-1",
                Scopes = "Read read",
            },
        };

        await Service(store, port).UpdateAsync(
            new DeploymentOAuthUpdateRequest
            {
                DeploymentId = DeploymentId,
                ConnectionName = "aad-conn",
                ClientId = "client-2",
            },
            CancellationToken.None);

        Assert.Equal("Read read", Assert.Single(port.Upserts).Scopes);
    }

    [Fact]
    public async Task UpdateAsync_UsesCanonicalValidatedDeploymentIdForLookup()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort
        {
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "aad-conn",
                ServiceProviderId = "provider-id-Aadv2",
                ClientId = "client-1",
            },
        };

        var report = await Service(store, port).UpdateAsync(
            new DeploymentOAuthUpdateRequest
            {
                DeploymentId = Guid.Parse(DeploymentId).ToString("N"),
                ConnectionName = "aad-conn",
                ClientId = "client-2",
            },
            CancellationToken.None);

        Assert.Equal(DeploymentOperationStatus.Updated, report.Status);
    }
    [Fact]
    public async Task UpdateAsync_ClearsOptionalValues()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort
        {
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "aad-conn",
                ServiceProviderId = "provider-id-Aadv2",
                ClientId = "client-1",
                Scopes = "openid profile",
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["TenantId"] = "common",
                    ["TokenExchangeUrl"] = "api://old",
                },
            },
        };
        var service = Service(store, port);

        await service.UpdateAsync(
            new DeploymentOAuthUpdateRequest
            {
                DeploymentId = DeploymentId,
                ConnectionName = "aad-conn",
                Scopes = string.Empty,
                Parameters = ["TokenExchangeUrl="],
            },
            CancellationToken.None);

        var upsert = Assert.Single(port.Upserts);
        Assert.Equal(string.Empty, upsert.Scopes);
        Assert.Equal(string.Empty, upsert.Parameters.Single(p => p.Key == "TokenExchangeUrl").Value);
        Assert.Equal("common", upsert.Parameters.Single(p => p.Key == "TenantId").Value);
    }

    [Fact]
    public async Task UpdateAsync_CannotClearRequiredClientId()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort
        {
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "aad-conn",
                ServiceProviderId = "provider-id-Aadv2",
                ClientId = "client-1",
            },
        };
        var service = Service(store, port);

        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            service.UpdateAsync(
                new DeploymentOAuthUpdateRequest
                {
                    DeploymentId = DeploymentId,
                    ConnectionName = "aad-conn",
                    ClientId = string.Empty,
                },
                CancellationToken.None));

        Assert.Contains("cannot be cleared", ex.Message);
        Assert.Empty(port.Upserts);
    }

    [Fact]
    public async Task UpdateAsync_ValidatesAgainstLiveProviderAfterConnectionGet()
    {
        var store = new FakeStore(ProvisionedRecord() with
        {
            OAuthConnections =
            [
                new DeploymentOAuthConnection
                {
                    ResourceId = "/connections/aad-conn",
                    ConnectionName = "aad-conn",
                    ServiceProviderId = "provider-id-Aadv2",
                    ServiceProviderDisplayName = "Azure Active Directory v2",
                },
            ],
        });
        var port = new FakeOAuthPort
        {
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "aad-conn",
                ServiceProviderId = "provider-id-Aadv2",
            },
        };

        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            Service(store, port).UpdateAsync(
                new DeploymentOAuthUpdateRequest
                {
                    DeploymentId = DeploymentId,
                    ConnectionName = "aad-conn",
                    Parameters = ["DoesNotExist=value"],
                },
                CancellationToken.None));

        Assert.Contains("DoesNotExist", ex.Message);
        Assert.Equal(1, port.GetCallCount);
        Assert.Empty(port.Upserts);
    }

    [Fact]
    public async Task UpdateAsync_MissingPersistedProviderUsesLiveProvider()
    {
        var port = new FakeOAuthPort
        {
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "aad-conn",
                ServiceProviderId = "provider-id-Aadv2",
                ClientId = "client-1",
            },
        };

        var result = await Service(new FakeStore(ProvisionedRecord()), port).UpdateAsync(
            new DeploymentOAuthUpdateRequest
            {
                DeploymentId = DeploymentId,
                ConnectionName = "aad-conn",
                Scopes = "openid",
            },
            CancellationToken.None);

        Assert.Equal(DeploymentOperationStatus.Updated, result.Status);
        Assert.Equal(1, port.GetCallCount);
        Assert.Single(port.Upserts);
    }

    [Fact]
    public async Task UpdateAsync_StalePersistedProviderUsesLiveProvider()
    {
        var store = new FakeStore(ProvisionedRecord() with
        {
            OAuthConnections =
            [
                new DeploymentOAuthConnection
                {
                    ResourceId = "/connections/aad-conn",
                    ConnectionName = "aad-conn",
                    ServiceProviderId = "stale-provider-id",
                    ServiceProviderDisplayName = "Stale provider",
                },
            ],
        });
        var port = new FakeOAuthPort
        {
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "aad-conn",
                ServiceProviderId = "provider-id-Aadv2",
                ClientId = "client-1",
            },
        };

        var result = await Service(store, port).UpdateAsync(
            new DeploymentOAuthUpdateRequest
            {
                DeploymentId = DeploymentId,
                ConnectionName = "aad-conn",
                Scopes = "openid",
            },
            CancellationToken.None);

        Assert.Equal(DeploymentOperationStatus.Updated, result.Status);
        Assert.Equal(1, port.GetCallCount);
        Assert.Equal("provider-id-Aadv2", Assert.Single(port.Upserts).ServiceProviderId);
    }

    [Fact]
    public async Task UpdateAsync_CannotClearRequiredProviderParameter()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort
        {
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "fic",
                ServiceProviderId = "provider-id-Fic",
                ClientId = "client-1",
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["UniqueIdentifier"] = "uid-1",
                },
            },
        };
        var service = Service(store, port);

        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            service.UpdateAsync(
                new DeploymentOAuthUpdateRequest
                {
                    DeploymentId = DeploymentId,
                    ConnectionName = "fic",
                    Parameters = ["UniqueIdentifier="],
                },
                CancellationToken.None));

        Assert.Contains("cannot be cleared", ex.Message);
        Assert.Empty(port.Upserts);
    }

    [Fact]
    public async Task UpdateAsync_MissingConnection_ThrowsNotFound()
    {
        var service = Service(new FakeStore(ProvisionedRecord()), new FakeOAuthPort());

        await Assert.ThrowsAsync<DeploymentException>(() =>
            service.UpdateAsync(
                new DeploymentOAuthUpdateRequest
                {
                    DeploymentId = DeploymentId,
                    ConnectionName = "missing",
                    Scopes = "openid",
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task UpdateAsync_UnknownLiveProviderId_DoesNotChangeProvider()
    {
        var store = new FakeStore(ProvisionedRecord() with
        {
            OAuthConnections =
            [
                new DeploymentOAuthConnection
                {
                    ResourceId = "/connections/aad-conn",
                    ConnectionName = "aad-conn",
                    ServiceProviderId = "provider-id-Aadv2",
                    ServiceProviderDisplayName = "Azure Active Directory v2",
                },
            ],
        });
        var port = new FakeOAuthPort
        {
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "aad-conn",
                ServiceProviderId = "unknown-live-provider",
                ServiceProviderDisplayName = "Azure Active Directory v2",
                ClientId = "client-1",
            },
        };
        var service = Service(store, port);

        await Assert.ThrowsAsync<DeploymentException>(() =>
            service.UpdateAsync(
                new DeploymentOAuthUpdateRequest
                {
                    DeploymentId = DeploymentId,
                    ConnectionName = "aad-conn",
                    Scopes = "openid",
                },
                CancellationToken.None));

        Assert.Empty(port.Upserts);
    }

    [Fact]
    public async Task ShowAsync_Found_ReturnsRetrievedAndPersists()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort
        {
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "aad-conn",
                ServiceProviderId = "provider-id-Aadv2",
                ClientId = "client-1",
            },
        };
        var service = Service(store, port);

        var report = await service.GetAsync(
            new DeploymentOAuthShowRequest { DeploymentId = DeploymentId, ConnectionName = "aad-conn" },
            CancellationToken.None);

        Assert.Equal(DeploymentOperationStatus.Retrieved, report.Status);
        Assert.Equal("client-1", report.Payload!.ClientId);
        Assert.Single(Assert.Single(store.Records).OAuthConnections);
    }

    [Fact]
    public async Task ShowAsync_PersistsMaskedSecureProviderParameters()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort
        {
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "aad-conn",
                ServiceProviderId = "provider-id-Aadv2",
                ClientId = "client-1",
                Scopes = "openid",
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["ClientSecret"] = "must-not-persist",
                    ["TenantId"] = "common",
                },
            },
        };
        var service = Service(store, port);

        await service.GetAsync(
            new DeploymentOAuthShowRequest { DeploymentId = DeploymentId, ConnectionName = "aad-conn" },
            CancellationToken.None);

        var persisted = Assert.Single(Assert.Single(store.Records).OAuthConnections);
        Assert.Equal("client-1", persisted.ClientId);
        Assert.Equal("common", persisted.Parameters!["TenantId"]);
        Assert.DoesNotContain("ClientId", persisted.Parameters.Keys);
        Assert.Equal("***", persisted.Parameters["ClientSecret"]);
        Assert.DoesNotContain("Scopes", persisted.Parameters.Keys);
        Assert.Null(persisted.RawArmResponse);
    }

    [Fact]
    public async Task ShowAsync_RedactsSecretsFromRawArmResponse()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort
        {
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "aad-conn",
                ServiceProviderId = "provider-id-Aadv2",
                RawArmResponse = Arm(
                    """
                    {
                      "name": "aad-conn",
                      "properties": {
                        "clientSecret": "must-not-render",
                        "parameters": [
                          { "key": "ClientSecret", "value": "must-not-render" },
                          { "key": "TenantId", "value": "common" }
                        ]
                      }
                    }
                    """),
            },
        };
        var service = Service(store, port);

        var report = await service.GetAsync(
            new DeploymentOAuthShowRequest { DeploymentId = DeploymentId, ConnectionName = "aad-conn" },
            CancellationToken.None);

        var properties = report.ArmResponse!.Value.GetProperty("properties");
        Assert.Equal("***", properties.GetProperty("clientSecret").GetString());
        var parameters = properties.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(2, parameters.Length);
        Assert.Equal("***", parameters[0].GetProperty("value").GetString());
        Assert.Equal("TenantId", parameters[1].GetProperty("key").GetString());
        Assert.Equal("common", parameters[1].GetProperty("value").GetString());
    }

    [Fact]
    public async Task ShowAsync_UnknownProvider_RedactsSecretLikeParameters()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort
        {
            Providers = [],
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "aad-conn",
                ServiceProviderId = "unknown-provider",
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["UnknownSecret"] = "must-not-persist",
                    ["UnknownValue"] = "safe-value",
                },
            },
        };
        var service = Service(store, port);

        await service.GetAsync(
            new DeploymentOAuthShowRequest { DeploymentId = DeploymentId, ConnectionName = "aad-conn" },
            CancellationToken.None);

        var parameters = Assert.Single(Assert.Single(store.Records).OAuthConnections).Parameters;
        Assert.Equal("***", parameters["UnknownSecret"]);
        Assert.Equal("safe-value", parameters["UnknownValue"]);
    }

    [Fact]
    public async Task ShowAsync_PersistsArmProviderIdAndDisplayName()
    {
        var store = new FakeStore(ProvisionedRecord() with
        {
            OAuthConnections =
            [
                new DeploymentOAuthConnection
                {
                    ResourceId = "/connections/aad-conn",
                    ConnectionName = "aad-conn",
                    ServiceProviderId = "stale-provider-id",
                    ServiceProviderDisplayName = "Stale provider",
                },
            ],
        });
        var port = new FakeOAuthPort
        {
            Providers = [],
            Existing = new FakeOAuthConnectionResponse
            {
                ConnectionName = "aad-conn",
                ServiceProviderId = "provider-id-Aadv2",
                ServiceProviderDisplayName = "Azure Active Directory v2",
            },
        };

        await Service(store, port).GetAsync(
            new DeploymentOAuthShowRequest { DeploymentId = DeploymentId, ConnectionName = "aad-conn" },
            CancellationToken.None);

        var connection = Assert.Single(Assert.Single(store.Records).OAuthConnections);
        Assert.Equal("provider-id-Aadv2", connection.ServiceProviderId);
        Assert.Equal("Azure Active Directory v2", connection.ServiceProviderDisplayName);
    }

    [Fact]
    public async Task ListAsync_ReplacesRecordConnections()
    {
        var store = new FakeStore(ProvisionedRecord());
        var port = new FakeOAuthPort
        {
            All =
            [
                new FakeOAuthConnectionResponse { ConnectionName = "a", ServiceProviderId = "provider-a" },
                new FakeOAuthConnectionResponse { ConnectionName = "b", ServiceProviderId = "provider-b" },
            ],
        };
        var service = Service(store, port);

        var report = await service.ListAsync(
            new DeploymentOAuthListRequest { DeploymentId = DeploymentId },
            CancellationToken.None);

        Assert.Equal(DeploymentOperationStatus.Listed, report.Status);
        Assert.Equal(2, report.Payload!.Length);
        Assert.Equal(2, Assert.Single(store.Records).OAuthConnections.Count);
    }

    [Fact]
    public async Task DeleteAsync_RemovesConnectionFromRecord()
    {
        var seed = ProvisionedRecord() with
        {
            OAuthConnections =
            [
                new DeploymentOAuthConnection
                {
                    ResourceId = "/connections/aad-conn",
                    ConnectionName = "aad-conn",
                    ServiceProviderId = "provider-id-Aadv2",
                    ServiceProviderDisplayName = "Azure Active Directory v2",
                },
            ],
        };
        var store = new FakeStore(seed);
        var port = new FakeOAuthPort { IsDeleteSuccessful = false };
        var service = Service(store, port);

        var report = await service.DeleteAsync(
            new DeploymentOAuthDeleteRequest { DeploymentId = DeploymentId, ConnectionName = "aad-conn" },
            CancellationToken.None);

        Assert.Equal(DeploymentOperationStatus.Deleted, report.Status);
        Assert.Null(report.Payload);
        Assert.Equal("aad-conn", report.TargetInstanceName);
        Assert.Single(port.DeletedBotResourceIds);
        Assert.Empty(Assert.Single(store.Records).OAuthConnections);
    }

    [Fact]
    public async Task ListProvidersAsync_ReturnsLiveCatalogue()
    {
        var service = Service(
            new FakeStore(ProvisionedRecord()),
            new FakeOAuthPort());

        var report = await service.ListProvidersAsync(
            new DeploymentOAuthProviderListRequest(),
            CancellationToken.None);

        Assert.Equal(2, report.Length);
    }

    [Fact]
    public async Task ListProvidersAsync_UsesSubscriptionResolverWithoutDeployment()
    {
        var resolver = new FakeSubscriptionResolver();
        var service = new DeploymentOAuthService(
            new FakeStore(),
            new FakeOAuthPort(),
            new CompositeDeploymentTransport(subscriptionResolver: resolver));

        await service.ListProvidersAsync(
            new DeploymentOAuthProviderListRequest
            {
                SubscriptionId = "22222222-2222-2222-2222-222222222222",
                TenantId = "33333333-3333-3333-3333-333333333333",
            },
            CancellationToken.None);

        Assert.Equal("22222222-2222-2222-2222-222222222222", resolver.RequestedPreferred);
        Assert.Equal("33333333-3333-3333-3333-333333333333", resolver.RequestedTenant);
    }

    [Fact]
    public async Task ListProvidersAsync_Filter_MatchesCaseInsensitively()
    {
        var service = Service(
            new FakeStore(ProvisionedRecord()),
            new FakeOAuthPort());

        var report = await service.ListProvidersAsync(
            new DeploymentOAuthProviderListRequest
            {
                ServiceProviderName = "aadv2withfic",
            },
            CancellationToken.None);

        Assert.Equal("Aadv2WithFic", Assert.Single(report).Name);
    }

    private sealed class FakeStore : IDeploymentStore
    {
        public FakeStore(params CodeAgentDeployment[] seed) => Records = seed.ToList();

        public List<CodeAgentDeployment> Records { get; private set; }

        public IReadOnlyList<CodeAgentDeployment> List(string? project) => Records.ToList();

        public void Upsert(string? project, CodeAgentDeployment deployment)
        {
            Records = Records
                .Where(r => !string.Equals(r.DeploymentId, deployment.DeploymentId, StringComparison.OrdinalIgnoreCase))
                .Append(deployment)
                .ToList();
        }

        public void Remove(string? project, string deploymentId)
        {
            Records = Records
                .Where(r => !string.Equals(r.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private sealed class FakeOAuthPort : IDeploymentOAuthTransport
    {
        public IReadOnlyList<OAuthProviderDefinition> Providers { get; set; } =
        [
            new()
            {
                Name = "Aadv2",
                Id = "provider-id-Aadv2",
                DisplayName = "Azure Active Directory v2",
                Parameters =
                [
                    new() { Name = "ClientId", DisplayName = "Client id", Type = "string", IsRequired = true },
                    new() { Name = "ClientSecret", DisplayName = "Client secret", Type = "securestring", IsRequired = true },
                    new() { Name = "TokenExchangeUrl", DisplayName = "Token exchange URL", Type = "string" },
                    new() { Name = "TenantId", DisplayName = "Tenant id", Type = "string", DefaultValue = "common" },
                ],
            },
            new()
            {
                Name = "Aadv2WithFic",
                Id = "provider-id-Fic",
                DisplayName = "AAD v2 with Federated Credentials",
                Parameters =
                [
                    new() { Name = "ClientId", DisplayName = "Client id", Type = "string", IsRequired = true },
                    new() { Name = "UniqueIdentifier", DisplayName = "Unique identifier", Type = "string", IsRequired = true },
                ],
            },
        ];

        public FakeOAuthConnectionResponse? Existing { get; set; }

        public Exception? GetThrows { get; set; }

        public IReadOnlyList<FakeOAuthConnectionResponse> All { get; set; } = [];

        public List<BotServiceOAuthUpsertRequest> Upserts { get; } = [];

        public List<string> DeletedBotResourceIds { get; } = [];

        public bool IsDeleteSuccessful { get; set; } = true;
        public int ProviderListCallCount { get; private set; }
        public int GetCallCount { get; private set; }
        public JsonElement? UpsertRawArmResponse { get; set; }

        public Task<IReadOnlyList<OAuthProviderDefinition>> ListServiceProvidersAsync(
            string subscriptionId,
            string? targetTenantId,
            CancellationToken cancellationToken)
        {
            ProviderListCallCount++;
            return Task.FromResult(Providers);
        }

        public Task<DeploymentOAuthConnection> UpsertOAuthConnectionAsync(
            BotServiceOAuthUpsertRequest upsert,
            CancellationToken cancellationToken)
        {
            Upserts.Add(upsert);
            return Task.FromResult(new DeploymentOAuthConnection
            {
                ResourceId = $"/connections/{upsert.ConnectionName}",
                ConnectionName = upsert.ConnectionName,
                ServiceProviderDisplayName = Providers.FirstOrDefault(provider =>
                    string.Equals(provider.Id, upsert.ServiceProviderId, StringComparison.OrdinalIgnoreCase))?.DisplayName
                    ?? string.Empty,
                ClientId = upsert.ClientId,
                Scopes = ParseScopes(upsert.Scopes),
                Parameters = upsert.Parameters.ToDictionary(
                    parameter => parameter.Key,
                    parameter => parameter.Value,
                    StringComparer.Ordinal),
                ServiceProviderId = upsert.ServiceProviderId,
                RawArmResponse = Redact(UpsertRawArmResponse),
            });
        }

        public Task<DeploymentOAuthConnection?> GetOAuthConnectionAsync(
            string connectionName,
            string botResourceId,
            string? targetTenantId,
            CancellationToken cancellationToken)
        {
            GetCallCount++;
            if (GetThrows is not null)
            {
                throw GetThrows;
            }

            return Task.FromResult(Existing is null
                ? null
                : Map(Existing));
        }

        public Task<IReadOnlyList<DeploymentOAuthConnection>> ListOAuthConnectionsAsync(
            string botResourceId,
            string? targetTenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeploymentOAuthConnection>>(
                All.Select(Map).ToArray());

        public Task<bool> DeleteOAuthConnectionAsync(
            string connectionName,
            string botResourceId,
            string? targetTenantId,
            CancellationToken cancellationToken)
        {
            DeletedBotResourceIds.Add(botResourceId);
            return Task.FromResult(IsDeleteSuccessful);
        }

        private static DeploymentOAuthConnection Map(FakeOAuthConnectionResponse response)
        {
            return new DeploymentOAuthConnection
            {
                ConnectionName = response.ConnectionName,
                ServiceProviderId = response.ServiceProviderId,
                ServiceProviderDisplayName = response.ServiceProviderDisplayName ?? string.Empty,
                ClientId = response.ClientId,
                Scopes = ParseScopes(response.Scopes),
                Parameters = response.Parameters.ToDictionary(
                    parameter => parameter.Key,
                    parameter => parameter.Value,
                    StringComparer.Ordinal),
                ResourceId = response.ResourceId ?? string.Empty,
                RawArmResponse = Redact(response.RawArmResponse),
            };
        }

        private static string[] ParseScopes(string? scopes) =>
            string.IsNullOrWhiteSpace(scopes)
                ? []
                : scopes.Split(
                    [' ', ','],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static JsonElement? Redact(JsonElement? response)
            => BotServiceResponseRedactor.RedactOAuth(response);

    }

    private sealed record FakeOAuthConnectionResponse
    {
        public required string ConnectionName { get; init; }
        public required string ServiceProviderId { get; init; }
        public string? ServiceProviderDisplayName { get; init; }
        public string? ClientId { get; init; }
        public string? Scopes { get; init; }
        public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
            new Dictionary<string, string?>(StringComparer.Ordinal);
        public string? ResourceId { get; init; }
        public JsonElement? RawArmResponse { get; init; }
    }
}
