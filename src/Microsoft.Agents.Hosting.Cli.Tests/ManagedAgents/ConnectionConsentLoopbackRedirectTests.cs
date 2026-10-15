using System.Net.Sockets;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ConnectionConsentLoopbackRedirectTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly Uri Landing =
        ConnectionConsentLink.BuildConnectionsListLink(CoreServicesClusterCategory.Prod, ConnectionFixtures.EnvironmentId);

    private static async Task<string> SendAndReadAsync(Uri callbackUrl, string query) =>
        await SendToAndReadAsync(callbackUrl, callbackUrl.AbsolutePath, query);

    private static async Task<string> SendToAndReadAsync(Uri callbackUrl, string path, string query) =>
        await SendRawAndReadAsync(callbackUrl, $"GET {path}?{query} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");

    private static async Task<string> SendRawAndReadAsync(Uri callbackUrl, string requestText)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", callbackUrl.Port);

        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(requestText));
        await stream.FlushAsync();

        var response = new StringBuilder();
        var buffer = new byte[1024];
        int read;

        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            response.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return response.ToString();
    }

    [Fact]
    public async Task ALateCallbackStillLandsOnTheConnectionsPage()
    {
        using var loopback = new ConnectionConsentLoopback(Landing);
        using var abandoned = new CancellationTokenSource();

        var waiting = loopback.WaitForConsentCodeAsync(Timeout, abandoned.Token);
        await abandoned.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(loopback.AnsweredCallback);

        loopback.ServeLateCallbacks();

        var response = await SendAndReadAsync(loopback.CallbackUrl, "code=consent-code");

        Assert.Contains("302", response, StringComparison.Ordinal);
        Assert.Contains(Landing.ToString(), response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnsweredCallbackIsReported()
    {
        using var loopback = new ConnectionConsentLoopback(Landing);
        var waiting = loopback.WaitForConsentCodeAsync(Timeout, CancellationToken.None);

        await SendAndReadAsync(loopback.CallbackUrl, "code=consent-code");
        await waiting;

        Assert.True(loopback.AnsweredCallback);
    }

    [Fact]
    public async Task ARequestToAnotherPathIsNotReportedAsAnsweringThisSession()
    {
        using var loopback = new ConnectionConsentLoopback(Landing);
        using var abandoned = new CancellationTokenSource();

        var waiting = loopback.WaitForConsentCodeAsync(Timeout, abandoned.Token);

        await SendToAndReadAsync(loopback.CallbackUrl, "/callback/not-this-session", "code=consent-code");
        await abandoned.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        Assert.False(loopback.AnsweredCallback);
    }

    [Fact]
    public async Task TheConsentRedirectReportsItsCodeAndIgnoresUnrelatedRequests()
    {
        using var loopback = new ConnectionConsentLoopback();
        var waiting = loopback.WaitForConsentCodeAsync(Timeout, CancellationToken.None);

        await SendAndReadAsync(loopback.CallbackUrl, "state=abc");

        var response = await SendAndReadAsync(loopback.CallbackUrl, "code=consent-code");
        var reported = await waiting;

        Assert.NotNull(reported);
        Assert.Equal("consent-code", reported!.Code);
        Assert.Null(reported.Error);
        Assert.Contains("Signed in", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheConsentRedirectReportsTheFailureItCarries()
    {
        using var loopback = new ConnectionConsentLoopback();
        var waiting = loopback.WaitForConsentCodeAsync(Timeout, CancellationToken.None);

        await SendAndReadAsync(loopback.CallbackUrl, "error=access_denied&error_description=the%20operator%20declined");
        var reported = await waiting;

        Assert.NotNull(reported);
        Assert.Null(reported!.Code);
        Assert.Equal("access_denied: the operator declined", reported.Error);
    }

    [Fact]
    public async Task ARedirectWithoutACodeDoesNotAskTheOperatorToDoAnythingElse()
    {
        using var loopback = new ConnectionConsentLoopback();
        var waiting = loopback.WaitForConsentCodeAsync(Timeout, CancellationToken.None);

        var response = await SendAndReadAsync(loopback.CallbackUrl, "state=abc");

        Assert.Contains("You can close this tab", response, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing to report", response, StringComparison.Ordinal);

        await SendAndReadAsync(loopback.CallbackUrl, "code=consent-code");

        Assert.Equal("consent-code", (await waiting)?.Code);
    }

    [Fact]
    public async Task ARedirectWithoutACodeStillSendsTheBrowserOnToTheConnectionsList()
    {
        using var loopback = new ConnectionConsentLoopback(Landing);
        var waiting = loopback.WaitForConsentCodeAsync(Timeout, CancellationToken.None);

        var response = await SendAndReadAsync(loopback.CallbackUrl, "state=abc");

        Assert.Contains("302", response, StringComparison.Ordinal);
        Assert.Contains(Landing.AbsoluteUri, response, StringComparison.Ordinal);

        await SendAndReadAsync(loopback.CallbackUrl, "code=consent-code");

        Assert.Equal("consent-code", (await waiting)?.Code);
    }

    [Fact]
    public async Task TheCallbackKeepsAnsweringAfterAConsentErrorEndsTheWait()
    {
        using var loopback = new ConnectionConsentLoopback(Landing);
        var waiting = loopback.WaitForConsentCodeAsync(Timeout, CancellationToken.None);

        await SendAndReadAsync(loopback.CallbackUrl, "error=OAuth2%20flow%20failed");
        await waiting;

        var response = await SendAndReadAsync(loopback.CallbackUrl, string.Empty);

        Assert.Contains("302", response, StringComparison.Ordinal);
        Assert.Contains(Landing.AbsoluteUri, response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConsentErrorStillSendsTheBrowserOnToTheConnectionsList()
    {
        using var loopback = new ConnectionConsentLoopback(Landing);
        var waiting = loopback.WaitForConsentCodeAsync(Timeout, CancellationToken.None);

        var response = await SendAndReadAsync(loopback.CallbackUrl, "error=OAuth2%20flow%20failed");

        Assert.Equal("OAuth2 flow failed", (await waiting)?.Error);
        Assert.Contains("302", response, StringComparison.Ordinal);
        Assert.Contains(Landing.AbsoluteUri, response, StringComparison.Ordinal);
        Assert.DoesNotContain("Sign-in failed", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARedeemedConsentCodeSendsTheBrowserOnToTheConnectionsList()
    {
        using var loopback = new ConnectionConsentLoopback(Landing);
        var waiting = loopback.WaitForConsentCodeAsync(Timeout, CancellationToken.None);

        var response = await SendAndReadAsync(loopback.CallbackUrl, "code=consent-code");

        Assert.Equal("consent-code", (await waiting)?.Code);
        Assert.Contains("302", response, StringComparison.Ordinal);
        Assert.Contains(Landing.AbsoluteUri, response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReloadAfterTheCodeArrivedStillReportsTheSignIn()
    {
        using var loopback = new ConnectionConsentLoopback();
        var waiting = loopback.WaitForConsentCodeAsync(Timeout, CancellationToken.None);

        await SendAndReadAsync(loopback.CallbackUrl, "code=consent-code");
        await waiting;

        var response = await SendAndReadAsync(loopback.CallbackUrl, string.Empty);

        Assert.Contains("Signed in", response, StringComparison.Ordinal);
        Assert.DoesNotContain("Signing in", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACreatedConnectionSendsTheBrowserOnToTheConnectionsList()
    {
        using var loopback = new ConnectionConsentLoopback(Landing);
        var waiting = loopback.WaitAsync("nonce-1", Timeout, CancellationToken.None);

        var response = await SendAndReadAsync(loopback.CallbackUrl, "nonce=nonce-1&status=created&connectionId=conn-1");
        var result = await waiting;

        Assert.Equal(ConnectionConsentStatus.Created, result.Status);
        Assert.StartsWith("HTTP/1.1 302 Found", response, StringComparison.Ordinal);
        Assert.Contains($"Location: {Landing.AbsoluteUri}", response, StringComparison.Ordinal);
        Assert.StartsWith("https://", Landing.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoDestinationACreatedConnectionStaysOnTheLocalPage()
    {
        using var loopback = new ConnectionConsentLoopback();
        var waiting = loopback.WaitAsync("nonce-1", Timeout, CancellationToken.None);

        var response = await SendAndReadAsync(loopback.CallbackUrl, "nonce=nonce-1&status=created&connectionId=conn-1");

        await waiting;

        Assert.StartsWith("HTTP/1.1 200 OK", response, StringComparison.Ordinal);
        Assert.Contains("Connection created", response, StringComparison.Ordinal);
        Assert.DoesNotContain("Location:", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureKeepsItsExplanationInsteadOfRedirecting()
    {
        using var loopback = new ConnectionConsentLoopback(Landing);
        var waiting = loopback.WaitAsync("nonce-1", Timeout, CancellationToken.None);

        var response = await SendAndReadAsync(loopback.CallbackUrl, "nonce=nonce-1&status=error&message=consent%20was%20refused");
        var result = await waiting;

        Assert.Equal(ConnectionConsentStatus.Error, result.Status);
        Assert.StartsWith("HTTP/1.1 200 OK", response, StringComparison.Ordinal);
        Assert.DoesNotContain("Location:", response, StringComparison.Ordinal);
        Assert.Contains("consent was refused", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledConnectionKeepsItsPageInsteadOfRedirecting()
    {
        using var loopback = new ConnectionConsentLoopback(Landing);
        var waiting = loopback.WaitAsync("nonce-1", Timeout, CancellationToken.None);

        var response = await SendAndReadAsync(loopback.CallbackUrl, "nonce=nonce-1&status=cancelled");
        var result = await waiting;

        Assert.Equal(ConnectionConsentStatus.Cancelled, result.Status);
        Assert.StartsWith("HTTP/1.1 200 OK", response, StringComparison.Ordinal);
        Assert.DoesNotContain("Location:", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallbackOnAnotherPathNeitherEndsTheWaitNorIsMistakenForTheCode()
    {
        using var loopback = new ConnectionConsentLoopback();
        var waiting = loopback.WaitForConsentCodeAsync(Timeout, CancellationToken.None);

        await SendToAndReadAsync(loopback.CallbackUrl, "/callback", "code=injected-code");
        await SendToAndReadAsync(loopback.CallbackUrl, "/callback/00000000000000000000000000000000", "error=access_denied");

        Assert.False(waiting.IsCompleted);

        await SendAndReadAsync(loopback.CallbackUrl, "code=consent-code");

        Assert.Equal("consent-code", (await waiting)?.Code);
    }

    [Fact]
    public void EachSessionIsAddressedByItsOwnUnguessableCallbackPath()
    {
        using var first = new ConnectionConsentLoopback();
        using var second = new ConnectionConsentLoopback();

        Assert.StartsWith("/callback/", first.CallbackUrl.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(32, first.CallbackUrl.AbsolutePath["/callback/".Length..].Length);
        Assert.NotEqual(first.CallbackUrl.AbsolutePath, second.CallbackUrl.AbsolutePath);
    }

    [Fact]
    public async Task AConnectionCallbackOnAnotherPathIsIgnoredEvenWithTheRightNonce()
    {
        using var loopback = new ConnectionConsentLoopback();
        var waiting = loopback.WaitAsync("nonce-1", Timeout, CancellationToken.None);

        await SendToAndReadAsync(loopback.CallbackUrl, "/callback", "nonce=nonce-1&status=error&message=injected");

        Assert.False(waiting.IsCompleted);

        await SendAndReadAsync(loopback.CallbackUrl, "nonce=nonce-1&status=created&connectionId=conn-1");

        Assert.Equal(ConnectionConsentStatus.Created, (await waiting).Status);
    }

    [Fact]
    public async Task ACallbackSentWithAnotherMethodIsNotMistakenForTheConsentRedirect()
    {
        using var loopback = new ConnectionConsentLoopback();
        var waiting = loopback.WaitForConsentCodeAsync(Timeout, CancellationToken.None);

        await SendRawAndReadAsync(loopback.CallbackUrl, $"POST {loopback.CallbackUrl.AbsolutePath}?code=injected-code HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");

        Assert.False(waiting.IsCompleted);

        await SendAndReadAsync(loopback.CallbackUrl, "code=consent-code");

        Assert.Equal("consent-code", (await waiting)?.Code);
    }

    [Theory]
    [InlineData("ftp://example.com/")]
    [InlineData("file:///c:/temp")]
    public void ADestinationTheBrowserCannotFollowIsRefused(string destination)
    {
        Assert.Throws<ArgumentException>(() => new ConnectionConsentLoopback(new Uri(destination)));
    }
}
