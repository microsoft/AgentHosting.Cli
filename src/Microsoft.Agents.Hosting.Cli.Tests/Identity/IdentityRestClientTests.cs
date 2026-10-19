using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Identity;

public class IdentityRestClientTests
{
    [Fact]
    public async Task SendGraphAsync_UsesExplicitScopeAndTargetTenant()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"id":"app"}"""));
        var tokens = new RecordingTokenProvider();
        var client = new IdentityRestClient(new HttpClient(handler), tokens);

        var response = await client.SendGraphAsync(
            HttpMethod.Post,
            "/beta/applications",
            new { displayName = "Contoso" },
            [IdentityProvisioningConfig.GraphReadWriteScope],
            "tenant",
            cancellationToken: default);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal([IdentityProvisioningConfig.GraphReadWriteScope], tokens.Scopes);
        Assert.Equal("tenant", tokens.Tenant);
        Assert.Equal("https://graph.microsoft.com/beta/applications", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer token", handler.LastRequest.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task SendGraphAsync_UsesGraphConsentProvider_WhenWired()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"id":"app"}"""));
        var silent = new RecordingTokenProvider();
        var consent = new RecordingGraphConsentProvider();
        var client = new IdentityRestClient(new HttpClient(handler), silent, consent);

        var response = await client.SendGraphAsync(
            HttpMethod.Post,
            "/v1.0/applications",
            new { displayName = "Contoso" },
            [IdentityProvisioningConfig.GraphApplicationReadWriteScope],
            "tenant",
            cancellationToken: default);

        Assert.Equal(200, response.StatusCode);
        // Graph goes through the consenting client, not the silent provider.
        Assert.Equal([IdentityProvisioningConfig.GraphApplicationReadWriteScope], consent.Scopes);
        Assert.Equal("tenant", consent.Tenant);
        Assert.Null(silent.Scopes);
        Assert.Null(silent.Resource);
    }

    [Fact]
    public async Task SendArmAsync_UsesSilentProvider_EvenWhenGraphConsentWired()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Created, """{"id":"/identity"}"""));
        var silent = new RecordingTokenProvider();
        var consent = new RecordingGraphConsentProvider();
        var client = new IdentityRestClient(new HttpClient(handler), silent, consent);

        await client.SendArmAsync(
            HttpMethod.Put,
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/mi",
            new { location = "westus" },
            "tenant",
            cancellationToken: default);

        // ARM must never use the consent-capable Graph client.
        Assert.Equal(IdentityProvisioningConfig.ArmResource, silent.Resource);
        Assert.Null(consent.Scopes);
    }

    [Fact]
    public async Task SendArmAsync_UsesArmConsentProvider_WhenWired()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Created, """{"id":"/identity"}"""));
        var silent = new RecordingTokenProvider();
        var armConsent = new RecordingArmConsentProvider();
        var client = new IdentityRestClient(
            new HttpClient(handler),
            silent,
            graphConsentTokenProvider: null,
            armConsentTokenProvider: armConsent);

        var response = await client.SendArmAsync(
            HttpMethod.Put,
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/mi",
            new { location = "westus" },
            "tenant",
            cancellationToken: default);

        Assert.Equal(201, response.StatusCode);
        // ARM goes through the consenting client, not the silent provider.
        Assert.True(armConsent.Called);
        Assert.Equal("tenant", armConsent.Tenant);
        Assert.Null(silent.Resource);
    }

    [Fact]
    public async Task SendGraphAsync_UsesGraphConsentProvider_EvenWhenArmConsentWired()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"id":"app"}"""));
        var silent = new RecordingTokenProvider();
        var graphConsent = new RecordingGraphConsentProvider();
        var armConsent = new RecordingArmConsentProvider();
        var client = new IdentityRestClient(new HttpClient(handler), silent, graphConsent, armConsent);

        await client.SendGraphAsync(
            HttpMethod.Post,
            "/v1.0/applications",
            new { displayName = "Contoso" },
            [IdentityProvisioningConfig.GraphApplicationReadWriteScope],
            "tenant",
            cancellationToken: default);

        // Graph uses the Graph consent client; the ARM consent client is never touched for Graph calls.
        Assert.Equal([IdentityProvisioningConfig.GraphApplicationReadWriteScope], graphConsent.Scopes);
        Assert.False(armConsent.Called);
        Assert.Null(silent.Scopes);
    }

    [Fact]
    public async Task SendGraphAsync_RejectsOffOriginAbsoluteUriBeforeTokenAcquisition()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"value":[]}"""));
        var tokens = new RecordingTokenProvider();
        var client = new IdentityRestClient(new HttpClient(handler), tokens);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendGraphAsync(
            HttpMethod.Get,
            "https://example.com/beta/applications?$skiptoken=attacker",
            body: null,
            [IdentityProvisioningConfig.GraphReadWriteScope],
            "tenant",
            cancellationToken: default));

        Assert.Null(tokens.Scopes);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task SendArmAsync_UsesArmResourceTokenAndEscapedResourcePath()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Created, """{"id":"/identity"}"""));
        var tokens = new RecordingTokenProvider();
        var client = new IdentityRestClient(new HttpClient(handler), tokens);

        await client.SendArmAsync(
            HttpMethod.Put,
            "/subscriptions/sub/resourceGroups/agent rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/agent",
            new { location = "westus" },
            "tenant",
            cancellationToken: default);

        Assert.Equal(IdentityProvisioningConfig.ArmResource, tokens.Resource);
        Assert.Contains("resourceGroups/agent%20rg/", handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendGraphAsync_MapsNetworkAndTimeoutFailures(bool timeout)
    {
        Exception failure = timeout
            ? new TaskCanceledException("request timed out")
            : new HttpRequestException("connection failed");
        var handler = new FailingHandler(failure);
        var client = new IdentityRestClient(
            new HttpClient(handler),
            new RecordingTokenProvider(),
            retryDelay: (_, _) => Task.CompletedTask);

        var error = await Assert.ThrowsAsync<TargetRequestException>(() =>
            client.SendGraphAsync(
                HttpMethod.Get,
                "/beta/applications",
                body: null,
                [IdentityProvisioningConfig.GraphReadWriteScope],
                "tenant",
                cancellationToken: default));

        Assert.Equal(TargetErrorKind.RequestFailed, error.Kind);
        Assert.Equal(0, error.Status);
        Assert.DoesNotContain(failure.Message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendGraphAsync_PreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new FailingHandler(new OperationCanceledException(cancellation.Token));
        var client = new IdentityRestClient(new HttpClient(handler), new RecordingTokenProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendGraphAsync(
                HttpMethod.Get,
                "/beta/applications",
                body: null,
                [IdentityProvisioningConfig.GraphReadWriteScope],
                "tenant",
                cancellation.Token));
    }

    [Fact]
    public async Task SendGraphAsync_RetriesThrottlingAfterRetryAfterDelay()
    {
        var handler = new SequenceHandler(
            TooManyRequests(TimeSpan.FromSeconds(7)),
            Json(HttpStatusCode.OK, """{"id":"app"}"""));
        var delays = new List<TimeSpan>();
        var client = new IdentityRestClient(
            new HttpClient(handler),
            new RecordingTokenProvider(),
            retryDelay: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var response = await client.SendGraphAsync(
            HttpMethod.Post,
            "/v1.0/applications",
            new { displayName = "Contoso" },
            [IdentityProvisioningConfig.GraphApplicationReadWriteScope],
            "tenant",
            cancellationToken: default);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal([TimeSpan.FromSeconds(7)], delays);
    }

    [Fact]
    public async Task SendGraphAsync_RetriesIdempotentTransientFailureWithBackoff()
    {
        var handler = new SequenceHandler(
            Json(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"ServiceUnavailable"}}"""),
            Json(HttpStatusCode.OK, """{"value":[]}"""));
        var delays = new List<TimeSpan>();
        var client = new IdentityRestClient(
            new HttpClient(handler),
            new RecordingTokenProvider(),
            retryDelay: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var response = await client.SendGraphAsync(
            HttpMethod.Get,
            "/v1.0/applications",
            body: null,
            [IdentityProvisioningConfig.GraphApplicationReadScope],
            "tenant",
            cancellationToken: default);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal([TimeSpan.FromSeconds(1)], delays);
    }

    [Fact]
    public async Task SendGraphAsync_RetriesIdempotentNetworkFailure()
    {
        var handler = new SequenceHandler(
            new HttpRequestException("connection reset"),
            Json(HttpStatusCode.OK, """{"value":[]}"""));
        var client = new IdentityRestClient(
            new HttpClient(handler),
            new RecordingTokenProvider(),
            retryDelay: (_, _) => Task.CompletedTask);

        var response = await client.SendGraphAsync(
            HttpMethod.Get,
            "/v1.0/applications",
            body: null,
            [IdentityProvisioningConfig.GraphApplicationReadScope],
            "tenant",
            cancellationToken: default);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task SendGraphAsync_DoesNotRetryPostAfterAmbiguousServiceFailure()
    {
        var handler = new SequenceHandler(
            Json(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"ServiceUnavailable"}}"""),
            Json(HttpStatusCode.Created, """{"id":"duplicate"}"""));
        var client = new IdentityRestClient(
            new HttpClient(handler),
            new RecordingTokenProvider(),
            retryDelay: (_, _) => Task.CompletedTask);

        var response = await client.SendGraphAsync(
            HttpMethod.Post,
            "/v1.0/applications",
            new { displayName = "Contoso" },
            [IdentityProvisioningConfig.GraphApplicationReadWriteScope],
            "tenant",
            cancellationToken: default);

        Assert.Equal(503, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SendGraphAsync_StopsAfterBoundedTransientRetries()
    {
        var handler = new SequenceHandler(
            Json(HttpStatusCode.BadGateway, """{"error":{"code":"BadGateway"}}"""),
            Json(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"ServiceUnavailable"}}"""),
            Json(HttpStatusCode.GatewayTimeout, """{"error":{"code":"GatewayTimeout"}}"""));
        var delays = new List<TimeSpan>();
        var client = new IdentityRestClient(
            new HttpClient(handler),
            new RecordingTokenProvider(),
            retryDelay: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var response = await client.SendGraphAsync(
            HttpMethod.Patch,
            "/v1.0/applications/app-object-id",
            new { displayName = "Contoso" },
            [IdentityProvisioningConfig.GraphApplicationReadWriteScope],
            "tenant",
            cancellationToken: default);

        Assert.Equal(504, response.StatusCode);
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task SendGraphAsync_PreservesCancellationDuringRetryDelay()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new SequenceHandler(TooManyRequests(TimeSpan.FromSeconds(30)));
        var client = new IdentityRestClient(
            new HttpClient(handler),
            new RecordingTokenProvider(),
            retryDelay: (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendGraphAsync(
                HttpMethod.Get,
                "/v1.0/applications",
                body: null,
                [IdentityProvisioningConfig.GraphApplicationReadScope],
                "tenant",
                cancellation.Token));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SendArmAsync_RetriesThrottling()
    {
        var handler = new SequenceHandler(
            TooManyRequests(TimeSpan.Zero),
            Json(HttpStatusCode.OK, """{"id":"identity"}"""));
        var client = new IdentityRestClient(
            new HttpClient(handler),
            new RecordingTokenProvider(),
            retryDelay: (_, _) => Task.CompletedTask);

        var response = await client.SendArmAsync(
            HttpMethod.Put,
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/mi",
            new { location = "westus" },
            "tenant",
            cancellationToken: default);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage TooManyRequests(TimeSpan retryAfter)
    {
        var response = Json(
            HttpStatusCode.TooManyRequests,
            """{"error":{"code":"TooManyRequests"}}""");
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class FailingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(failure);
    }

    private sealed class SequenceHandler(params object[] outcomes) : HttpMessageHandler
    {
        private readonly Queue<object> _outcomes = new(outcomes);

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return _outcomes.Dequeue() switch
            {
                HttpResponseMessage response => Task.FromResult(response),
                Exception error => Task.FromException<HttpResponseMessage>(error),
                _ => throw new InvalidOperationException("Unsupported test outcome."),
            };
        }
    }

    private sealed class RecordingTokenProvider : ITokenProvider
    {
        public string? Resource { get; private set; }
        public IReadOnlyList<string>? Scopes { get; private set; }
        public string? Tenant { get; private set; }

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
            string resource,
            string? targetTenantId,
            CancellationToken cancellationToken)
        {
            Resource = resource;
            Tenant = targetTenantId;
            return Result();
        }

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
            IReadOnlyList<string> scopes,
            string? targetTenantId,
            CancellationToken cancellationToken)
        {
            Scopes = scopes;
            Tenant = targetTenantId;
            return Result();
        }

        private static Task<TokenAcquisitionResult> Result() => Task.FromResult(new TokenAcquisitionResult
        {
            AccessToken = "token",
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
        });
    }

    private sealed class RecordingGraphConsentProvider : IGraphConsentTokenProvider
    {
        public IReadOnlyList<string>? Scopes { get; private set; }
        public string? Tenant { get; private set; }

        public Task<TokenAcquisitionResult> AcquireGraphTokenAsync(
            IReadOnlyList<string> scopes,
            string? targetTenantId,
            CancellationToken cancellationToken)
        {
            Scopes = scopes;
            Tenant = targetTenantId;
            return Task.FromResult(new TokenAcquisitionResult
            {
                AccessToken = "consent-token",
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
            });
        }
    }

    private sealed class RecordingArmConsentProvider : IArmConsentTokenProvider
    {
        public bool Called { get; private set; }
        public string? Tenant { get; private set; }

        public Task<TokenAcquisitionResult> AcquireArmTokenAsync(
            string? targetTenantId,
            CancellationToken cancellationToken)
        {
            Called = true;
            Tenant = targetTenantId;
            return Task.FromResult(new TokenAcquisitionResult
            {
                AccessToken = "arm-consent-token",
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
            });
        }
    }
}
