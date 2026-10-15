using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// The interactive sign-in flow <c>msagent auth login</c> uses. It adapts PAC's public-client flow split
/// (<c>OperatingSystemAuthFlow</c>, <c>InteractiveAuthFlow</c>, and <c>DeviceCodeAuthFlow</c> in
/// <c>bolt.authentication.store.AuthenticationFlow</c>) to the scoped demonstrator surface.
/// </summary>
public enum AuthLoginFlow
{
    /// <summary>OS-broker sign-in (WAM on Windows, Company Portal on macOS, the identity broker on Linux), silently as an explicitly requested account or otherwise through the broker's account picker.</summary>
    Broker,

    /// <summary>System-browser interactive public-client acquisition (PAC <c>InteractiveAuthFlow</c>).</summary>
    BrowserInteractive,

    /// <summary>Device-code acquisition, with the verification message written to stderr.</summary>
    DeviceCode,
}

/// <summary>Selects the <see cref="AuthLoginFlow"/>: device code when it is requested or on an OS without a broker, and the broker otherwise.</summary>
public static class AuthLoginFlowSelector
{
    /// <summary>Selects the login flow for <paramref name="request"/> on <paramref name="platform"/> without inspecting the machine, so MSAL alone decides what its broker can do.</summary>
    public static AuthLoginFlow Select(AuthLoginRequest request, IPlatformInfo platform)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(platform);

        return !request.DeviceCode && BrokerCapability.IsSupported(platform) ? AuthLoginFlow.Broker : AuthLoginFlow.DeviceCode;
    }
}
