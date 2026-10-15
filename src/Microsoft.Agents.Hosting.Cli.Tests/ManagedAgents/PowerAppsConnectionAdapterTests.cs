using Microsoft.Agents.Hosting.Cli.Core.Environments;
using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class PowerAppsConnectionAdapterTests
{
    private static CopilotEnvironmentContext Environment() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "tenant-1",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private sealed class FixedTokenProvider : ITokenProvider
    {
        public FixedTokenProvider(string? tenantId = null) => TenantId = tenantId;

        private string? TenantId { get; }

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(string resource, string? targetTenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TokenAcquisitionResult { AccessToken = "token-1", TenantId = TenantId, ExpiresOn = DateTimeOffset.UtcNow.AddHours(1) });

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TokenAcquisitionResult { AccessToken = "token-1", TenantId = TenantId, ExpiresOn = DateTimeOffset.UtcNow.AddHours(1) });
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public CapturingHandler(HttpStatusCode statusCode, string body = "")
        {
            StatusCode = statusCode;
            Body = body;
        }

        public HttpStatusCode StatusCode { get; }

        public string Body { get; }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;

            return Task.FromResult(new HttpResponseMessage(StatusCode) { Content = new StringContent(Body) });
        }
    }

    private static PowerAppsConnectionAdapter Adapter(CapturingHandler handler, CoreServicesClusterCategory cluster = CoreServicesClusterCategory.Prod, string? acquiredTenantId = null) =>
        new(new FixedTokenProvider(acquiredTenantId), cluster, () => handler);

    [Theory]
    [InlineData("{\"error\":{\"code\":\"ConnectionNotFound\"}}")]
    [InlineData("{\"error\":{\"code\":\"ApiNotFound\"}}")]
    [InlineData("")]
    public async Task Delete_WhenTheServiceSaysTheConnectionIsGone_Succeeds(string body)
    {
        using var handler = new CapturingHandler(HttpStatusCode.NotFound, body);
        using var adapter = Adapter(handler);

        await adapter.DeleteConnectionAsync(Environment(), "shared_teams", "conn-1", CancellationToken.None);

        Assert.NotNull(handler.Request);
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":\"ServiceToServiceEnvironmentNotFound\"}}")]
    [InlineData("{\"error\":{\"code\":\"EnvironmentNotFound\"}}")]
    [InlineData("{\"error\":{\"code\":\"RouteNotFound\"}}")]
    [InlineData("{\"error\":{\"code\":\"ServiceDecommissioned\"}}")]
    public async Task Delete_WhenTheRequestNeverReachedTheConnection_IsNotReportedAsDeleted(string body)
    {
        using var handler = new CapturingHandler(HttpStatusCode.NotFound, body);
        using var adapter = Adapter(handler);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => adapter.DeleteConnectionAsync(Environment(), "shared_teams", "conn-1", CancellationToken.None));

        Assert.Contains("conn-1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_SendsTheTokenWhenTheLibraryAcquiredItForTheTargetedTenant()
    {
        using var handler = new CapturingHandler(HttpStatusCode.OK);
        using var adapter = Adapter(handler, acquiredTenantId: "tenant-1");

        await adapter.DeleteConnectionAsync(Environment(), "shared_teams", "conn-1", CancellationToken.None);

        Assert.NotNull(handler.Request);
    }

    [Fact]
    public async Task Delete_DoesNotSendTheRequestWhenTheLibraryAcquiredAnotherTenantsToken()
    {
        using var handler = new CapturingHandler(HttpStatusCode.OK);
        using var adapter = Adapter(handler, acquiredTenantId: "another-tenant");

        var error = await Assert.ThrowsAsync<TokenClaimException>(() => adapter.DeleteConnectionAsync(Environment(), "shared_teams", "conn-1", CancellationToken.None));

        Assert.Equal(TokenClaimFailure.WrongTenant, error.Failure);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Delete_SendsADeleteToTheConnectionsResource()
    {
        using var handler = new CapturingHandler(HttpStatusCode.OK);
        using var adapter = Adapter(handler);

        await adapter.DeleteConnectionAsync(Environment(), "shared_teams", "conn-1", CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.Request!.Method);
        Assert.Equal("api.powerapps.com", handler.Request.RequestUri!.Host);
        Assert.Contains("/providers/Microsoft.PowerApps/apis/shared_teams/connections/conn-1", handler.Request.RequestUri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("api-version=2016-11-01", handler.Request.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_ScopesTheRequestToTheEnvironment()
    {
        using var handler = new CapturingHandler(HttpStatusCode.OK);
        using var adapter = Adapter(handler);

        await adapter.DeleteConnectionAsync(Environment(), "shared_teams", "conn-1", CancellationToken.None);

        Assert.Contains(Uri.EscapeDataString("environment eq 'env-1'"), handler.Request!.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_CarriesTheBearerToken()
    {
        using var handler = new CapturingHandler(HttpStatusCode.OK);
        using var adapter = Adapter(handler);

        await adapter.DeleteConnectionAsync(Environment(), "shared_teams", "conn-1", CancellationToken.None);

        Assert.Equal("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        Assert.Equal("token-1", handler.Request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Delete_AcceptsAProviderPathForEitherIdentifier()
    {
        using var handler = new CapturingHandler(HttpStatusCode.OK);
        using var adapter = Adapter(handler);

        await adapter.DeleteConnectionAsync(Environment(), "/providers/Microsoft.PowerApps/apis/shared_teams", "/providers/Microsoft.PowerApps/apis/shared_teams/connections/conn-1", CancellationToken.None);

        Assert.Contains("/apis/shared_teams/connections/conn-1", handler.Request!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_AConnectionThatIsAlreadyGoneSucceeds()
    {
        using var handler = new CapturingHandler(HttpStatusCode.NotFound);
        using var adapter = Adapter(handler);

        await adapter.DeleteConnectionAsync(Environment(), "shared_teams", "conn-1", CancellationToken.None);

        Assert.NotNull(handler.Request);
    }

    [Fact]
    public async Task Delete_ARefusalIsReportedWithTheServiceCorrelationRatherThanItsMessage()
    {
        using var handler = new CapturingHandler(HttpStatusCode.Forbidden, "you do not own this connection");
        using var adapter = Adapter(handler);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => adapter.DeleteConnectionAsync(Environment(), "shared_teams", "conn-1", CancellationToken.None));

        Assert.Equal("connection-not-deleted", failure.ErrorKind);
        Assert.Contains("403", failure.Message, StringComparison.Ordinal);
        Assert.Contains("clientRequestId=", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("you do not own this connection", failure.Message, StringComparison.Ordinal);
    }

    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()));
    }

    [Fact]
    public async Task Delete_ATransportTimeoutIsReportedAsATimeoutRatherThanACancellation()
    {
        using var handler = new CancellingHandler();
        using var adapter = new PowerAppsConnectionAdapter(new FixedTokenProvider(), CoreServicesClusterCategory.Prod, () => handler);

        var failure = await Assert.ThrowsAsync<TimeoutException>(() => adapter.DeleteConnectionAsync(Environment(), "shared_teams", "conn-1", CancellationToken.None));

        Assert.Contains("conn-1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_ACancellationByTheCallerStaysACancellation()
    {
        using var handler = new CancellingHandler();
        using var adapter = new PowerAppsConnectionAdapter(new FixedTokenProvider(), CoreServicesClusterCategory.Prod, () => handler);
        using var cancellation = new CancellationTokenSource();

        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() => adapter.DeleteConnectionAsync(Environment(), "shared_teams", "conn-1", cancellation.Token));
    }

    [Theory]
    [InlineData(CoreServicesClusterCategory.Gov, "gov.api.powerapps.us")]
    [InlineData(CoreServicesClusterCategory.High, "high.api.powerapps.us")]
    [InlineData(CoreServicesClusterCategory.Mooncake, "api.powerapps.cn")]
    public async Task Delete_UsesTheApiHostForTheCloud(CoreServicesClusterCategory cluster, string expectedHost)
    {
        using var handler = new CapturingHandler(HttpStatusCode.OK);
        using var adapter = Adapter(handler, cluster);

        await adapter.DeleteConnectionAsync(Environment(), "shared_teams", "conn-1", CancellationToken.None);

        Assert.Equal(expectedHost, handler.Request!.RequestUri!.Host);
    }
}
