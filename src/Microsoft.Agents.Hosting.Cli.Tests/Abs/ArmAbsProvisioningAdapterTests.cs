using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Abs;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Offline unit tests for <see cref="ArmAbsProvisioningAdapter"/>. They drive the adapter through a
/// fake <see cref="HttpMessageHandler"/> — no network, no MSAL, no <c>az</c> subprocess — and assert
/// the exact direct-ARM contract the TypeScript oracle uses: every URL/api-version, the bearer
/// header, the SingleTenant PUT body, the scope/registration status mappings, the 404-to-null bot read,
/// and the typed <see cref="TargetRequestException"/> categories. They also prove the zero-Graph
/// guarantee structurally: no request is ever issued to a Graph host.
/// </summary>
public sealed class ArmAbsProvisioningAdapterTests
{
    private const string Arm = "https://management.azure.com";

    private static ArmAbsProvisioningAdapter Adapter(
        StubHttpMessageHandler handler,
        string? tenantId = "tenant-contoso",
        string token = "arm-token") =>
        new(
            new HttpClient(handler),
            new StubTokenProvider { AccessToken = token, TenantId = tenantId });

    [Theory]
    [InlineData("Registered", "Registered")]
    [InlineData("NotRegistered", "NotRegistered")]
    [InlineData("Unregistered", "NotRegistered")]
    [InlineData("Registering", "Registering")]
    [InlineData("Deleting", "Unknown")]
    public async Task GetProviderRegistration_MapsArmStateAndUrl(string armState, string expected)
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, $$"""{"registrationState":"{{armState}}"}"""));
        var adapter = Adapter(handler);

        var result = await adapter.GetProviderRegistrationAsync("sub-1", "Microsoft.BotService", "tenant-contoso", default);

        Assert.Equal(expected, result.RegistrationState);
        Assert.Equal("Microsoft.BotService", result.ProviderNamespace);
        Assert.Equal(
            $"{Arm}/subscriptions/sub-1/providers/Microsoft.BotService?api-version=2021-04-01",
            handler.Requests.Single().Uri.ToString());
        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task GetProviderRegistration_NonOk_IsUnknown()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.Forbidden, "{}"));
        var result = await Adapter(handler).GetProviderRegistrationAsync("sub-1", "Microsoft.BotService", "tenant-contoso", default);
        Assert.Equal("Unknown", result.RegistrationState);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true, true)]
    [InlineData(HttpStatusCode.Forbidden, true, false)]
    [InlineData(HttpStatusCode.NotFound, false, false)]
    [InlineData(HttpStatusCode.InternalServerError, true, false)]
    public async Task GetSubscriptionAccess_MapsStatusAndUrl(HttpStatusCode status, bool exists, bool read)
    {
        var handler = new StubHttpMessageHandler(_ => Json(status, "{}"));
        var result = await Adapter(handler).GetSubscriptionAccessAsync("sub-1", "tenant-contoso", default);

        Assert.Equal(exists, result.Exists);
        Assert.Equal(read, result.HasReadAccess);
        Assert.Equal($"{Arm}/subscriptions/sub-1?api-version=2020-01-01", handler.Requests.Single().Uri.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true, true)]
    [InlineData(HttpStatusCode.Forbidden, true, false)]
    [InlineData(HttpStatusCode.NotFound, false, false)]
    public async Task GetResourceGroupAccess_MapsStatusAndUrl(HttpStatusCode status, bool exists, bool read)
    {
        var handler = new StubHttpMessageHandler(_ => Json(status, "{}"));
        var result = await Adapter(handler).GetResourceGroupAccessAsync("sub-1", "rg-1", "tenant-contoso", default);

        Assert.Equal(exists, result.Exists);
        Assert.Equal(read, result.HasReadAccess);
        Assert.Equal(
            $"{Arm}/subscriptions/sub-1/resourcegroups/rg-1?api-version=2021-04-01",
            handler.Requests.Single().Uri.ToString());
    }

    [Fact]
    public async Task GetBot_MapsSnapshotFromArmBody()
    {
        const string botResourceId =
            "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/bot-1";
        var body = $$"""
            {
              "id": "{{botResourceId}}",
              "name": "bot-1",
              "sku": { "name": "S1" },
              "properties": {
                "endpoint": "https://example.com/api/messages",
                "msaAppId": "app-123",
                "msaAppType": "SingleTenant"
              }
            }
            """;
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, body));

        var snapshot = await Adapter(handler).GetBotAsync(botResourceId, "tenant-contoso", default);

        Assert.NotNull(snapshot);
        Assert.Equal(botResourceId, snapshot!.BotResourceId);
        Assert.Equal("app-123", snapshot.AppId);
        Assert.Equal("SingleTenant", snapshot.MsaAppType);
        Assert.Equal("https://example.com/api/messages", snapshot.MessagingEndpoint);
        Assert.Equal("S1", snapshot.Sku);
        Assert.Equal($"{Arm}{botResourceId}?api-version=2022-09-15", handler.Requests.Single().Uri.ToString());
    }

    [Fact]
    public async Task GetBot_404_IsNull()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.NotFound, """{"error":{"code":"NotFound"}}"""));
        var snapshot = await Adapter(handler).GetBotAsync("/subscriptions/s/x", "tenant-contoso", default);
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task GetBot_OtherError_Throws()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.InternalServerError, "{}"));
        var ex = await Assert.ThrowsAsync<TargetRequestException>(() =>
            Adapter(handler).GetBotAsync("/subscriptions/s/x", "tenant-contoso", default));
        Assert.Equal(TargetErrorKind.RequestFailed, ex.Kind);
        Assert.Equal(500, ex.Status);
    }

    [Fact]
    public async Task UpsertBot_SendsSingleTenantPutBodyAndUrl()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, """{"id":"/created","properties":{"msaAppId":"app-1"}}"""));
        var adapter = Adapter(handler);

        await adapter.UpsertBotAsync(
            new Microsoft.Agents.Hosting.Cli.Core.Ports.AbsBotUpsert
            {
                SubscriptionId = "sub-1",
                ResourceGroup = "rg-1",
                Name = "bot-1",
                Endpoint = "https://example.com/api/messages",
                MsaAppId = "app-1",
                TenantId = "tenant-1",
            },
            default);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal(
            $"{Arm}/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/bot-1?api-version=2022-09-15",
            request.Uri.ToString());

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("global", root.GetProperty("location").GetString());
        Assert.Equal("F0", root.GetProperty("sku").GetProperty("name").GetString());
        Assert.Equal("azurebot", root.GetProperty("kind").GetString());
        var props = root.GetProperty("properties");
        Assert.Equal("bot-1", props.GetProperty("displayName").GetString());
        Assert.Equal("https://example.com/api/messages", props.GetProperty("endpoint").GetString());
        Assert.Equal("app-1", props.GetProperty("msaAppId").GetString());
        Assert.Equal("SingleTenant", props.GetProperty("msaAppType").GetString());
        Assert.Equal("tenant-1", props.GetProperty("msaAppTenantId").GetString());
        Assert.Equal("1.3", props.GetProperty("schemaTransformationVersion").GetString());
    }

    [Fact]
    public async Task UpsertBot_HonorsOptionalOverrides()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{}"));
        await Adapter(handler).UpsertBotAsync(
            new Microsoft.Agents.Hosting.Cli.Core.Ports.AbsBotUpsert
            {
                SubscriptionId = "s",
                ResourceGroup = "r",
                Name = "b",
                Endpoint = "https://e",
                MsaAppId = "a",
                TenantId = "t",
                DisplayName = "Friendly",
                Sku = "S1",
                Location = "westus",
            },
            default);

        using var body = JsonDocument.Parse(handler.Requests.Single().Body!);
        Assert.Equal("westus", body.RootElement.GetProperty("location").GetString());
        Assert.Equal("S1", body.RootElement.GetProperty("sku").GetProperty("name").GetString());
        Assert.Equal("Friendly", body.RootElement.GetProperty("properties").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Request_SetsBearerHeader()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{}"));
        await Adapter(handler, token: "secret-token").GetSubscriptionAccessAsync("s", "tenant-contoso", default);
        Assert.Equal("Bearer secret-token", handler.Requests.Single().Authorization);
    }

    [Fact]
    public async Task Request_EmptyToken_OmitsAuthorizationHeader()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{}"));
        await Adapter(handler, token: string.Empty).GetSubscriptionAccessAsync("s", "tenant-contoso", default);
        Assert.Null(handler.Requests.Single().Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}", TargetErrorKind.AuthExpired, ExitCodes.NotSignedIn)]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"code":"InvalidAudience","message":"wrong audience"}}""", TargetErrorKind.WrongAudience, ExitCodes.Usage)]
    [InlineData(HttpStatusCode.Forbidden, "{}", TargetErrorKind.PermissionDenied, ExitCodes.Failure)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"ConsentRequired","message":"admin consent needed"}}""", TargetErrorKind.ConsentRequired, ExitCodes.Failure)]
    [InlineData(HttpStatusCode.BadGateway, "{}", TargetErrorKind.RequestFailed, ExitCodes.Failure)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":"LocationNotAvailableForResourceType","message":"The provided location is not available."}}""", TargetErrorKind.RequestFailed, ExitCodes.Usage)]
    public async Task Mutation_MapsErrorCategories(
        HttpStatusCode status,
        string body,
        TargetErrorKind expectedKind,
        int expectedExit)
    {
        var handler = new StubHttpMessageHandler(_ => Json(status, body));
        var ex = await Assert.ThrowsAsync<TargetRequestException>(() =>
            Adapter(handler).UpsertBotAsync(
                new Microsoft.Agents.Hosting.Cli.Core.Ports.AbsBotUpsert
                {
                    SubscriptionId = "s",
                    ResourceGroup = "r",
                    Name = "b",
                    Endpoint = "https://e",
                    MsaAppId = "a",
                    TenantId = "t",
                },
                default));

        Assert.Equal(expectedKind, ex.Kind);
        Assert.Equal(expectedExit, ex.ExitCode);
        Assert.Equal((int)status, ex.Status);
        // Secret-free: the message never echoes the bearer token or the request body.
        Assert.DoesNotContain("arm-token", ex.Message);
    }

    [Fact]
    public async Task Mutation_WhenTheLocationIsRejected_NamesGlobal()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.BadRequest, """{"error":{"code":"LocationNotAvailableForResourceType","message":"The provided location 'westus2' is not available for resource type."}}"""));

        var ex = await Assert.ThrowsAsync<TargetRequestException>(() =>
            Adapter(handler).UpsertBotAsync(
                new Microsoft.Agents.Hosting.Cli.Core.Ports.AbsBotUpsert
                {
                    SubscriptionId = "s",
                    ResourceGroup = "r",
                    Name = "b",
                    Endpoint = "https://e",
                    MsaAppId = "a",
                    TenantId = "t",
                },
                default));

        Assert.Contains("global", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--location", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
    }

    [Fact]
    public async Task AllArmCalls_TargetManagementHost_NeverGraph()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, """{"id":"/x","registrationState":"Registered"}"""));
        var adapter = Adapter(handler);

        await adapter.GetProviderRegistrationAsync("s", "Microsoft.BotService", "tenant-contoso", default);
        await adapter.GetSubscriptionAccessAsync("s", "tenant-contoso", default);
        await adapter.GetResourceGroupAccessAsync("s", "r", "tenant-contoso", default);
        await adapter.GetBotAsync("/subscriptions/s/x", "tenant-contoso", default);
        await adapter.UpsertBotAsync(
            new Microsoft.Agents.Hosting.Cli.Core.Ports.AbsBotUpsert
            {
                SubscriptionId = "s",
                ResourceGroup = "r",
                Name = "b",
                Endpoint = "https://e",
                MsaAppId = "a",
                TenantId = "t",
            },
            default);

        Assert.All(handler.Requests, r => Assert.Equal("management.azure.com", r.Uri.Host));
        Assert.DoesNotContain(handler.Requests, r => r.Uri.Host.Contains("graph", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AllArmCalls_ScopeTheArmToken_ToTheTargetTenant()
    {
        // Every ARM read/write must mint its token for the operator's intended tenant (MSAL
        // WithTenantId), never the signed-in account's home tenant. Otherwise a personal MSA on
        // /consumers yields a home-tenant token that ARM rejects (AADSTS9002332).
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, """{"id":"/x","registrationState":"Registered"}"""));
        var token = new StubTokenProvider { AccessToken = "arm-token", TenantId = "tenant-contoso" };
        var adapter = new ArmAbsProvisioningAdapter(new HttpClient(handler), token);

        await adapter.GetProviderRegistrationAsync("s", "Microsoft.BotService", "tenant-contoso", default);
        await adapter.GetSubscriptionAccessAsync("s", "tenant-contoso", default);
        await adapter.GetResourceGroupAccessAsync("s", "r", "tenant-contoso", default);
        await adapter.GetBotAsync("/subscriptions/s/x", "tenant-contoso", default);
        await adapter.UpsertBotAsync(
            new Microsoft.Agents.Hosting.Cli.Core.Ports.AbsBotUpsert
            {
                SubscriptionId = "s",
                ResourceGroup = "r",
                Name = "b",
                Endpoint = "https://e",
                MsaAppId = "a",
                TenantId = "tenant-contoso",
            },
            default);

        Assert.All(token.RequestedTargetTenants, t => Assert.Equal("tenant-contoso", t));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}

