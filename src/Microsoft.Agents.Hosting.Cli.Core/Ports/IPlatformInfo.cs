namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>
/// Reports the current operating-system family. Seamed so cache-protection selection and public-client
/// configuration (which are platform-specific in PAC via <c>RuntimeInformation.IsOSPlatform</c>) can be
/// unit-tested with a fake platform instead of the real host OS.
/// </summary>
public interface IPlatformInfo
{
    /// <summary>Whether the current OS is Windows.</summary>
    bool IsWindows { get; }

    /// <summary>Whether the current OS is macOS.</summary>
    bool IsMacOs { get; }

    /// <summary>Whether the current OS is Linux.</summary>
    bool IsLinux { get; }
}
