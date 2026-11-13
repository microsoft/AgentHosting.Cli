namespace Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;

internal sealed class DbusValue(string signature, object data) : IDisposable
{
    internal string Signature { get; } = signature;
    internal object Data { get; } = data;
    internal static DbusValue Text(string value) => new("s", value);
    internal static DbusValue Path(string value) => new("o", value);
    internal static DbusValue Boolean(bool value) => new("b", value);
    internal static DbusValue Bytes(byte[] value) => new("ay", value);
    internal static DbusValue Variant(DbusValue value) => new("v", value);
    internal static DbusValue Tuple(params DbusValue[] values) => new($"({string.Concat(values.Select(v => v.Signature))})", values);
    internal static DbusValue Array(string elementSignature, params DbusValue[] values) => new($"a{elementSignature}", values);
    internal static DbusValue Entry(DbusValue key, DbusValue value) => new($"{{{key.Signature}{value.Signature}}}", new[] { key, value });
    public override string ToString() => "D-Bus value (redacted)";

    public void Dispose()
    {
        if (Data is byte[] bytes) Protection.Wipe(bytes);
        else if (Data is DbusValue child) child.Dispose();
        else if (Data is DbusValue[] children) foreach (var value in children) value.Dispose();
    }
}

internal interface ISecretServiceTransport : IDisposable
{
    DbusValue Call(string path, string interfaceName, string method, DbusValue parameters,
        string replySignature, CancellationToken ct);
}

internal interface ISecretServiceTransportFactory
{
    ISecretServiceTransport Open(CancellationToken ct);
}

internal sealed class GioSecretServiceTransportFactory : ISecretServiceTransportFactory
{
    private readonly Func<string, string?>? _readEnvironment;
    private readonly Func<string, CancellationToken, ISecretServiceTransport> _connect;
    internal GioSecretServiceTransportFactory(Func<string, string?>? readEnvironment,
        Func<string, CancellationToken, ISecretServiceTransport>? connect = null)
    {
        _readEnvironment = readEnvironment;
        _connect = connect ?? ((address, ct) => new GioSecretServiceTransport(address, ct));
    }

    public ISecretServiceTransport Open(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_readEnvironment is null) throw Protection.Failure("Linux", "resolved-session-bus-settings-required");
        if (!string.IsNullOrEmpty(_readEnvironment("G_DBUS_DEBUG")))
            throw Protection.Failure("Linux", "native-diagnostics-not-permitted");
        string? address = _readEnvironment("DBUS_SESSION_BUS_ADDRESS");
        if (address is null || address.Length is 0 or > 4096 || !address.StartsWith("unix:", StringComparison.Ordinal) ||
            address.Any(c => c < '!' || c > '~' || c == ';'))
            throw Protection.Failure("Linux", "local-session-bus-address-required");
        string[] parts = address[5..].Split(',');
        if (parts.Length is < 1 or > 2 ||
            (!parts[0].StartsWith("path=/", StringComparison.Ordinal) && !parts[0].StartsWith("abstract=", StringComparison.Ordinal)) ||
            parts[0].EndsWith('=') ||
            (parts.Length == 2 && (!parts[1].StartsWith("guid=", StringComparison.Ordinal) ||
                parts[1].Length != 37 || parts[1][5..].Any(c => !char.IsAsciiHexDigit(c)))))
            throw Protection.Failure("Linux", "invalid-local-session-bus-address");
        ct.ThrowIfCancellationRequested();
        return _connect(address, ct);
    }
}

internal sealed class LinuxOwnedProtectedStorage : OwnedProtectedStorageBackend
{
    private const string Root = "/org/freedesktop/secrets";
    private const string Service = "org.freedesktop.Secret.Service";
    private const string ItemInterface = "org.freedesktop.Secret.Item";
    private const string CollectionInterface = "org.freedesktop.Secret.Collection";
    private const string Properties = "org.freedesktop.DBus.Properties";
    private const string ContentType = "application/octet-stream";
    private readonly ISecretServiceTransportFactory _transportFactory;

    internal LinuxOwnedProtectedStorage(ISecretServiceTransportFactory transportFactory) : base("Linux") =>
        _transportFactory = transportFactory;

    protected override Task<byte[]?> ReadCoreAsync(OwnedProtectedStorageReference reference, CancellationToken ct)
    {
        using var transport = _transportFactory.Open(ct);
        var (_, item) = FindOwnedItem(transport, reference, ct);
        if (item is null) return Task.FromResult<byte[]?>(null);
        string session = OpenSession(transport, ct);
        try
        {
            using var reply = Call(transport, item, ItemInterface, "GetSecret",
                DbusValue.Tuple(DbusValue.Path(session)), "((oayays))", ct);
            var secret = Children(Children(reply, 1)[0], 4);
            // GNOME Keyring labels binary secrets as text/plain without changing their bytes.
            if (ObjectPath(secret[0]) != session || ByteArray(secret[1]).Length != 0 ||
                Text(secret[3]) is not (ContentType or "text/plain"))
                throw new NativeStorageException(NativeStorageFailure.Malformed);
            return Task.FromResult<byte[]?>(ByteArray(secret[2]).ToArray());
        }
        catch (NativeStorageException ex) when (ex.Failure == NativeStorageFailure.NotFound)
        {
            return Task.FromResult<byte[]?>(null);
        }
    }

