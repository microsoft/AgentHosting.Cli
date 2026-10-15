using System.Runtime.InteropServices;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>
/// Minimal Win32 interop to resolve the parent window handle MSAL attaches interactive/broker (WAM)
/// dialogs to on Windows. It follows MSAL's console-app guidance: the console window is walked up
/// through its parent and owner chains so the dialog parents to the hosting terminal window rather
/// than a hidden pseudoconsole. It is invoked only on Windows, guarded by
/// <see cref="Core.Ports.IPlatformInfo.IsWindows"/> at the call site.
/// </summary>
internal static class NativeMethods
{
    // GA_ROOTOWNER walks both parent and owner chains to reach the visible host for pseudoconsoles.
    private const uint GA_ROOTOWNER = 3;

    // Classic DllImport (not LibraryImport) keeps the project free of AllowUnsafeBlocks; these two
    // blittable P/Invokes do not warrant enabling unsafe code for the whole assembly.
#pragma warning disable SYSLIB1054 // Use LibraryImportAttribute instead of DllImportAttribute
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
#pragma warning restore SYSLIB1054

    /// <summary>Returns the root-owner window handle of the current console/terminal, for MSAL parenting.</summary>
    public static IntPtr GetConsoleOrTerminalWindow()
    {
        var consoleHandle = GetConsoleWindow();
        return GetAncestor(consoleHandle, GA_ROOTOWNER);
    }
}
