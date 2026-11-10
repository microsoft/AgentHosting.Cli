using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;

/// <summary>
/// Private local session-bus connection. No libsecret convenience methods, auto-start,
/// interactive authorization, Unlock, or Prompt. Session lifetime is tied to this connection.
/// GCancellable/deadlines bound synchronous IPC, not server-side rollback of an issued mutation.
/// GLib, D-Bus, and service-internal secret copies cannot all be erased by managed callers.
/// </summary>
internal sealed class GioSecretServiceTransport : ISecretServiceTransport
{
    private const int DeadlineMilliseconds = 15000;
    private const int CallMilliseconds = 5000;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly CancellationTokenSource _deadline;
    private CancellationTokenRegistration _registration;
    private IntPtr _cancellable;
    private IntPtr _connection;
    private bool _disposed;

    internal GioSecretServiceTransport(string resolvedBusAddress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _deadline.CancelAfter(DeadlineMilliseconds);
        try
        {
            _cancellable = Gio.g_cancellable_new();
            RequirePointer(_cancellable);
            _registration = _deadline.Token.Register(() => Gio.g_cancellable_cancel(_cancellable));
            IntPtr error = IntPtr.Zero;
            _connection = Gio.g_dbus_connection_new_for_address_sync(Utf8(resolvedBusAddress), 1 | 8, IntPtr.Zero, _cancellable, ref error);
            CheckError(error, ct);
            RequirePointer(_connection);
            Gio.g_dbus_connection_set_exit_on_close(_connection, 0);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            if (ex is NativeStorageException native) native.Step ??= NativeStorageStep.Connect;
            Dispose();
            throw;
        }
    }

    public DbusValue Call(string path, string interfaceName, string method, DbusValue parameters,
        string replySignature, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (method is not ("ReadAlias" or "SearchItems" or "OpenSession" or "Get" or "GetSecret" or
            "SetSecret" or "CreateItem" or "Delete"))
            throw new NativeStorageException(NativeStorageFailure.Unavailable);
        int remaining = DeadlineMilliseconds - (int)Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        if (remaining <= 0 || _deadline.IsCancellationRequested)
            throw new NativeStorageException(NativeStorageFailure.Unavailable);

        using var request = Build(parameters);
        using var expectedType = new VariantType(replySignature);
        IntPtr error = IntPtr.Zero;
        IntPtr pointer = Gio.g_dbus_connection_call_sync(_connection, Utf8("org.freedesktop.secrets"), Utf8(path),
            Utf8(interfaceName), Utf8(method), request.DangerousGetHandle(), expectedType.DangerousGetHandle(),
            1, Math.Min(CallMilliseconds, remaining), _cancellable, ref error);
        using var reply = new VariantHandle(pointer, floating: false);
        CheckError(error, ct);
        RequirePointer(pointer);
        ct.ThrowIfCancellationRequested();
        if (Gio.g_variant_get_size(pointer) > 32u * 1024u * 1024u)
            throw new NativeStorageException(NativeStorageFailure.Malformed);
        int nodes = 0;
        DbusValue result = Parse(pointer, 0, ref nodes);
        try
        {
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Shutdown is bounded, releases the private connection/session, and cannot replace
        // the primary cancellation or operation outcome with a transport-cleanup error.
        if (_connection != IntPtr.Zero)
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            IntPtr cleanup = Gio.g_cancellable_new();
            if (cleanup != IntPtr.Zero)
            {
                using (cleanupTimeout.Token.Register(() => Gio.g_cancellable_cancel(cleanup)))
                {
                    IntPtr error = IntPtr.Zero;
                    _ = Gio.g_dbus_connection_close_sync(_connection, cleanup, ref error);
                    if (error != IntPtr.Zero) Gio.g_error_free(error);
                }
                Gio.g_object_unref(cleanup);
            }
            Gio.g_object_unref(_connection);
            _connection = IntPtr.Zero;
        }
        _registration.Dispose();
        _deadline.Dispose();
        if (_cancellable != IntPtr.Zero)
        {
            Gio.g_object_unref(_cancellable);
            _cancellable = IntPtr.Zero;
        }
    }

