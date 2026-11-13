using System.Net;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ProfileCredentialFederationTests
{
    [Fact]
    public async Task FederationTimeoutIsRedactedAuthenticationFailureNotCallerCancellation()
    {
        using var provider = new ProfileFederationAssertionProvider(new()
        { GitHubRequestUrl = "https://runtime.test/oidc?private=url-secret", GitHubRequestToken = "runtime-secret" }, new TimeoutTransport());
        var error = await Assert.ThrowsAsync<AuthenticationException>(() => provider.GetAssertionAsync(AuthCredentialMethod.GitHubFederated, default));
        Assert.DoesNotContain("private-timeout", error.ToString());
    }

    [Fact]
    public void RuntimeConfigurationSerializationDoesNotDiscloseFederationBearerEndpoints()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new ProfileCredentialRuntimeOptions
        { GitHubRequestUrl = "https://runtime.test/?private=url-secret", AzureDevOpsRequestUrl = "https://runtime.test/?private=url-secret", GitHubRequestToken = "runtime-secret" });
        Assert.DoesNotContain("url-secret", json);
        Assert.DoesNotContain("runtime-secret", json);
    }

    private sealed class TimeoutTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("private-timeout"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssertionUsesExactRuntimeProtocolAndPreservesQuery(bool ado)
    {
        var handler = new CaptureHandler(ado ? """{"oidcToken":"fresh-assertion"}""" : """{"value":"fresh-assertion"}""");
        var options = new ProfileCredentialRuntimeOptions
        {
            GitHubRequestUrl = "https://runtime.test/oidc?nonce=one&audience=api%3A%2F%2FAzureADTokenExchange",
            GitHubRequestToken = "runtime-only",
            AzureDevOpsRequestUrl = "https://runtime.test/oidc?serviceConnectionId=abc&api-version=other",
            AzureDevOpsRequestToken = "runtime-only",
        };
        using var provider = new ProfileFederationAssertionProvider(options, handler);
        Assert.Equal("fresh-assertion", await provider.GetAssertionAsync(
            ado ? AuthCredentialMethod.AzureDevOpsFederated : AuthCredentialMethod.GitHubFederated, default));
        Assert.Equal(ado ? HttpMethod.Post : HttpMethod.Get, handler.Method);
        Assert.Equal("Bearer runtime-only", handler.Authorization);
        Assert.Equal(ado ? "?serviceConnectionId=abc&api-version=other" : "?nonce=one&audience=api%3A%2F%2FAzureADTokenExchange", handler.Uri?.Query);
    }

    [Theory]
    [InlineData(false, "?audience=api%3A%2F%2FAzureADTokenExchange")]
    [InlineData(true, "?api-version=7.1-preview.1")]
    public async Task MissingProtocolQueryIsAppended(bool ado, string query)
    {
        var handler = new CaptureHandler(ado ? """{"oidcToken":"assertion"}""" : """{"value":"assertion"}""");
        using var provider = new ProfileFederationAssertionProvider(new()
        {
            GitHubRequestUrl = "https://runtime.test/oidc", GitHubRequestToken = "runtime",
            AzureDevOpsRequestUrl = "https://runtime.test/oidc", AzureDevOpsRequestToken = "runtime",
        }, handler);
        await provider.GetAssertionAsync(ado ? AuthCredentialMethod.AzureDevOpsFederated : AuthCredentialMethod.GitHubFederated, default);
        Assert.Equal(query, handler.Uri?.Query);
    }

    [Theory]
    [InlineData("http://runtime.test/oidc")]
    [InlineData("https://user:password@runtime.test/oidc")]
    [InlineData("https://runtime.test/oidc#secret")]
    [InlineData("https://runtime.test/oidc?audience=other")]
    [InlineData("https://runtime.test/oidc?audience=api%3A%2F%2FAzureADTokenExchange&audience=other")]
    public async Task InvalidRuntimeUriFailsBeforeBearerForwarding(string url)
    {
        var handler = new CaptureHandler("{}");
        using var provider = new ProfileFederationAssertionProvider(new() { GitHubRequestUrl = url, GitHubRequestToken = "sensitive" }, handler);
        var failure = await Assert.ThrowsAnyAsync<AgentHostingException>(() => provider.GetAssertionAsync(AuthCredentialMethod.GitHubFederated, default));
        Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain(url, failure.ToString());
        Assert.DoesNotContain("sensitive", failure.ToString());
    }

    [Theory]
    [InlineData(302, """{"value":"leaked-body"}""")]
    [InlineData(401, """{"value":"leaked-body"}""")]
    [InlineData(200, """{"value":42}""")]
    [InlineData(200, """{"value":""}""")]
    [InlineData(200, """{"missing":"leaked-body"}""")]
    public async Task FailedResponseIsTypedAndRedactedWithoutRedirectRetry(int status, string body)
    {
        var handler = new CaptureHandler(body, (HttpStatusCode)status);
        using var provider = new ProfileFederationAssertionProvider(new()
        {
            GitHubRequestUrl = "https://runtime.test/oidc?private=endpoint-secret", GitHubRequestToken = "runtime-secret",
        }, handler);
        var error = await Assert.ThrowsAnyAsync<AgentHostingException>(() => provider.GetAssertionAsync(AuthCredentialMethod.GitHubFederated, default));
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("runtime-secret", error.ToString());
        Assert.DoesNotContain("endpoint-secret", error.ToString());
        Assert.DoesNotContain("leaked-body", error.ToString());
    }

    [Fact]
    public async Task OversizeResponseIsRejectedAndCancellationDoesNotSendRequest()
    {
        var handler = new CaptureHandler(new string('x', 70000));
        using var provider = new ProfileFederationAssertionProvider(new()
        {
            GitHubRequestUrl = "https://runtime.test/oidc", GitHubRequestToken = "runtime",
        }, handler);
        await Assert.ThrowsAnyAsync<AgentHostingException>(() => provider.GetAssertionAsync(AuthCredentialMethod.GitHubFederated, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetAssertionAsync(AuthCredentialMethod.GitHubFederated, cancellation.Token));
        Assert.Equal(1, handler.Calls);
    }

    internal sealed class CaptureHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal int Calls;
        internal Uri? Uri;
        internal string? Authorization;
        internal HttpMethod? Method;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Uri = request.RequestUri; Authorization = request.Headers.Authorization?.ToString(); Method = request.Method;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body), Headers = { Location = new Uri("https://redirect.test") } });
        }
    }
}
