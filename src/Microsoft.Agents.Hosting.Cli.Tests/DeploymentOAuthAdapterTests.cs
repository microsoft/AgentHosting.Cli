using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Offline unit tests for the OAuth connection surface of <see cref="DeploymentOAuthAdapter"/>
/// They drive the adapter through a fake <see cref="HttpMessageHandler"/> and assert the exact listAuthServiceProviders,
/// connection PUT/GET/DELETE, and list URLs and bodies, so the OAuth transport is verified without network or MSAL.
/// </summary>
public sealed class DeploymentOAuthAdapterTests
{
    private const string Arm = "https://management.azure.com";
    private const string ApiVersion = "2023-09-15-preview";
    private const string BotResourceId =
        "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev1";
    private const string TenantId = "tenant-1";

    private static readonly OAuthProviderDefinition Provider = new()
    {
        Name = "Aadv2",
        Id = "provider-id",
        DisplayName = "Azure Active Directory v2",
        Parameters =
        [
            new() { Name = "ClientId", DisplayName = "Client id", Type = "string", IsRequired = true },
            new() { Name = "ClientSecret", DisplayName = "Client secret", Type = "securestring", IsRequired = true },
            new() { Name = "TenantId", DisplayName = "Tenant id", Type = "string" },
            new() { Name = "ApiKey", DisplayName = "API key", Type = "securestring" },
        ],
    };

    private static DeploymentOAuthAdapter Adapter(
        StubHttpMessageHandler handler,
        StubTokenProvider? tokenProvider = null) =>
        new(
            new HttpClient(handler),
            tokenProvider ?? new StubTokenProvider { AccessToken = "arm-token", TenantId = "home" });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Empty(HttpStatusCode status) => new(status);

