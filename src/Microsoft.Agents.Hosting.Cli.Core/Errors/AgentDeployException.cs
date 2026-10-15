using Microsoft.Agents.Hosting.Cli.Core.Http;

namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>Typed, secret-free failures from the concrete AMS package and Deployment path.</summary>
public sealed class AgentDeployException : AgentHostingException
{
    private AgentDeployException(
        string message,
        int exitCode,
        string errorKind,
        string remediation,
        Exception? innerException = null)
        : base(message, exitCode, errorKind, remediation, innerException)
    {
    }

    public static AgentDeployException TargetNotConfigured(string detail, string remediation) =>
        new(detail, ExitCodes.Failure, "service-target-not-configured", remediation);

    public static AgentDeployException EndpointNotUsable(string detail, string remediation) =>
        new(detail, ExitCodes.Failure, "endpoint-untrusted", remediation);

    public static AgentDeployException PackageReadFailed(string packagePath, Exception innerException) =>
        new(
            $"The supplied agent package could not be read from '{packagePath}'.",
            ExitCodes.Failure,
            "package-read-failed",
            "Check that the package exists, is a regular readable file, and is not being changed concurrently.",
            innerException);

    public static AgentDeployException PackageEmpty() =>
        new(
            "The supplied agent package is empty.",
            ExitCodes.Usage,
            "package-empty",
            "Supply the non-empty agent.zip produced by the package step.");

    public static AgentDeployException PackageTooLarge(long size, long limit) =>
        new(
            $"The supplied agent package is {size} bytes; the deployed upload contract accepts at most {limit} bytes.",
            ExitCodes.Usage,
            "package-too-large",
            "Reduce the package below the deployed request-body bound.");

    public static AgentDeployException AgentNotCompatible(string detail) =>
        new(
            detail,
            ExitCodes.Usage,
            "agent-not-deployable",
            "Select a repository-bound CodeAgent with a declared language and language version.");

    public static AgentDeployException ResponseMismatch(
        string phase,
        string field,
        string? clientRequestId = null,
        string? serviceRequestId = null) =>
        new(
            AddCorrelation(
                $"The {phase} response field '{field}' did not match the requested or locally verified value.",
                clientRequestId,
                serviceRequestId),
            ExitCodes.Failure,
            "ams-response-identity-mismatch",
            "Do not treat the operation as successful. Use the service correlation to inspect the mismatched response before retrying.");

    public static AgentDeployException DeploymentUpdateResponseMismatch(
        string field,
        bool mutationAttempted,
        string? clientRequestId = null,
        string? serviceRequestId = null) =>
        new(
            AddCorrelation(
                $"The deployment update response field '{field}' did not match the requested or freshly read deployment identity. {DeploymentUpdateOutcome(mutationAttempted)}",
                clientRequestId,
                serviceRequestId),
            ExitCodes.Failure,
            "ams-response-identity-mismatch",
            mutationAttempted
                ? "Do not treat the update as successful. Inspect authoritative deployment state before retrying."
                : "Verify the selected environment, agent, and deployment before retrying.");

    internal static string AddCorrelation(string message, string? clientRequestId, string? serviceRequestId) =>
        new ServiceCorrelation(clientRequestId, serviceRequestId).Annotate(message);

    public static AgentDeployException InvalidResponse(
        string phase,
        string? clientRequestId = null,
        string? serviceRequestId = null) =>
        new(
            AddCorrelation(
                $"The {phase} returned a success status without the required response fields.",
                clientRequestId,
                serviceRequestId),
            ExitCodes.Failure,
            "invalid-ams-response",
            "Do not treat the operation as successful. Confirm that the target implements the concrete v1 contract.");

    public static AgentDeployException DeploymentUpdateInvalidResponse(
        bool mutationAttempted,
        string? clientRequestId = null,
        string? serviceRequestId = null,
        Exception? innerException = null) =>
        new(
            AddCorrelation(
                $"The Agent Management Service returned a success status without the required deployment fields. {DeploymentUpdateOutcome(mutationAttempted)}",
                clientRequestId,
                serviceRequestId),
            ExitCodes.Failure,
            "invalid-ams-response",
            mutationAttempted
                ? "Do not treat the update as successful. Inspect authoritative deployment state before retrying."
                : "Verify the deployed AMS contract before retrying the update.",
            innerException);

