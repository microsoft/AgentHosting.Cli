namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>
/// The frozen process-exit taxonomy shared by every AgentHost CLI failure. It mirrors the
/// 3/2/1 taxonomy carried by the TypeScript implementation (see <c>Errors/AuthError.ts</c> and
/// <c>Errors/ProvisioningError.ts</c>) so both implementations map the same condition to the same
/// exit code.
/// </summary>
public static class ExitCodes
{
    /// <summary>Success, including preview and an unchanged (no-op) apply.</summary>
    public const int Success = 0;

    /// <summary>
    /// Environmental, authorization, policy, provider, quota, transport, or internal failure that a
    /// caller cannot fix by re-running with different arguments.
    /// </summary>
    public const int Failure = 1;

    /// <summary>
    /// Usage, missing context, ambiguity, wrong tenant, unsupported mode, or a blocking conflict —
    /// conditions the caller can correct in its own inputs.
    /// </summary>
    public const int Usage = 2;

    /// <summary>Not signed in, or interaction is required while interaction is unavailable.</summary>
    public const int NotSignedIn = 3;
}

/// <summary>
/// Base class for every typed AgentHost CLI failure. Carries the process <see cref="ExitCode"/> plus
/// the optional, secret-free, agent-loop-actionable <see cref="ErrorKind"/> and
/// <see cref="Remediation"/> that the JSON failure envelope surfaces. Messages, error kinds, and
/// remediation are secret-free by construction — they name context identifiers and operations only,
/// never tokens, secrets, or request bodies.
/// </summary>
public abstract class AgentHostingException : Exception
{
    protected AgentHostingException(
        string message,
        int exitCode,
        string? errorKind = null,
        string? remediation = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ExitCode = exitCode;
        ErrorKind = errorKind;
        Remediation = remediation;
    }

    /// <summary>The process exit code this failure maps to (see <see cref="ExitCodes"/>).</summary>
    public int ExitCode { get; }

    /// <summary>
    /// Optional stable, kebab-case error category emitted as <c>errorKind</c> in the JSON failure
    /// envelope. <see langword="null"/> for plain usage/auth failures that carry no category.
    /// </summary>
    public string? ErrorKind { get; }

    /// <summary>Optional actionable remediation hint emitted as <c>remediation</c>.</summary>
    public string? Remediation { get; }
}

/// <summary>
/// A recoverable misuse the caller can correct — unknown command/verb, a missing required option, or
/// a malformed flag. Maps to <see cref="ExitCodes.Usage"/> and carries no error kind, mirroring how
/// the TypeScript implementation renders parse and usage failures.
/// </summary>
public sealed class UsageException : AgentHostingException
{
    public UsageException(string message)
        : base(message, ExitCodes.Usage)
    {
    }
}

/// <summary>
/// Raised by the operation facade for behavior that is explicitly not implemented. This keeps an
/// incomplete adapter boundary visible rather than silently returning a success-shaped result.
/// Maps to <see cref="ExitCodes.Failure"/>.
/// </summary>
public sealed class PendingImplementationException : AgentHostingException
{
    public PendingImplementationException(string operation)
        : base(
            $"The '{operation}' operation is not yet implemented in the C# demonstrator. " +
            "The command host and authentication are in place; Azure Bot Service behavior " +
            "arrives in a later milestone.",
            ExitCodes.Failure)
    {
    }
}

/// <summary>A transport operation timed out without caller-requested cancellation.</summary>
public sealed class TransportTimeoutException : AgentHostingException
{
    public TransportTimeoutException(Exception innerException)
        : base(
            "The operation timed out before the target returned a response.",
            ExitCodes.Failure,
            "transport-timeout",
            "Retry the operation. If it can mutate remote state, check the target before retrying because the request may have completed.",
            innerException)
    {
    }
}