    private static VariantHandle Build(DbusValue value)
    {
        IntPtr result;
        switch (value.Signature)
        {
            case "s": result = Gio.g_variant_new_string(Utf8((string)value.Data)); break;
            case "o": result = Gio.g_variant_new_object_path(Utf8((string)value.Data)); break;
            case "b": result = Gio.g_variant_new_boolean((bool)value.Data ? 1 : 0); break;
            case "ay": return BuildBytes((byte[])value.Data);
            case "v":
                using (var child = Build((DbusValue)value.Data))
                    result = Gio.g_variant_new_variant(child.DangerousGetHandle());
                break;
            default:
                var children = (DbusValue[])value.Data;
                var handles = new List<VariantHandle>(children.Length);
                try
                {
                    foreach (var child in children) handles.Add(Build(child));
                    IntPtr[] pointers = handles.Select(h => h.DangerousGetHandle()).ToArray();
                    if (value.Signature.StartsWith('(')) result = Gio.g_variant_new_tuple(pointers, (nuint)pointers.Length);
                    else if (value.Signature.StartsWith('{')) result = Gio.g_variant_new_dict_entry(pointers[0], pointers[1]);
                    else
                    {
                        using var elementType = new VariantType(value.Signature[1..]);
                        result = Gio.g_variant_new_array(elementType.DangerousGetHandle(), pointers, (nuint)pointers.Length);
                    }
                }
                finally { foreach (var handle in handles) handle.Dispose(); }
                break;
        }

        RequirePointer(result);
        return new VariantHandle(result, floating: true);
    }

    private static VariantHandle BuildBytes(byte[] bytes)
    {
        IntPtr nativeBytes = SensitiveNativeBuffer.CreateNativeOwner(bytes, Gio.g_bytes_new_with_free_func);
        try
        {
            using var type = new VariantType("ay");
            IntPtr value = Gio.g_variant_new_from_bytes(type.DangerousGetHandle(), nativeBytes, 1);
            RequirePointer(value);
            return new VariantHandle(value, floating: true);
        }
        finally { Gio.g_bytes_unref(nativeBytes); }
    }

    private static DbusValue Parse(IntPtr value, int depth, ref int nodes)
    {
        if (depth > 8 || ++nodes > 128) throw new NativeStorageException(NativeStorageFailure.Malformed);
        nuint typeLength = Gio.g_variant_type_get_string_length(Gio.g_variant_get_type(value));
        if (typeLength is 0 or > 255) throw new NativeStorageException(NativeStorageFailure.Malformed);
        string signature = Marshal.PtrToStringUTF8(Gio.g_variant_get_type_string(value), (int)typeLength)!;
        if (signature is "s" or "o")
        {
            IntPtr text = Gio.g_variant_get_string(value, out nuint length);
            if (length > 4096) throw new NativeStorageException(NativeStorageFailure.Malformed);
            RequirePointer(text);
            return new DbusValue(signature, Marshal.PtrToStringUTF8(text, (int)length)!);
        }
        if (signature == "b") return DbusValue.Boolean(Gio.g_variant_get_boolean(value) != 0);
        if (signature == "ay")
        {
            IntPtr bytes = Gio.g_variant_get_fixed_array(value, out nuint count, 1);
            if (count > Protection.MaxValueBytes || (count != 0 && bytes == IntPtr.Zero))
                throw new NativeStorageException(NativeStorageFailure.Malformed);
            var copy = new byte[(int)count];
            if (count != 0) Marshal.Copy(bytes, copy, 0, (int)count);
            return DbusValue.Bytes(copy);
        }
        if (signature == "v")
        {
            using var child = new VariantHandle(Gio.g_variant_get_variant(value), floating: false);
            RequirePointer(child.DangerousGetHandle());
            return DbusValue.Variant(Parse(child.DangerousGetHandle(), depth + 1, ref nodes));
        }
        if (!signature.StartsWith('a') && !signature.StartsWith('(') && !signature.StartsWith('{'))
            throw new NativeStorageException(NativeStorageFailure.Malformed);
        nuint countChildren = Gio.g_variant_n_children(value);
        if (countChildren > 32) throw new NativeStorageException(NativeStorageFailure.Malformed);
        var children = new List<DbusValue>((int)countChildren);
        try
        {
            for (nuint i = 0; i < countChildren; i++)
            {
                using var child = new VariantHandle(Gio.g_variant_get_child_value(value, i), floating: false);
                RequirePointer(child.DangerousGetHandle());
                children.Add(Parse(child.DangerousGetHandle(), depth + 1, ref nodes));
            }
            return new DbusValue(signature, children.ToArray());
        }
        catch
        {
            foreach (var child in children) child.Dispose();
            throw;
        }
    }

