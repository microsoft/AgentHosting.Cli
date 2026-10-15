namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>Typed, secret-free failures raised while creating an agent through AMS.</summary>
public sealed class AgentCreationException : AgentHostingException
{
    private AgentCreationException(
        string message,
        int exitCode,
        string errorKind,
        string remediation,
        Exception? innerException = null)
        : base(message, exitCode, errorKind, remediation, innerException)
    {
    }

    public static AgentCreationException EndpointNotUsable(string detail, string remediation) =>
        new(detail, ExitCodes.Failure, "endpoint-untrusted", remediation);

    public static AgentCreationException RedirectRejected(int status, string configuredHost) =>
        new(
            $"The Agent Management Service answered HTTP {status} with a redirect, which this command does not follow. The delegated authorization header is only ever sent to the configured host '{configuredHost}'.",
            ExitCodes.Failure,
            "redirect-rejected",
            "Verify the configured Agent Management Service endpoint. An authenticated create endpoint that redirects is not usable.");

    public static AgentCreationException TimedOut(
        string clientRequestId,
        string idempotencyKey,
        Exception innerException) =>
        new(
            $"The Agent Management Service create request timed out before a response was received (clientRequestId={clientRequestId}). The create outcome may be indeterminate.",
            ExitCodes.Failure,
            "agent-create-timeout",
            $"Retry the same create request so AMS resumes the durable reservation instead of minting another agent. Reuse this exact idempotency key value: {idempotencyKey}. When passing it through `--idempotency-key`, quote or escape it for your shell.",
            innerException);

    public static AgentCreationException Canceled(
        string idempotencyKey,
        Exception innerException) =>
        new(
            "The Agent Management Service create operation was canceled while its transport was in progress. If AMS received the request, the create outcome may be indeterminate.",
            ExitCodes.Failure,
            "agent-create-canceled",
            RecoveryKeyGuidance(idempotencyKey),
            innerException);

    public static AgentCreationException TransportFailed(
        string idempotencyKey,
        Exception innerException) =>
        new(
            "The Agent Management Service create transport failed before a response was received. If AMS received the request, the create outcome may be indeterminate.",
            ExitCodes.Failure,
            "agent-create-transport-failed",
            RecoveryKeyGuidance(idempotencyKey),
            innerException);

    public static AgentCreationException ResponseDecodingFailed(
        string clientRequestId,
        string idempotencyKey,
        Exception innerException) =>
        new(
            $"The Agent Management Service answered the create request, but its response could not be decoded (clientRequestId={clientRequestId}). The create outcome may be indeterminate.",
            ExitCodes.Failure,
            "agent-create-response-unreadable",
            RecoveryKeyGuidance(idempotencyKey),
            innerException);

    public static AgentCreationException PreserveGeneratedIdempotencyKey(
        AgentHostingException failure,
        string idempotencyKey) =>
        new(
            failure.Message,
            failure.ExitCode,
            failure.ErrorKind ?? "agent-create-failed",
            string.IsNullOrWhiteSpace(failure.Remediation)
                ? RecoveryKeyGuidance(idempotencyKey)
                : $"{failure.Remediation} {RecoveryKeyGuidance(idempotencyKey)}",
            failure);

    public static AgentCreationException InvalidResponse() =>
        new(
            "The Agent Management Service returned a success response that did not contain the required agent and repository fields.",
            ExitCodes.Failure,
            "invalid-ams-response",
            "Do not treat the agent as created. Verify that the target deployment implements the agent-create response contract.");

    public static AgentCreationException ResponseMismatch(string field, string requestedValue, string returnedValue) =>
        new(
            $"The Agent Management Service returned {field} '{returnedValue}', which does not match the requested value '{requestedValue}'.",
            ExitCodes.Failure,
            "ams-response-identity-mismatch",
            "Do not treat the agent as created. Verify the selected environment and the target service routing.");

    public static AgentCreationException ServiceRejected(
        string errorKind,
        string message,
        int exitCode,
        string remediation) =>
        new(message, exitCode, errorKind, remediation);

    private static string RecoveryKeyGuidance(string idempotencyKey) =>
        $"Preserve this exact idempotency key value for the create attempt: {idempotencyKey}. If retrying the same create request, pass it through `--idempotency-key` so AMS resumes the durable reservation instead of minting another agent; quote or escape it for your shell.";
}
