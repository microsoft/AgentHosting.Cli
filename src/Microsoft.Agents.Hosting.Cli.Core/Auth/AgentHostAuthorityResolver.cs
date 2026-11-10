using System.Collections.Frozen;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// Resolves the MSAL authority for an AgentHost sign-in or token request. This is the AgentHost
/// adaptation of PAC's <c>AuthorityInfo</c> constructors:
///
/// <list type="bullet">
///   <item>the cloud-to-login-host map (public / GCC / GCC-High / DoD / China / national / secure);</item>
///   <item>the <c>organizations</c> pseudo-tenant as the default authority tenant;</item>
///   <item>an explicit tenant override that pins the authority to <c>{host}/{tenantId}</c> without
///   rewriting the stored home-account profile.</item>
/// </list>
///
/// A supplied authority URL must be absolute HTTPS, mirroring PAC's <c>AuthorityInfo(Uri)</c> guard.
/// No token is acquired here; this only computes the authority string.
/// </summary>
public static class AgentHostAuthorityResolver
{
    /// <summary>The <c>organizations</c> pseudo-tenant PAC uses as the default authority tenant.</summary>
    public const string OrganizationsTenant = "organizations";

    // Public Preprod/Test share the commercial login host; their ring is a PPAPI coordinate.
    // https://learn.microsoft.com/entra/identity-platform/authentication-national-cloud
    private static readonly FrozenDictionary<CloudInstance, string> AuthorityHosts =
        new Dictionary<CloudInstance, string>
        {
            [CloudInstance.Public] = "https://login.microsoftonline.com",
            [CloudInstance.UsGov] = "https://login.microsoftonline.com",
            [CloudInstance.UsGovHigh] = "https://login.microsoftonline.us",
            [CloudInstance.UsGovDod] = "https://login.microsoftonline.us",
            [CloudInstance.China] = "https://login.partner.microsoftonline.cn",
            [CloudInstance.UsNat] = "https://login.microsoftonline.eaglex.ic.gov",
            [CloudInstance.UsSec] = "https://login.microsoftonline.microsoft.scloud",
        }.ToFrozenDictionary();

    /// <summary>The login host for a cloud (for example <c>https://login.microsoftonline.com</c>).</summary>
    public static string GetAuthorityHost(CloudInstance cloud) =>
        AuthorityHosts.TryGetValue(cloud, out var host)
            ? host
            : throw new ArgumentOutOfRangeException(nameof(cloud), cloud, "Unknown cloud instance.");

    /// <summary>
    /// Resolves the default authority for a cloud. With no <paramref name="tenantId"/> the authority
    /// uses the <c>organizations</c> pseudo-tenant; with one it pins that tenant.
    /// </summary>
    public static AuthorityInfo Resolve(CloudInstance cloud, Guid? tenantId = null)
    {
        var host = GetAuthorityHost(cloud);

        if (tenantId is null || tenantId == Guid.Empty)
        {
            return new AuthorityInfo
            {
                AuthorityUrl = new Uri($"{host}/{OrganizationsTenant}"),
                Cloud = cloud,
                TenantId = null,
            };
        }

        return new AuthorityInfo
        {
            AuthorityUrl = new Uri($"{host}/{tenantId.Value}"),
            Cloud = cloud,
            TenantId = tenantId,
        };
    }

    /// <summary>
    /// Wraps an explicitly supplied authority URL on the selected cloud's login host, using HTTPS
    /// and its default port without embedded credentials. The tenant is not parsed out of the URL.
    /// </summary>
    public static AuthorityInfo ResolveFromUrl(CloudInstance cloud, Uri authorityUrl)
    {
        ArgumentNullException.ThrowIfNull(authorityUrl);

        var host = new Uri(GetAuthorityHost(cloud)).Host;
        if (!authorityUrl.IsAbsoluteUri || authorityUrl.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(authorityUrl.UserInfo) || !authorityUrl.IsDefaultPort
            || !string.Equals(authorityUrl.Host.TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Authority must be an absolute HTTPS URL on the selected cloud's login host, without user information or a non-default port.", nameof(authorityUrl));
        }

        return new AuthorityInfo
        {
            AuthorityUrl = authorityUrl,
            Cloud = cloud,
        };
    }
}