    protected override Task WriteCoreAsync(OwnedProtectedStorageReference reference, byte[] data, CancellationToken ct)
    {
        using var transport = _transportFactory.Open(ct);
        var (collection, item) = FindOwnedItem(transport, reference, ct);
        string session = OpenSession(transport, ct);
        var secret = DbusValue.Tuple(DbusValue.Path(session), DbusValue.Bytes([]),
            DbusValue.Bytes(data.ToArray()), DbusValue.Text(ContentType));
        if (item is not null)
        {
            using var reply = Call(transport, item, ItemInterface, "SetSecret",
                DbusValue.Tuple(secret), "()", ct);
            _ = Children(reply, 0);
        }
        else
        {
            var properties = DbusValue.Array("{sv}",
                DbusValue.Entry(DbusValue.Text("org.freedesktop.Secret.Item.Label"),
                    DbusValue.Variant(DbusValue.Text($"msagent owned {reference.KindName}"))),
                DbusValue.Entry(DbusValue.Text("org.freedesktop.Secret.Item.Attributes"), DbusValue.Variant(Attributes(reference))));
            using var reply = Call(transport, collection, CollectionInterface, "CreateItem",
                DbusValue.Tuple(properties, secret, DbusValue.Boolean(false)), "(oo)", ct);
            var values = Children(reply, 2);
            RefusePrompt(values[1]);
            CheckItemPath(ObjectPath(values[0]), collection);
        }

        return Task.CompletedTask;
    }

    protected override Task ClearCoreAsync(OwnedProtectedStorageReference reference, CancellationToken ct)
    {
        using var transport = _transportFactory.Open(ct);
        var (_, item) = FindOwnedItem(transport, reference, ct);
        if (item is null) return Task.CompletedTask;
        try
        {
            using var reply = Call(transport, item, ItemInterface, "Delete", DbusValue.Tuple(), "(o)", ct);
            RefusePrompt(Children(reply, 1)[0]);
        }
        catch (NativeStorageException ex) when (ex.Failure == NativeStorageFailure.NotFound)
        {
            // Precisely missing owned item; never swallow lock, prompt, or transport failures.
        }

        return Task.CompletedTask;
    }

    private static (string Collection, string? Item) FindOwnedItem(
        ISecretServiceTransport transport, OwnedProtectedStorageReference reference, CancellationToken ct)
    {
        string collection;
        using (var alias = Call(transport, Root, Service, "ReadAlias",
                   DbusValue.Tuple(DbusValue.Text("default")), "(o)", ct))
        {
            collection = ObjectPath(Children(alias, 1)[0]);
            if (collection == "/") throw new NativeStorageException(NativeStorageFailure.Unavailable);
        }

        RequireUnlocked(transport, collection, CollectionInterface, ct);
        string item;
        using (var reply = Call(transport, Root, Service, "SearchItems",
                   DbusValue.Tuple(Attributes(reference)), "(aoao)", ct))
        {
            var found = Children(reply, 2);
            var unlocked = ObjectPaths(found[0]);
            var locked = ObjectPaths(found[1]);
            if (locked.Length != 0) throw new NativeStorageException(NativeStorageFailure.Locked);
            if (unlocked.Length == 0) return (collection, null);
            if (unlocked.Length != 1) throw new NativeStorageException(NativeStorageFailure.Malformed);
            item = ObjectPath(unlocked[0]);
        }

        CheckItemPath(item, collection);
        RequireUnlocked(transport, item, ItemInterface, ct);
        using (var reply = GetProperty(transport, item, ItemInterface, "Attributes", ct))
        {
            DbusValue value = VariantValue(Children(reply, 1)[0]);
            if (value.Signature != "a{ss}" || value.Data is not DbusValue[] entries || entries.Length is < 3 or > 4)
                throw new NativeStorageException(NativeStorageFailure.Malformed);
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["application"] = "com.microsoft.msagent.owned.v1",
                ["kind"] = reference.KindName,
                ["id"] = reference.OpaqueId,
                // GNOME Keyring can add this schema without changing the owned coordinates.
                ["xdg:schema"] = "org.freedesktop.Secret.Generic",
            };
            foreach (var entry in entries)
            {
                if (entry.Signature != "{ss}") throw new NativeStorageException(NativeStorageFailure.Malformed);
                var pair = Children(entry, 2);
                if (!expected.Remove(Text(pair[0]), out string? expectedValue) || expectedValue != Text(pair[1]))
                    throw new NativeStorageException(NativeStorageFailure.Malformed);
            }
            expected.Remove("xdg:schema");
            if (expected.Count != 0) throw new NativeStorageException(NativeStorageFailure.Malformed);
        }

