using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Checks the deployments AMS returns and turns them into the records the commands show.</summary>
internal static class AmsDeploymentView
{
    /// <summary>Returns the record the commands show for a deployment AMS returned.</summary>
    internal static AgentDeploymentAmsRecord ToRecord(AmsDeploymentResult deployment) => new()
    {
        DeploymentId = deployment.DeploymentId,
        DeploymentName = deployment.DeploymentName,
        AgentId = deployment.AgentId,
        TenantId = deployment.TenantId,
        HomeEnvironmentId = deployment.HomeEnvironmentId,
        TargetEnvironmentId = deployment.TargetEnvironmentId,
        DeploymentType = deployment.DeploymentType,
        EntraId = deployment.EntraId,
        EntraIdType = deployment.EntraIdType,
        State = deployment.State,
        Kind = deployment.IsCustomEndpoint ? AgentDeploymentAmsRecord.DevEndpointKind : AgentDeploymentAmsRecord.PackageKind,
        PackageRevisionId = deployment.PackageRevisionId,
        CommitId = deployment.CommitId,
        PackageDigest = deployment.PackageSha256,
        PackageSizeInBytes = deployment.PackageSizeInBytes,
        Endpoint = deployment.RuntimeEndpoint,
        RequestedEndpoint = deployment.RequestedEndpoint,
        EndpointChangePending = deployment.RequestedEndpoint is not null && !string.Equals(deployment.RequestedEndpoint, deployment.RuntimeEndpoint, StringComparison.Ordinal),
        ManagedIdentityId = deployment.ManagedIdentityId,
        BotService = deployment.BotService,
    };

    /// <summary>Refuses a deployment that AMS is deleting.</summary>
    internal static void RequireNotDeleting(AmsDeploymentResult deployment)
    {
        if (deployment.IsDeleting || deployment.State == AmsDeploymentResult.DeletingState)
        {
            throw AgentDeployException.DeploymentDeleting(deployment.DeploymentId, deployment.ClientRequestId, deployment.ServiceRequestId);
        }
    }

    /// <summary>Returns a deployment AMS returned, or refuses it when it is not the deployment addressed or its bot answers to another application.</summary>
    internal static AmsDeploymentResult RequireAddressed(AmsDeploymentReadTransportRequest reference, AmsDeploymentResult deployment, string phase) => FirstMismatch(reference, deployment) is { } field ? throw AgentDeployException.ResponseMismatch(phase, field, deployment.ClientRequestId, deployment.ServiceRequestId) : deployment;

    private static string? FirstMismatch(AmsDeploymentReadTransportRequest reference, AmsDeploymentResult deployment) =>
        !EqualsId(reference.ServiceTarget.TenantId, deployment.TenantId) ? "tenantId"
        : !EqualsId(reference.ServiceTarget.EnvironmentId, deployment.HomeEnvironmentId) ? "homeEnvironmentId"
        : !EqualsId(reference.AgentId, deployment.AgentId) ? "agentId"
        : !EqualsId(reference.DeploymentId, deployment.DeploymentId) ? "deploymentId"
        : deployment.BotService is { } bot && !EqualsId(bot.MsaAppId, deployment.EntraId) ? "botService.msaAppId"
        : null;

    private static bool EqualsId(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
