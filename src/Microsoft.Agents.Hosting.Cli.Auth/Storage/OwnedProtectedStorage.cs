using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;

public enum OwnedProtectedStorageKind
{
    Credential = 1,
    TokenCache = 2,
}

/// <summary>An owned item coordinate, never a path or a provider-supplied service name.</summary>
public sealed class OwnedProtectedStorageReference
{
    public OwnedProtectedStorageReference(OwnedProtectedStorageKind kind, string opaqueId)
    {
        if (kind is not (OwnedProtectedStorageKind.Credential or OwnedProtectedStorageKind.TokenCache) ||
            opaqueId is not { Length: 32 } ||
            opaqueId.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            opaqueId.All(c => c == '0'))
        {
            throw new ArgumentException("An owned storage reference requires a known kind and a nonzero, lowercase 32-hex identifier.");
        }

        Kind = kind;
        OpaqueId = opaqueId;
    }

    public OwnedProtectedStorageKind Kind { get; }
    public string OpaqueId { get; }
    internal string KindName => Kind == OwnedProtectedStorageKind.Credential ? "credential" : "token-cache";
    internal string ServiceName => $"com.microsoft.msagent.owned.v1.{KindName}";
    internal string FileName => $"{KindName}-{OpaqueId}.bin";
    internal byte[] Entropy => Encoding.UTF8.GetBytes($"{ServiceName}:{OpaqueId}");
    public override string ToString() => nameof(OwnedProtectedStorageReference);
}

/// <summary>
/// Protected persistence of one validated owned item. Null means only not found.
/// Ownership inventories, locking between writers, and credential acquisition belong to callers.
/// </summary>
public interface IOwnedProtectedStorageBackend
{
    Task<byte[]?> ReadAsync(OwnedProtectedStorageReference reference, CancellationToken ct);
    Task WriteAsync(OwnedProtectedStorageReference reference, ReadOnlyMemory<byte> data, CancellationToken ct);
    Task ClearAsync(OwnedProtectedStorageReference reference, CancellationToken ct);
}

public static class OwnedProtectedStorageBackendFactory
{
    /// <summary>
    /// Selects an adapter without opening storage. The host supplies an existing trusted directory.
    /// macOS requires explicit CLI-process opt-in; this factory never changes native UI policy.
    /// On Linux the host must supply resolved DBUS_SESSION_BUS_ADDRESS and G_DBUS_DEBUG settings
    /// through readEnvironment. GIO diagnostics must stay disabled before any GIO initialization
    /// and throughout the controlled host's lifetime; native diagnostic flags may be cached.
    /// </summary>
    public static IOwnedProtectedStorageBackend Create(
        string resolvedStorageDirectory,
        MacOsKeychainProcessPolicy? macOsPolicy = null,
        Func<string, string?>? readEnvironment = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedStorageDirectory);
        if (OperatingSystem.IsWindows())
        {
            return new WindowsOwnedProtectedStorage(resolvedStorageDirectory, new CurrentUserDpapiProtector());
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsOwnedProtectedStorage(macOsPolicy);
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxOwnedProtectedStorage(new GioSecretServiceTransportFactory(readEnvironment));
        }

        throw Protection.Failure("platform", "unsupported");
    }
}

internal static class Protection
{
    internal const int MaxValueBytes = 16 * 1024 * 1024;

    private const string AvailabilityFailure = "msagent.protection-availability-failure";
    internal static bool AllowsTokenFallback(CacheProtectionUnavailableException failure) =>
        failure.Data[AvailabilityFailure] is true;