    private static void RequirePointer(IntPtr value)
    {
        if (value == IntPtr.Zero) throw new NativeStorageException(NativeStorageFailure.Unavailable);
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");

    private static void CheckError(IntPtr error, CancellationToken ct)
    {
        if (error == IntPtr.Zero) { ct.ThrowIfCancellationRequested(); return; }
        IntPtr name = IntPtr.Zero;
        try
        {
            ct.ThrowIfCancellationRequested();
            name = Gio.g_dbus_error_get_remote_error(error);
            string? remote = null;
            if (name != IntPtr.Zero)
            {
                int length = 0;
                while (length <= 512 && Marshal.ReadByte(name, length) != 0) length++;
                if (length > 512) throw new NativeStorageException(NativeStorageFailure.Malformed);
                remote = Marshal.PtrToStringUTF8(name, length);
            }
            throw new NativeStorageException(ClassifyRemoteError(remote));
        }
        finally
        {
            if (name != IntPtr.Zero) Gio.g_free(name);
            Gio.g_error_free(error);
        }
    }

    internal static NativeStorageFailure ClassifyRemoteError(string? remote) => remote switch
    {
        "org.freedesktop.Secret.Error.IsLocked" => NativeStorageFailure.Locked,
        "org.freedesktop.Secret.Error.NoSuchObject" or "org.freedesktop.DBus.Error.UnknownObject" => NativeStorageFailure.NotFound,
        "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner" => NativeStorageFailure.Unavailable,
        _ => NativeStorageFailure.Unknown,
    };

    private sealed class VariantType : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal VariantType(string signature) : base(true)
        {
            SetHandle(Gio.g_variant_type_new(Utf8(signature)));
            RequirePointer(handle);
        }
        protected override bool ReleaseHandle() { Gio.g_variant_type_free(handle); return true; }
    }

    private sealed class VariantHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal VariantHandle(IntPtr pointer, bool floating) : base(true) =>
            SetHandle(pointer == IntPtr.Zero || !floating ? pointer : Gio.g_variant_ref_sink(pointer));
        protected override bool ReleaseHandle() { Gio.g_variant_unref(handle); return true; }
    }

#pragma warning disable SYSLIB1054 // Fixed, nonvarargs C ABI; pointer-sized gsize, int gboolean, rooted callback.
    private static class Gio
    {
        private const string Io = "libgio-2.0.so.0";
        private const string Glib = "libglib-2.0.so.0";
        private const string Object = "libgobject-2.0.so.0";
        [DllImport(Io, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_cancellable_new();
        [DllImport(Io, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_cancellable_cancel(IntPtr value);
        [DllImport(Io, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_dbus_connection_new_for_address_sync(byte[] address, int flags, IntPtr observer, IntPtr cancellation, ref IntPtr error);
        [DllImport(Io, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_dbus_connection_set_exit_on_close(IntPtr connection, int value);
        [DllImport(Io, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr g_dbus_connection_call_sync(IntPtr connection,
            byte[] busName, byte[] path, byte[] interfaceName, byte[] method,
            IntPtr parameters, IntPtr expected, int flags, int timeout, IntPtr cancellation, ref IntPtr error);
        [DllImport(Io, CallingConvention = CallingConvention.Cdecl)] internal static extern int g_dbus_connection_close_sync(IntPtr connection, IntPtr cancellation, ref IntPtr error);
        [DllImport(Io, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_dbus_error_get_remote_error(IntPtr error);
        [DllImport(Object, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_object_unref(IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_free(IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_error_free(IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_variant_type_new(byte[] signature);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_variant_type_free(IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_variant_new_string(byte[] value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_variant_new_object_path(byte[] value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_variant_new_boolean(int value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_variant_new_tuple(IntPtr[] values, nuint count);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_variant_new_array(IntPtr type, IntPtr[] values, nuint count);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_variant_new_dict_entry(IntPtr key, IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_variant_new_variant(IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_variant_new_from_bytes(IntPtr type, IntPtr bytes, int trusted);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_bytes_new_with_free_func(IntPtr value, nuint count, SensitiveNativeBuffer.ReleaseNativeOwner release, IntPtr state);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_bytes_unref(IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_variant_ref_sink(IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_variant_unref(IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern nuint g_variant_get_size(IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_variant_get_type(IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern nuint g_variant_type_get_string_length(IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_variant_get_type_string(IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_variant_get_string(IntPtr value, out nuint length);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern int g_variant_get_boolean(IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_variant_get_fixed_array(IntPtr value, out nuint count, nuint elementSize);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_variant_get_variant(IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern nuint g_variant_n_children(IntPtr value);
        [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr g_variant_get_child_value(IntPtr value, nuint index);
    }
#pragma warning restore SYSLIB1054
}
