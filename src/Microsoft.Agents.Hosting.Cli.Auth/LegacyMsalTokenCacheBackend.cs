using System.Text;
using Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using static Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage.LinuxOwnedProtectedStorage;

namespace Microsoft.Agents.Hosting.Cli.Auth;

public interface ILegacyMsalTokenCacheBackend
{
    // Hold this across a paired MSAL before/after notification. Clear acquires it itself.
    IDisposable AcquireLease(CancellationToken ct);
    Task<byte[]?> ReadAsync(CancellationToken ct);
    Task WriteAsync(ReadOnlyMemory<byte> value, CancellationToken ct);
    Task ClearAsync(CancellationToken ct);
}

public static class LegacyMsalTokenCacheBackendFactory
{
    public static ILegacyMsalTokenCacheBackend Create(string configDirectory, MacOsKeychainProcessPolicy? macOsPolicy = null,
        Func<string, string?>? readEnvironment = null) =>
        OperatingSystem.IsWindows() ? Windows(configDirectory, new CurrentUserDpapiProtector()) :
        OperatingSystem.IsMacOS() ? MacOs(configDirectory, macOsPolicy ?? throw Protection.Failure("macOS", "explicit-cli-process-opt-in-required")) :
        OperatingSystem.IsLinux() ? Linux(configDirectory, new GioSecretServiceTransportFactory(readEnvironment)) :
        throw Protection.Failure("legacy", "unsupported-platform");

    public static ILegacyMsalTokenCacheBackend CreateUnprotectedTokenCache(string configDirectory)
    {
        var files = new LegacyCacheFiles(configDirectory, plaintext: true);
        return new LegacyCacheBackend(files, files.ReadAsync, files.WriteAsync, _ => { files.Delete(); return Task.CompletedTask; });
    }

    public static ILegacyMsalTokenCacheBackend CreateWithUnprotectedTokenCacheFallback(string configDirectory,
        Func<string, string?>? readEnvironment = null)
    {
        if (!OperatingSystem.IsLinux()) throw Protection.Failure("legacy", "unprotected-fallback-requires-linux");
        return new ProtectedFirstLegacyCache(() => Create(configDirectory, readEnvironment: readEnvironment),
            CreateUnprotectedTokenCache(configDirectory));
    }

    internal static ILegacyMsalTokenCacheBackend Windows(string directory, IDataProtector protector)
    {
        var files = new LegacyCacheFiles(directory, plaintext: false);
        return new LegacyCacheBackend(files, async ct =>
        {
            var encrypted = await files.ReadAsync(ct).ConfigureAwait(false);
            if (encrypted is null || encrypted.Length == 0) return encrypted;
            try { return protector.Unprotect(encrypted, []); }
            finally { Protection.Wipe(encrypted); }
        }, async (data, ct) =>
        {
            var plain = data.ToArray();
            byte[]? encrypted = null;
            try { encrypted = protector.Protect(plain, []); await files.WriteAsync(encrypted, ct).ConfigureAwait(false); }
            finally { Protection.Wipe(plain); Protection.Wipe(encrypted); }
        }, _ => { files.Delete(); return Task.CompletedTask; });
    }

    internal static ILegacyMsalTokenCacheBackend MacOs(string directory, MacOsKeychainProcessPolicy policy)
    {
        var files = new LegacyCacheFiles(directory, plaintext: false);
        var native = new MacOsOwnedProtectedStorage(policy);
        Task<byte[]?> Read(CancellationToken ct)
        {
            byte[]? data = null; IMacOwnedHandle? item = null;
            using var keychain = native.OpenReadyKeychain(ct);
            try
            {
                var status = native.Call(() => policy.Native.Find(keychain, files.Descriptor.MacKeychainService, files.Descriptor.MacKeychainAccount, true, out data, out item), ct);
                if (status == -25300) return Task.FromResult<byte[]?>(null);
                MacOsKeychainProcessPolicy.Check(status);
                if (item is null || data is null) throw new NativeStorageException(NativeStorageFailure.Malformed);
                var result = data; data = null; return Task.FromResult<byte[]?>(result);
            }
            finally { item?.Dispose(); Protection.Wipe(data); }
        }
        Task Mutate(ReadOnlyMemory<byte>? value, CancellationToken ct)
        {
            byte[]? unused = null; IMacOwnedHandle? item = null;
            using var keychain = native.OpenReadyKeychain(ct);
            try
            {
                var status = native.Call(() => policy.Native.Find(keychain, files.Descriptor.MacKeychainService, files.Descriptor.MacKeychainAccount, false, out unused, out item), ct);
                if (status != -25300) MacOsKeychainProcessPolicy.Check(status);
                if (value is { } memory)
                {
                    var bytes = memory.ToArray();
                    try
                    {
                        if (status == -25300) MacOsKeychainProcessPolicy.Check(native.Call(() => policy.Native.Add(keychain, files.Descriptor.MacKeychainService, files.Descriptor.MacKeychainAccount, bytes), ct));
                        else MacOsKeychainProcessPolicy.Check(native.Call(() => policy.Native.Modify(item ?? throw new NativeStorageException(NativeStorageFailure.Malformed), bytes), ct));
                    }
                    finally { Protection.Wipe(bytes); }
                }
                else if (status != -25300)
                {
                    var deleted = native.Call(() => policy.Native.Delete(item ?? throw new NativeStorageException(NativeStorageFailure.Malformed)), ct);
                    if (deleted != -25300) MacOsKeychainProcessPolicy.Check(deleted);
                }
                return Task.CompletedTask;
            }
            finally { item?.Dispose(); Protection.Wipe(unused); }
        }
        return new LegacyCacheBackend(files, Read, (data, ct) => Mutate(data, ct), ct => Mutate(null, ct));
    }

