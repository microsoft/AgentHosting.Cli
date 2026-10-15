using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>
/// Typed, secret-free failures raised while publishing a package to the Agent Management Service.
///
/// <para>
/// None of these messages carries a bearer token, a signed URL, or a response body. Non-success
/// response bodies are classified, never echoed: they are service-controlled text and can carry
/// destination or capability material that has no business in operator output, a log, or a retained
/// evidence file.
/// </para>
/// </summary>
public sealed class PackagePublicationException : AgentHostingException
{
    private PackagePublicationException(
        string message,
        int exitCode,
        string errorKind,
        string remediation,
        Exception? innerException = null,
        AgentPackageUploadPartialResult? partialResult = null)
        : base(message, exitCode, errorKind, remediation, innerException)
    {
        PartialResult = partialResult;
    }

    public AgentPackageUploadPartialResult? PartialResult { get; }

    /// <summary>
    /// The publication target is not configured, so there is no endpoint or agent to publish to. This
    /// is deliberately a failure rather than a prompt for a caller-supplied endpoint: the target is
    /// deployment configuration, and a command that accepted one from the operator could aim a
    /// delegated token at an arbitrary host.
    /// </summary>
    public static PackagePublicationException TargetNotConfigured(string detail, string remediation) =>
        new(detail, ExitCodes.Failure, "publication-target-not-configured", remediation);

    /// <summary>The configured publication endpoint is not one an authorization header may be sent to.</summary>
    public static PackagePublicationException EndpointNotUsable(string detail, string remediation) =>
        new(detail, ExitCodes.Failure, "endpoint-untrusted", remediation);

    /// <summary>The supplied package could not be read for sending.</summary>
    public static PackagePublicationException PackageReadFailed(string packagePath, Exception innerException) =>
        new(
            $"The supplied agent package could not be read from '{packagePath}'.",
            ExitCodes.Failure,
            "package-read-failed",
            "Check that the selected package is a readable regular file and is not being changed concurrently.",
            innerException);

    /// <summary>
    /// The bytes on disk no longer hash to what the builder reported. The digest that is compared
    /// against the service is computed from the bytes actually sent, so this is a real disagreement
    /// rather than a bookkeeping check.
    /// </summary>
    public static PackagePublicationException LocalDigestChanged(string builtSha256, string sentSha256) =>
        new(
            $"The built package hashed to '{builtSha256}', but the bytes read back for publication hashed to '{sentSha256}'.",
            ExitCodes.Failure,
            "package-local-digest-changed",
            "Do not publish this package. The archive changed between being built and being read; retry `msagent agent deploy` on an idle working copy.");

    /// <summary>
    /// The service answered with a redirect. It is never followed: following it would resend the
    /// delegated authorization header to a host that is not the configured one.
    /// </summary>
    public static PackagePublicationException RedirectRejected(int status, string configuredHost) =>
        new(
            $"The Agent Management Service answered HTTP {status} with a redirect, which this command does not follow. The delegated authorization header is only ever sent to the configured host '{configuredHost}'.",
            ExitCodes.Failure,
            "redirect-rejected",
            "Verify the configured Agent Management Service endpoint. A publication endpoint that redirects is not usable, because the credential cannot be replayed to the redirect target.");

    /// <summary>
    /// The publication request timed out after it may have reached AMS. The absence of a response is
    /// not evidence that the service did not persist the package.
    /// </summary>
    public static PackagePublicationException RequestTimedOut(
        string agentId,
        string clientRequestId,
        Exception innerException) =>
        new(
            $"The Agent Management Service package upload for Agent '{agentId}' timed out before a response was received (clientRequestId={clientRequestId}). The upload outcome may be indeterminate.",
            ExitCodes.Failure,
            "publication-timeout",
            "Do not retry blindly. Check AMS package state or service-side correlation for this client request before deciding whether another upload is required.",
            innerException);

    public static PackagePublicationException RequestFailed(
        string clientRequestId,
        Exception innerException) =>
        new(
            $"The Agent Management Service package upload failed before a response was received (clientRequestId={clientRequestId}). The upload outcome may be indeterminate.",
            ExitCodes.Failure,
            "publication-transport-failed",
            "Do not retry blindly. Check AMS package state or service-side correlation for this client request before deciding whether another upload is required.",
            innerException);

    /// <summary>
    /// The response identifies a durably recorded upload operation that did not confirm success.
    /// </summary>
    public static PackagePublicationException DurableOperationDidNotSucceed(
        string operationId,
        string status,
        string clientRequestId,
        string? serviceRequestId) =>
        new(
            $"The Agent Management Service durably recorded package upload operation '{operationId}' with status '{status}', but no Package Revision was confirmed (clientRequestId={clientRequestId}" +
            (string.IsNullOrWhiteSpace(serviceRequestId) ? ")." : $", serviceRequestId={serviceRequestId})."),
            ExitCodes.Failure,
            "publication-failed",
            $"Do not retry blindly. Check package operation '{operationId}' or service-side correlation before deciding whether another upload is required.",
            partialResult: new AgentPackageUploadPartialResult
            {
                PackageOperationId = operationId,
                OperationStatus = status,
            });

    /// <summary>The service returned a success status whose body is not the publication contract.</summary>
    public static PackagePublicationException InvalidResponse(
        string detail,
        Exception? innerException = null) =>
        new(
            detail,
            ExitCodes.Failure,
            "invalid-ams-response",
            "Do not treat this as uploaded. Verify that the target deployment implements the v1 terminal Package Upload Operation response.",
            innerException);

    /// <summary>
    /// The service reported a digest that does not match the bytes that were sent. A client-side
    /// integrity guard that holds independently of anything the service validates.
    /// </summary>
    public static PackagePublicationException DigestMismatch(string localSha256, string remoteSha256) =>
        new(
            $"The Agent Management Service reported SHA-256 '{remoteSha256}', which does not match the SHA-256 '{localSha256}' of the bytes that were sent.",
            ExitCodes.Failure,
            "package-digest-mismatch",
            "Do not treat this as published. Investigate package transfer and artifact persistence integrity before retrying.");

    /// <summary>
    /// The service's response identifies something other than what was requested. Only the agent is
    /// checkable here: the caller supplies no repository, so there is no requested repository value to
    /// compare a response against, and inventing one would be a check against itself.
    /// </summary>
    public static PackagePublicationException ResponseIdentityMismatch(
        string field,
        string requestedValue,
        string returnedValue) =>
        new(
            $"The Agent Management Service returned {field} '{returnedValue}', which does not match the requested value '{requestedValue}'.",
            ExitCodes.Failure,
            "ams-response-identity-mismatch",
            "Do not treat this as published. Verify the configured agent identifier and the service's routing before retrying.");

    /// <summary>
    /// A rejection the service classified with one of its own error codes. The code is mapped to a
    /// stable CLI error kind by <see cref="Ams.AmsPublicationErrorMap"/>; the service's message is not
    /// echoed.
    /// </summary>
    public static PackagePublicationException ServiceRejected(
        string errorKind,
        string message,
        int exitCode,
        string remediation) =>
        new(message, exitCode, errorKind, remediation);
}
