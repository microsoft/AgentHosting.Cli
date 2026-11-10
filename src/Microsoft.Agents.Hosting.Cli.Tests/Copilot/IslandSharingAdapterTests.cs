using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class IslandSharingAdapterTests
{
    private const string Gateway = "https://powervamg.us-il301.gateway.prod.island.powerapps.com/";
    private const string Environment = "env-1";
    private const string Tenant = "tenant-1";
    private const string Bot = "bot-1";
    private const string Owner = "11111111-1111-1111-1111-1111111111aa";
    private const string Ada = "22222222-2222-2222-2222-2222222222bb";

    private static CopilotEnvironmentContext Context(string? gateway = Gateway) => new()
    {
        EnvironmentId = Environment,
        TenantId = Tenant,
        DataverseOrganization = new Uri("https://org.crm.dynamics.com"),
        AgentManagementUrl = gateway is null ? null : new Uri(gateway),
    };

    private static (IslandSharingAdapter Adapter, RecordingHandler Handler, StubTokenProvider Tokens) CreateWithTokens(string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new RecordingHandler(responseBody, status);
        var tokens = new StubTokenProvider();

        return (new IslandSharingAdapter(tokens, CloudMap, CoreServicesClusterCategory.Prod, () => handler), handler, tokens);
    }

    private static readonly PowerPlatformCloudMap CloudMap = PowerPlatformCloudMap.ForCloud(CloudInstance.Public);

    private static (IslandSharingAdapter Adapter, RecordingHandler Handler) Create(string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var (adapter, handler, _) = CreateWithTokens(responseBody, status);

        return (adapter, handler);
    }

    [Fact]
    public async Task Read_RequestsTheNamedDelegatedScopeRatherThanTheDefaultForm()
    {
        var (adapter, _, tokens) = CreateWithTokens(StateJson());

        await adapter.ReadAsync(Context(), Bot, CancellationToken.None);

        Assert.Equal(["api://96ff4394-9197-43aa-b393-6a41652e21f8/user_impersonation"], tokens.RequestedScopes);
    }

    [Theory]
    [InlineData(CoreServicesClusterCategory.Test, "https://powervamg.us-il106.gateway.test.island.powerapps.com/", "api://a522f059-bb65-47c0-8934-7db6e5286414/user_impersonation")]
    [InlineData(CoreServicesClusterCategory.Mooncake, "https://powervamg.cn-il101.gateway.prod.island.powerapps.cn/", "api://96ff4394-9197-43aa-b393-6a41652e21f8/user_impersonation")]
    public async Task Read_RequestsTheDelegatedScopeOfTheClustersControlPlaneFromItsGateway(CoreServicesClusterCategory cluster, string gateway, string scope)
    {
        var handler = new RecordingHandler(StateJson(), HttpStatusCode.OK);
        var tokens = new StubTokenProvider();

        await new IslandSharingAdapter(tokens, CloudMap, cluster, () => handler).ReadAsync(Context(gateway), Bot, CancellationToken.None);

        Assert.Equal([scope], tokens.RequestedScopes);
        Assert.Equal($"{gateway}api/botmanagement/v1/environments/{Environment}/bots/{Bot}/shareV2", handler.Request!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Read_RequestsTheTokenForTheEnvironmentTenant()
    {
        var (adapter, _, tokens) = CreateWithTokens(StateJson());

        await adapter.ReadAsync(Context(), Bot, CancellationToken.None);

        Assert.Equal(Tenant, tokens.RequestedTenantId);
    }

    private static string StateJson(params (string ObjectId, string SharingType)[] targets) =>
        JsonSerializer.Serialize(new
        {
            shareWithOrg = false,
            targets = targets.Select(target => new { objectIdType = "Object", sharingType = new[] { target.SharingType }, objectId = target.ObjectId, clientId = (string?)null }),
        });

    private static string StateJson(string objectId, params string[] sharingTypes) =>
        JsonSerializer.Serialize(new
        {
            shareWithOrg = false,
            targets = new[] { new { objectIdType = "Object", sharingType = sharingTypes, objectId, clientId = (string?)null } },
        });

    [Fact]
    public async Task Read_CallsShareV2OnTheEnvironmentGateway()
    {
        var (adapter, handler) = Create(StateJson((Owner, "Owner")));

        await adapter.ReadAsync(Context(), Bot, CancellationToken.None);

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal($"{Gateway}api/botmanagement/v1/environments/{Environment}/bots/{Bot}/shareV2", handler.Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Read_SendsEveryRoutingHeaderTheGatewayRequires()
    {
        var (adapter, handler) = Create(StateJson());

        await adapter.ReadAsync(Context(), Bot, CancellationToken.None);

        Assert.Equal(Tenant, handler.Header("x-cci-tenantid"));
        Assert.Equal(Tenant, handler.Header("x-cci-routing-tenantid"));
        Assert.Equal(Bot, handler.Header("x-cci-botid"));
        Assert.Equal(Bot, handler.Header("x-cci-cdsbotid"));
        Assert.Equal(Bot, handler.Header("x-cci-routing-botid"));
        Assert.Equal("Web", handler.Header("x-cci-applicationsource"));
        Assert.Equal("Bearer stub-token", handler.Header("Authorization"));
    }

    [Theory]
    [InlineData("Owner", AgentSharingRole.Owner)]
    [InlineData("Collaborator", AgentSharingRole.Collaborator)]
    [InlineData("Consumer", AgentSharingRole.Consumer)]
    [InlineData("Analyst", AgentSharingRole.Analyst)]
    [InlineData("consumer", AgentSharingRole.Consumer)]
    public async Task Read_MapsEveryKnownSharingType(string sharingType, AgentSharingRole expected)
    {
        var (adapter, _) = Create(StateJson((Ada, sharingType)));

        var state = await adapter.ReadAsync(Context(), Bot, CancellationToken.None);

        Assert.Equal(expected, Assert.Single(state.Targets).SoleRole());
    }

    [Fact]
    public async Task Read_KeepsATargetWhoseRoleThisBuildDoesNotKnow()
    {
        var (adapter, _) = Create(StateJson((Ada, "SomethingNew"), (Owner, "Owner")));

        var state = await adapter.ReadAsync(Context(), Bot, CancellationToken.None);

        Assert.Equal([Ada, Owner], state.Targets.Select(target => target.ObjectId));
        Assert.Null(state.Targets[0].SoleRole());
        Assert.Equal(["SomethingNew"], state.Targets[0].SharingTypes);
    }

    [Fact]
    public async Task Write_SendsBackAnAccessLevelThisBuildDoesNotKnow()
    {
        var (adapter, handler) = Create(StateJson((Ada, "SomethingNew")));

        var state = await adapter.ReadAsync(Context(), Bot, CancellationToken.None);
        await adapter.WriteAsync(Context(), Bot, state, CancellationToken.None);

        using var document = JsonDocument.Parse(handler.RequestBody!);

        Assert.Equal("SomethingNew", document.RootElement.GetProperty("targets")[0].GetProperty("sharingType")[0].GetString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    public async Task Read_DoesNotReadANumericSharingTypeAsARole(string sharingType)
    {
        var (adapter, _) = Create(StateJson((Ada, sharingType)));

        var state = await adapter.ReadAsync(Context(), Bot, CancellationToken.None);

        Assert.Null(Assert.Single(state.Targets).SoleRole());
    }

    [Fact]
    public async Task Read_KeepsEveryAccessLevelAPrincipalHolds()
    {
        var (adapter, _) = Create(StateJson(Ada, "Consumer", "Analyst"));

        var state = await adapter.ReadAsync(Context(), Bot, CancellationToken.None);

        Assert.Equal(["Consumer", "Analyst"], Assert.Single(state.Targets).SharingTypes);
    }

    [Fact]
    public async Task Read_KeepsTheObjectIdTypeAndClientIdTheServiceReported()
    {
        var (adapter, _) = Create($$"""{"shareWithOrg":false,"targets":[{"objectId":"{{Ada}}","objectIdType":"Something","sharingType":["Consumer"],"clientId":"app-1"}]}""");

        var target = Assert.Single((await adapter.ReadAsync(Context(), Bot, CancellationToken.None)).Targets);

        Assert.Equal("Something", target.ObjectIdType);
        Assert.Equal("app-1", target.ClientId);
    }

    [Fact]
    public async Task Read_DefaultsTheObjectIdTypeWhenTheServiceOmitsIt()
    {
        var (adapter, _) = Create($$"""{"shareWithOrg":false,"targets":[{"objectId":"{{Ada}}","sharingType":["Consumer"],"clientId":null}]}""");

        var target = Assert.Single((await adapter.ReadAsync(Context(), Bot, CancellationToken.None)).Targets);

        Assert.Equal("Object", target.ObjectIdType);
        Assert.Null(target.ClientId);
    }

    [Theory]
    [InlineData("""{"shareWithOrg":false,"storeDiscoverable":"true","targets":[]}""")]
    [InlineData("""{"shareWithOrg":false,"enableActivitySharing":1,"targets":[]}""")]
    [InlineData("""{"shareWithOrg":false,"targets":[{"objectId":"22222222-2222-2222-2222-2222222222bb","objectIdType":42,"sharingType":["Consumer"]}]}""")]
    [InlineData("""{"shareWithOrg":false,"targets":[{"objectId":"22222222-2222-2222-2222-2222222222bb","sharingType":["Consumer"],"clientId":{"app":"app-1"}}]}""")]
    [InlineData("""{"shareWithOrg":false,"targets":[{"objectId":"not-a-guid","sharingType":["Consumer"]}]}""")]
    [InlineData("""{"shareWithOrg":false,"targets":[{"objectId":"   ","sharingType":["Consumer"]}]}""")]
    public async Task AFieldThisBuildCannotReadIsAFailureRatherThanADefault(string payload)
    {
        var (adapter, _) = Create(payload);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ReadAsync(Context(), Bot, CancellationToken.None));

        Assert.Equal("sharing-state-not-returned", failure.ErrorKind);
    }

    [Fact]
    public async Task AMalformedFieldStopsAWriteRatherThanRewritingIt()
    {
        var (adapter, handler) = Create("""{"shareWithOrg":false,"storeDiscoverable":"true","targets":[]}""");

        await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ReadAsync(Context(), Bot, CancellationToken.None));

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
    }

    [Fact]
    public async Task Read_ReportsOrgWideAccess()
    {
        var (adapter, _) = Create("""{"shareWithOrg":true,"targets":[]}""");

        Assert.True((await adapter.ReadAsync(Context(), Bot, CancellationToken.None)).ShareWithOrg);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"targets":[]}""")]
    [InlineData("""{"shareWithOrg":false}""")]
    [InlineData("""{"shareWithOrg":false,"targets":{}}""")]
    [InlineData("""{"shareWithOrg":false,"targets":[{"sharingType":["Consumer"]}]}""")]
    [InlineData("""{"shareWithOrg":false,"targets":[{"objectId":"ada-object-id"}]}""")]
    [InlineData("""{"shareWithOrg":false,"targets":[{"objectId":"ada-object-id","sharingType":[7]}]}""")]
    public async Task AStateThatCannotBeReadIsAFailureRatherThanAnEmptyState(string payload)
    {
        var (adapter, _) = Create(payload);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ReadAsync(Context(), Bot, CancellationToken.None));

        Assert.Equal("sharing-state-not-returned", failure.ErrorKind);
    }

    [Fact]
    public async Task Write_PostsTheWholeStateIncludingTheOwner()
    {
        var (adapter, handler) = Create(StateJson((Owner, "Owner"), (Ada, "Consumer")));

        await adapter.WriteAsync(
            Context(),
            Bot,
            new AgentSharingState
            {
                Targets = [AgentShareTarget.For(Owner, AgentSharingRole.Owner), AgentShareTarget.For(Ada, AgentSharingRole.Consumer)],
            },
            CancellationToken.None);

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var targets = document.RootElement.GetProperty("targets");

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal(2, targets.GetArrayLength());
        Assert.Equal("Owner", targets[0].GetProperty("sharingType")[0].GetString());
        Assert.Equal(Owner, targets[0].GetProperty("objectId").GetString());
        Assert.Equal("Object", targets[0].GetProperty("objectIdType").GetString());
        Assert.Equal("Consumer", targets[1].GetProperty("sharingType")[0].GetString());
    }

    [Fact]
    public async Task Write_SendsOrgWideAccess()
    {
        var (adapter, handler) = Create(StateJson());

        await adapter.WriteAsync(Context(), Bot, new AgentSharingState { ShareWithOrg = true }, CancellationToken.None);

        using var document = JsonDocument.Parse(handler.RequestBody!);

        Assert.True(document.RootElement.GetProperty("shareWithOrg").GetBoolean());
    }

    [Fact]
    public async Task Write_SendsTheContractsOptionalFields()
    {
        var (adapter, handler) = Create(StateJson());

        await adapter.WriteAsync(Context(), Bot, new AgentSharingState(), CancellationToken.None);

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var root = document.RootElement;

        Assert.False(root.GetProperty("storeDiscoverable").GetBoolean());
        Assert.False(root.GetProperty("enableActivitySharing").GetBoolean());
        Assert.False(root.GetProperty("isApprovedFromRequest").GetBoolean());
    }

    [Fact]
    public async Task Write_SendsBackTheServiceOwnedSettingsTheReadReported()
    {
        var (adapter, handler) = Create("""{"shareWithOrg":false,"storeDiscoverable":true,"enableActivitySharing":true,"targets":[]}""");

        var state = await adapter.ReadAsync(Context(), Bot, CancellationToken.None);
        await adapter.WriteAsync(Context(), Bot, state, CancellationToken.None);

        using var document = JsonDocument.Parse(handler.RequestBody!);

        Assert.True(document.RootElement.GetProperty("storeDiscoverable").GetBoolean());
        Assert.True(document.RootElement.GetProperty("enableActivitySharing").GetBoolean());
    }

    [Fact]
    public async Task AnEnvironmentWithNoGatewayIsAFailure()
    {
        var (adapter, _) = Create(StateJson());

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ReadAsync(Context(gateway: null), Bot, CancellationToken.None));

        Assert.Equal("agent-management-endpoint-missing", failure.ErrorKind);
    }

    [Theory]
    [InlineData("https://powervamg.us-il301.gateway.prod.island.powerapps.com.attacker.example/")]
    [InlineData("https://attacker.example/")]
    [InlineData("http://powervamg.us-il301.gateway.prod.island.powerapps.com/")]
    [InlineData("https://powervamg.us-il301.gateway.prod.island.powerapps.com:8443/")]
    [InlineData("https://notisland.powerapps.com/")]
    public async Task AnUntrustedGatewayIsRejectedBeforeATokenIsRequested(string gateway)
    {
        var (adapter, handler, tokens) = CreateWithTokens(StateJson());

        await Assert.ThrowsAsync<EndpointTrustException>(() => adapter.ReadAsync(Context(gateway), Bot, CancellationToken.None));

        Assert.Null(tokens.RequestedScopes);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task AFailedResponseNamesTheServiceCodeWithoutEchoingTheBody()
    {
        var (adapter, _) = Create("""{"ErrorMessage":"secret detail","Error":{"Code":"MissingRoutingHeader"}}""", HttpStatusCode.BadRequest);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ReadAsync(Context(), Bot, CancellationToken.None));

        Assert.Contains("MissingRoutingHeader", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret detail", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClassifiedFailureRelaysTheRuleTheServiceEnforced()
    {
        var (adapter, _) = Create(
            """{"Error":{"Code":"InvalidBotShareRequest","Message":"User 6d531c04 is already an Environment Maker and can't be added as an Analytics Viewer for this agent."}}""",
            HttpStatusCode.BadRequest);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.WriteAsync(Context(), Bot, new AgentSharingState(), CancellationToken.None));

        Assert.Contains("InvalidBotShareRequest", failure.Message, StringComparison.Ordinal);
        Assert.Contains("already an Environment Maker", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidationFailureNamesTheFaultedFieldsWithoutEchoingTheirValues()
    {
        var (adapter, _) = Create(
            """{"errors":{"targets[1].objectId":["Error converting value \"steve@contoso.com\" to type 'System.Guid'."]},"title":"One or more validation errors occurred.","status":400}""",
            HttpStatusCode.BadRequest);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ReadAsync(Context(), Bot, CancellationToken.None));

        Assert.Contains("One or more validation errors occurred.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("targets[1].objectId", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("steve@contoso.com", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnrecognisedFailureShapeReportsTheStatusAlone()
    {
        var (adapter, _) = Create("""{"something":"else"}""", HttpStatusCode.InternalServerError);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ReadAsync(Context(), Bot, CancellationToken.None));

        Assert.Contains("HTTP 500", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("else", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"unavailable\"")]
    [InlineData("""{"Error":null}""")]
    [InlineData("""{"Error":"unavailable"}""")]
    [InlineData("""{"Error":{"Code":42}}""")]
    [InlineData("""{"title":42}""")]
    [InlineData("""{"title":"Invalid.","errors":[]}""")]
    public async Task AFailureEnvelopeThisBuildCannotReadStillReportsTheHttpStatus(string payload)
    {
        var (adapter, _) = Create(payload, HttpStatusCode.Forbidden);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ReadAsync(Context(), Bot, CancellationToken.None));

        Assert.Contains("HTTP 403", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryRequestSharesOneTransport()
    {
        var handler = new RecordingHandler(StateJson(), HttpStatusCode.OK);
        var created = 0;
        var adapter = new IslandSharingAdapter(new StubTokenProvider(), CloudMap, CoreServicesClusterCategory.Prod, () => { created++; return handler; });

        await adapter.ReadAsync(Context(), Bot, CancellationToken.None);
        await adapter.ReadAsync(Context(), Bot, CancellationToken.None);
        await adapter.WriteAsync(Context(), Bot, new AgentSharingState(), CancellationToken.None);

        Assert.Equal(1, created);
    }

    [Fact]
    public async Task Write_ReadsTheStateBackWhenTheServiceAcceptsTheWriteWithoutDescribingIt()
    {
        var handler = new RecordingHandler(string.Empty, HttpStatusCode.OK) { NextBody = StateJson((Ada, "Consumer")) };
        var adapter = new IslandSharingAdapter(new StubTokenProvider(), CloudMap, CoreServicesClusterCategory.Prod, () => handler);

        var state = await adapter.WriteAsync(Context(), Bot, new AgentSharingState(), CancellationToken.None);

        Assert.Equal(Ada, Assert.Single(state.Targets).ObjectId);
        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
    }

    [Fact]
    public async Task Write_FailsWhenNeitherTheWriteNorTheReadBackDescribesTheState()
    {
        var (adapter, _) = Create(string.Empty);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.WriteAsync(Context(), Bot, new AgentSharingState(), CancellationToken.None));

        Assert.Equal("sharing-state-not-returned", failure.ErrorKind);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public RecordingHandler(string body, HttpStatusCode status)
        {
            _body = body;
            _status = status;
        }

        public HttpRequestMessage? Request { get; private set; }

        public string? RequestBody { get; private set; }

        public string? NextBody { get; set; }

        public string? Header(string name) =>
            Request!.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = Request is null ? _body : NextBody ?? _body;

            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubTokenProvider : ITokenProvider
    {
        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(string resource, string? targetTenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken cancellationToken)

        {            RequestedScopes = scopes;            RequestedTenantId = targetTenantId;

            return Task.FromResult(new TokenAcquisitionResult { AccessToken = "stub-token", ExpiresOn = DateTimeOffset.UtcNow.AddHours(1) });
        }

        public IReadOnlyList<string>? RequestedScopes { get; private set; }

        

        public string? RequestedTenantId { get; private set; }
    }
}