        return (collection, item);
    }

    private static DbusValue Attributes(OwnedProtectedStorageReference reference) => DbusValue.Array("{ss}",
        DbusValue.Entry(DbusValue.Text("application"), DbusValue.Text("com.microsoft.msagent.owned.v1")),
        DbusValue.Entry(DbusValue.Text("kind"), DbusValue.Text(reference.KindName)),
        DbusValue.Entry(DbusValue.Text("id"), DbusValue.Text(reference.OpaqueId)));

    internal static string OpenSession(ISecretServiceTransport transport, CancellationToken ct)
    {
        using var reply = Call(transport, Root, Service, "OpenSession",
            DbusValue.Tuple(DbusValue.Text("plain"), DbusValue.Variant(DbusValue.Text(""))), "(vo)", ct);
        var values = Children(reply, 2);
        if (Text(VariantValue(values[0])) != "") throw new NativeStorageException(NativeStorageFailure.Malformed);
        string session = ObjectPath(values[1]);
        if (session == "/") throw new NativeStorageException(NativeStorageFailure.Malformed);
        return session;
    }

    internal static void RequireUnlocked(ISecretServiceTransport transport, string path, string interfaceName, CancellationToken ct)
    {
        using var reply = GetProperty(transport, path, interfaceName, "Locked", ct);
        DbusValue value = VariantValue(Children(reply, 1)[0]);
        if (value.Signature != "b" || value.Data is not bool locked)
            throw new NativeStorageException(NativeStorageFailure.Malformed);
        if (locked) throw new NativeStorageException(NativeStorageFailure.Locked);
    }

    internal static DbusValue GetProperty(ISecretServiceTransport transport, string path,
        string interfaceName, string name, CancellationToken ct) => Call(transport, path, Properties, "Get",
            DbusValue.Tuple(DbusValue.Text(interfaceName), DbusValue.Text(name)), "(v)", ct);

    internal static DbusValue Call(ISecretServiceTransport transport, string path, string interfaceName,
        string method, DbusValue parameters, string signature, CancellationToken ct)
    {
        using (parameters)
        {
            DbusValue? reply = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                reply = transport.Call(path, interfaceName, method, parameters, signature, ct);
                ct.ThrowIfCancellationRequested();
                if (reply.Signature != signature) throw new NativeStorageException(NativeStorageFailure.Malformed);
                return reply;
            }
            catch (Exception ex)
            {
                if (ex is NativeStorageException native)
                {
                    native.Step ??= method switch
                    {
                        "ReadAlias" => NativeStorageStep.ReadAlias,
                        "SearchItems" => NativeStorageStep.SearchItems,
                        "OpenSession" => NativeStorageStep.OpenSession,
                        "Get" => NativeStorageStep.GetProperty,
                        "GetSecret" => NativeStorageStep.GetSecret,
                        "SetSecret" => NativeStorageStep.SetSecret,
                        "CreateItem" => NativeStorageStep.CreateItem,
                        "Delete" => NativeStorageStep.Delete,
                        _ => null,
                    };
                }
                reply?.Dispose();
                throw;
            }
        }
    }

    internal static void CheckItemPath(string item, string collection)
    {
        // Refuse cross-collection matches rather than selecting/falling back to another destination.
        if (!item.StartsWith(collection + "/", StringComparison.Ordinal))
            throw new NativeStorageException(NativeStorageFailure.Malformed);
    }

    internal static void RefusePrompt(DbusValue value)
    {
        if (ObjectPath(value) != "/") throw new NativeStorageException(NativeStorageFailure.PromptRequired);
    }

    internal static DbusValue[] ObjectPaths(DbusValue value)
    {
        if (value.Signature != "ao" || value.Data is not DbusValue[] children || children.Length > 32)
            throw new NativeStorageException(NativeStorageFailure.Malformed);
        foreach (var child in children) _ = ObjectPath(child);
        return children;
    }

    internal static DbusValue[] Children(DbusValue value, int count)
    {
        if (value.Data is not DbusValue[] children || children.Length != count)
            throw new NativeStorageException(NativeStorageFailure.Malformed);
        return children;
    }

    internal static DbusValue VariantValue(DbusValue value) =>
        value.Signature == "v" && value.Data is DbusValue child ? child : throw new NativeStorageException(NativeStorageFailure.Malformed);

    internal static string Text(DbusValue value) =>
        value.Signature == "s" && value.Data is string text && text.Length <= 4096
            ? text : throw new NativeStorageException(NativeStorageFailure.Malformed);

    internal static string ObjectPath(DbusValue value)
    {
        if (value.Signature != "o" || value.Data is not string path || path.Length is 0 or > 4096 ||
            path[0] != '/' || (path != "/" && (path.EndsWith('/') || path.Contains("//", StringComparison.Ordinal))) ||
            path.Any(c => c != '/' && c != '_' && !char.IsAsciiLetterOrDigit(c)))
            throw new NativeStorageException(NativeStorageFailure.Malformed);
        return path;
    }

    internal static byte[] ByteArray(DbusValue value) =>
        value.Signature == "ay" && value.Data is byte[] bytes && bytes.Length <= Protection.MaxValueBytes
            ? bytes : throw new NativeStorageException(NativeStorageFailure.Malformed);
}
