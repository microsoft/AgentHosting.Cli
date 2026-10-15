using System.Runtime.InteropServices;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// Concrete <see cref="IPlatformInfo"/> backed by <see cref="RuntimeInformation"/>, mirroring PAC's
/// <c>RuntimeInformation.IsOSPlatform(...)</c> checks. Tests inject a fake instead so cache and
/// public-client configuration are exercised for every platform without depending on the host OS.
/// </summary>
public sealed class RuntimePlatformInfo : IPlatformInfo
{
    /// <inheritdoc />
    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <inheritdoc />
    public bool IsMacOs => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <inheritdoc />
    public bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
}
