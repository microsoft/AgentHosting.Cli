using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>The protection actually resolved for the token cache, plus where it lives.</summary>
public sealed record ResolvedCacheProtection(CacheProtectionKind Kind, string CacheFilePath, bool IsProtected);

/// <summary>
/// Resolves how the AgentHost MSAL token cache is protected and enforces the demonstrator's key
/// security adaptation of PAC's <c>MsalExtensionCache</c>:
/// <b>secure-cache unavailability must never silently fall back to an unprotected file.</b>
///
/// PAC catches <c>MsalCachePersistenceException</c> on Linux and transparently switches to a plaintext
/// cache. The demonstrator refuses that downgrade: when the platform secret store is unavailable it
/// throws <see cref="CacheProtectionUnavailableException"/> unless the operator has explicitly set
/// <c>AH_CLI_ALLOW_UNPROTECTED_CACHE=true</c>, in which case an unprotected file is used
/// deliberately. No token is acquired here; availability is checked through the
/// <see cref="ISecureTokenCacheProbe"/> seam so this is unit-testable with fakes.
/// </summary>
public sealed class AgentHostTokenCache
{
    /// <summary>The environment setting that opts into an unprotected cache when secure storage is unavailable.</summary>
    public const string AllowUnprotectedCacheSettingName = "AH_CLI_ALLOW_UNPROTECTED_CACHE";

    private readonly IPlatformInfo _platform;
    private readonly ISecureTokenCacheProbe _probe;

    public AgentHostTokenCache(IPlatformInfo platform, ISecureTokenCacheProbe probe)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <summary>
    /// The protected cache kind that <em>would</em> be used on the current platform (before any
    /// availability probe): DPAPI on Windows, Keychain on macOS, Keyring on Linux.
    /// </summary>
    public CacheProtectionKind GetPlatformProtectionKind()
    {
        if (_platform.IsWindows)
        {
            return CacheProtectionKind.WindowsDpapi;
        }

        if (_platform.IsMacOs)
        {
            return CacheProtectionKind.MacKeychain;
        }

        if (_platform.IsLinux)
        {
            return CacheProtectionKind.LinuxKeyring;
        }

        throw new CacheProtectionUnavailableException(
            "The current operating system is not a supported AgentHost token-cache platform.",
            remediation: "Run the AgentHost CLI on Windows, macOS, or Linux.");
    }

    /// <summary>
    /// Resolves the cache protection for <paramref name="descriptor"/>. Returns a protected result when
    /// the platform secret store is available; otherwise honours the explicit unprotected opt-in or, if
    /// absent, throws — it never downgrades silently.
    /// </summary>
    /// <param name="descriptor">The cache configuration (paths and names).</param>
    /// <param name="allowUnprotectedCache">
    /// The resolved value of <c>AH_CLI_ALLOW_UNPROTECTED_CACHE</c> (the host reads the environment;
    /// Core does not).
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<ResolvedCacheProtection> ResolveProtectionAsync(
        AgentHostCacheDescriptor descriptor,
        bool allowUnprotectedCache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var protectedKind = GetPlatformProtectionKind();

        var isProtectedAvailable = await _probe
            .IsProtectedPersistenceAvailableAsync(descriptor, cancellationToken)
            .ConfigureAwait(false);

        if (isProtectedAvailable)
        {
            return new ResolvedCacheProtection(protectedKind, descriptor.CacheFilePath, IsProtected: true);
        }

        if (allowUnprotectedCache)
        {
            return new ResolvedCacheProtection(
                CacheProtectionKind.UnprotectedFile,
                descriptor.CacheFilePath,
                IsProtected: false);
        }

        throw new CacheProtectionUnavailableException(
            $"Secure token-cache persistence ({protectedKind}) is unavailable and unprotected persistence " +
            "was not enabled, so the AgentHost cache will not silently fall back to a plaintext file.",
            remediation: $"Install or enable the platform secret store, then run the command again. Only on a machine that cannot have one, set {AllowUnprotectedCacheSettingName}=true as a last resort to opt into an unprotected cache.");
    }
}
