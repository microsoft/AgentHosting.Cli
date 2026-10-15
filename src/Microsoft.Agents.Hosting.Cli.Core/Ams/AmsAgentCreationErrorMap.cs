using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Maps AMSDataPlane agent-create rejections without surfacing response-body text.</summary>
public static class AmsAgentCreationErrorMap
{
    public static AgentHostingException Map(
        int status,
        string? bodyText,
        string method,
        string url,
        string binaryName,
        string clientRequestId,
        string? serviceRequestId)
    {
        var (code, _) = TargetRequestException.ParseTargetError(bodyText);

        AgentHostingException mapped = code?.Trim().ToLowerInvariant() switch
        {
            "missinguseridentity" => AgentCreationException.ServiceRejected(
                "ams-missing-user-identity",
                "The Agent Management Service Data Plane did not receive a user identity for this request.",
                ExitCodes.NotSignedIn,
                $"Run `{binaryName} auth login` and retry."),

            "tenantidmismatch" => AgentCreationException.ServiceRejected(
                "tenant-mismatch",
                "The Agent Management Service Data Plane rejected the create because the route tenant does not match the access token tenant.",
                ExitCodes.Usage,
                $"Run `{binaryName} env select <environment-id>` to select an environment in the signed-in account's tenant."),

            "invalidagentdefinition" or
            "missingagentfield" or
            "invalidagentfieldvalue" or
            "agentfieldlengthexceeded" or
            "invalididempotencykey" => AgentCreationException.ServiceRejected(
                "ams-create-request-rejected",
                $"The Agent Management Service Data Plane rejected the agent-create request ({code}).",
                ExitCodes.Usage,
                "Correct the create options and retry."),

            "idempotencyconflict" => AgentCreationException.ServiceRejected(
                "agent-create-idempotency-conflict",
                "The Agent Management Service Data Plane rejected an Idempotency-Key that was already used for a different create request.",
                ExitCodes.Usage,
                "Retry the original request with this key, or use a new key for a distinct agent."),

            "agentcreationinprogress" => AgentCreationException.ServiceRejected(
                "agent-create-in-progress",
                "The Agent Management Service Data Plane is already processing this idempotent create request.",
                ExitCodes.Failure,
                "Wait briefly, then retry the same create request with the same Idempotency-Key."),

            "agentconcurrencyconflict" => AgentCreationException.ServiceRejected(
                "agent-create-concurrency-conflict",
                "The Agent Management Service Data Plane could not complete this create attempt because a concurrent durable write won the race.",
                ExitCodes.Failure,
                "Wait briefly, then retry the same create request with the same Idempotency-Key so AMS resumes or reconciles the existing reservation. Do not use a new key for this retry."),

            "agentrepositorybindingfailed" => AgentCreationException.ServiceRejected(
                "agent-repository-binding-failed",
                "The Agent Management Service Data Plane recorded a failure while binding the service-owned repository.",
                ExitCodes.Failure,
                "Do not retry this create blindly. Preserve the Idempotency-Key and use the correlation identifiers to inspect the recorded service state."),

            "environmentnotfound" => AgentCreationException.ServiceRejected(
                "environment-not-found",
                "The Agent Management Service Data Plane could not resolve the selected environment.",
                ExitCodes.Failure,
                $"Run `{binaryName} env list`, select an available environment, and retry."),

            _ => TargetRequestException.MapTargetResponseError(
                status,
                bodyText,
                method,
                url,
                "Agent Management Service Data Plane",
                binaryName),
        };

        var correlation =
            $" Correlation: clientRequestId={clientRequestId}" +
            (string.IsNullOrWhiteSpace(serviceRequestId) ? "." : $"; requestId={serviceRequestId}.");

        return mapped switch
        {
            AgentCreationException creation => AgentCreationException.ServiceRejected(
                creation.ErrorKind ?? "request-failed",
                creation.Message + correlation,
                creation.ExitCode,
                creation.Remediation ?? "Use the correlation identifiers to inspect the target service request."),
            TargetRequestException target => new TargetRequestException(
                target.Message + correlation,
                target.ExitCode,
                target.Kind,
                target.Status),
            _ => mapped,
        };
    }
}
