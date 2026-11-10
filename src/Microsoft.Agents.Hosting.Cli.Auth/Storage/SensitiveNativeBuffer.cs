using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;

internal sealed class SensitiveNativeBuffer : SafeHandleZeroOrMinusOneIsInvalid
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ReleaseNativeOwner(IntPtr state);

    private static readonly ReleaseNativeOwner ReleaseOwner = state =>
    {
        var root = GCHandle.FromIntPtr(state);
        ((SensitiveNativeBuffer)root.Target!).Dispose();
        root.Free();
    };

    internal static IntPtr CreateNativeOwner(byte[] data,
        Func<IntPtr, nuint, ReleaseNativeOwner, IntPtr, IntPtr> create)
    {
        var buffer = new SensitiveNativeBuffer(data);
        GCHandle root = default;
        bool transferred = false;
        try
        {
            root = GCHandle.Alloc(buffer);
            IntPtr owner = create(buffer.DangerousGetHandle(), (nuint)data.Length, ReleaseOwner, GCHandle.ToIntPtr(root));
            if (owner == IntPtr.Zero) throw new NativeStorageException(NativeStorageFailure.Unavailable);
            transferred = true;
            return owner;
        }
        finally
        {
            if (!transferred)
            {
                if (root.IsAllocated) root.Free();
                buffer.Dispose();
            }
        }
    }

    private static readonly byte[] Zeros = new byte[4096];
    internal int Length { get; }

    internal SensitiveNativeBuffer(byte[] data) : base(true)
    {
        Length = data.Length;
        SetHandle(Marshal.AllocHGlobal(Math.Max(1, data.Length)));
        try { Marshal.Copy(data, 0, handle, data.Length); }
        catch { Dispose(); throw; }
    }

    internal static void Zero(IntPtr pointer, int length)
    {
        for (int offset = 0; offset < length;)
        {
            int count = Math.Min(Zeros.Length, length - offset);
            Marshal.Copy(Zeros, 0, IntPtr.Add(pointer, offset), count);
            offset += count;
        }
    }

    protected override bool ReleaseHandle()
    {
        Zero(handle, Length);
        Marshal.FreeHGlobal(handle);
        return true;
    }
}
