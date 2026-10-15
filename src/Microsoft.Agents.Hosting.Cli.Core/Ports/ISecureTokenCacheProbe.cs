namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>
/// Probes whether protected token-cache persistence is actually available for a given cache
/// configuration on the current platform. This isolates the one operation PAC's <c>MsalExtensionCache</c>
/// performs with a live side effect — <c>MsalCacheHelper.VerifyPersistence()</c>, which throws
/// <c>MsalCachePersistenceException</c> when the OS secret store is unavailable — behind a seam so the
/// no-silent-fallback policy in <see cref="Microsoft.Agents.Hosting.Cli.Core.Auth.AgentHostTokenCache"/> is
/// unit-testable with a fake that simulates availability or unavailability, using temp directories and
/// no token acquisition.
/// </summary>
public interface ISecureTokenCacheProbe
{
    /// <summary>
    /// Returns <see langword="true"/> when the platform secret store (DPAPI / Keychain / Keyring) can
    /// protect the cache described by <paramref name="descriptor"/>; <see langword="false"/> when only
    /// unprotected persistence would be possible.
    /// </summary>
    Task<bool> IsProtectedPersistenceAvailableAsync(
        Microsoft.Agents.Hosting.Cli.Core.Auth.AgentHostCacheDescriptor descriptor,
        CancellationToken cancellationToken);
}
