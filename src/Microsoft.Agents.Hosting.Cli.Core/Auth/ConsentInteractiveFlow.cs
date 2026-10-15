using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

public enum ConsentInteractiveFlow
{
    Broker,

    BrowserInteractive,

    DeviceCode,
}

public static class ConsentInteractiveFlowSelector
{
    public static bool UseBroker(
        AuthProfile? activeProfile,
        bool isAgentHostClient,
        bool interactiveBrokerSupported) =>
        AuthProfileSelection.IsBrokerProfile(activeProfile)
        && isAgentHostClient
        && interactiveBrokerSupported;

    public static ConsentInteractiveFlow Select(
        AuthProfile? activeProfile,
        bool useBroker,
        string? redirectUri)
    {
        if (useBroker)
        {
            return ConsentInteractiveFlow.Broker;
        }

        if (AuthProfileSelection.IsDeviceCodeProfile(activeProfile))
        {
            return ConsentInteractiveFlow.DeviceCode;
        }

        return AgentHostClientAppConfiguration.IsLoopbackRedirect(redirectUri)
            ? ConsentInteractiveFlow.BrowserInteractive
            : ConsentInteractiveFlow.DeviceCode;
    }
}
