using System.Net;
using System.Net.Http.Headers;
using Microsoft.Agents.Hosting.Cli.Core.Http;

namespace Microsoft.Agents.Hosting.Cli.Tests;

internal sealed class SequencedResponseHandler(params HttpStatusCode[] statusCodes) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public TimeSpan? RetryAfterDelta { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var status = statusCodes[Math.Min(Requests.Count - 1, statusCodes.Length - 1)];
        var response = new HttpResponseMessage(status);

        if (RetryAfterDelta is { } delta && status == HttpStatusCode.TooManyRequests)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
        }

        return Task.FromResult(response);
    }
}

public sealed class ResilientRequestHandlerTests
{
    private static HttpClient Client(SequencedResponseHandler inner, out List<TimeSpan> waits, int maximumAttempts = 3)
    {
        var recorded = new List<TimeSpan>();
        waits = recorded;

        return new HttpClient(new ResilientRequestHandler(maximumAttempts, (wait, _) => { recorded.Add(wait); return Task.CompletedTask; }, () => 0d) { InnerHandler = inner });
    }

    [Fact]
    public async Task ASuccessfulRequestIsSentOnce()
    {
        var inner = new SequencedResponseHandler(HttpStatusCode.OK);
        using var client = Client(inner, out _);

        await client.GetAsync(new Uri("https://contoso.crm.dynamics.com/api"), CancellationToken.None);

        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task AThrottledRequestIsRetried()
    {
        var inner = new SequencedResponseHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        using var client = Client(inner, out _);

        var response = await client.GetAsync(new Uri("https://contoso.crm.dynamics.com/api"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Requests.Count);
    }

    [Fact]
    public async Task TheRetryAfterHeaderIsHonoured()
    {
        var inner = new SequencedResponseHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK) { RetryAfterDelta = TimeSpan.FromSeconds(42) };
        using var client = Client(inner, out var waits);

        await client.GetAsync(new Uri("https://contoso.crm.dynamics.com/api"), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(42), Assert.Single(waits));
    }

    [Fact]
    public async Task AttemptsAreBounded()
    {
        var inner = new SequencedResponseHandler(HttpStatusCode.TooManyRequests);
        using var client = Client(inner, out _);

        var response = await client.GetAsync(new Uri("https://contoso.crm.dynamics.com/api"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(3, inner.Requests.Count);
    }

    [Fact]
    public async Task ATransientServerFailureIsRetriedForAnIdempotentMethod()
    {
        var inner = new SequencedResponseHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var client = Client(inner, out _);

        await client.GetAsync(new Uri("https://contoso.crm.dynamics.com/api"), CancellationToken.None);

        Assert.Equal(2, inner.Requests.Count);
    }

    [Fact]
    public async Task ATransientServerFailureIsNotRetriedForAPost()
    {
        var inner = new SequencedResponseHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var client = Client(inner, out _);

        await client.PostAsync(new Uri("https://contoso.crm.dynamics.com/api"), new StringContent("{}"), CancellationToken.None);

        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task AThrottledPostIsStillRetried()
    {
        var inner = new SequencedResponseHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        using var client = Client(inner, out _);

        await client.PostAsync(new Uri("https://contoso.crm.dynamics.com/api"), new StringContent("{\"a\":1}"), CancellationToken.None);

        Assert.Equal(2, inner.Requests.Count);
    }

    [Fact]
    public async Task RetriedContentIsResent()
    {
        var inner = new SequencedResponseHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        using var client = Client(inner, out _);

        await client.PostAsync(new Uri("https://contoso.crm.dynamics.com/api"), new StringContent("{\"a\":1}"), CancellationToken.None);

        Assert.Equal("{\"a\":1}", await inner.Requests[1].Content!.ReadAsStringAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnAbandonedRetryCloneIsDisposed()
    {
        var inner = new SequencedResponseHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        using var client = Client(inner, out _);

        await client.PostAsync(new Uri("https://contoso.crm.dynamics.com/api"), new StringContent("{\"a\":1}"), CancellationToken.None);

        Assert.Equal(3, inner.Requests.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => inner.Requests[1].Content!.ReadAsStringAsync(CancellationToken.None));
    }

    private sealed class ThrowingOnAttemptHandler(int throwOnAttempt) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            return Requests.Count == throwOnAttempt
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("transport failed"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }
    }

    [Fact]
    public async Task ARetryCloneIsDisposedWhenSendingThrows()
    {
        var inner = new ThrowingOnAttemptHandler(throwOnAttempt: 2);
        using var client = new HttpClient(new ResilientRequestHandler(3, (_, _) => Task.CompletedTask, () => 0d) { InnerHandler = inner });

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.PostAsync(new Uri("https://contoso.crm.dynamics.com/api"), new StringContent("{\"a\":1}"), CancellationToken.None));

        Assert.Equal(2, inner.Requests.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => inner.Requests[1].Content!.ReadAsStringAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheCallersOwnRequestIsNeverDisposedWhenSendingThrows()
    {
        var inner = new ThrowingOnAttemptHandler(throwOnAttempt: 1);
        using var client = new HttpClient(new ResilientRequestHandler(3, (_, _) => Task.CompletedTask, () => 0d) { InnerHandler = inner });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://contoso.crm.dynamics.com/api")) { Content = new StringContent("{\"a\":1}") };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, CancellationToken.None));

        Assert.Equal("{\"a\":1}", await request.Content!.ReadAsStringAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheReturnedResponseKeepsAUsableRequest()
    {
        var inner = new SequencedResponseHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        using var client = Client(inner, out _);

        var response = await client.PostAsync(new Uri("https://contoso.crm.dynamics.com/api"), new StringContent("{\"a\":1}"), CancellationToken.None);

        Assert.Equal("{\"a\":1}", await inner.Requests[1].Content!.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EveryRequestCarriesACorrelationIdentifier()
    {
        var inner = new SequencedResponseHandler(HttpStatusCode.OK);
        using var client = Client(inner, out _);

        await client.GetAsync(new Uri("https://contoso.crm.dynamics.com/api"), CancellationToken.None);

        Assert.True(Guid.TryParse(Assert.Single(inner.Requests[0].Headers.GetValues(ResilientRequestHandler.ClientRequestIdHeaderName)), out _));
    }

    [Fact]
    public async Task RetriesShareTheOriginalCorrelationIdentifier()
    {
        var inner = new SequencedResponseHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        using var client = Client(inner, out _);

        await client.GetAsync(new Uri("https://contoso.crm.dynamics.com/api"), CancellationToken.None);

        Assert.Equal(
            inner.Requests[0].Headers.GetValues(ResilientRequestHandler.ClientRequestIdHeaderName),
            inner.Requests[1].Headers.GetValues(ResilientRequestHandler.ClientRequestIdHeaderName));
    }

    [Fact]
    public async Task ACallerSuppliedCorrelationIdentifierIsPreserved()
    {
        var inner = new SequencedResponseHandler(HttpStatusCode.OK);
        using var client = Client(inner, out _);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://contoso.crm.dynamics.com/api"));
        request.Headers.TryAddWithoutValidation(ResilientRequestHandler.ClientRequestIdHeaderName, "caller-supplied");

        await client.SendAsync(request, CancellationToken.None);

        Assert.Equal("caller-supplied", Assert.Single(inner.Requests[0].Headers.GetValues(ResilientRequestHandler.ClientRequestIdHeaderName)));
    }

    [Fact]
    public void BackoffGrowsExponentiallyAndIsSpreadByJitter()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), ResilientRequestHandler.Backoff(1, 0d));
        Assert.Equal(TimeSpan.FromSeconds(2), ResilientRequestHandler.Backoff(2, 0d));
        Assert.Equal(TimeSpan.FromSeconds(4), ResilientRequestHandler.Backoff(3, 0d));
        Assert.Equal(TimeSpan.FromSeconds(1.5), ResilientRequestHandler.Backoff(1, 1d));
    }

    [Theory]
    [InlineData("GET", HttpStatusCode.TooManyRequests, true)]
    [InlineData("POST", HttpStatusCode.TooManyRequests, true)]
    [InlineData("GET", HttpStatusCode.ServiceUnavailable, true)]
    [InlineData("POST", HttpStatusCode.ServiceUnavailable, false)]
    [InlineData("PATCH", HttpStatusCode.BadGateway, false)]
    [InlineData("DELETE", HttpStatusCode.GatewayTimeout, true)]
    [InlineData("GET", HttpStatusCode.NotFound, false)]
    [InlineData("GET", HttpStatusCode.Unauthorized, false)]
    public void RetryEligibilityFollowsMethodAndStatus(string method, HttpStatusCode statusCode, bool expected) =>
        Assert.Equal(expected, ResilientRequestHandler.ShouldRetry(new HttpMethod(method), statusCode));

    [Fact]
    public void AtLeastOneAttemptIsRequired() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResilientRequestHandler(0));
}
