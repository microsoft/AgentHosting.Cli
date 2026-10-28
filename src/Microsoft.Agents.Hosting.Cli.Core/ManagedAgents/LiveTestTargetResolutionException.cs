using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>A safe, usage-class failure while resolving a live-test destination.</summary>
public sealed class LiveTestTargetResolutionException : AgentHostingException
{
    private LiveTestTargetResolutionException(int errorCode, string message, string errorKind, string remediation)
        : base(message, ExitCodes.Usage, errorKind, remediation, errorCode: errorCode)
    {
    }

    /// <summary>Creates an invalid-input target failure.</summary>
    public static LiveTestTargetResolutionException InvalidInput(string message, string remediation) =>
        new(ErrorCodes.Environment.LiveTestInvalidInput, message, "invalid-input", remediation);

    /// <summary>Creates an invalid-configuration target failure.</summary>
    public static LiveTestTargetResolutionException InvalidConfiguration(string message, string remediation) =>
        new(ErrorCodes.Environment.LiveTestInvalidConfiguration, message, "invalid-configuration", remediation);

    /// <summary>Creates an unsupported-target failure.</summary>
    public static LiveTestTargetResolutionException UnsupportedTarget(string message, string remediation) =>
        new(ErrorCodes.Environment.LiveTestUnsupportedTarget, message, "unsupported-target", remediation);
}
