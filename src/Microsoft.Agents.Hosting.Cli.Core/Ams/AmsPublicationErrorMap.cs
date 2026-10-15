using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>
/// Maps an Agent Management Service publication rejection to a typed CLI failure.
///
/// <para>
/// This maps the repository-standard codes the service sends:
/// <c>MissingUserIdentity</c>, <c>TenantIdMismatch</c>, <c>Forbidden</c>,
/// <c>AppNotFound</c>, and <c>InvalidRequestContent</c>. Mapping names that are not present on the
/// wire would be a mapping to nothing. The status-driven fallback below keeps unknown future codes
/// safe and useful without echoing service-controlled response text.
/// </para>
///
/// <para>
/// Anything unrecognized falls through to the shared
/// <see cref="TargetRequestException.MapTargetResponseError"/>, which classifies by HTTP status and
/// keeps the response body and the bearer token out of the message. Nothing here echoes the service's
/// free-text message.
/// </para>
/// </summary>
public static class AmsPublicationErrorMap
{
    /// <summary>The audience label used in fallback messages.</summary>
    public const string AudienceLabel = "Agent Management Service";

    /// <summary>Classifies a non-success publication response.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="bodyText">The raw response body; classified, never echoed.</param>
    /// <param name="method">The request method, for the fallback message.</param>
    /// <param name="url">The request url, for the fallback message.</param>
    /// <param name="binaryName">The CLI binary name used in remediation text.</param>
    /// <param name="clientRequestId">The client-generated correlation id sent with the request.</param>
    /// <param name="serviceRequestId">The service-returned request id, when the response carried one.</param>
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
            "missinguseridentity" => PackagePublicationException.ServiceRejected(
                "ams-missing-user-identity",
                $"The {AudienceLabel} did not receive a user identity for this request.",
                ExitCodes.NotSignedIn,
                $"Run `{binaryName} auth login` and retry. If sign-in succeeds and this persists, the request is not reaching the service with the delegated token attached."),

            "tenantidmismatch" => PackagePublicationException.ServiceRejected(
                "tenant-mismatch",
                $"The {AudienceLabel} rejected the request because the tenant it was addressed to is not the tenant the access token was issued for.",
                ExitCodes.Usage,
                $"Run `{binaryName} env select <environment-id>` to select an environment in the signed-in account's tenant, or sign in to the selected environment's tenant."),

            // On this route the service answers Forbidden when package publication is not enabled in
            // the target deployment. It ships closed in every deployment group.
            "forbidden" => PackagePublicationException.ServiceRejected(
                "ams-publication-disabled",
                $"The {AudienceLabel} refused the request. This route answers Forbidden when agent package publication is not enabled in the target deployment.",
                ExitCodes.Failure,
                "Package publication must be enabled in the target deployment's configuration before this command can publish."),

            "appnotfound" => PackagePublicationException.ServiceRejected(
                "ams-agent-not-bound",
                $"The {AudienceLabel} has no package-upload binding for the requested agent.",
                ExitCodes.Failure,
                "Select an existing repository-bound Agent with --agent, then run `msagent agent package upload` again."),

            "invalidrequestcontent" => MapInvalidRequestContent(status),

            _ => TargetRequestException.MapTargetResponseError(
                status,
                bodyText,
                method,
                url,
                AudienceLabel,
                binaryName),
        };

        return AddCorrelation(mapped, clientRequestId, serviceRequestId);
    }

    private static AgentHostingException AddCorrelation(
        AgentHostingException mapped,
        string clientRequestId,
        string? serviceRequestId)
    {
        var correlation =
            $" Correlation: clientRequestId={clientRequestId}" +
            (string.IsNullOrWhiteSpace(serviceRequestId) ? "." : $"; requestId={serviceRequestId}.");

        return mapped switch
        {
            PackagePublicationException publication => PackagePublicationException.ServiceRejected(
                publication.ErrorKind ?? "request-failed",
                publication.Message + correlation,
                publication.ExitCode,
                publication.Remediation ?? "Use the correlation identifiers to inspect the target service request."),
            TargetRequestException target => new TargetRequestException(
                target.Message + correlation,
                target.ExitCode,
                target.Kind,
                target.Status),
            _ => mapped,
        };
    }

    /// <summary>
    /// The service reuses one code for every request-contract rejection, so the status carries the
    /// distinction: an oversized body is something the operator's project can fix, while a rejected
    /// media type or declared version means the client and the service disagree about the transport
    /// contract, which the operator cannot fix by re-running.
    /// </summary>
    private static PackagePublicationException MapInvalidRequestContent(int status) => status switch
    {
        413 => PackagePublicationException.ServiceRejected(
            "package-too-large",
            $"The {AudienceLabel} rejected the package as larger than the v1 request bound.",
            ExitCodes.Usage,
            "Reduce the supplied package below the 128 MiB deployed request bound and retry."),

        415 => PackagePublicationException.ServiceRejected(
            "ams-content-type-rejected",
            $"The {AudienceLabel} rejected the request media type for a v1 package upload.",
            ExitCodes.Failure,
            "The client and the target deployment disagree about the v1 package transport contract. Do not retry until the deployment implements it."),

        _ => PackagePublicationException.ServiceRejected(
            "ams-request-content-rejected",
            $"The {AudienceLabel} rejected the publication request body.",
            ExitCodes.Failure,
            "The client and the target deployment disagree about the package request-body contract. Do not retry until the deployment implements it."),
    };
}
