using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Offline unit tests for <see cref="DeploymentAdapter"/> — the deployment module's bot-lifecycle
/// adapter, which also carries the inherited subscription/resource-group resolvers. They drive the
/// adapter through a fake <see cref="HttpMessageHandler"/> (no network, no MSAL, no <c>az</c>
/// subprocess) and assert the exact checkNameAvailability, bot PUT, and
/// <c>GET /subscriptions[/{id}/resourcegroups]</c> contracts.
/// </summary>
public sealed class DeploymentAdapterTests
{
    private const string Arm = "https://management.azure.com";
    private const string BotApiVersion = "2023-09-15-preview";
    private const string BotResourceId =
        "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev1";

    private static DeploymentAdapter Adapter(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), new StubTokenProvider { AccessToken = "arm-token", TenantId = "home" });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Empty(HttpStatusCode status) => new(status);

    [Fact]
    public async Task ResolveSubscription_SingleEnabled_ReturnsItAndHitsSubscriptionsUrl()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """{"value":[{"subscriptionId":"sub-1","tenantId":"tenant-1","displayName":"Sub One","state":"Enabled"}]}"""));

        var result = await Adapter(handler).ResolveSubscriptionAsync(null, "tenant-1", default);

        Assert.Equal("sub-1", result.SubscriptionId);
        Assert.Equal("tenant-1", result.TenantId);
        Assert.Equal($"{Arm}/subscriptions?api-version=2020-01-01", handler.Requests.Single().Uri.ToString());
    }

    [Fact]
    public async Task ResolveSubscription_MultipleEnabled_NoPreference_ThrowsAmbiguousContext()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """{"value":[{"subscriptionId":"sub-a","tenantId":"tenant-a","state":"Enabled"},{"subscriptionId":"sub-b","tenantId":"tenant-b","state":"Enabled"}]}"""));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler).ResolveSubscriptionAsync(null, null, default));

        Assert.Equal(ProvisioningErrorKind.AmbiguousContext, ex.Kind);
    }

    [Fact]
    public async Task ResolveResourceGroup_Single_ReturnsItAndHitsResourceGroupsUrl()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """{"value":[{"name":"rg-1","location":"eastus"}]}"""));

        var result = await Adapter(handler).ResolveResourceGroupAsync("sub-1", null, "tenant-1", default);

        Assert.Equal("rg-1", result.Name);
        Assert.Equal("eastus", result.Location);
        Assert.Equal(
            $"{Arm}/subscriptions/sub-1/resourcegroups?api-version=2021-04-01",
            handler.Requests.Single().Uri.ToString());
    }

    [Fact]
    public async Task ResolveResourceGroup_None_ThrowsMissingContext()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, """{"value":[]}"""));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler).ResolveResourceGroupAsync("sub-1", null, null, default));

        Assert.Equal(ProvisioningErrorKind.MissingContext, ex.Kind);
    }

    [Fact]
    public async Task ResolveResourceGroup_Preferred_Found_ReturnsCanonicalWithoutExtraCall()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """{"value":[{"name":"rg-1","location":"eastus"},{"name":"rg-2","location":"westus"}]}"""));

        // Case-insensitive match against the single listing; returns the canonical name/region.
        var result = await Adapter(handler).ResolveResourceGroupAsync("sub-1", "RG-2", "tenant-1", default);

        Assert.Equal("rg-2", result.Name);
        Assert.Equal("westus", result.Location);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ResolveResourceGroup_Preferred_NotFound_ThrowsMissingContext()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """{"value":[{"name":"rg-1","location":"eastus"}]}"""));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler).ResolveResourceGroupAsync("sub-1", "rg-missing", null, default));

        Assert.Equal(ProvisioningErrorKind.MissingContext, ex.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ResolveResourceGroup_Multiple_NoPreference_ThrowsAmbiguousContext()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """{"value":[{"name":"rg-1","location":"eastus"},{"name":"rg-2","location":"westus"}]}"""));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            Adapter(handler).ResolveResourceGroupAsync("sub-1", null, null, default));

        Assert.Equal(ProvisioningErrorKind.AmbiguousContext, ex.Kind);
    }

    [Fact]
    public async Task CheckBotNameAvailability_Valid_ParsesResponseAndUrlAndBody()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.OK, """{"valid":true,"message":"","absCode":null}"""));
        var adapter = Adapter(handler);

        var result = await adapter.CheckBotNameAvailabilityAsync("TestBot02", "tenant-contoso", default);

        Assert.True(result.IsValid);
        Assert.Equal(string.Empty, result.Message);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            $"{Arm}/providers/Microsoft.BotService/checkNameAvailability?api-version={BotApiVersion}",
            request.Uri.ToString());
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("TestBot02", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("azurebot", body.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task CheckBotNameAvailability_Unavailable_ParsesReason()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.OK, """{"valid":false,"message":"Bot name is not available.","absCode":"NameConflict"}"""));

        var result = await Adapter(handler).CheckBotNameAvailabilityAsync("taken", "tenant-contoso", default);

        Assert.False(result.IsValid);
        Assert.Equal("Bot name is not available.", result.Message);
    }

    [Fact]
    public async Task TrueE2eReplay_MirrorsAzBotCreateTrace_CheckNameThenPutBotServices()
    {
        // Replays the reference `az bot create --debug` sequence offline: the provider-level
        // checkNameAvailability POST (200 {valid:true}) followed by the botServices PUT (201 with the
        // full bot resource), asserting the deployment adapter reproduces the same two calls, URLs,
        // api-version, SingleTenant body, and success projection — without az, MSAL, or network.
        const string appId = "604485e2-ecb4-4f37-8c2e-bcfba6b6c544";
        const string tenantId = "a30263b9-1caf-4db5-ab53-ed3850c0bd1f";
        const string subscription = "fc2deb45-cdec-4675-9ded-229ed3176ba7";
        const string resourceGroup = "rg-2599";
        const string botName = "TestBot02";
        var botResourceId =
            $"/subscriptions/{subscription}/resourceGroups/{resourceGroup}" +
            $"/providers/Microsoft.BotService/botServices/{botName}";

        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("checkNameAvailability", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"valid":true,"message":"","absCode":null}""")
                : Json(HttpStatusCode.Created, $$"""
                    {
                      "id": "{{botResourceId}}",
                      "name": "{{botName}}",
                      "type": "Microsoft.BotService/botServices",
                      "location": "global",
                      "sku": { "name": "F0" },
                      "kind": "azurebot",
                      "properties": {
                        "displayName": "{{botName}}",
                        "endpoint": "",
                        "msaAppId": "{{appId}}",
                        "msaAppTenantId": "{{tenantId}}",
                        "msaAppType": "SingleTenant",
                        "schemaTransformationVersion": "1.3",
                        "provisioningState": "Succeeded"
                      }
                    }
                    """));
        var adapter = new DeploymentAdapter(
            new HttpClient(handler),
            new StubTokenProvider { AccessToken = "arm-token", TenantId = tenantId });

        var availability = await adapter.CheckBotNameAvailabilityAsync(botName, tenantId, default);
        Assert.True(availability.IsValid);

        var snapshot = await adapter.CreateBotAsync(
            new DeploymentCreateRequest
            {
                AgentId = "11111111-1111-1111-1111-111111111111",
                SubscriptionId = subscription,
                ResourceGroup = resourceGroup,
                DeploymentName = botName,
                DeploymentType = DeploymentType.Test.ToString(),
                Endpoint = string.Empty,
                EntraId = appId,
                TenantId = tenantId,
                EntraIdType = "AppReg",
            },
            default);

        Assert.Equal(2, handler.Requests.Count);

        var check = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, check.Method);
        Assert.Equal(
            $"{Arm}/providers/Microsoft.BotService/checkNameAvailability?api-version={BotApiVersion}",
            check.Uri.ToString());

        var put = handler.Requests[1];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal($"{Arm}{botResourceId}?api-version={BotApiVersion}", put.Uri.ToString());
        using var putBody = JsonDocument.Parse(put.Body!);
        var props = putBody.RootElement.GetProperty("properties");
        Assert.Equal("SingleTenant", props.GetProperty("msaAppType").GetString());
        Assert.Equal(appId, props.GetProperty("msaAppId").GetString());
        Assert.Equal(tenantId, props.GetProperty("msaAppTenantId").GetString());
        Assert.False(props.GetProperty("isCmekEnabled").GetBoolean());
        Assert.Equal("Enabled", props.GetProperty("publicNetworkAccess").GetString());
        Assert.False(props.GetProperty("isStreamingSupported").GetBoolean());

        Assert.Equal(botResourceId, snapshot.ResourceId);
        Assert.Equal(appId, snapshot.EntraId);
        Assert.Equal("AppReg", snapshot.EntraIdType);
    }

    [Fact]
    public async Task ResolveSubscription_FollowsNextLink_FindsPreferredOnLaterPage()
    {
        var page1 = $$"""{"value":[{"subscriptionId":"sub-a","tenantId":"tenant-a","state":"Enabled"}],"nextLink":"{{Arm}}/subscriptions?api-version=2020-01-01&$skiptoken=page2"}""";
        var page2 = """{"value":[{"subscriptionId":"sub-b","tenantId":"tenant-b","state":"Enabled"}]}""";
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.Query.Contains("skiptoken", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, page2)
                : Json(HttpStatusCode.OK, page1));

        // Without pagination the preferred subscription (on page 2) would be reported missing.
        var result = await Adapter(handler).ResolveSubscriptionAsync("sub-b", null, default);

        Assert.Equal("sub-b", result.SubscriptionId);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ResolveResourceGroup_FollowsNextLink_FindsPreferredOnLaterPage()
    {
        var page1 = $$"""{"value":[{"name":"rg-1","location":"eastus"}],"nextLink":"{{Arm}}/subscriptions/sub-1/resourcegroups?api-version=2021-04-01&$skiptoken=page2"}""";
        var page2 = """{"value":[{"name":"rg-2","location":"westus"}]}""";
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.Query.Contains("skiptoken", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, page2)
                : Json(HttpStatusCode.OK, page1));

        var result = await Adapter(handler).ResolveResourceGroupAsync("sub-1", "rg-2", "tenant-1", default);

        Assert.Equal("rg-2", result.Name);
        Assert.Equal("westus", result.Location);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetBot_RedactsSchemaSensitiveFields_PreservesNullsAndNonSecrets()
    {
        const string botResourceId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.BotService/botServices/bb";
        var body = """
            {
              "id": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.BotService/botServices/bb",
              "name": "bb",
              "location": "global",
              "sku": { "name": "F0" },
              "kind": "azurebot",
              "properties": {
                "msaAppId": "app-1",
                "msaAppType": "SingleTenant",
                "msaAppTenantId": "t",
                "displayName": "bb",
                "endpoint": "https://x.example/messages",
                "cmekKeyVaultUrl": "https://kv.vault.azure.net/keys/k",
                "publishingCredentials": { "clientSecret": "SUPERSECRET" },
                "developerAppInsightsApiKey": "KEY123",
                "luisKey": null,
                "allSettings": { "token": "SUPERSECRET" }
              }
            }
            """;
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, body));

        var snapshot = await Adapter(handler).GetBotAsync(botResourceId, "t", default);

        Assert.NotNull(snapshot.RawArmResponse);
        var props = snapshot.RawArmResponse!.Value.GetProperty("properties");
        // Known secret-bearing properties are masked as a whole (object or scalar).
        Assert.Equal("***", props.GetProperty("publishingCredentials").GetString());
        Assert.Equal("***", props.GetProperty("developerAppInsightsApiKey").GetString());
        // Free-form bags are masked entirely, catching secrets under arbitrary key names.
        Assert.Equal("***", props.GetProperty("allSettings").GetString());
        // A null-valued sensitive field stays null (nothing sensitive present).
        Assert.Equal(JsonValueKind.Null, props.GetProperty("luisKey").ValueKind);
        // Non-secret fields survive verbatim — including cmekKeyVaultUrl, which the old name heuristic over-redacted.
        Assert.Equal("https://kv.vault.azure.net/keys/k", props.GetProperty("cmekKeyVaultUrl").GetString());
        Assert.Equal("app-1", props.GetProperty("msaAppId").GetString());
        Assert.Equal("app-1", snapshot.EntraId);
    }

    [Fact]
    public async Task GetBot_PreservesUnknownFieldsUnlessTheirNamesIdentifySecrets()
    {
        const string botResourceId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.BotService/botServices/bb";
        // A future/unknown field and read-only response fields that are NOT in the template schema,
        // alongside template fields (secret and non-secret).
        var body = """
            {
              "id": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.BotService/botServices/bb",
              "name": "bb",
              "location": "global",
              "sku": { "name": "F0" },
              "kind": "azurebot",
              "properties": {
                "msaAppId": "app-1",
                "msaAppType": "SingleTenant",
                "displayName": "bb",
                "endpoint": "",
                "publishingCredentials": "PUBLISH-SECRET",
                "provisioningState": "Succeeded",
                "migrationToken": "MIGRATE-SECRET",
                "futureUnknownField": "COULD-BE-A-SECRET"
              }
            }
            """;
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, body));

        var snapshot = await Adapter(handler).GetBotAsync(botResourceId, "t", default);

        var props = snapshot.RawArmResponse!.Value.GetProperty("properties");
        // Unknown fields are preserved unless their names identify likely secret material.
        Assert.Equal("COULD-BE-A-SECRET", props.GetProperty("futureUnknownField").GetString());
        Assert.Equal("Succeeded", props.GetProperty("provisioningState").GetString());
        Assert.Equal(BotServiceResponseRedactor.Mask, props.GetProperty("migrationToken").GetString());
        // A template field on the redact list is masked.
        Assert.Equal(
            BotServiceResponseRedactor.Mask,
            props.GetProperty("publishingCredentials").GetString());
        // Template fields not on the redact list are preserved verbatim.
        Assert.Equal("app-1", props.GetProperty("msaAppId").GetString());
        Assert.Equal("bb", props.GetProperty("displayName").GetString());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"just-a-string\"")]
    public async Task GetBot_ResponseNotAnObject_ThrowsInvalidResponse(string body)
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, body));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(
            () => Adapter(handler).GetBotAsync(BotResourceId, "t", default));
        Assert.Equal(ProvisioningErrorKind.InvalidResponse, ex.Kind);
        Assert.Equal("invalid-response", ex.ErrorKind);
        Assert.Equal(1, ex.ExitCode);
    }

    [Theory]
    [InlineData("properties")]
    [InlineData("sku")]
    [InlineData("tags")]
    public async Task GetBot_ContainerMemberNotObject_ThrowsInvalidResponse(string member)
    {
        // A container member present but shaped as an array instead of an object.
        var body = $$"""
            {
              "id": "/rid",
              "name": "bb",
              "sku": { "name": "F0" },
              "kind": "azurebot",
              "properties": { "msaAppId": "app-1", "msaAppType": "SingleTenant" },
              "{{member}}": [ "not", "an", "object" ]
            }
            """;
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, body));

        var ex = await Assert.ThrowsAsync<ProvisioningException>(
            () => Adapter(handler).GetBotAsync(BotResourceId, "t", default));
        Assert.Equal(ProvisioningErrorKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public async Task GetBot_ShapeValid_DoesNotValidatePropertyContent()
    {
        // Values the earlier content validation would have rejected (bad name, unknown enum) are
        // accepted now that only the structural shape is validated.
        var body = """
            {
              "id": "/rid",
              "name": "b",
              "sku": { "name": "Z9" },
              "kind": "notakind",
              "properties": {
                "msaAppId": "app-1",
                "msaAppType": "Nope",
                "displayName": "b",
                "endpoint": "https://x.example/messages"
              }
            }
            """;
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, body));

        var snapshot = await Adapter(handler).GetBotAsync(BotResourceId, "t", default);

        Assert.Equal("app-1", snapshot.EntraId);
    }
}
