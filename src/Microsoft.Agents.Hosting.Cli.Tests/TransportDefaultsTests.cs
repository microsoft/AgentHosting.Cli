using System.Net.Http;
using System.Security.Authentication;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class TransportDefaultsTests
{
    [Fact]
    public void TheSharedHandlerNegotiatesTlsThirteenOnly()
    {
        using var handler = TransportDefaults.CreateRedirectRejectingHandler();

        Assert.Equal(SslProtocols.Tls13, handler.SslProtocols);
    }

    [Fact]
    public void TheSharedHandlerFollowsNoRedirect()
    {
        using var handler = TransportDefaults.CreateRedirectRejectingHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void EveryCallCreatesItsOwnHandler()
    {
        using var first = TransportDefaults.CreateRedirectRejectingHandler();
        using var second = TransportDefaults.CreateRedirectRejectingHandler();

        Assert.NotSame(first, second);
    }

    [Fact]
    public void TheDataverseAccessorUsesTheSharedTransportSettings()
    {
        using var handler = Assert.IsType<HttpClientHandler>(AgentHostDataverseHttpClientAccessor.CreateRedirectRejectingHandler());

        Assert.Equal(SslProtocols.Tls13, handler.SslProtocols);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Theory]
    [InlineData(SslProtocols.Tls12)]
    [InlineData(SslProtocols.None)]
    public void TheSharedHandlerNegotiatesNoWeakerProtocol(SslProtocols rejected)
    {
        using var handler = TransportDefaults.CreateRedirectRejectingHandler();

        Assert.NotEqual(rejected, handler.SslProtocols);
        Assert.Equal(SslProtocols.None, handler.SslProtocols & rejected);
    }
}