    [Fact]
    public async Task ListServiceProviders_PostsToCatalogueUrl_AndParses()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """
            {"value":[{"properties":{
              "serviceProviderName":"Aadv2",
              "id":"provider-id",
              "displayName":"Azure Active Directory v2",
              "parameters":[
                {"name":"ClientId","type":"string","displayName":"Client id","description":"Application id","default":null,"metadata":{"constraints":{"required":true}}},
                {"name":"ClientSecret","type":"securestring","displayName":"Client secret","description":"Application secret","default":null,"metadata":{"constraints":{"required":true}}},
                {"name":"TenantId","type":"string","displayName":"Tenant","description":"Directory tenant","default":"common","metadata":null},
                {"name":"IsMultiTenantFlowSupported","type":"bool","displayName":"Multi-tenant","description":"Enable flow","default":false,"metadata":null}
              ]
            }}]}
            """));

        var providers = await Adapter(handler).ListServiceProvidersAsync("sub-1", "tenant-1", default);

        var provider = Assert.Single(providers);
        Assert.Equal("Aadv2", provider.Name);
        Assert.Equal("provider-id", provider.Id);
        Assert.Equal(4, provider.Parameters.Count);
        Assert.True(provider.Parameters[0].IsRequired);
        Assert.Equal("securestring", provider.Parameters[1].Type);
        Assert.Equal("common", provider.Parameters[2].DefaultValue);
        Assert.Equal("Directory tenant", provider.Parameters[2].Description);
        Assert.Equal("false", provider.Parameters[3].DefaultValue);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            $"{Arm}/subscriptions/sub-1/providers/Microsoft.BotService/listAuthServiceProviders?api-version={ApiVersion}",
            request.Uri.ToString());
    }

    [Fact]
    public async Task ListServiceProviders_MalformedResponse_ThrowsInvalidResponse()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, """{"unexpected":[]}"""));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler).ListServiceProvidersAsync("sub-1", "tenant-1", default));

        Assert.Equal(ProvisioningErrorKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public async Task UpsertConnection_PutsExpectedBody_WithSecret()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """{"name":"aad-conn","properties":{"serviceProviderId":"provider-id","serviceProviderDisplayName":"Azure Active Directory v2","clientId":"client-1","provisioningState":"Succeeded"}}"""));

        var upsert = new BotServiceOAuthUpsertRequest
        {
            BotResourceId = BotResourceId,
            TenantId = TenantId,
            ConnectionName = "aad-conn",
            ServiceProviderId = Provider.Id,
            ClientId = "client-1",
            ClientSecret = "secret-1",
            Scopes = "openid",
            Parameters = [new KeyValuePair<string, string?>("TenantId", "common")],
        };

        var snapshot = await Adapter(handler).UpsertOAuthConnectionAsync(upsert, default);

        Assert.Equal("aad-conn", snapshot.ConnectionName);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal(
            $"{Arm}/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev1/connections/aad-conn?api-version={ApiVersion}",
            request.Uri.ToString());

        using var doc = JsonDocument.Parse(request.Body!);
        var props = doc.RootElement.GetProperty("properties");
        Assert.Equal("client-1", props.GetProperty("clientId").GetString());
        Assert.Equal("provider-id", props.GetProperty("serviceProviderId").GetString());
        Assert.Equal("secret-1", props.GetProperty("clientSecret").GetString());
        Assert.Equal("openid", props.GetProperty("scopes").GetString());
        var parameter = props.GetProperty("parameters").EnumerateArray().Single();
        Assert.Equal("TenantId", parameter.GetProperty("key").GetString());
        Assert.Equal("common", parameter.GetProperty("value").GetString());
    }

    [Fact]
    public async Task UpsertConnection_OmitsClientSecret_WhenAbsent()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """{"name":"fic-conn","properties":{"serviceProviderId":"provider-id","serviceProviderDisplayName":"Azure Active Directory v2"}}"""));

        var upsert = new BotServiceOAuthUpsertRequest
        {
            BotResourceId = BotResourceId,
            ConnectionName = "fic-conn",
            ServiceProviderId = Provider.Id,
            ClientId = "client-1",
            ClientSecret = null,
            Parameters = [],
        };

        await Adapter(handler).UpsertOAuthConnectionAsync(upsert, default);

        using var doc = JsonDocument.Parse(handler.Requests.Single().Body!);
        Assert.False(doc.RootElement.GetProperty("properties").TryGetProperty("clientSecret", out _));
    }

    [Fact]
    public async Task UpsertConnection_SendsEmptyOptionalValuesForClearing()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            HttpStatusCode.OK,
            """{"name":"fic-conn","properties":{"serviceProviderId":"provider-id","serviceProviderDisplayName":"Azure Active Directory v2","provisioningState":"Succeeded"}}"""));
        var upsert = new BotServiceOAuthUpsertRequest
        {
            BotResourceId = BotResourceId,
            ConnectionName = "fic-conn",
            ServiceProviderId = Provider.Id,
            ClientId = "client-1",
            Scopes = string.Empty,
            Parameters =
            [
                new KeyValuePair<string, string?>("TokenExchangeUrl", string.Empty),
            ],
        };

        await Adapter(handler).UpsertOAuthConnectionAsync(upsert, default);

        using var document = JsonDocument.Parse(handler.Requests.Single().Body!);
        var properties = document.RootElement.GetProperty("properties");
        Assert.Equal(string.Empty, properties.GetProperty("scopes").GetString());
        Assert.Equal(
            string.Empty,
            properties.GetProperty("parameters")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task GetConnection_Found_ReturnsMappedRedactedConnection()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """
            {
              "id":"/connections/aad-conn",
              "name":"aad-conn",
              "properties":{
                "clientId":"client-1",
                "clientSecret":"secret-1",
                "serviceProviderId":"provider-id",
                "serviceProviderDisplayName":"Azure Active Directory v2",
                "scopes":"openid profile",
                "parameters":[
                  {"key":"ClientId","value":"client-1"},
                  {"key":"TenantId","value":"common"},
                  {"key":"ApiKey","value":"secret-2"}
                ]
              }
            }
            """));

        var connection = await Adapter(handler).GetOAuthConnectionAsync(
            "aad-conn",
            BotResourceId,
            TenantId,
            cancellationToken: default);

        Assert.NotNull(connection);
        Assert.Equal("aad-conn", connection!.ConnectionName);
        Assert.Equal("provider-id", connection.ServiceProviderId);
        Assert.Equal("Azure Active Directory v2", connection.ServiceProviderDisplayName);
        Assert.Equal("client-1", connection.ClientId);
        Assert.Equal(["openid", "profile"], connection.Scopes);
        Assert.Equal("common", connection.Parameters["TenantId"]);
        Assert.DoesNotContain("ClientId", connection.Parameters);
        Assert.Equal("***", connection.Parameters["ApiKey"]);
        var rawProperties = connection.RawArmResponse!.Value.GetProperty("properties");
        Assert.Equal("***", rawProperties.GetProperty("clientSecret").GetString());
        var rawParameters = rawProperties.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(3, rawParameters.Length);
        Assert.Equal(
            "***",
            rawParameters.Single(parameter => parameter.GetProperty("key").GetString() == "ApiKey")
                .GetProperty("value")
                .GetString());
        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task GetConnection_PreservesCaseDistinctScopes()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            HttpStatusCode.OK,
            """{"name":"aad-conn","properties":{"serviceProviderId":"provider-id","serviceProviderDisplayName":"Azure Active Directory v2","scopes":"Read read"}}"""));

        var connection = await Adapter(handler).GetOAuthConnectionAsync(
            "aad-conn",
            BotResourceId,
            TenantId,
            cancellationToken: default);

        Assert.Equal(["Read", "read"], connection!.Scopes);
    }

    [Fact]
    public async Task GetConnection_InvalidBotResourceId_DoesNotAcquireTokenOrSendRequest()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Request must not be sent."));
        var tokenProvider = new StubTokenProvider { AccessToken = "arm-token", TenantId = "home" };

        var ex = await Assert.ThrowsAsync<DeploymentException>(() =>
            Adapter(handler, tokenProvider).GetOAuthConnectionAsync(
                "aad-conn",
                "@attacker.example/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev1",
                TenantId,
                cancellationToken: default));

        Assert.Equal("deployment-invalid-bot", ex.ErrorKind);
        Assert.Empty(tokenProvider.RequestedResources);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetConnection_EscapesValidConnectionNamePathSegment()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """{"name":"name with space","properties":{"serviceProviderId":"provider-id","serviceProviderDisplayName":"Azure Active Directory v2"}}"""));

        await Adapter(handler).GetOAuthConnectionAsync(
            "name with space",
            BotResourceId,
            TenantId,
            cancellationToken: default);

        Assert.Contains("/connections/name%20with%20space?", handler.Requests.Single().Uri.AbsoluteUri);
    }

    [Fact]
    public async Task GetConnection_InvalidResourceName_IsRejectedBeforeTokenOrRequest()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Request must not be sent."));
        var tokenProvider = new StubTokenProvider { AccessToken = "arm-token", TenantId = "home" };

        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            Adapter(handler, tokenProvider).GetOAuthConnectionAsync(
                "name/with/slashes",
                BotResourceId,
                TenantId,
                cancellationToken: default));

        Assert.Contains("--connection", ex.Message);
        Assert.Empty(tokenProvider.RequestedResources);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetConnection_MissingServiceProviderId_ThrowsInvalidResponse()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            HttpStatusCode.OK,
            """{"name":"aad-conn","properties":{"serviceProviderDisplayName":"Azure Active Directory v2"}}"""));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler).GetOAuthConnectionAsync(
                "aad-conn",
                BotResourceId,
                TenantId,
                cancellationToken: default));

        Assert.Equal(ProvisioningErrorKind.InvalidResponse, ex.Kind);
        Assert.Contains("properties.serviceProviderId", ex.Message);
    }

    [Theory]
    [InlineData("""{"name":"aad-conn"}""")]
    [InlineData("""{"name":"aad-conn","properties":null}""")]
    [InlineData("""{"name":"aad-conn","properties":[]}""")]
    public async Task GetConnection_PropertiesNotAnObject_ThrowsInvalidResponse(string body)
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, body));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler).GetOAuthConnectionAsync(
                "aad-conn",
                BotResourceId,
                TenantId,
                cancellationToken: default));

        Assert.Equal(ProvisioningErrorKind.InvalidResponse, ex.Kind);
        Assert.Contains("'properties' must be a JSON object", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetConnection_ParametersAbsentOrNull_ReturnsEmptyParameters(bool includeNull)
    {
        var parameters = includeNull ? ""","parameters":null""" : string.Empty;
        var handler = new StubHttpMessageHandler(_ => Json(
            HttpStatusCode.OK,
            string.Concat(
                "{\"name\":\"aad-conn\",\"properties\":{\"serviceProviderId\":\"provider-id\",\"serviceProviderDisplayName\":\"Azure Active Directory v2\"",
                parameters,
                "}}")));

        var connection = await Adapter(handler).GetOAuthConnectionAsync(
            "aad-conn",
            BotResourceId,
            TenantId,
            cancellationToken: default);

        Assert.NotNull(connection);
        Assert.Empty(connection.Parameters);
    }

    [Fact]
    public async Task GetConnection_ParametersNotAnArray_ThrowsInvalidResponse()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            HttpStatusCode.OK,
            """{"name":"aad-conn","properties":{"serviceProviderId":"provider-id","serviceProviderDisplayName":"Azure Active Directory v2","parameters":{}}}"""));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler).GetOAuthConnectionAsync(
                "aad-conn",
                BotResourceId,
                TenantId,
                cancellationToken: default));

        Assert.Equal(ProvisioningErrorKind.InvalidResponse, ex.Kind);
        Assert.Contains("'parameters' must be a JSON array or null", ex.Message);
    }

    [Fact]
    public async Task GetConnection_ClientIdOnlyInParameters_IsNotHoisted()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            HttpStatusCode.OK,
            """
            {
              "name":"aad-conn",
              "properties":{
                "serviceProviderId":"provider-id",
                "serviceProviderDisplayName":"Azure Active Directory v2",
                "parameters":[{"key":"ClientId","value":"client-1"}]
              }
            }
            """));

        var connection = await Adapter(handler).GetOAuthConnectionAsync(
            "aad-conn",
            BotResourceId,
            TenantId,
            cancellationToken: default);

        Assert.NotNull(connection);
        Assert.Null(connection.ClientId);
        Assert.DoesNotContain("ClientId", connection.Parameters);
    }

    [Fact]
    public async Task GetConnection_ResponseWithoutName_DoesNotFallBackToRequest()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            HttpStatusCode.OK,
            """
            {
              "properties":{
                "serviceProviderId":"provider-id",
                "serviceProviderDisplayName":"Azure Active Directory v2"
              }
            }
            """));

        var exception = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler).GetOAuthConnectionAsync(
                "aad-conn",
                BotResourceId,
                TenantId,
                cancellationToken: default));

        Assert.Equal(ProvisioningErrorKind.InvalidResponse, exception.Kind);
        Assert.Contains("did not contain name", exception.Message);
    }

    [Fact]
    public async Task GetConnection_NotFound_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.NotFound,
            """{"error":{"code":"NotFound","message":"gone"}}"""));

        var connection = await Adapter(handler).GetOAuthConnectionAsync(
            "aad-conn",
            BotResourceId,
            TenantId,
            cancellationToken: default);

        Assert.Null(connection);
    }

    [Fact]
    public async Task ListConnections_GetsCollectionUrl_AndParses()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """{"value":[{"name":"a","properties":{"serviceProviderId":"provider-a","serviceProviderDisplayName":"Provider A","clientId":"c-a"}},{"name":"b","properties":{"serviceProviderId":"provider-b","serviceProviderDisplayName":"Provider B","clientId":"c-b"}}]}"""));

        var connections = await Adapter(handler).ListOAuthConnectionsAsync(
            BotResourceId,
            TenantId,
            default);

        Assert.Equal(2, connections.Count);
        Assert.Equal("provider-a", connections[0].ServiceProviderId);
        Assert.Equal("Provider A", connections[0].ServiceProviderDisplayName);
        var raw = connections[0].RawArmResponse!.Value;
        Assert.False(raw.TryGetProperty("kind", out _));
        Assert.False(raw.TryGetProperty("sku", out _));
        Assert.False(raw.TryGetProperty("resourceGroup", out _));
        var rawProperties = raw.GetProperty("properties");
        Assert.Equal("c-a", rawProperties.GetProperty("clientId").GetString());
        Assert.False(rawProperties.TryGetProperty("clientSecret", out _));
        Assert.False(rawProperties.TryGetProperty("parameters", out _));
        Assert.False(rawProperties.TryGetProperty("scopes", out _));
        Assert.False(rawProperties.TryGetProperty("settingId", out _));
        Assert.Equal(
            $"{Arm}/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev1/connections?api-version={ApiVersion}",
            handler.Requests.Single().Uri.ToString());
    }

    [Fact]
    public async Task ListConnections_CrossOriginNextLink_IsRejectedBeforeSecondTokenOrRequest()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            HttpStatusCode.OK,
            """{"value":[],"nextLink":"https://attacker.example/connections?page=2"}"""));
        var tokenProvider = new StubTokenProvider { AccessToken = "arm-token", TenantId = "home" };

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler, tokenProvider).ListOAuthConnectionsAsync(
                BotResourceId,
                TenantId,
                default));

        Assert.Equal(ProvisioningErrorKind.InvalidResponse, ex.Kind);
        Assert.Single(tokenProvider.RequestedResources);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListConnections_MalformedResponse_ThrowsInvalidResponse()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, """{"unexpected":[]}"""));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler).ListOAuthConnectionsAsync(
                BotResourceId,
                TenantId,
                default));

        Assert.Equal(ProvisioningErrorKind.InvalidResponse, ex.Kind);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"connections\"")]
    public async Task ListConnections_ValueNotAnArray_ThrowsInvalidResponse(string value)
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            HttpStatusCode.OK,
            $$"""{"value":{{value}}}"""));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler).ListOAuthConnectionsAsync(
                BotResourceId,
                TenantId,
                default));

        Assert.Equal(ProvisioningErrorKind.InvalidResponse, ex.Kind);
        Assert.Contains("'value' must be a JSON array", ex.Message);
    }

    [Fact]
    public async Task DeleteConnection_Ok_ReturnsTrue()
    {
        var handler = new StubHttpMessageHandler(_ => Empty(HttpStatusCode.OK));

        var deleted = await Adapter(handler).DeleteOAuthConnectionAsync(
            "aad-conn",
            BotResourceId,
            TenantId,
            default);

        Assert.True(deleted);
        Assert.Equal(HttpMethod.Delete, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task DeleteConnection_NotFound_ReturnsFalse()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.NotFound,
            """{"error":{"code":"NotFound","message":"gone"}}"""));

        var deleted = await Adapter(handler).DeleteOAuthConnectionAsync(
            "aad-conn",
            BotResourceId,
            TenantId,
            default);

        Assert.False(deleted);
    }
}
