namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Decides whether a consent endpoint named by the service is one the signed-in user's token may be
/// posted to.
/// </summary>
public static class ConsentEndpointTrust
{
    private static readonly string[] TrustedSuffixes =
    [
        ".azure-apim.net",
        ".azure-apihub.net",
        ".azure-apim.us",
        ".azure-apihub.us",
        ".azure-apim.cn",
        ".azure-apihub.cn",
        ".azure-apihub.eaglex.ic.gov",
        ".azure-apihub.microsoft.scloud",
    ];

    /// <summary>Reports whether a consent endpoint is a Microsoft connector consent host.</summary>
    /// <param name="endpoint">The endpoint the connectivity API named.</param>
    /// <returns><see langword="true"/> when the endpoint may receive an access token.</returns>
    public static bool IsTrusted(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsAbsoluteUri || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return false;
        }

        var host = endpoint.Host;

        return TrustedSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Throws unless a consent endpoint may receive an access token.</summary>
    /// <param name="endpoint">The endpoint the connectivity API named.</param>
    /// <returns>The endpoint, when it is trusted.</returns>
    public static Uri Require(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!IsTrusted(endpoint))
        {
            throw new SilentConnectionUnavailableException($"The connectivity API named '{endpoint.GetLeftPart(UriPartial.Authority)}' as a consent endpoint, which is not a recognized Microsoft connector consent host. No token was sent to it.");
        }

        return endpoint;
    }
}
