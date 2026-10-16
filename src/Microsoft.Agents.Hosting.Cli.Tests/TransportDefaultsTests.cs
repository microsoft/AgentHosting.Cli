using System.Net.Http;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.CopilotStudio.AskClient;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed partial class TransportDefaultsTests
{
    [Fact]
    public void TheSharedPolicyPrefersTlsThirteenWithATlsTwelveFloor()
    {
        Assert.Equal(SslProtocols.Tls12 | SslProtocols.Tls13, TransportDefaults.EnabledSslProtocols);
    }

    [Fact]
    public void TheSharedHandlerUsesTheSharedPolicy()
    {
        using var handler = TransportDefaults.CreateRedirectRejectingHandler();

        Assert.Equal(TransportDefaults.EnabledSslProtocols, handler.SslProtocols);
    }

    [Fact]
    public void TheSharedHandlerFollowsNoRedirect()
    {
        using var handler = TransportDefaults.CreateRedirectRejectingHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void TheRedirectFollowingHandlerUsesTheSharedPolicyAndFollowsRedirects()
    {
        using var handler = TransportDefaults.CreateRedirectFollowingHandler();

        Assert.Equal(TransportDefaults.EnabledSslProtocols, handler.SslProtocols);
        Assert.True(handler.AllowAutoRedirect);
    }

    [Fact]
    public void EveryCallCreatesItsOwnHandler()
    {
        using var first = TransportDefaults.CreateRedirectRejectingHandler();
        using var second = TransportDefaults.CreateRedirectRejectingHandler();
        using var third = TransportDefaults.CreateRedirectFollowingHandler();
        using var fourth = TransportDefaults.CreateRedirectFollowingHandler();

        Assert.NotSame(first, second);
        Assert.NotSame(third, fourth);
    }

    [Fact]
    public void TheDataverseAccessorUsesTheSharedTransportSettings()
    {
        using var handler = Assert.IsType<HttpClientHandler>(AgentHostDataverseHttpClientAccessor.CreateRedirectRejectingHandler());

        Assert.Equal(TransportDefaults.EnabledSslProtocols, handler.SslProtocols);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void TheLiveTestTransportUsesTheSharedPolicyWithoutCookiesOrRedirects()
    {
        using var handler = McsLiveTestSession.CreateTransport();

        Assert.Equal(TransportDefaults.EnabledSslProtocols, handler.SslProtocols);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
    }

    [Fact]
    public void EveryProductionHttpHandlerComesFromTheSharedTransportDefaults()
    {
        var productionSources = DependencyBoundaryTests.SourceFiles("*.cs")
            .Where(path => !path.Contains($".Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !path.Contains($".E2E{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToLookup(path => Path.GetFileName(path) == "TransportDefaults.cs");

        Assert.Matches(HandlerConstruction(), File.ReadAllText(Assert.Single(productionSources[true])));
        Assert.Empty(productionSources[false].Where(path => HandlerConstruction().IsMatch(File.ReadAllText(path))).Select(Path.GetFileName));
    }

    [GeneratedRegex(@"new\s+(HttpClientHandler|SocketsHttpHandler)\b|new\s+HttpClient\s*\(\s*\)|\b(HttpClientHandler|SocketsHttpHandler)\b[^;{}\n]*=>?\s*new\s*\(")]
    private static partial Regex HandlerConstruction();
}
