using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>The organization host shapes each cloud serves Dataverse from.</summary>
public static partial class DataverseHostPatterns
{
    /// <summary>Commercial organizations, whose regional hosts are <c>crm</c> followed by an optional number.</summary>
    public static readonly Regex CommercialDynamics = CommercialDynamicsPattern();

    /// <summary>Test-ring organizations, which add the <c>crmtest</c> test geography to the commercial hosts.</summary>
    public static readonly Regex TestDynamics = TestDynamicsPattern();

    /// <summary>United States government organizations.</summary>
    public static readonly Regex UnitedStatesGovernment = UnitedStatesGovernmentPattern();

    /// <summary>China organizations, operated by 21Vianet.</summary>
    public static readonly Regex China = ChinaPattern();

    [GeneratedRegex(@"^[a-z0-9-]+\.crm[0-9]*\.dynamics\.com$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommercialDynamicsPattern();

    [GeneratedRegex(@"^[a-z0-9-]+\.crm(?:[0-9]*|test)\.dynamics\.com$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TestDynamicsPattern();

    [GeneratedRegex(@"^[a-z0-9-]+\.crm[0-9]*\.(microsoftdynamics|appsplatform|dynamics\.microsoft)\.us$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnitedStatesGovernmentPattern();

    [GeneratedRegex(@"^[a-z0-9-]+\.crm[0-9]*\.dynamics\.cn$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChinaPattern();
}

/// <summary>
/// The Power Platform cloud map: the host-suffix allowlist that decides whether an endpoint returned
/// by the environment inventory may be called at all.
///
/// The environment inventory is service-provided input. Because the CLI attaches a delegated bearer
/// token to these calls, absolute HTTPS alone is not sufficient: the host must be one the selected
/// cloud actually serves.
///
/// Matching is label-aligned, not substring: an entry <c>api.powerplatform.com</c> matches that exact
/// host and any subdomain of it, and never <c>notapi.powerplatform.com</c> or
/// <c>api.powerplatform.com.attacker.example</c>. Non-default ports and embedded user information are
/// rejected outright.
///
/// Only <see cref="CloudInstance.Public"/> is exercised by version zero; the sovereign entries are
/// carried so a non-public target touches this one type, and they are unverified against a live
/// sovereign service.
/// </summary>
public sealed class PowerPlatformCloudMap
{
    private static readonly PowerPlatformCloudMap PublicCloud =
        new(CloudInstance.Public, ["api.powerplatform.com"], DataverseHostPatterns.CommercialDynamics);

    private static readonly PowerPlatformCloudMap TestCloud =
        new(CloudInstance.Public, ["api.test.powerplatform.com"], DataverseHostPatterns.TestDynamics);

    private static readonly PowerPlatformCloudMap PreprodCloud =
        new(CloudInstance.Public, ["api.preprod.powerplatform.com"], DataverseHostPatterns.CommercialDynamics);

    private static readonly PowerPlatformCloudMap UsGovCloud =
        new(CloudInstance.UsGov, ["api.gov.powerplatform.microsoft.us"], DataverseHostPatterns.UnitedStatesGovernment);

    private static readonly PowerPlatformCloudMap UsGovHighCloud =
        new(CloudInstance.UsGovHigh, ["api.high.powerplatform.microsoft.us"], DataverseHostPatterns.UnitedStatesGovernment);

    private static readonly PowerPlatformCloudMap UsGovDodCloud =
        new(CloudInstance.UsGovDod, ["api.appsplatform.us"], DataverseHostPatterns.UnitedStatesGovernment);

    private static readonly PowerPlatformCloudMap ChinaCloud =
        new(CloudInstance.China, ["api.powerplatform.partner.microsoftonline.cn"], DataverseHostPatterns.China);

    private PowerPlatformCloudMap(CloudInstance cloud, IReadOnlyList<string> trustedHostSuffixes, Regex trustedDataverseHostPattern)
    {
        Cloud = cloud;
        TrustedHostSuffixes = trustedHostSuffixes;
        TrustedDataverseHostPattern = trustedDataverseHostPattern;
    }

    /// <summary>The cloud this map describes.</summary>
    public CloudInstance Cloud { get; }

    /// <summary>The organization hosts this cloud serves Dataverse from.</summary>
    public Regex TrustedDataverseHostPattern { get; }

    /// <summary>
    /// The trusted registrable host suffixes. A candidate host must equal one of these or be a
    /// subdomain of one.
    /// </summary>
    public IReadOnlyList<string> TrustedHostSuffixes { get; }

    /// <summary>Resolves the map for <paramref name="cloud"/>.</summary>
    public static PowerPlatformCloudMap ForCloud(CloudInstance cloud) => cloud switch
    {
        CloudInstance.Public => PublicCloud,
        CloudInstance.UsGov => UsGovCloud,
        CloudInstance.UsGovHigh => UsGovHighCloud,
        CloudInstance.UsGovDod => UsGovDodCloud,
        CloudInstance.China => ChinaCloud,
        _ => throw new ArgumentOutOfRangeException(nameof(cloud), cloud, "Unknown Power Platform cloud."),
    };

    /// <summary>
    /// Resolves the host allowlist paired with <paramref name="target"/>. Ring selection is made once
    /// by the host; inventory data and per-command arguments cannot widen this map.
    /// </summary>
    public static PowerPlatformCloudMap ForTarget(PowerPlatformApiTargetConfig target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return target.Ring switch
        {
            PowerPlatformApiRing.Production => ForCloud(target.Cloud),
            PowerPlatformApiRing.Preprod when target.Cloud == CloudInstance.Public => PreprodCloud,
            PowerPlatformApiRing.Test when target.Cloud == CloudInstance.Public => TestCloud,
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target.Ring,
                $"The {target.Ring} Power Platform API ring is not configured for {target.Cloud}."),
        };
    }

    /// <summary>
    /// Whether <paramref name="endpoint"/> is absolute HTTPS, carries no user information, uses the
    /// default HTTPS port, and has a host inside the allowlist.
    /// </summary>
    public bool IsTrusted(Uri? endpoint) => IsTrustedAgainst(endpoint, TrustedHostSuffixes);

    private static bool IsTrustedAgainst(Uri? endpoint, IReadOnlyList<string> suffixes) =>
        IsAddressableHttpsEndpoint(endpoint) && IsTrustedHost(endpoint!.Host, suffixes);

    /// <summary>Whether the url is an organization this cloud serves and may be given an access token.</summary>
    /// <param name="dataverseOrganization">The candidate organization url.</param>
    /// <returns><see langword="true"/> when the url is safe to mint a token for.</returns>
    public bool IsTrustedDataverseOrganization(Uri? dataverseOrganization) =>
        IsAddressableHttpsEndpoint(dataverseOrganization)
        && TrustedDataverseHostPattern.IsMatch(dataverseOrganization!.Host.Trim().TrimEnd('.'));

    /// <summary>Whether the url is absolute HTTPS on the default port with no embedded credentials.</summary>
    private static bool IsAddressableHttpsEndpoint(Uri? endpoint) =>
        endpoint is not null
        && endpoint.IsAbsoluteUri
        && string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(endpoint.UserInfo)
        && endpoint.IsDefaultPort;

    /// <summary>Whether <paramref name="host"/> is an allowlisted host or a subdomain of one.</summary>
    public bool IsTrustedHost(string? host) => IsTrustedHost(host, TrustedHostSuffixes);

    private static bool IsTrustedHost(string? host, IReadOnlyList<string> suffixes)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var candidate = host.Trim().TrimEnd('.');

        return suffixes.Any(suffix =>
            string.Equals(candidate, suffix, StringComparison.OrdinalIgnoreCase) ||
            candidate.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The failure raised when an inventory-provided organization url is not one this cloud serves.</summary>
    /// <param name="dataverseOrganization">The rejected organization url.</param>
    /// <param name="environmentId">The environment that reported it.</param>
    /// <returns>The exception to throw.</returns>
    public EndpointTrustException UntrustedDataverseOrganization(Uri dataverseOrganization, string environmentId) => new(
        $"Environment {environmentId} reports the Dataverse organization '{dataverseOrganization.Scheme}://{dataverseOrganization.Authority}', which is not a {Cloud} Dataverse host. The CLI will not send an access token to it.",
        remediation: "Re-run `msagent env list` to refresh the environment inventory. If the organization url is still untrusted the inventory response cannot be used, and the environment must not be selected.");

    /// <summary>
    /// Returns <paramref name="endpoint"/> when it is trusted; otherwise throws
    /// <see cref="EndpointTrustException"/> naming <paramref name="context"/>. The message carries the
    /// rejected host and the allowlist, both non-secret.
    /// </summary>
    public Uri RequireTrusted(Uri? endpoint, string context) => IsTrusted(endpoint) ? endpoint! : throw UntrustedEndpoint(endpoint, context, TrustedHostSuffixes);

    /// <summary>The failure raised when an endpoint is not a host trusted for a use, naming the use and the trusted host suffixes but never the endpoint's path.</summary>
    /// <param name="endpoint">The rejected endpoint.</param>
    /// <param name="context">What the endpoint was to be used for.</param>
    /// <param name="trustedHostSuffixes">The host suffixes trusted for that use.</param>
    /// <returns>The exception to throw.</returns>
    public EndpointTrustException UntrustedEndpoint(Uri? endpoint, string context, IEnumerable<string> trustedHostSuffixes) => new($"The {context} endpoint '{Describe(endpoint)}' is not a trusted {Cloud} Power Platform host. Trusted hosts end with: {string.Join(", ", trustedHostSuffixes)}.", remediation: "Re-run `msagent env list` to refresh the environment inventory; if the endpoint is still untrusted the inventory response cannot be used and the selection must not proceed.");

    private static string Describe(Uri? endpoint) => endpoint is null ? "<none>" : endpoint.IsAbsoluteUri ? $"{endpoint.Scheme}://{endpoint.Authority}" : endpoint.OriginalString;
}