/// <summary>A recording, offline <see cref="HttpMessageHandler"/> that captures each request and returns a canned response.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    public List<CapturedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add(new CapturedRequest
        {
            Method = request.Method,
            Uri = request.RequestUri!,
            Authorization = request.Headers.Authorization?.ToString(),
            Body = body,
        });
        return _responder(request);
    }

    internal sealed record CapturedRequest
    {
        public required HttpMethod Method { get; init; }
        public required Uri Uri { get; init; }
        public string? Authorization { get; init; }
        public string? Body { get; init; }
    }
}

/// <summary>A silent, offline <see cref="Microsoft.Agents.Hosting.Cli.Core.Ports.ITokenProvider"/> returning a fixed token.</summary>
internal sealed class StubTokenProvider : Microsoft.Agents.Hosting.Cli.Core.Ports.ITokenProvider
{
    public string AccessToken { get; init; } = "token";

    public string? TenantId { get; init; }

    /// <summary>Captures the resource each acquisition targeted, so exact scope input is assertable.</summary>
    public List<string> RequestedResources { get; } = [];

    /// <summary>Captures the tenant each acquisition was scoped to, proving the ABS reads thread it through.</summary>
    public List<string?> RequestedTargetTenants { get; } = [];

    public Task<Microsoft.Agents.Hosting.Cli.Core.Ports.TokenAcquisitionResult> AcquireTokenSilentAsync(
        string resource,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        RequestedResources.Add(resource);
        RequestedTargetTenants.Add(targetTenantId);
        return Task.FromResult(new Microsoft.Agents.Hosting.Cli.Core.Ports.TokenAcquisitionResult
        {
            AccessToken = AccessToken,
            TenantId = TenantId,
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
        });
    }

    public Task<Microsoft.Agents.Hosting.Cli.Core.Ports.TokenAcquisitionResult> AcquireTokenSilentAsync(
        IReadOnlyList<string> scopes,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        AcquireTokenSilentAsync(string.Join(' ', scopes), targetTenantId, cancellationToken);
}