    internal static ILegacyMsalTokenCacheBackend Linux(string directory, ISecretServiceTransportFactory transport)
    {
        var files = new LegacyCacheFiles(directory, plaintext: false);
        var protocol = new LegacySecretServiceCache(files.Descriptor, transport);
        return new LegacyCacheBackend(files, protocol.ReadAsync, protocol.WriteAsync, protocol.ClearAsync);
    }
}

internal sealed class ProtectedFirstLegacyCache(Func<ILegacyMsalTokenCacheBackend> protectedCache, ILegacyMsalTokenCacheBackend unprotectedCache) : ILegacyMsalTokenCacheBackend
{
    private ILegacyMsalTokenCacheBackend? _selected;
    public IDisposable AcquireLease(CancellationToken ct) => unprotectedCache.AcquireLease(ct);
    public async Task<byte[]?> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _selected = null;
        var candidate = protectedCache();
        var result = candidate is LegacyCacheBackend native
            ? await native.ReadForFallbackAsync(ct).ConfigureAwait(false)
            : (Value: await candidate.ReadAsync(ct).ConfigureAwait(false), ProtectionUnavailable: false);
        if (result.ProtectionUnavailable)
        {
            _selected = unprotectedCache;
            return await unprotectedCache.ReadAsync(ct).ConfigureAwait(false);
        }
        _selected = candidate;
        return result.Value ?? await unprotectedCache.ReadAsync(ct).ConfigureAwait(false);
    }
    public Task WriteAsync(ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return (_selected ?? throw new AuthProfileStateException("The legacy cache write has no paired availability decision.")).WriteAsync(value, ct);
    }
    public async Task ClearAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CacheProtectionUnavailableException? failure = null;
        try { await protectedCache().ClearAsync(ct).ConfigureAwait(false); }
        catch (CacheProtectionUnavailableException ex) { failure = ex; }
        try { await unprotectedCache.ClearAsync(ct).ConfigureAwait(false); }
        catch (CacheProtectionUnavailableException ex) { failure ??= ex; }
        if (failure is not null) throw failure;
    }
}

