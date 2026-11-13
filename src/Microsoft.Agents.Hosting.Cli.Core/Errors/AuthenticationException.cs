namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>
/// Base class for operator authentication failures that carry a process exit code. Mirrors the
/// TypeScript <c>AuthError</c>: exit <c>3</c> = not signed in or
/// interaction required with none possible, <c>2</c> = a recoverable usage/input error, <c>1</c> =
/// an environmental failure the caller cannot fix.
/// </summary>
public class AuthenticationException : AgentHostingException
{
    public AuthenticationException(string message, int exitCode = ExitCodes.Failure, string? remediation = null, int errorCode = ErrorCodes.Authentication.AuthenticationFailed)
        : base(message, exitCode, errorKind: null, remediation: remediation, errorCode: errorCode)
    {
    }

    protected AuthenticationException(string message, int exitCode, string? errorKind,
        string? remediation, Exception? innerException, int errorCode)
        : base(message, exitCode, errorKind, remediation, innerException, errorCode)
    {
    }
}

/// <summary>
/// No signed-in account is available and interactive sign-in is not possible (for example under
/// <c>--non-interactive</c>). Maps to <see cref="ExitCodes.NotSignedIn"/>.
/// </summary>
public sealed class NotSignedInException : AuthenticationException
{
    public NotSignedInException(string message, string? remediation = null)
        : base(message, ExitCodes.NotSignedIn, remediation, ErrorCodes.Authentication.NotSignedIn)
    {
    }
}

/// <summary>
/// The client-application identity is missing, a placeholder, or forbidden. Raised before any
/// interactive request is constructed when <c>AH_CLI_CLIENT_ID</c> is unconfigured (empty or the
/// all-zero sentinel), is not a valid GUID, or reuses PAC's first-party client id. It is an
/// environmental configuration failure the caller cannot fix by re-running with different arguments,
/// so it maps to <see cref="ExitCodes.Failure"/>. This is the AgentHost adaptation of PAC's
/// <c>PacOAuth.ClientAppConfig</c>, which hard-codes a product client id; the demonstrator refuses to
/// ship or borrow one.
/// </summary>
public sealed class ClientApplicationConfigurationException : AuthenticationException
{
    public ClientApplicationConfigurationException(string message, string? remediation = null)
        : base(message, ExitCodes.Failure, remediation, ErrorCodes.Authentication.ClientApplicationConfiguration)
    {
    }
}

/// <summary>
/// The persisted AgentHost auth profile store is corrupt or internally inconsistent — invalid JSON, an
/// unsupported schema version, duplicate account keys, or an active pointer that names a profile that
/// is not present. It is a typed state error with remediation and is deliberately <b>not</b> treated as
/// "signed out." Maps to <see cref="ExitCodes.Failure"/>.
/// </summary>
public sealed class AuthProfileStateException : AuthenticationException
{
    public AuthProfileStateException(string message, string? remediation = null)
        : base(message, ExitCodes.Failure, remediation, ErrorCodes.Authentication.AuthProfileState)
    {
    }
}

/// <summary>
/// Interactive consent returned an account other than the active AgentHost profile. Rejecting the
/// token prevents subsequent Graph or ARM mutations from running under an identity the operator did
/// not select through <c>msagent auth create</c>.
/// </summary>
public sealed class ActiveAccountMismatchException : AuthenticationException
{
    public ActiveAccountMismatchException()
        : base(
            "The account returned by interactive consent does not match the active AgentHost account.",
            ExitCodes.Failure,
            "Run `msagent auth create` for the intended account and cloud, then retry the command without switching accounts.",
            ErrorCodes.Authentication.ActiveAccountMismatch)
    {
    }
}

/// <summary>
/// Secure token-cache persistence is unavailable on the current platform and unprotected persistence
/// was not explicitly opted into. This is the deliberate AgentHost security adaptation of PAC's
/// <c>MsalExtensionCache</c>, which silently falls back to a plaintext file on Linux when the keyring
/// is unavailable: the demonstrator refuses that silent downgrade and requires
/// <c>AH_CLI_ALLOW_UNPROTECTED_CACHE=true</c> before any unprotected cache is used. Maps to
/// <see cref="ExitCodes.Failure"/>.
/// </summary>
public sealed class CacheProtectionUnavailableException : AuthenticationException
{
    public CacheProtectionUnavailableException(string message, string? remediation = null)
        : base(message, ExitCodes.Failure, remediation, ErrorCodes.Authentication.CacheProtectionUnavailable)
    {
    }
}

