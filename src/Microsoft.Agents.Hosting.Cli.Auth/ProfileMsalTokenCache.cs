using System.Security.Cryptography;
using Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Auth;

internal static class ProfileMsalTokenCache
{
    internal static void Attach(ITokenCache cache, IOwnedProtectedStorageBackend backend, OwnedProtectedStorageReference reference, CancellationToken ct)
    {
        cache.SetBeforeAccessAsync(async notification =>
        {
            var bytes = await backend.ReadAsync(reference, ct).ConfigureAwait(false);
            if (bytes is null) return;
            try
            {
                if (bytes.Length > 16 * 1024 * 1024) throw new AuthProfileStateException("The private token cache exceeds its size limit.");
                notification.TokenCache.DeserializeMsalV3(bytes, shouldClearExistingCache: true);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        });
        cache.SetAfterAccessAsync(async notification =>
        {
            if (!notification.HasStateChanged) return;
            var bytes = notification.TokenCache.SerializeMsalV3();
            try { await backend.WriteAsync(reference, bytes, ct).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        });
    }
}