internal sealed class LegacyCacheBackend(LegacyCacheFiles files, Func<CancellationToken, Task<byte[]?>> read,
    Func<ReadOnlyMemory<byte>, CancellationToken, Task> write, Func<CancellationToken, Task> clear) : ILegacyMsalTokenCacheBackend
{
    public IDisposable AcquireLease(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return files.Lease(); }
        catch (Exception ex) when (Protection.IsProviderFailure(ex)) { throw Failure(); }
    }
    public async Task<byte[]?> ReadAsync(CancellationToken ct) =>
        (await ReadCoreAsync(classifyProtectionAvailability: false, ct).ConfigureAwait(false)).Value;
    internal Task<(byte[]? Value, bool ProtectionUnavailable)> ReadForFallbackAsync(CancellationToken ct) =>
        ReadCoreAsync(classifyProtectionAvailability: true, ct);
    private async Task<(byte[]? Value, bool ProtectionUnavailable)> ReadCoreAsync(bool classifyProtectionAvailability, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        byte[]? value = null;
        try
        {
            value = await read(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            if (value is { Length: > Protection.MaxValueBytes }) throw Failure();
            var result = value; value = null; return (result, false);
        }
        catch (Exception ex) when (classifyProtectionAvailability && IsKnownProtectionUnavailability(ex)) { return (null, true); }
        catch (Exception ex) when (Protection.IsProviderFailure(ex) || ex is FormatException) { throw Failure(); }
        finally { Protection.Wipe(value); }
    }
    private static bool IsKnownProtectionUnavailability(Exception ex) => ex is
        NativeStorageException { Failure: NativeStorageFailure.Unavailable or NativeStorageFailure.Locked or NativeStorageFailure.PromptRequired } or
        DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException;
    public async Task WriteAsync(ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (value.Length > Protection.MaxValueBytes) throw Failure();
        var copy = value.ToArray();
        try { await write(copy, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); }
        catch (Exception ex) when (Protection.IsProviderFailure(ex)) { throw Failure(); }
        finally { Protection.Wipe(copy); }
    }
    public async Task ClearAsync(CancellationToken ct)
    {
        using var lease = AcquireLease(ct);
        try { await clear(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); files.Delete(); }
        catch (Exception ex) when (Protection.IsProviderFailure(ex)) { throw Failure(); }
    }
    private static CacheProtectionUnavailableException Failure() => new("The legacy msagent token cache is unavailable.",
        "Check its dedicated namespace, permissions and no-UI native prerequisites. No unlock, persistence probe or shared-account removal was attempted.");
}

