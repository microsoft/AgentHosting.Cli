using Microsoft.Agents.Hosting.Cli.Core.Http;

namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>Typed, secret-free failures from the concrete AMS package and Deployment path.</summary>
public sealed class AgentDeployException : AgentHostingException
{
    private AgentDeployException(
        int errorCode,
        string message,
        int exitCode,
        string errorKind,
        string remediation,
        Exception? innerException = null)
        : base(message, exitCode, errorKind, remediation, innerException, errorCode)
    {
    }

    public static AgentDeployException EndpointNotUsable(string detail, string remediation) =>
        new(ErrorCodes.Deployment.AgentDeployEndpointNotUsable, detail, ExitCodes.Failure, "endpoint-untrusted", remediation);

    public static AgentDeployException PackageReadFailed(string packagePath, Exception innerException) =>
        new(
            ErrorCodes.Deployment.AgentDeployPackageReadFailed,
            $"The supplied agent package could not be read from '{packagePath}'.",
            ExitCodes.Failure,
            "package-read-failed",
            "Check that the package exists, is a regular readable file, and is not being changed concurrently.",
            innerException);

    public static AgentDeployException PackageEmpty() =>
        new(
            ErrorCodes.Deployment.AgentDeployPackageEmpty,
            "The supplied agent package is empty.",
            ExitCodes.Usage,
            "package-empty",
            "Supply the non-empty agent.zip produced by the package step.");

    public static AgentDeployException PackageTooLarge(long size, long limit) =>
        new(
            ErrorCodes.Deployment.AgentDeployPackageTooLarge,
            $"The supplied agent package is {size} bytes; the deployed upload contract accepts at most {limit} bytes.",
            ExitCodes.Usage,
            "package-too-large",
            "Reduce the package below the deployed request-body bound.");

    public static AgentDeployException AgentNotCompatible(string detail) =>
        new(
            ErrorCodes.Deployment.AgentDeployAgentNotCompatible,
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
            ErrorCodes.Deployment.AgentDeployResponseMismatch,
            AddCorrelation(
                $"The {phase} response field '{field}' did not match the requested or locally verified value.",
                clientRequestId,
                serviceRequestId),
            ExitCodes.Failure,
            "ams-response-identity-mismatch",
            "Do not treat the operation as successful. Use the service correlation to inspect the mismatched response before retrying.");

    internal static string AddCorrelation(string message, string? clientRequestId, string? serviceRequestId) =>
        new ServiceCorrelation(clientRequestId, serviceRequestId).Annotate(message);

    public static AgentDeployException InvalidResponse(
        string phase,
        string? clientRequestId = null,
        string? serviceRequestId = null) =>
        new(
            ErrorCodes.Deployment.AgentDeployInvalidResponse,
            AddCorrelation(
                $"The {phase} returned a success status without the required response fields.",
                clientRequestId,
                serviceRequestId),
            ExitCodes.Failure,
            "invalid-ams-response",
            "Do not treat the operation as successful. Confirm that the target implements the concrete v1 contract.");

    public static AgentDeployException RedirectRejected(string phase, int status, string configuredHost) =>
        new(
            ErrorCodes.Deployment.AgentDeployRedirectRejected,
            $"The {phase} answered HTTP {status} with a redirect, which this command does not follow. The delegated authorization header was sent only to '{configuredHost}'.",
            ExitCodes.Failure,
            "redirect-rejected",
            "Verify the configured endpoint. A credential-bearing request cannot follow redirects.");

    public static AgentDeployException TimedOut(
        string phase,
        string clientRequestId,
        bool outcomeMayBeIndeterminate,
        Exception innerException) =>
        new(
            ErrorCodes.Deployment.AgentDeployTimedOut,
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
            ErrorCodes.Deployment.AgentDeployTransportFailed,
            $"The {phase} failed before a response was received (clientRequestId={clientRequestId})." +
            (outcomeMayBeIndeterminate ? " The remote outcome may be indeterminate." : string.Empty),
            ExitCodes.Failure,
            "deploy-transport-failed",
            outcomeMayBeIndeterminate
                ? "Do not retry blindly. Inspect the target using the correlation identifier before deciding whether another mutation is safe."
                : "Confirm that the configured target is reachable, then retry.",
            innerException);

    public static AgentDeployException ServiceRejected(
        string message,
        int exitCode,
        string errorKind,
        string remediation,
        Exception? innerException = null) =>
        new(ErrorCodes.Deployment.AgentDeployServiceRejected, message, exitCode, errorKind, remediation, innerException);

    public static AgentDeployException DeploymentDeleting(string? deploymentId, string? clientRequestId, string? serviceRequestId) =>
        ServiceRejected(
            AddCorrelation(
                deploymentId is null
                    ? "This agent's deployment in the target environment is being deleted, so it cannot be created, changed or deployed."
                    : $"This agent's deployment {deploymentId} in the target environment is being deleted, so it cannot be created, changed or deployed.",
                clientRequestId,
                serviceRequestId),
            ExitCodes.Failure,
            "deployment-deleting",
            $"Finish the deletion with `msagent deployment delete --agent-id <agent-id> --deployment-id {deploymentId ?? "<deployment-id>"}`, or with `msagent agent delete` when the whole agent is being deleted. The hosting runtime keeps a deleted deployment for now, so use a deployment in another target environment afterwards.");
}
