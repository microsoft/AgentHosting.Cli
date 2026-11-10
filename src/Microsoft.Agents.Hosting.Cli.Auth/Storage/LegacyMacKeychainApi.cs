using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;

/// <summary>
/// Ordinary legacy generic-password APIs only. The caller supplies an established process policy.
/// Native calls are synchronous/noncancellable: callers check cancellation before/after, never abandon
/// a background write. Native/library/OS copies cannot all be guaranteed erased by this adapter.
/// </summary>
internal sealed class LegacyMacKeychainApi : IMacOsKeychainApi
{
    public int SetInteractionAllowed(bool allowed) => MacNative.SecKeychainSetUserInteractionAllowed(allowed ? (byte)1 : (byte)0);
    public int GetInteractionAllowed(out bool allowed)
    {
        int status = MacNative.SecKeychainGetUserInteractionAllowed(out byte value);
        allowed = value != 0;
        return status;
    }

    public int CopyDefaultKeychain(out IMacOwnedHandle? keychain)
    {
        int status = MacNative.SecKeychainCopyDefault(out IntPtr pointer);
        keychain = pointer == IntPtr.Zero ? null : new MacHandle(pointer);
        return status;
    }

    public int GetStatus(IMacOwnedHandle keychain, out uint status) =>
        MacNative.SecKeychainGetStatus(Pointer(keychain), out status);

    public int Find(IMacOwnedHandle keychain, string service, string account, bool readData,
        out byte[]? data, out IMacOwnedHandle? item)
    {
        data = null;
        item = null;
        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
        byte[] accountBytes = Encoding.UTF8.GetBytes(account);
        IntPtr secret = IntPtr.Zero;
        IntPtr itemPointer = IntPtr.Zero;
        uint length = 0;
        try
        {
            int status = readData
                ? MacNative.SecKeychainFindGenericPassword(Pointer(keychain), (uint)serviceBytes.Length, serviceBytes,
                    (uint)accountBytes.Length, accountBytes, out length, out secret, out itemPointer)
                : MacNative.FindItem(Pointer(keychain), (uint)serviceBytes.Length, serviceBytes,
                    (uint)accountBytes.Length, accountBytes, IntPtr.Zero, IntPtr.Zero, out itemPointer);
            if (itemPointer != IntPtr.Zero)
            {
                item = new MacHandle(itemPointer);
                itemPointer = IntPtr.Zero;
            }

            if (status == 0 && readData)
            {
                if (length > Protection.MaxValueBytes || (length != 0 && secret == IntPtr.Zero))
                    throw new NativeStorageException(NativeStorageFailure.Malformed);
                data = new byte[(int)length];
                if (length != 0) Marshal.Copy(secret, data, 0, (int)length);
            }

            return status;
        }
        catch
        {
            item?.Dispose();
            item = null;
            Protection.Wipe(data);
            data = null;
            throw;
        }
        finally
        {
            if (itemPointer != IntPtr.Zero) MacNative.CFRelease(itemPointer);
            if (secret != IntPtr.Zero)
            {
                // Bound clearing even if an invalid provider length was returned.
                if (length <= Protection.MaxValueBytes) SensitiveNativeBuffer.Zero(secret, (int)length);
                _ = MacNative.SecKeychainItemFreeContent(IntPtr.Zero, secret);
            }
        }
    }

    public int Add(IMacOwnedHandle keychain, string service, string account, byte[] data)
    {
        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
        byte[] accountBytes = Encoding.UTF8.GetBytes(account);
        using var buffer = new SensitiveNativeBuffer(data);
        return MacNative.SecKeychainAddGenericPassword(Pointer(keychain), (uint)serviceBytes.Length, serviceBytes,
            (uint)accountBytes.Length, accountBytes, (uint)data.Length, buffer.DangerousGetHandle(), IntPtr.Zero);
    }

    public int Modify(IMacOwnedHandle item, byte[] data)
    {
        using var buffer = new SensitiveNativeBuffer(data);
        return MacNative.SecKeychainItemModifyContent(Pointer(item), IntPtr.Zero, (uint)data.Length, buffer.DangerousGetHandle());
    }

    public int Delete(IMacOwnedHandle item) => MacNative.SecKeychainItemDelete(Pointer(item));
    private static IntPtr Pointer(IMacOwnedHandle handle) => ((MacHandle)handle).DangerousGetHandle();

    private sealed class MacHandle : SafeHandleZeroOrMinusOneIsInvalid, IMacOwnedHandle
    {
        internal MacHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() { MacNative.CFRelease(handle); return true; }
    }

#pragma warning disable SYSLIB1054 // Blittable legacy APIs; no unsafe project setting or new interop dependency.
    private static class MacNative
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        [DllImport(Security)] internal static extern int SecKeychainSetUserInteractionAllowed(byte state);
        [DllImport(Security)] internal static extern int SecKeychainGetUserInteractionAllowed(out byte state);
        [DllImport(Security)] internal static extern int SecKeychainCopyDefault(out IntPtr keychain);
        [DllImport(Security)] internal static extern int SecKeychainGetStatus(IntPtr keychain, out uint status);
        [DllImport(Security)] internal static extern int SecKeychainFindGenericPassword(IntPtr keychain,
            uint serviceLength, byte[] service, uint accountLength, byte[] account,
            out uint passwordLength, out IntPtr password, out IntPtr item);
        [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
        internal static extern int FindItem(IntPtr keychain, uint serviceLength, byte[] service,
            uint accountLength, byte[] account, IntPtr passwordLength, IntPtr password, out IntPtr item);
        [DllImport(Security)] internal static extern int SecKeychainAddGenericPassword(IntPtr keychain,
            uint serviceLength, byte[] service, uint accountLength, byte[] account, uint length, IntPtr password, IntPtr item);
        [DllImport(Security)] internal static extern int SecKeychainItemModifyContent(IntPtr item, IntPtr attributes, uint length, IntPtr data);
        [DllImport(Security)] internal static extern int SecKeychainItemDelete(IntPtr item);
        [DllImport(Security)] internal static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
        [DllImport(CoreFoundation)] internal static extern void CFRelease(IntPtr value);
    }
#pragma warning restore SYSLIB1054
}