    public static AgentDeployException RedirectRejected(string phase, int status, string configuredHost) =>
        new(
            $"The {phase} answered HTTP {status} with a redirect, which this command does not follow. The delegated authorization header was sent only to '{configuredHost}'.",
            ExitCodes.Failure,
            "redirect-rejected",
            "Verify the configured endpoint. A credential-bearing request cannot follow redirects.");

    public static AgentDeployException DeploymentUpdateRedirectRejected(
        string phase,
        int status,
        string configuredHost,
        bool mutationAttempted,
        string? clientRequestId,
        string? serviceRequestId) =>
        new(
            AddCorrelation(
                $"The {phase} answered HTTP {status} with a redirect, which this command does not follow. The delegated authorization header was sent only to '{configuredHost}'. {DeploymentUpdateOutcome(mutationAttempted)}",
                clientRequestId,
                serviceRequestId),
            ExitCodes.Failure,
            "redirect-rejected",
            mutationAttempted
                ? "Inspect authoritative deployment state before retrying against the configured endpoint."
                : "Verify the configured endpoint before retrying the update.");

    public static AgentDeployException TimedOut(
        string phase,
        string clientRequestId,
        bool outcomeMayBeIndeterminate,
        Exception innerException) =>
        new(
            $"The {phase} timed out before a response was received (clientRequestId={clientRequestId})." +
            (outcomeMayBeIndeterminate ? " The remote outcome may be indeterminate." : string.Empty),
            ExitCodes.Failure,
            "deploy-request-timeout",
            outcomeMayBeIndeterminate
                ? "Do not retry blindly. Inspect the target using the correlation identifier before deciding whether another mutation is safe."
                : "Retry after confirming the target is reachable.",
            innerException);

    public static AgentDeployException TransportFailed(
        string phase,
        string clientRequestId,
        bool outcomeMayBeIndeterminate,
        Exception innerException) =>
        new(
            $"The {phase} failed before a response was received (clientRequestId={clientRequestId})." +
            (outcomeMayBeIndeterminate ? " The remote outcome may be indeterminate." : string.Empty),
            ExitCodes.Failure,
            "deploy-transport-failed",
            outcomeMayBeIndeterminate
                ? "Do not retry blindly. Inspect the target using the correlation identifier before deciding whether another mutation is safe."
                : "Confirm that the configured target is reachable, then retry.",
            innerException);

    public static AgentDeployException DeploymentUpdateTimedOut(
        string phase,
        string clientRequestId,
        bool mutationAttempted,
        Exception innerException,
        string? serviceRequestId = null) =>
        new(
            AddCorrelation(
                $"The {phase} timed out before its response was fully received. {DeploymentUpdateOutcome(mutationAttempted)}",
                clientRequestId,
                serviceRequestId),
            ExitCodes.Failure,
            mutationAttempted ? "agent-deployment-timeout" : "agent-deployment-read-timeout",
            mutationAttempted
                ? "Do not retry blindly. Inspect authoritative deployment state using the correlation identifiers."
                : "Retry the update when the service is reachable.",
            innerException);

    public static AgentDeployException DeploymentUpdateTransportFailed(
        string phase,
        string clientRequestId,
        bool mutationAttempted,
        Exception innerException,
        string? serviceRequestId = null) =>
        new(
            AddCorrelation(
                $"The {phase} failed before its response was fully received. {DeploymentUpdateOutcome(mutationAttempted)}",
                clientRequestId,
                serviceRequestId),
            ExitCodes.Failure,
            mutationAttempted ? "agent-deployment-transport-failed" : "agent-deployment-read-failed",
            mutationAttempted
                ? "Do not retry blindly. Inspect authoritative deployment state using the correlation identifiers."
                : "Retry the update when the service is reachable.",
            innerException);

    public static AgentDeployException ServiceRejected(
        string message,
        int exitCode,
        string errorKind,
        string remediation,
        Exception? innerException = null) =>
        new(message, exitCode, errorKind, remediation, innerException);

    private static string DeploymentUpdateOutcome(bool mutationAttempted) =>
        mutationAttempted
            ? "The deployment outcome may be indeterminate."
            : "No update request was sent.";
}