internal sealed class LegacyCacheFiles(string root, bool plaintext)
{
    internal AgentHostCacheDescriptor Descriptor { get; } = AgentHostCacheDescriptor.Create(Path.GetFullPath(root));
    internal IDisposable Lease()
    {
        OwnedProfileFiles.EnsureDirectory(Descriptor.CacheDirectory);
        var path = Descriptor.CacheFilePath + ".lockfile";
        if (File.Exists(path)) RegularFile(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal async Task<byte[]?> ReadAsync(CancellationToken ct)
    {
        OwnedProfileFiles.CheckAncestors(Path.GetDirectoryName(Descriptor.CacheFilePath)!);
        try
        {
            RegularFile(Descriptor.CacheFilePath);
            if (plaintext) OwnedProfileFiles.CheckFile(Descriptor.CacheFilePath);
            await using var stream = new FileStream(Descriptor.CacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > Protection.MaxValueBytes) throw new NativeStorageException(NativeStorageFailure.Malformed);
            var data = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false); return data;
        }
        catch (FileNotFoundException) { return null; }
    }
    internal async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var path = Descriptor.CacheFilePath;
        OwnedProfileFiles.CheckAncestors(Descriptor.CacheDirectory);
        if (File.Exists(path)) RegularFile(path);
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            await using (var stream = OwnedProfileFiles.Create(pending))
            {
                await stream.WriteAsync(data, ct).ConfigureAwait(false); await stream.FlushAsync(ct).ConfigureAwait(false); stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested(); File.Move(pending, path, overwrite: true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    internal void Delete()
    {
        if (!File.Exists(Descriptor.CacheFilePath)) return;
        OwnedProfileFiles.CheckAncestors(Descriptor.CacheDirectory); RegularFile(Descriptor.CacheFilePath); File.Delete(Descriptor.CacheFilePath);
    }
    private static void RegularFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new NativeStorageException(NativeStorageFailure.Malformed);
    }
}

internal sealed class LegacySecretServiceCache(AgentHostCacheDescriptor descriptor, ISecretServiceTransportFactory factory)
{
    private const string Root = "/org/freedesktop/secrets", Service = "org.freedesktop.Secret.Service",
        ItemInterface = "org.freedesktop.Secret.Item", CollectionInterface = "org.freedesktop.Secret.Collection";
    private Dictionary<string, string> Expected => new(StringComparer.Ordinal)
    { ["xdg:schema"] = descriptor.LinuxKeyringSchema, ["Version"] = "1", ["CacheKind"] = "MSAL_Token_Cache" };
    private DbusValue Attributes() => DbusValue.Array("{ss}", Expected.Select(p => DbusValue.Entry(DbusValue.Text(p.Key), DbusValue.Text(p.Value))).ToArray());
    private (string Collection, string? Item) Find(ISecretServiceTransport transport, CancellationToken ct)
    {
        string collection;
        using (var reply = Call(transport, Root, Service, "ReadAlias", DbusValue.Tuple(DbusValue.Text("default")), "(o)", ct))
            collection = ObjectPath(Children(reply, 1)[0]);
        if (collection == "/") throw new NativeStorageException(NativeStorageFailure.Unavailable);
        RequireUnlocked(transport, collection, CollectionInterface, ct);
        string item;
        using (var reply = Call(transport, Root, Service, "SearchItems", DbusValue.Tuple(Attributes()), "(aoao)", ct))
        {
            var found = Children(reply, 2);
            var unlocked = ObjectPaths(found[0]); var locked = ObjectPaths(found[1]);
            if (locked.Length != 0) throw new NativeStorageException(NativeStorageFailure.Locked);
            if (unlocked.Length == 0) return (collection, null);
            if (unlocked.Length != 1) throw new NativeStorageException(NativeStorageFailure.Malformed);
            item = ObjectPath(unlocked[0]);
        }
        CheckItemPath(item, collection); RequireUnlocked(transport, item, ItemInterface, ct);
        using (var reply = GetProperty(transport, item, ItemInterface, "Attributes", ct))
        {
            var value = VariantValue(Children(reply, 1)[0]); var expected = Expected;
            if (value.Signature != "a{ss}") throw new NativeStorageException(NativeStorageFailure.Malformed);
            foreach (var entry in Children(value, 3))
            {
                if (entry.Signature != "{ss}") throw new NativeStorageException(NativeStorageFailure.Malformed);
                var pair = Children(entry, 2);
                if (!expected.Remove(Text(pair[0]), out var text) || text != Text(pair[1])) throw new NativeStorageException(NativeStorageFailure.Malformed);
            }
        }
        return (collection, item);
    }
    internal Task<byte[]?> ReadAsync(CancellationToken ct)
    {
        using var transport = factory.Open(ct);
        var (_, item) = Find(transport, ct);
        if (item is null) return Task.FromResult<byte[]?>(null);
        var session = OpenSession(transport, ct);
        using var reply = Call(transport, item, ItemInterface, "GetSecret", DbusValue.Tuple(DbusValue.Path(session)), "((oayays))", ct);
        var secret = Children(Children(reply, 1)[0], 4);
        if (ObjectPath(secret[0]) != session || ByteArray(secret[1]).Length != 0 || !Text(secret[3]).StartsWith("text/plain", StringComparison.Ordinal))
            throw new NativeStorageException(NativeStorageFailure.Malformed);
        return Task.FromResult<byte[]?>(Convert.FromBase64String(Encoding.UTF8.GetString(ByteArray(secret[2]))));
    }
    internal Task WriteAsync(ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        using var transport = factory.Open(ct);
        var (collection, item) = Find(transport, ct); var session = OpenSession(transport, ct);
        var encoded = Encoding.UTF8.GetBytes(Convert.ToBase64String(value.Span));
        if (encoded.Length > Protection.MaxValueBytes) { Protection.Wipe(encoded); throw new NativeStorageException(NativeStorageFailure.Malformed); }
        var secret = DbusValue.Tuple(DbusValue.Path(session), DbusValue.Bytes([]), DbusValue.Bytes(encoded), DbusValue.Text("text/plain; charset=utf8"));
        if (item is null)
        {
            var properties = DbusValue.Array("{sv}",
                DbusValue.Entry(DbusValue.Text("org.freedesktop.Secret.Item.Label"), DbusValue.Variant(DbusValue.Text(descriptor.LinuxKeyringLabel))),
                DbusValue.Entry(DbusValue.Text("org.freedesktop.Secret.Item.Attributes"), DbusValue.Variant(Attributes())));
            using var reply = Call(transport, collection, CollectionInterface, "CreateItem", DbusValue.Tuple(properties, secret, DbusValue.Boolean(false)), "(oo)", ct);
            var result = Children(reply, 2); RefusePrompt(result[1]); CheckItemPath(ObjectPath(result[0]), collection);
        }
        else { using var reply = Call(transport, item, ItemInterface, "SetSecret", DbusValue.Tuple(secret), "()", ct); _ = Children(reply, 0); }
        return Task.CompletedTask;
    }
    internal Task ClearAsync(CancellationToken ct)
    {
        using var transport = factory.Open(ct); var (_, item) = Find(transport, ct);
        if (item is not null)
        {
            using var reply = Call(transport, item, ItemInterface, "Delete", DbusValue.Tuple(), "(o)", ct);
            RefusePrompt(Children(reply, 1)[0]);
        }
        return Task.CompletedTask;
    }
}
