using System.Net.Http;
using System.Security.Authentication;

namespace Microsoft.Agents.Hosting.Cli.Core.Http;

/// <summary>
/// The transport settings every CLI HTTP client is built on.
/// </summary>
public static class TransportDefaults
{
    /// <summary>The CLI supports TLS 1.3 and TLS 1.2.</summary>
    public const SslProtocols EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>Creates a handler that offers <see cref="EnabledSslProtocols"/> and follows no redirect.</summary>
    public static HttpClientHandler CreateRedirectRejectingHandler() => CreateHandler(allowAutoRedirect: false);

    /// <summary>Creates a handler that offers <see cref="EnabledSslProtocols"/> and follows redirects.</summary>
    public static HttpClientHandler CreateRedirectFollowingHandler() => CreateHandler(allowAutoRedirect: true);

    private static HttpClientHandler CreateHandler(bool allowAutoRedirect) => new() { AllowAutoRedirect = allowAutoRedirect, SslProtocols = EnabledSslProtocols };
}
