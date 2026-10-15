using System.Net.Http;
using System.Security.Authentication;

namespace Microsoft.Agents.Hosting.Cli.Core.Http;

/// <summary>
/// The transport settings every CLI HTTP client is built on.
/// </summary>
public static class TransportDefaults
{
    /// <summary>Creates a handler that negotiates TLS 1.3 only and follows no redirect.</summary>
    public static HttpClientHandler CreateRedirectRejectingHandler() =>
        new()
        {
            AllowAutoRedirect = false,
            SslProtocols = SslProtocols.Tls13,
        };
}
