using System.Runtime.CompilerServices;

namespace Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;

/// <summary>
/// Explicit CLI-host capability disabling optional legacy Keychain UI for the process lifetime.
/// Establish before concurrent Keychain work; never restore or toggle this setting.
/// Other native clients in the process must not reenable UI or explicitly request unlocking/ACL UI.
/// This is not a restriction on browser or broker identity UI, nor a policy for arbitrary Auth hosts.
/// </summary>
public sealed class MacOsKeychainProcessPolicy
{
    private static readonly ConditionalWeakTable<IMacOsKeychainApi, MacOsKeychainProcessPolicy> Policies = new();
    private static readonly LegacyMacKeychainApi ProcessNative = new();
    private readonly object _gate = new();
    private bool _attempted;
    private bool _established;
    internal IMacOsKeychainApi Native { get; }

    private MacOsKeychainProcessPolicy(IMacOsKeychainApi native) => Native = native;

    public static MacOsKeychainProcessPolicy EstablishForCliProcess(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) throw Protection.Failure("macOS", "unsupported-platform");
        return EstablishForCliProcess(ProcessNative, ct);
    }

    internal static MacOsKeychainProcessPolicy EstablishForCliProcess(IMacOsKeychainApi native, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(native);
        var policy = Policies.GetValue(native, n => new MacOsKeychainProcessPolicy(n));
        lock (policy._gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!policy._attempted)
            {
                policy._attempted = true;
                try
                {
                    Check(native.SetInteractionAllowed(false));
                    ct.ThrowIfCancellationRequested();
                    Check(native.GetInteractionAllowed(out bool allowed));
                    if (allowed) throw Protection.Failure("macOS", "policy-not-established");
                    ct.ThrowIfCancellationRequested();
                    policy._established = true;
                }
                catch (Exception ex) when (Protection.IsProviderFailure(ex))
                {
                    throw Protection.Failure("macOS", "policy-not-established");
                }
            }
            else
            {
                policy.Ensure(ct);
            }
        }

        return policy;
    }

    internal void Ensure(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_established) throw Protection.Failure("macOS", "policy-not-established");
        try
        {
            Check(Native.GetInteractionAllowed(out bool allowed));
            ct.ThrowIfCancellationRequested();
            if (allowed) throw Protection.Failure("macOS", "policy-reenabled");
        }
        catch (Exception ex) when (Protection.IsProviderFailure(ex))
        {
            throw Protection.Failure("macOS", "policy-unavailable");
        }
    }

    internal static void Check(int status)
    {
        if (status != 0) throw Protection.Failure("macOS", $"native-status-{status}");
    }
}

internal interface IMacOwnedHandle : IDisposable;

internal interface IMacOsKeychainApi
{
    int SetInteractionAllowed(bool allowed);
    int GetInteractionAllowed(out bool allowed);
    int CopyDefaultKeychain(out IMacOwnedHandle? keychain);
    int GetStatus(IMacOwnedHandle keychain, out uint status);
    int Find(IMacOwnedHandle keychain, string service, string account, bool readData,
        out byte[]? data, out IMacOwnedHandle? item);
    int Add(IMacOwnedHandle keychain, string service, string account, byte[] data);
    int Modify(IMacOwnedHandle item, byte[] data);
    int Delete(IMacOwnedHandle item);
}

internal sealed class MacOsOwnedProtectedStorage : OwnedProtectedStorageBackend
{
    private readonly MacOsKeychainProcessPolicy _policy;
    private const int NotFound = -25300;

    internal MacOsOwnedProtectedStorage(MacOsKeychainProcessPolicy? policy) : base("macOS") =>
        _policy = policy ?? throw Protection.Failure("macOS", "explicit-cli-process-opt-in-required");

    protected override Task<byte[]?> ReadCoreAsync(OwnedProtectedStorageReference reference, CancellationToken ct)
    {
        byte[]? data = null;
        IMacOwnedHandle? item = null;
        using var keychain = OpenReadyKeychain(ct);
        try
        {
            int status = Call(() => _policy.Native.Find(keychain, reference.ServiceName, reference.OpaqueId,
                true, out data, out item), ct);
            if (status == NotFound) return Task.FromResult<byte[]?>(null);
            MacOsKeychainProcessPolicy.Check(status);
            if (item is null || data is null) throw Protection.Failure("macOS", "malformed-read");
            byte[] result = data;
            data = null;
            return Task.FromResult<byte[]?>(result);
        }
        finally
        {
            item?.Dispose();
            Protection.Wipe(data);
        }
    }

    protected override Task WriteCoreAsync(OwnedProtectedStorageReference reference, byte[] data, CancellationToken ct)
    {
        IMacOwnedHandle? item = null;
        byte[]? unused = null;
        using var keychain = OpenReadyKeychain(ct);
        try
        {
            int status = Call(() => _policy.Native.Find(keychain, reference.ServiceName, reference.OpaqueId,
                false, out unused, out item), ct);
            if (status == NotFound)
            {
                MacOsKeychainProcessPolicy.Check(Call(() => _policy.Native.Add(keychain,
                    reference.ServiceName, reference.OpaqueId, data), ct));
            }
            else
            {
                MacOsKeychainProcessPolicy.Check(status);
                if (item is null) throw Protection.Failure("macOS", "malformed-item");
                MacOsKeychainProcessPolicy.Check(Call(() => _policy.Native.Modify(item, data), ct));
            }
        }
        finally
        {
            item?.Dispose();
            Protection.Wipe(unused);
        }

        return Task.CompletedTask;
    }

    protected override Task ClearCoreAsync(OwnedProtectedStorageReference reference, CancellationToken ct)
    {
        IMacOwnedHandle? item = null;
        byte[]? unused = null;
        using var keychain = OpenReadyKeychain(ct);
        try
        {
            int status = Call(() => _policy.Native.Find(keychain, reference.ServiceName, reference.OpaqueId,
                false, out unused, out item), ct);
            if (status != NotFound)
            {
                MacOsKeychainProcessPolicy.Check(status);
                if (item is null) throw Protection.Failure("macOS", "malformed-item");
                int deleteStatus = Call(() => _policy.Native.Delete(item), ct);
                if (deleteStatus != NotFound) MacOsKeychainProcessPolicy.Check(deleteStatus);
            }
        }
        finally
        {
            item?.Dispose();
            Protection.Wipe(unused);
        }

        return Task.CompletedTask;
    }

    internal IMacOwnedHandle OpenReadyKeychain(CancellationToken ct)
    {
        IMacOwnedHandle? keychain = null;
        try
        {
            MacOsKeychainProcessPolicy.Check(Call(() => _policy.Native.CopyDefaultKeychain(out keychain), ct));
            if (keychain is null) throw Protection.Failure("macOS", "missing-default-keychain");
            uint status = 0;
            MacOsKeychainProcessPolicy.Check(Call(() => _policy.Native.GetStatus(keychain, out status), ct));
            if ((status & 1) == 0) throw Protection.Failure("macOS", "default-keychain-locked");
            return keychain;
        }
        catch
        {
            keychain?.Dispose();
            throw;
        }
    }

    internal int Call(Func<int> call, CancellationToken ct)
    {
        _policy.Ensure(ct);
        int result = call();
        ct.ThrowIfCancellationRequested();
        _policy.Ensure(ct);
        return result;
    }
}
