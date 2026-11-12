using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// Builds an MSAL <c>/.default</c> scope from a resource URL, isolated so PAC compatibility and any
/// required ARM adaptation are measurable in one place.
///
/// This adapts PAC's <c>AuthResource.UpdateDefaultScope()</c>:
/// <code>DefaultScope = Resource.GetLeftPart(UriPartial.Authority) + "//.default";</code>
/// PAC defaults to the <b>double-slash</b> separator (its <c>Parse</c> passes
/// <c>useDoubleSlashScopeSeparator: true</c>) to preserve ADAL-era resource-id semantics under MSAL.
/// The demonstrator keeps that default so its behavior starts identical to PAC and any deviation ARM
/// requires becomes an explicit, logged adaptation rather than a hidden change.
/// </summary>
public sealed class PacResourceScopeNormalizer
{
    private readonly bool _useDoubleSlashSeparator;

    /// <param name="useDoubleSlashSeparator">
    /// When <see langword="true"/> (the PAC default) the scope uses the <c>//.default</c> separator;
    /// when <see langword="false"/> it uses the plain <c>/.default</c> form.
    /// </param>
    public PacResourceScopeNormalizer(bool useDoubleSlashSeparator = true)
    {
        _useDoubleSlashSeparator = useDoubleSlashSeparator;
    }

    /// <summary>Selects resource scope formatting for the profile's principal and credential method.</summary>
    public static PacResourceScopeNormalizer ForProfile(AuthProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var useUserScopes = profile.PrincipalType == AuthPrincipalType.User
            && profile.CredentialMethod != AuthCredentialMethod.DefaultAzureCredential;
        return new PacResourceScopeNormalizer(useDoubleSlashSeparator: useUserScopes);
    }

    /// <summary>Normalizes a resource URL string to its default scope; the URL must be absolute.</summary>
    public string ToDefaultScope(string resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            throw new ArgumentException("Resource must be a non-empty absolute url.", nameof(resource));
        }

        if (!Uri.TryCreate(resource.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Resource '{resource}' is not an absolute url.", nameof(resource));
        }

        return ToDefaultScope(uri);
    }

    /// <summary>Normalizes an absolute resource URL to its default scope.</summary>
    public string ToDefaultScope(Uri resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (!resource.IsAbsoluteUri)
        {
            throw new ArgumentException("Resource must be an absolute url.", nameof(resource));
        }

        // PAC: Resource.GetLeftPart(UriPartial.Authority) + "//.default"
        var separator = _useDoubleSlashSeparator ? "//.default" : "/.default";
        return resource.GetLeftPart(UriPartial.Authority) + separator;
    }

    /// <summary>The default sign-in scope for a ring, so a sign-in targets the ring it names.</summary>
    /// <param name="ring">The Power Platform API ring the sign-in targets.</param>
    /// <returns>The <c>/.default</c> scope for that ring's audience.</returns>
    public string DefaultResourceScope(PowerPlatformApiRing ring) =>
        ToDefaultScope(PowerPlatformApiRingSelection.TargetFor(ring).Audience);
}
