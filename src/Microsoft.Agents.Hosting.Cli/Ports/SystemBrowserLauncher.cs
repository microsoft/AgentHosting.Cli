using System.Diagnostics;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Ports;

/// <summary>
/// Opens a URL with the operating system's default handler.
///
/// <see cref="ProcessStartInfo.UseShellExecute"/> is required: without it .NET tries to execute the
/// URL as a file, which fails on every platform.
/// </summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    /// <inheritdoc />
    public void Launch(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        using var process = Process.Start(new ProcessStartInfo { FileName = url.ToString(), UseShellExecute = true });
    }
}
