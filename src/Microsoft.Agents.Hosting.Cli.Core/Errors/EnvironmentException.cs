using Microsoft.Agents.Hosting.Cli.Core.Environments;

namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>
/// The endpoint supplied for a Power Platform environment is not trusted: it is not absolute HTTPS,
/// or its host is not covered by the configured <see cref="PowerPlatformCloudMap"/> host-suffix
/// allowlist. The frontend fails closed <b>before</b> attaching an authorization header because HTTPS
/// alone cannot distinguish a Power Platform host from an attacker-controlled one. This is an
/// environmental/trust failure the caller cannot fix by re-running with different arguments, so it
/// maps to <see cref="ExitCodes.Failure"/>.
/// </summary>
public sealed class EndpointTrustException : AgentHostingException
{
    public EndpointTrustException(string message, string? remediation = null)
        : base(message, ExitCodes.Failure, errorKind: "endpoint-untrusted", remediation: remediation, errorCode: ErrorCodes.Environment.EndpointTrust)
    {
    }
}

/// <summary>
/// The active account was retargeted at a different Power Platform cloud after this process built its
/// clients, so the services in memory still address the cloud it started in. Maps to
/// <see cref="ExitCodes.Usage"/> — re-running resolves it.
/// </summary>
public sealed class CloudRetargetedException : AgentHostingException
{
    public CloudRetargetedException(string message, string? remediation = null)
        : base(message, ExitCodes.Usage, errorKind: "cloud-retargeted", remediation: remediation, errorCode: ErrorCodes.Environment.CloudRetargeted)
    {
    }
}

/// <summary>
/// A command needs an active environment and none is selected. Missing context the caller corrects in
/// its own inputs, so it maps to <see cref="ExitCodes.Usage"/>.
/// </summary>
public sealed class EnvironmentNotSelectedException : AgentHostingException
{
    public EnvironmentNotSelectedException(string message, string? remediation = null)
        : base(
            message,
            ExitCodes.Usage,
            errorKind: "environment-not-selected",
            remediation: remediation ?? "Run `msagent env list` to see the environments available to the signed-in account, then `msagent env select <environment-id>`.", errorCode: ErrorCodes.Environment.EnvironmentNotSelected)
    {
    }
}

/// <summary>
/// The requested environment id is not present in the inventory available to the signed-in operator.
/// Maps to <see cref="ExitCodes.Usage"/>.
/// </summary>
public sealed class EnvironmentNotFoundException : AgentHostingException
{
    public EnvironmentNotFoundException(string message, string? remediation = null)
        : base(
            message,
            ExitCodes.Usage,
            errorKind: "environment-not-found",
            remediation: remediation ?? "Run `msagent env list` to see the environments available to the signed-in account.", errorCode: ErrorCodes.Environment.EnvironmentNotFound)
    {
    }
}

/// <summary>
/// The environment's tenant does not match the active auth profile's tenant. Selecting it would leave
/// the CLI holding an account from one tenant and a target in another, so it is rejected. Maps to
/// <see cref="ExitCodes.Usage"/> — the caller corrects the selection or signs in to the right tenant.
/// </summary>
public sealed class EnvironmentTenantMismatchException : AgentHostingException
{
    public EnvironmentTenantMismatchException(string message, string? remediation = null)
        : base(message, ExitCodes.Usage, errorKind: "tenant-mismatch", remediation: remediation, errorCode: ErrorCodes.Environment.EnvironmentTenantMismatch)
    {
    }
}

/// <summary>
/// The persisted environment selection is corrupt, unreadable, or unwritable. It is deliberately not
/// treated as "no environment selected": silently forgetting a selection would send the next command
/// at a target the operator did not choose. Maps to <see cref="ExitCodes.Failure"/>.
/// </summary>
public sealed class AgentHostingStateException : AgentHostingException
{
    public AgentHostingStateException(string message, string? remediation = null)
        : base(message, ExitCodes.Failure, errorKind: "state-unusable", remediation: remediation, errorCode: ErrorCodes.Environment.AgentHostingState)
    {
    }
}

/// <summary>Category of an access-token rejection, whether read from token claims or from acquisition metadata.</summary>
public enum TokenClaimFailure
{
    /// <summary>The token is not a readable JWT (exit 1).</summary>
    Malformed,

    /// <summary><c>aud</c> is not the selected environment's Power Platform API audience (exit 2).</summary>
    WrongAudience,

    /// <summary><c>appid</c> is not the AgentHostCLI application (exit 2).</summary>
    WrongApplication,

    /// <summary>The token belongs to a tenant other than the one the operation targets (exit 2).</summary>
    WrongTenant,

    /// <summary><c>scp</c> does not carry the delegated permission the operation needs (exit 1).</summary>
    MissingPermission,
}

/// <summary>
/// Token claim exception
/// </summary>
public sealed class TokenClaimException : AgentHostingException
{
    public TokenClaimException(string message, TokenClaimFailure failure, string? remediation = null)
        : base(message, ToExitCode(failure), ToErrorKind(failure), remediation, errorCode: ToErrorCode(failure))
    {
        Failure = failure;
    }

    /// <summary>The classified claim rejection.</summary>
    public TokenClaimFailure Failure { get; }

    private static int ToErrorCode(TokenClaimFailure failure) => failure switch
    {
        TokenClaimFailure.Malformed => ErrorCodes.Authentication.TokenClaimMalformed,
        TokenClaimFailure.WrongAudience => ErrorCodes.Authentication.TokenClaimWrongAudience,
        TokenClaimFailure.WrongApplication => ErrorCodes.Authentication.TokenClaimWrongApplication,
        TokenClaimFailure.WrongTenant => ErrorCodes.Authentication.TokenClaimWrongTenant,
        TokenClaimFailure.MissingPermission => ErrorCodes.Authentication.TokenClaimMissingPermission,
        _ => ErrorCodes.Authentication.TokenClaimMissingPermission,
    };

    private static int ToExitCode(TokenClaimFailure failure) => failure switch
    {
        TokenClaimFailure.WrongAudience or
        TokenClaimFailure.WrongApplication or
        TokenClaimFailure.WrongTenant => ExitCodes.Usage,
        _ => ExitCodes.Failure,
    };

    private static string ToErrorKind(TokenClaimFailure failure) => failure switch
    {
        TokenClaimFailure.Malformed => "token-malformed",
        TokenClaimFailure.WrongAudience => "wrong-audience",
        TokenClaimFailure.WrongApplication => "wrong-application",
        TokenClaimFailure.WrongTenant => "tenant-mismatch",
        _ => "permission-denied",
    };
}
