namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>An internal authentication-context or token-provider wiring invariant failed, not a token-claim check.</summary>
public sealed class AuthenticationContextException : AgentHostingException
{
    public AuthenticationContextException(string reason)
        : base(
            $"The operation cannot continue because its authentication context is missing or inconsistent. {reason}",
            ExitCodes.Failure,
            "authentication-context-invalid",
            "Report the command, CLI version, error code, and redacted message to the CLI issue tracker. " +
            "Do not include tokens or credentials. Signing in again or clearing profiles is not the recommended remedy. " +
            "If embedding Core, supply the captured authentication context and configure the required token provider.",
            errorCode: ErrorCodes.General.AuthenticationContextInvalid)
    {
    }
}