/// <summary>
/// A cached session exists but MSAL determined that interaction is required to obtain a fresh token
/// (for example an expired refresh token or a new consent/Conditional-Access requirement), while the
/// current command must not open identity UI. This is the C# surface of PAC's silent-acquisition
/// <c>MsalUiRequiredException</c> path: an ABS command requesting an ARM token silently, or a
/// non-interactive login, never opens a browser/broker — it fails with a "sign in again" remediation
/// and maps to <see cref="ExitCodes.NotSignedIn"/>.
/// </summary>
public enum InteractionRequiredResource
{
    Unspecified,
    MicrosoftGraph,
    AzureResourceManager,
}

public sealed class InteractionRequiredException : AuthenticationException
{
    public InteractionRequiredException(
        string message,
        string? remediation = null,
        InteractionRequiredResource resource = InteractionRequiredResource.Unspecified)
        : base(
            message,
            ExitCodes.NotSignedIn,
            remediation ?? "Run `msagent auth create` to sign in interactively for the intended cloud, then retry.",
            ErrorCodes.Authentication.InteractionRequired)
    {
        Resource = resource;
    }

    public InteractionRequiredResource Resource { get; }
}

/// <summary>
/// Microsoft Entra ID is throttling token requests from this client.
/// </summary>
public sealed class TokenThrottledException : AuthenticationException
{
    public TokenThrottledException(string message, TimeSpan? retryAfter = null)
        : base(
            retryAfter is null ? message : $"{message} Retry after {retryAfter.Value.TotalSeconds:F0} seconds.",
            ExitCodes.Failure,
            retryAfter is null
                ? "Wait before retrying; repeated immediate retries extend the throttling period."
                : $"Wait {retryAfter.Value.TotalSeconds:F0} seconds before retrying.",
            ErrorCodes.Authentication.TokenThrottled)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>How long the service asked the client to wait, when it said.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// A browser (non-broker) interactive sign-in produced a Microsoft corporate account (a user in the
/// MSIT tenant whose UPN ends with <c>@microsoft.com</c>). PAC blocks this in
/// <c>AuthTokenStore.AcquireTokenForPublicClientFlow</c>: Microsoft employees must authenticate through
/// the OS broker (WAM), not the system browser. The demonstrator retains that protection. It is a
/// policy failure the caller cannot fix by re-running with different arguments on a browser-only
/// platform, so it maps to
/// <see cref="ExitCodes.Failure"/>.
/// </summary>
public sealed class CorporateAccountRequiresBrokerException : AuthenticationException
{
    public CorporateAccountRequiresBrokerException(string message, string? remediation = null)
        : base(
            message,
            ExitCodes.Failure,
            remediation ?? "Sign in on a platform with the OS broker (Windows WAM) so the Microsoft corporate account flows through the broker.",
            ErrorCodes.Authentication.CorporateAccountRequiresBroker)
    {
    }
}

/// <summary>The operating-system broker could not complete a sign-in, so the caller falls back to device code.</summary>
public sealed class BrokerUnavailableException : AuthenticationException
{
    /// <summary>Creates the failure from a short, secret-free <paramref name="reason"/> naming what the broker reported.</summary>
    public BrokerUnavailableException(string reason)
        : base($"The operating-system broker could not sign you in ({reason}).", ExitCodes.Failure, "Run `msagent auth create --deviceCode` for the intended cloud to sign in without the operating-system broker.", ErrorCodes.Authentication.BrokerUnavailable)
    {
        Reason = reason;
    }

    /// <summary>What the broker reported, including its error code and correlation ID when it gave them.</summary>
    public string Reason { get; }
}

/// <summary>The operator canceled the operating-system broker's sign-in, so sign-in stops instead of falling back to device code.</summary>
public sealed class SignInCanceledException : AuthenticationException
{
    /// <summary>Creates the failure from a short, secret-free <paramref name="reason"/> naming what the broker reported.</summary>
    public SignInCanceledException(string reason)
        : base($"Sign-in was canceled ({reason}).", ExitCodes.Failure, "Run `msagent auth create` again for the intended cloud to choose an account, or `msagent auth create --deviceCode` to sign in with a code instead.", ErrorCodes.Authentication.SignInCanceled)
    {
    }
}
