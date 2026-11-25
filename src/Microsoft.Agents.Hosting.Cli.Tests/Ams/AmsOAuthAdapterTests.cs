using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsOAuthAdapterTests
{
    private const string Audience = "https://api.powerplatform.com/";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Home = "22222222-2222-2222-2222-222222222222";
    private const string Agent = "44444444-4444-4444-4444-444444444444";
    private const string Deployment = "77777777-7777-7777-7777-777777777777";
    private const string Secret = "private-client-secret";
    private const string DeploymentRoute = $"https://api.powerplatform.com/agenthosting/environments/{Home}/agents/{Agent}/deployments/{Deployment}";
    private const string ConnectionId = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/ams-bots/providers/Microsoft.BotService/botServices/ams-11111111-22222222-88888888-77777777/connections/graph login";
    private const string ConnectionBody = $$$"""{"id":"{{{ConnectionId}}}","name":"graph login","serviceProviderId":"provider-aadv2","serviceProviderName":"Aadv2","serviceProviderDisplayName":"Azure Active Directory v2","clientId":"client-1","scopes":"openid, profile User.Read","parameters":{"TenantId":"common","TokenExchangeUrl":null}}""";
    private static readonly string[] KnownRefusalKinds = ["deployment-not-found", "oauth-connection-not-found", "invalid-oauth-request", "deployment-has-no-bot", "agent-bot-service-conflict", "oauth-operation-failed", "deployment-deleting"];

    [Fact]
    public async Task ListProviders_ReadsTheProviderRouteWithTheReadPermissionAndParsesTheSchemas()
    {
        var handler = Handler(HttpStatusCode.OK, """
            {"value":[
              {"serviceProviderName":"Aadv2","displayName":"Azure Active Directory v2","id":"provider-aadv2","parameters":[
                {"name":"ClientId","type":"string","displayName":"Client id","description":"The application id.","required":true,"secret":false},
                {"name":"ClientSecret","type":"securestring","displayName":"Client secret","required":true,"secret":true},
                {"name":"TenantId","type":"string","displayName":"Tenant id","default":"common","required":false,"secret":false},
                {"name":"TokenExchangeUrl"}]},
              {"serviceProviderName":"github","id":"provider-github"}]}
            """);
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsRead };

        var providers = await Adapter(handler, tokens).ListProvidersAsync(DeploymentRequest(), default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal($"{DeploymentRoute}/oauthProviders?api-version=1", sent.Uri.AbsoluteUri);
        Assert.True(sent.HasAuthorization);
        Assert.Empty(sent.Body);
        Assert.Equal([Audience], tokens.Resources);
        Assert.Equal([Tenant], tokens.Tenants);
        Assert.Equal(["Aadv2", "github"], providers.Select(provider => provider.Name));
        var aadv2 = providers[0];
        Assert.Equal(("provider-aadv2", "Azure Active Directory v2"), (aadv2.Id, aadv2.DisplayName));
        Assert.Equal(["ClientId", "ClientSecret", "TenantId", "TokenExchangeUrl"], aadv2.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(("string", "Client id", "The application id.", true), (aadv2.Parameters[0].Type, aadv2.Parameters[0].DisplayName, aadv2.Parameters[0].Description, aadv2.Parameters[0].IsRequired));
        Assert.Equal("securestring", aadv2.Parameters[1].Type);
        Assert.Equal(("common", false), (aadv2.Parameters[2].DefaultValue, aadv2.Parameters[2].IsRequired));
        Assert.Equal(("string", "TokenExchangeUrl", null, null, false), (aadv2.Parameters[3].Type, aadv2.Parameters[3].DisplayName, aadv2.Parameters[3].Description, aadv2.Parameters[3].DefaultValue, aadv2.Parameters[3].IsRequired));
        Assert.Equal(("provider-github", "github"), (providers[1].Id, providers[1].DisplayName));
        Assert.Empty(providers[1].Parameters);
    }

    [Fact]
    public async Task List_ReadsTheConnectionCollectionAndParsesTheSummaries()
    {
        var handler = Handler(HttpStatusCode.OK, $$$"""{"value":[{{{ConnectionBody}}},{"id":"/bots/ams/connections/legacy","name":"legacy","serviceProviderId":"provider-retired","serviceProviderDisplayName":"Retired"},{"id":"/bots/ams/connections/unknown","name":"unknown","serviceProviderId":"provider-unknown"}]}""");

        var connections = await Adapter(handler, new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsRead }).ListAsync(DeploymentRequest(), default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal($"{DeploymentRoute}/oauthConnections?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Equal(["graph login", "legacy", "unknown"], connections.Select(connection => connection.ConnectionName));
        Assert.Equal("Retired", connections[1].ServiceProviderDisplayName);
        Assert.Equal(string.Empty, connections[2].ServiceProviderDisplayName);
        Assert.All(connections.Skip(1), connection => Assert.Equal((null, 0, 0), (connection.ClientId, connection.Scopes.Count, connection.Parameters.Count)));
    }

    [Fact]
    public async Task Get_ReadsOneEscapedConnectionAndKeepsTheParametersAsAmsSentThem()
    {
        var handler = Handler(HttpStatusCode.OK, ConnectionBody);

        var connection = await Adapter(handler, new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsRead }).GetAsync(ConnectionRequest("graph login"), default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal($"{DeploymentRoute}/oauthConnections/graph%20login?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Equal(ConnectionId, connection.ResourceId);
        Assert.Equal(("graph login", "provider-aadv2", "Azure Active Directory v2", "client-1"), (connection.ConnectionName, connection.ServiceProviderId, connection.ServiceProviderDisplayName, connection.ClientId));
        Assert.Equal(["openid", "profile", "User.Read"], connection.Scopes);
        Assert.Equal(["TenantId", "TokenExchangeUrl"], connection.Parameters.Keys);
        Assert.Equal("common", connection.Parameters["TenantId"]);
        Assert.Null(connection.Parameters["TokenExchangeUrl"]);
        Assert.Null(connection.RawArmResponse);
    }

    [Fact]
    public async Task Get_FallsBackToTheProviderNameForTheDisplayName()
    {
        var handler = Handler(HttpStatusCode.OK, """{"id":"/bots/ams/connections/c","name":"c","serviceProviderId":"provider-aadv2","serviceProviderName":"Aadv2"}""");

        var connection = await Adapter(handler, ReadAndUpdateTokens()).GetAsync(ConnectionRequest("c"), default);

        Assert.Equal("Aadv2", connection.ServiceProviderDisplayName);
    }

    [Theory]
    [InlineData(HttpStatusCode.Created, true)]
    [InlineData(HttpStatusCode.OK, false)]
    public async Task Create_PostsTheNameProviderAndSettingsAndReportsWhetherAmsCreatedTheConnection(HttpStatusCode status, bool isCreated)
    {
        var handler = Handler(status, ConnectionBody);
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate };

        var result = await Adapter(handler, tokens).CreateAsync(new AmsOAuthCreateTransportRequest { Deployment = DeploymentRequest(), ConnectionName = "graph login", ServiceProviderName = "aadv2", Settings = new("client-1", Secret, "openid profile", new Dictionary<string, string?> { ["TenantId"] = "common" }) }, default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal($"{DeploymentRoute}/oauthConnections?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Equal("application/json", sent.ContentType);
        AssertBody($$$"""{"name":"graph login","serviceProviderName":"aadv2","clientId":"client-1","clientSecret":"{{{Secret}}}","scopes":"openid profile","parameters":{"TenantId":"common"}}""", sent);
        Assert.Equal(isCreated, result.IsCreated);
        Assert.Equal("graph login", result.Connection.ConnectionName);
    }

    [Fact]
    public async Task Create_LeavesOutTheSettingsTheRequestDoesNotCarry()
    {
        var handler = Handler(HttpStatusCode.Created, ConnectionBody);

        await Adapter(handler, new FakePpapiTokenProvider()).CreateAsync(new AmsOAuthCreateTransportRequest { Deployment = DeploymentRequest(), ConnectionName = "graph login", ServiceProviderName = "Aadv2WithFic", Settings = new(null, null, null, new Dictionary<string, string?>()) }, default);

        AssertBody("""{"name":"graph login","serviceProviderName":"Aadv2WithFic"}""", Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task Update_PatchesTheChangedSettingsIncludingClearedValuesWithoutTheNameOrProvider()
    {
        var handler = Handler(HttpStatusCode.OK, ConnectionBody);

        var connection = await Adapter(handler, new FakePpapiTokenProvider()).UpdateAsync(new AmsOAuthUpdateTransportRequest { Deployment = DeploymentRequest(), ConnectionName = "graph login", Settings = new(null, Secret, string.Empty, new Dictionary<string, string?> { ["TokenExchangeUrl"] = string.Empty, ["TenantId"] = "organizations" }) }, default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, sent.Method);
        Assert.Equal($"{DeploymentRoute}/oauthConnections/graph%20login?api-version=1", sent.Uri.AbsoluteUri);
        AssertBody($$$"""{"clientSecret":"{{{Secret}}}","scopes":"","parameters":{"TokenExchangeUrl":"","TenantId":"organizations"}}""", sent);
        Assert.Equal("graph login", connection.ConnectionName);
    }

    [Fact]
    public async Task Delete_SendsOneBodylessDeleteWithTheUpdatePermission()
    {
        var handler = new CapturingPackageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await Adapter(handler, new FakePpapiTokenProvider()).DeleteAsync(ConnectionRequest("graph login"), default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, sent.Method);
        Assert.Equal($"{DeploymentRoute}/oauthConnections/graph%20login?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Empty(sent.Body);
        Assert.Null(sent.ContentType);
    }

    [Theory]
    [InlineData("providers", PowerPlatformApiPermissions.AppsUpdate)]
    [InlineData("list", PowerPlatformApiPermissions.AppsUpdate)]
    [InlineData("get", PowerPlatformApiPermissions.AppsUpdate)]
    [InlineData("create", PowerPlatformApiPermissions.AppsRead)]
    [InlineData("update", PowerPlatformApiPermissions.AppsRead)]
    [InlineData("delete", PowerPlatformApiPermissions.AppsRead)]
    public async Task EveryCall_RequiresItsPermissionBeforeSending(string operation, string grantedScope)
    {
        var handler = Handler(HttpStatusCode.OK, ConnectionBody);

        await Assert.ThrowsAsync<TokenClaimException>(() => Invoke(Adapter(handler, new FakePpapiTokenProvider { Scopes = grantedScope }), operation));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("get", HttpStatusCode.NotFound, "DeploymentNotFound", "deployment-not-found", ExitCodes.Failure)]
    [InlineData("get", HttpStatusCode.NotFound, "OAuthConnectionNotFound", "oauth-connection-not-found", ExitCodes.Usage)]
    [InlineData("update", HttpStatusCode.NotFound, "oauthconnectionnotfound", "oauth-connection-not-found", ExitCodes.Usage)]
    [InlineData("list", HttpStatusCode.Conflict, "DeploymentBotServiceRequired", "deployment-has-no-bot", ExitCodes.Failure)]
    [InlineData("create", HttpStatusCode.Conflict, "AgentBotServiceConflict", "agent-bot-service-conflict", ExitCodes.Failure)]
    [InlineData("create", HttpStatusCode.ServiceUnavailable, "AgentBotServiceOAuthFailed", "oauth-operation-failed", ExitCodes.Failure)]
    [InlineData("providers", HttpStatusCode.BadGateway, "AgentBotServiceOAuthFailed", "oauth-operation-failed", ExitCodes.Failure)]
    [InlineData("delete", HttpStatusCode.Conflict, "DeploymentDeleting", "deployment-deleting", ExitCodes.Failure)]
    public async Task KnownRefusals_AreExplainedWithCorrelationAndNoServiceDetail(string operation, HttpStatusCode status, string code, string errorKind, int exitCode)
    {
        var response = FakeHttpResponse.Json(status, $$$"""{"error":{"code":"{{{code}}}","message":"private-response-detail"}}""");
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-oauth-id");
        var handler = new CapturingPackageHandler(_ => response);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, ReadAndUpdateTokens()), operation));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(errorKind, exception.ErrorKind);
        Assert.Equal(exitCode, exception.ExitCode);
        Assert.Equal(ErrorCodes.Deployment.AgentDeployServiceRejected, exception.ErrorCode);
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("serviceRequestId=service-oauth-id", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", exception.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OAuthConnectionNotFound_NamesTheRequestedConnection()
    {
        var handler = Handler(HttpStatusCode.NotFound, """{"error":{"code":"OAuthConnectionNotFound","message":"private-response-detail"}}""");

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Adapter(handler, ReadAndUpdateTokens()).GetAsync(ConnectionRequest("graph login"), default));

        Assert.Contains("no OAuth connection named 'graph login'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"error":{"code":"InvalidOAuthConnectionRequest","message":"The OAuth service provider 'line' is not supported."}}""", "AMS refused the OAuth connection request: The OAuth service provider 'line' is not supported.")]
    [InlineData("""{"error":{"code":"InvalidOAuthConnectionRequest"}}""", "AMS refused the OAuth connection request: it does not satisfy the rules of the provider.")]
    public async Task InvalidOAuthConnectionRequest_ShowsTheAmsValidationMessageAsAUsageError(string body, string message)
    {
        var handler = Handler(HttpStatusCode.BadRequest, body);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, ReadAndUpdateTokens()), "create"));

        Assert.Equal("invalid-oauth-request", exception.ErrorKind);
        Assert.Equal(ExitCodes.Usage, exception.ExitCode);
        Assert.StartsWith(message, exception.Message, StringComparison.Ordinal);
        Assert.Contains("msagent deployment oauth list-providers", exception.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("create", HttpStatusCode.GatewayTimeout, "AgentManagementServiceDataPlaneCallFailed")]
    [InlineData("get", HttpStatusCode.Conflict, "OAuthConnectionNotFound")]
    [InlineData("update", HttpStatusCode.BadRequest, "SomethingElse")]
    [InlineData("list", HttpStatusCode.NotImplemented, "DirectLineRegionNotSupported")]
    [InlineData("providers", HttpStatusCode.BadGateway, "AgentBotServiceChannelFailed")]
    [InlineData("delete", HttpStatusCode.NotFound, "ChannelNotFound")]
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
        { "providers", "{}" },
        { "providers", """{"value":{}}""" },
        { "providers", """{"value":[{"id":"provider-aadv2","parameters":[]}]}""" },
        { "providers", """{"value":[{"serviceProviderName":"Aadv2","id":"provider-aadv2","parameters":{}}]}""" },
        { "providers", """{"value":[{"serviceProviderName":"Aadv2","id":"provider-aadv2","parameters":[{"type":"string"}]}]}""" },
        { "providers", """{"value":[{"serviceProviderName":"Aadv2","id":"provider-aadv2","parameters":[{"name":"ClientId","required":"yes"}]}]}""" },
        { "list", """{"value":[{"id":"x","name":"c"}]}""" },
        { "get", "not json" },
        { "get", """{"name":"c","serviceProviderId":"p"}""" },
        { "get", """{"id":"x","name":"c","serviceProviderId":"p","parameters":[]}""" },
        { "get", """{"id":"x","name":"c","serviceProviderId":"p","parameters":{"TenantId":1}}""" },
        { "get", """{"id":"x","name":"c","serviceProviderId":"p","parameters":{"a":"1","a":"2"}}""" },
        { "get", """{"id":"x","name":"c","serviceProviderId":"p","scopes":["openid"]}""" },
        { "create", """{"id":"x","serviceProviderId":"p"}""" },
        { "update", """{"id":"x","name":"c"}""" },
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
        Assert.Contains("before acting on the OAuth connection", exception.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("create", true)]
    [InlineData("update", true)]
    [InlineData("delete", true)]
    [InlineData("get", false)]
    [InlineData("providers", false)]
    public async Task TransportFailure_IsIndeterminateOnlyForConnectionWrites(string operation, bool indeterminate)
    {
        var handler = new CapturingPackageHandler(_ => throw new HttpRequestException("private-transport-detail"));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, ReadAndUpdateTokens()), operation));

        Assert.Single(handler.Requests);
        Assert.Equal("deploy-transport-failed", exception.ErrorKind);
        Assert.Equal(indeterminate, exception.Message.Contains("outcome may be indeterminate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("private-transport-detail", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirect_IsRejectedWithoutASecondCredentialBearingRequest()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Redirect(HttpStatusCode.Redirect, "https://elsewhere.example/oauthConnections"));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, ReadAndUpdateTokens()), "create"));

        Assert.Single(handler.Requests);
        Assert.Equal("redirect-rejected", exception.ErrorKind);
        Assert.Contains("api.powerplatform.com", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryOperation_RequiresARequest()
    {
        var adapter = Adapter(Handler(HttpStatusCode.OK, "{}"), ReadAndUpdateTokens());

        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.ListProvidersAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.ListAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.GetAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.CreateAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.UpdateAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.DeleteAsync(null!, default));
    }

    private static Task Invoke(AmsOAuthAdapter adapter, string operation) => operation switch
    {
        "providers" => adapter.ListProvidersAsync(DeploymentRequest(), default),
        "list" => adapter.ListAsync(DeploymentRequest(), default),
        "get" => adapter.GetAsync(ConnectionRequest("graph login"), default),
        "create" => adapter.CreateAsync(new AmsOAuthCreateTransportRequest { Deployment = DeploymentRequest(), ConnectionName = "graph login", ServiceProviderName = "Aadv2", Settings = new("client-1", Secret, null, new Dictionary<string, string?>()) }, default),
        "update" => adapter.UpdateAsync(new AmsOAuthUpdateTransportRequest { Deployment = DeploymentRequest(), ConnectionName = "graph login", Settings = new(null, Secret, null, new Dictionary<string, string?>()) }, default),
        "delete" => adapter.DeleteAsync(ConnectionRequest("graph login"), default),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static void AssertBody(string expected, CapturedPackageRequest sent) => Assert.True(JsonElement.DeepEquals(Json(expected), Json(Encoding.UTF8.GetString(sent.Body))), Encoding.UTF8.GetString(sent.Body));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CapturingPackageHandler Handler(HttpStatusCode status, string body) => new(_ => FakeHttpResponse.Json(status, body));

    private static AmsOAuthAdapter Adapter(CapturingPackageHandler handler, FakePpapiTokenProvider tokens) => new(new HttpClient(handler), tokens);

    private static FakePpapiTokenProvider ReadAndUpdateTokens() => new() { Scopes = $"{PowerPlatformApiPermissions.AppsRead} {PowerPlatformApiPermissions.AppsUpdate}" };

    private static AmsDeploymentReadTransportRequest DeploymentRequest() => new()
    {
        ServiceTarget = new AmsServiceTarget { Endpoint = new Uri("https://api.powerplatform.com"), Audience = Audience, RouteForm = AmsRouteForm.TenantHost, TenantId = Tenant, EnvironmentId = Home },
        AgentId = Agent,
        DeploymentId = Deployment,
    };

    private static AmsOAuthConnectionTransportRequest ConnectionRequest(string connectionName) => new() { Deployment = DeploymentRequest(), ConnectionName = connectionName };
}
