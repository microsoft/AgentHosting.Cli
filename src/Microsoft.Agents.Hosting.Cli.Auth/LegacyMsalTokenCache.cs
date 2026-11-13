using System.Security.Cryptography;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Auth;

internal static class LegacyMsalTokenCache
{
    internal static void Attach(ITokenCache cache, Func<ILegacyMsalTokenCacheBackend> getBackend)
    {
        IDisposable? lease = null;
        ILegacyMsalTokenCacheBackend? backend = null;
        cache.SetBeforeAccessAsync(async notification =>
        {
            byte[]? value = null;
            try
            {
                backend = getBackend();
                lease = backend.AcquireLease(notification.CancellationToken);
                value = await backend.ReadAsync(notification.CancellationToken).ConfigureAwait(false);
                notification.TokenCache.DeserializeMsalV3(value is { Length: > 0 } ? value : "{}"u8.ToArray(), shouldClearExistingCache: true);
            }
            catch
            {
                Interlocked.Exchange(ref lease, null)?.Dispose();
                throw;
            }
            finally { if (value is not null) CryptographicOperations.ZeroMemory(value); }
        });
        cache.SetAfterAccessAsync(async notification =>
        {
            byte[]? value = null;
            try
            {
                if (!notification.HasStateChanged) return;
                if (lease is null || backend is null) throw new AuthProfileStateException("The legacy cache notification lease is missing.");
                value = notification.TokenCache.SerializeMsalV3();
                await backend.WriteAsync(value, notification.CancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (value is not null) CryptographicOperations.ZeroMemory(value);
                Interlocked.Exchange(ref lease, null)?.Dispose();
            }
        });
    }
}