    internal static CacheProtectionUnavailableException Failure(string platform, string reason, Exception? cause = null)
    {
        var failure = new CacheProtectionUnavailableException($"Owned protected storage is unavailable ({platform}: {reason}).",
            "Check native storage availability and the host's no-UI policy. No plaintext fallback or unlock was attempted.");
        if (cause is NativeStorageException { Failure: NativeStorageFailure.Unavailable or NativeStorageFailure.Locked or NativeStorageFailure.PromptRequired }
            or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
            failure.Data[AvailabilityFailure] = true;
        return failure;
    }

    internal static CacheProtectionUnavailableException ProviderFailure(string platform, string operation, Exception cause)
    {
        if (platform != "Linux") return Failure(platform, "native-or-io-failure", cause);
        string reason = cause switch
        {
            NativeStorageException { Failure: NativeStorageFailure.Unavailable } => "service-unavailable",
            NativeStorageException { Failure: NativeStorageFailure.Locked } => "store-locked",
            NativeStorageException { Failure: NativeStorageFailure.PromptRequired } => "prompt-required",
            NativeStorageException { Failure: NativeStorageFailure.Malformed } => "malformed-response",
            NativeStorageException { Failure: NativeStorageFailure.NotFound } => "native-object-not-found",
            NativeStorageException => "native-failure",
            DllNotFoundException => "native-library-unavailable",
            EntryPointNotFoundException => "native-entry-point-unavailable",
            PlatformNotSupportedException => "platform-not-supported",
            UnauthorizedAccessException => "access-denied",
            CryptographicException => "cryptographic-failure",
            _ => "io-failure",
        };
        string? step = cause is NativeStorageException native ? native.Step switch
        {
            NativeStorageStep.Connect => "connect",
            NativeStorageStep.ReadAlias => "read-alias",
            NativeStorageStep.SearchItems => "search-items",
            NativeStorageStep.OpenSession => "open-session",
            NativeStorageStep.GetProperty => "get-property",
            NativeStorageStep.GetSecret => "get-secret",
            NativeStorageStep.SetSecret => "set-secret",
            NativeStorageStep.CreateItem => "create-item",
            NativeStorageStep.Delete => "delete",
            _ => null,
        } : null;
        return Failure(platform, $"{reason}; operation: {operation}" + (step is null ? "" : $"; step: {step}"), cause);
    }

    internal static bool IsProviderFailure(Exception ex) => ex is
        IOException or UnauthorizedAccessException or CryptographicException or
        DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException or NativeStorageException;

    internal static void Wipe(byte[]? data)
    {
        if (data is not null)
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }
}

internal enum NativeStorageFailure { Unknown, Unavailable, Locked, NotFound, Malformed, PromptRequired }
internal enum NativeStorageStep { Connect, ReadAlias, SearchItems, OpenSession, GetProperty, GetSecret, SetSecret, CreateItem, Delete }

internal sealed class NativeStorageException(NativeStorageFailure failure) : Exception("Native protected storage failed.")
{
    internal NativeStorageFailure Failure { get; } = failure;
    internal NativeStorageStep? Step { get; set; }
}

internal abstract class OwnedProtectedStorageBackend(string platform) : IOwnedProtectedStorageBackend
{
    public async Task<byte[]?> ReadAsync(OwnedProtectedStorageReference reference, CancellationToken ct)
    {
        Validate(reference, ct);
        byte[]? result = null;
        try
        {
            result = await ReadCoreAsync(reference, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result is { Length: > Protection.MaxValueBytes })
            {
                throw Protection.Failure(platform, "invalid-size");
            }

            return result;
        }
        catch (Exception ex)
        {
            Protection.Wipe(result);
            if (Protection.IsProviderFailure(ex))
            {
                throw Protection.ProviderFailure(platform, "read", ex);
            }

            throw;
        }
    }

    public async Task WriteAsync(OwnedProtectedStorageReference reference, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        Validate(reference, ct);
        if (data.Length > Protection.MaxValueBytes)
        {
            throw Protection.Failure(platform, "invalid-size");
        }

        byte[] ownedCopy = data.ToArray();
        try
        {
            await WriteCoreAsync(reference, ownedCopy, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (Protection.IsProviderFailure(ex))
        {
            throw Protection.ProviderFailure(platform, "write", ex);
        }
        finally
        {
            Protection.Wipe(ownedCopy);
        }
    }

    public async Task ClearAsync(OwnedProtectedStorageReference reference, CancellationToken ct)
    {
        Validate(reference, ct);
        try
        {
            await ClearCoreAsync(reference, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (Protection.IsProviderFailure(ex))
        {
            throw Protection.ProviderFailure(platform, "clear", ex);
        }
    }

    private static void Validate(OwnedProtectedStorageReference reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reference);
    }

    protected abstract Task<byte[]?> ReadCoreAsync(OwnedProtectedStorageReference reference, CancellationToken ct);
    protected abstract Task WriteCoreAsync(OwnedProtectedStorageReference reference, byte[] data, CancellationToken ct);
    protected abstract Task ClearCoreAsync(OwnedProtectedStorageReference reference, CancellationToken ct);
}

internal interface IDataProtector
{
    byte[] Protect(byte[] data, byte[] entropy);
    byte[] Unprotect(byte[] data, byte[] entropy);
}

internal sealed class CurrentUserDpapiProtector : IDataProtector
{
    public byte[] Protect(byte[] data, byte[] entropy) => ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data, byte[] entropy) => ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser);
}
