using Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>Defers native selection and policy establishment until an owned item is used.</summary>
public sealed class DeferredOwnedProtectedStorage(
    Func<IOwnedProtectedStorageBackend> create, string? unprotectedTokenCacheDirectory = null) : IOwnedProtectedStorageBackend, IDisposable
{
    private readonly Lazy<IOwnedProtectedStorageBackend> _backend = new(create ?? throw new ArgumentNullException(nameof(create)));
    public async Task<byte[]?> ReadAsync(OwnedProtectedStorageReference reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reference);
        var fallback = Fallback(reference);
        try
        {
            var value = await _backend.Value.ReadAsync(reference, ct).ConfigureAwait(false);
            if (value is not null || fallback is null) return value;
            using var lease = fallback.AcquireLease(ct);
            return await fallback.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (CacheProtectionUnavailableException failure) when (fallback is not null && Protection.AllowsTokenFallback(failure))
        {
            using var lease = fallback.AcquireLease(ct);
            return await fallback.ReadAsync(ct).ConfigureAwait(false);
        }
    }
    public async Task WriteAsync(OwnedProtectedStorageReference reference, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reference);
        var fallback = Fallback(reference);
        try { await _backend.Value.WriteAsync(reference, data, ct).ConfigureAwait(false); }
        catch (CacheProtectionUnavailableException failure) when (fallback is not null && Protection.AllowsTokenFallback(failure))
        {
            using var lease = fallback.AcquireLease(ct);
            await fallback.WriteAsync(data, ct).ConfigureAwait(false);
        }
    }
    public async Task ClearAsync(OwnedProtectedStorageReference reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reference);
        var fallback = Fallback(reference);
        try { await _backend.Value.ClearAsync(reference, ct).ConfigureAwait(false); }
        finally { if (fallback is not null) await fallback.ClearAsync(ct).ConfigureAwait(false); }
    }
    private ILegacyMsalTokenCacheBackend? Fallback(OwnedProtectedStorageReference reference) =>
        unprotectedTokenCacheDirectory is not null && reference.Kind == OwnedProtectedStorageKind.TokenCache
            ? LegacyMsalTokenCacheBackendFactory.CreateUnprotectedTokenCache(
                Path.Combine(unprotectedTokenCacheDirectory, "owned-unprotected-token-caches-v1", reference.OpaqueId))
            : null;
    public void Dispose() { if (_backend.IsValueCreated && _backend.Value is IDisposable disposable) disposable.Dispose(); }
}

/// <summary>Defers the legacy backend, including its no-prompt policy, until a cache callback.</summary>
public sealed class DeferredLegacyMsalTokenCache(Func<ILegacyMsalTokenCacheBackend> create) : ILegacyMsalTokenCacheBackend, IDisposable
{
    private readonly Lazy<ILegacyMsalTokenCacheBackend> _backend = new(create ?? throw new ArgumentNullException(nameof(create)));
    public IDisposable AcquireLease(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return _backend.Value.AcquireLease(ct); }
    public Task<byte[]?> ReadAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return _backend.Value.ReadAsync(ct); }
    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return _backend.Value.WriteAsync(data, ct); }
    public Task ClearAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return _backend.Value.ClearAsync(ct); }
    public void Dispose() { if (_backend.IsValueCreated && _backend.Value is IDisposable disposable) disposable.Dispose(); }
}
