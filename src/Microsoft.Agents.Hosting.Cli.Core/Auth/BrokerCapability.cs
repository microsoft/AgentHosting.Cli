using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>Decides on which platforms the login, silent, and consent paths use the operating-system broker.</summary>
public static class BrokerCapability
{
    /// <summary>Whether MSAL has a broker for <paramref name="platform"/> (WAM on Windows, Company Portal on macOS, the Microsoft Identity Broker on Linux), regardless of what this machine has installed.</summary>
    public static bool IsSupported(IPlatformInfo platform) => platform.IsWindows || platform.IsMacOs || platform.IsLinux;
}
