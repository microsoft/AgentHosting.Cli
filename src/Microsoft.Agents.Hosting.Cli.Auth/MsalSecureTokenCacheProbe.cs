using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>
/// The live <see cref="ISecureTokenCacheProbe"/>. It performs the one side-effecting availability check
/// PAC's <c>MsalExtensionCache</c> relies on — <see cref="Storage.VerifyPersistence"/>, which round-trips
/// a probe blob through the OS secret store and throws <see cref="MsalCachePersistenceException"/> when
/// the store is unavailable (the libsecret keyring case on Linux). Windows DPAPI and macOS Keychain are
/// treated as available (verification succeeds without a prompt in the CLI context).
///
/// It exists only so <see cref="AgentHostTokenCache"/> can enforce the no-silent-fallback policy
/// against a real platform at runtime; every offline test uses a fake probe instead.
/// </summary>
public sealed class MsalSecureTokenCacheProbe : ISecureTokenCacheProbe
{
    /// <inheritdoc />
    public Task<bool> IsProtectedPersistenceAvailableAsync(
        AgentHostCacheDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        try
        {
            Directory.CreateDirectory(descriptor.CacheDirectory);
            var properties = MsalCacheStorage.Build(descriptor, useUnprotectedFile: false);
            Storage.Create(properties).VerifyPersistence();
            return Task.FromResult(true);
        }
        catch (MsalCachePersistenceException)
        {
            // The platform secret store (libsecret keyring) is unavailable; protected persistence is not
            // possible. The caller (AgentHostTokenCache) decides whether the unprotected opt-in applies.
            return Task.FromResult(false);
        }
    }
}
