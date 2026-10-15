using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>
/// Creates desired Deployment state selecting an existing Package Revision through AMS-CMP and
/// awaits the current PPRP handler's target-routed AHRG projection.
/// </summary>
public sealed class AgentDeployService
{
    private readonly AgentHostingServiceTargets _targets;
    private readonly EnvironmentSelectionService _environments;
    private readonly IAmsAgentReadTransport _agents;
    private readonly IAmsDeploymentTransport _deployments;

    public AgentDeployService(
        AgentHostingServiceTargets targets,
        EnvironmentSelectionService environments,
        IAmsAgentReadTransport agents,
        IAmsDeploymentTransport deployments)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _environments = environments ?? throw new ArgumentNullException(nameof(environments));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
    }

    public async Task<AgentDeployResult> DeployAsync(
        AgentDeployRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agentId = RequireGuid(request.AgentId, "--agent");
        var packageRevisionId = RequireGuid(request.PackageRevisionId, "--package-revision");
        var targetEnvironmentId = Require(request.TargetEnvironmentId, "--target-environment-id");
        var deploymentName = RequireDeploymentName(request.DeploymentName);
        var deploymentType = ParseDeploymentType(request.DeploymentType);
        var entraId = RequireGuid(request.EntraId, "--entra-id");
        var entraIdType = ParseEntraIdType(request.EntraIdType);
        var dataPlaneEndpoint = _targets.ResolveDataPlane();
        var controlPlaneEndpoint = _targets.ResolveControlPlane();

        var homeEnvironment = await _environments
            .ResolveForCommandAsync(request.EnvironmentId, cancellationToken)
            .ConfigureAwait(false);
        var targetEnvironment = await _environments
            .ResolveForCommandAsync(targetEnvironmentId, cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(
                homeEnvironment.TenantId,
                targetEnvironment.TenantId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException(
                "--target-environment must belong to the same tenant as the home environment.");
        }

        var agent = await _agents
            .GetAsync(
                new AmsAgentReadTransportRequest
                {
                    Endpoint = dataPlaneEndpoint,
                    Audience = homeEnvironment.ApiAudience,
                    TenantId = homeEnvironment.TenantId,
                    EnvironmentId = homeEnvironment.EnvironmentId,
                    AgentId = agentId,
                },
                cancellationToken)
            .ConfigureAwait(false);
        AmsAgentResponseValidator.ValidateBoundCodeAgent(agent, homeEnvironment, agentId);

        var deployment = await _deployments
            .CreateAsync(
                new AmsDeploymentCreateTransportRequest
                {
                    Endpoint = controlPlaneEndpoint,
                    Audience = homeEnvironment.ApiAudience,
                    TenantId = homeEnvironment.TenantId,
                    HomeEnvironmentId = homeEnvironment.EnvironmentId,
                    TargetEnvironmentId = targetEnvironment.EnvironmentId,
                    AgentId = agentId,
                    PackageRevisionId = packageRevisionId,
                    DeploymentName = deploymentName,
                    DeploymentType = deploymentType,
                    EntraId = entraId,
                    EntraIdType = entraIdType,
                    Language = agent.Language,
                    LanguageVersion = agent.LanguageVersion,
                },
                cancellationToken)
            .ConfigureAwait(false);

        ValidateDeployment(
            deployment,
            homeEnvironment,
            targetEnvironment,
            agentId,
            packageRevisionId,
            deploymentName,
            deploymentType,
            entraId,
            entraIdType,
            agent);

        return ToResult(deployment, "deployment-projection-acknowledged");
    }

    public async Task<AgentDeployResult> UpdateAsync(
        AgentDeploymentPackageUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agentId = RequireGuid(request.AgentId, "--agent-id", "deployment update");
        var deploymentId = RequireGuid(request.DeploymentId, "--deployment-id", "deployment update");
        var packageRevisionId = RequireGuid(
            request.PackageRevisionId,
            "--package-revision",
            "deployment update");
        var controlPlaneEndpoint = _targets.ResolveControlPlane();
        var homeEnvironment = await _environments
            .ResolveForCommandAsync(request.EnvironmentId, cancellationToken)
            .ConfigureAwait(false);
        var reference = new AmsDeploymentReadTransportRequest
        {
            Endpoint = controlPlaneEndpoint,
            Audience = homeEnvironment.ApiAudience,
            TenantId = homeEnvironment.TenantId,
            HomeEnvironmentId = homeEnvironment.EnvironmentId,
            AgentId = agentId,
            DeploymentId = deploymentId,
        };

        AmsDeploymentCreateResult updated;
        var mutationAttempted = false;
        try
        {
            var current = await _deployments
                .GetAsync(reference, cancellationToken)
                .ConfigureAwait(false);
            ValidateCurrentDeployment(reference, current);
            if (!TryParseNonEmptyGuid(current.PackageRevisionId))
            {
                throw AgentDeployException.DeploymentUpdateInvalidResponse(
                    mutationAttempted: false,
                    clientRequestId: current.ClientRequestId,
                    serviceRequestId: current.ServiceRequestId);
            }

            cancellationToken.ThrowIfCancellationRequested();
            mutationAttempted = true;
            updated = await _deployments
                .UpdateAsync(
                    new AmsDeploymentUpdateTransportRequest
                    {
                        Endpoint = reference.Endpoint,
                        Audience = reference.Audience,
                        TenantId = reference.TenantId,
                        HomeEnvironmentId = reference.HomeEnvironmentId,
                        AgentId = reference.AgentId,
                        DeploymentId = reference.DeploymentId,
                        PackageRevisionId = packageRevisionId,
                        ExpectedPackageRevisionId = Guid.Parse(current.PackageRevisionId).ToString("D"),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw AgentDeployException.DeploymentUpdateTransportFailed(
                mutationAttempted ? "Deployment update" : "Deployment read before update",
                clientRequestId: string.Empty,
                mutationAttempted: mutationAttempted,
                innerException: ex);
        }

        ValidateUpdatedDeployment(
            updated,
            homeEnvironment,
            agentId,
            deploymentId,
            packageRevisionId);
        return ToResult(updated, "agent-deployment-updated");
    }

    private static void ValidateDeployment(
        AmsDeploymentCreateResult deployment,
        PowerPlatformEnvironment homeEnvironment,
        PowerPlatformEnvironment targetEnvironment,
        string agentId,
        string packageRevisionId,
        string deploymentName,
        string deploymentType,
        string entraId,
        string entraIdType,
        AmsAgentReadResult agent)
    {
        if (!TryParseNonEmptyGuid(deployment.DeploymentId) ||
            !EqualsId(deployment.AgentId, agentId) ||
            !EqualsId(deployment.TenantId, homeEnvironment.TenantId) ||
            !EqualsId(deployment.HomeEnvironmentId, homeEnvironment.EnvironmentId) ||
            !EqualsId(deployment.TargetEnvironmentId, targetEnvironment.EnvironmentId) ||
            !EqualsId(deployment.PackageRevisionId, packageRevisionId) ||
            deployment.PackageSha256.Length != 64 ||
            !deployment.PackageSha256.All(Uri.IsHexDigit) ||
            deployment.PackageSizeInBytes <= 0 ||
            !string.Equals(deployment.AgentType, "CodeAgent", StringComparison.Ordinal) ||
            !string.Equals(deployment.DeploymentName, deploymentName, StringComparison.Ordinal) ||
            !string.Equals(deployment.DeploymentType, deploymentType, StringComparison.Ordinal) ||
            !EqualsId(deployment.EntraId, entraId) ||
            !string.Equals(deployment.EntraIdType, entraIdType, StringComparison.Ordinal) ||
            !string.Equals(deployment.Language, agent.Language, StringComparison.Ordinal) ||
            !string.Equals(deployment.LanguageVersion, agent.LanguageVersion, StringComparison.OrdinalIgnoreCase) ||
            !IsSafeRuntimeEndpoint(deployment.RuntimeEndpoint) ||
            deployment.IsCustomEndpoint ||
            deployment.IsQuarantined)
        {
            throw AgentDeployException.ResponseMismatch(
                "Deployment create and target-routed projection",
                "persisted deployment",
                deployment.ClientRequestId,
                deployment.ServiceRequestId);
        }
    }

    private static void ValidateCurrentDeployment(
        AmsDeploymentReadTransportRequest request,
        AmsDeploymentCurrentResult current)
    {
        RequireUpdateMatch("tenantId", request.TenantId, current.TenantId, current, mutationAttempted: false);
        RequireUpdateMatch(
            "homeEnvironmentId",
            request.HomeEnvironmentId,
            current.HomeEnvironmentId,
            current,
            mutationAttempted: false);
        RequireUpdateMatch("agentId", request.AgentId, current.AgentId, current, mutationAttempted: false);
        RequireUpdateMatch(
            "deploymentId",
            request.DeploymentId,
            current.DeploymentId,
            current,
            mutationAttempted: false);
    }

    private static void ValidateUpdatedDeployment(
        AmsDeploymentCreateResult deployment,
        PowerPlatformEnvironment homeEnvironment,
        string agentId,
        string deploymentId,
        string packageRevisionId)
    {
        if (!TryParseNonEmptyGuid(deployment.DeploymentId))
        {
            throw AgentDeployException.DeploymentUpdateInvalidResponse(
                mutationAttempted: true,
                clientRequestId: deployment.ClientRequestId,
                serviceRequestId: deployment.ServiceRequestId);
        }

        RequireUpdateMatch(
            "tenantId",
            homeEnvironment.TenantId,
            deployment.TenantId,
            deployment,
            mutationAttempted: true);
        RequireUpdateMatch(
            "homeEnvironmentId",
            homeEnvironment.EnvironmentId,
            deployment.HomeEnvironmentId,
            deployment,
            mutationAttempted: true);
        RequireUpdateMatch("agentId", agentId, deployment.AgentId, deployment, mutationAttempted: true);
        RequireUpdateMatch(
            "deploymentId",
            deploymentId,
            deployment.DeploymentId,
            deployment,
            mutationAttempted: true);
        RequireUpdateMatch(
            "packageRevisionId",
            packageRevisionId,
            deployment.PackageRevisionId,
            deployment,
            mutationAttempted: true);

        if (deployment.PackageSha256.Length != 64 ||
            !deployment.PackageSha256.All(Uri.IsHexDigit) ||
            deployment.PackageSizeInBytes <= 0 ||
            !string.Equals(deployment.AgentType, "CodeAgent", StringComparison.Ordinal) ||
            !IsSafeRuntimeEndpoint(deployment.RuntimeEndpoint) ||
            deployment.IsCustomEndpoint ||
            deployment.IsQuarantined)
        {
            throw AgentDeployException.DeploymentUpdateInvalidResponse(
                mutationAttempted: true,
                clientRequestId: deployment.ClientRequestId,
                serviceRequestId: deployment.ServiceRequestId);
        }
    }

    private static void RequireUpdateMatch(
        string field,
        string expected,
        string actual,
        object response,
        bool mutationAttempted)
    {
        if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var correlation = response switch
        {
            AmsDeploymentCurrentResult current => (current.ClientRequestId, current.ServiceRequestId),
            AmsDeploymentCreateResult deployment => (deployment.ClientRequestId, deployment.ServiceRequestId),
            _ => (null, null),
        };
        throw AgentDeployException.DeploymentUpdateResponseMismatch(
            field,
            mutationAttempted,
            correlation.Item1,
            correlation.Item2);
    }

    private static AgentDeployResult ToResult(AmsDeploymentCreateResult deployment, string status) =>
        new()
        {
            Status = status,
            TenantId = deployment.TenantId,
            HomeEnvironmentId = deployment.HomeEnvironmentId,
            TargetEnvironmentId = deployment.TargetEnvironmentId,
            AgentId = deployment.AgentId,
            PackageRevisionId = deployment.PackageRevisionId,
            PackageDigest = deployment.PackageSha256,
            PackageSizeInBytes = deployment.PackageSizeInBytes,
            DeploymentId = deployment.DeploymentId,
            DeploymentName = deployment.DeploymentName,
            DeploymentType = deployment.DeploymentType,
            EntraId = deployment.EntraId,
            EntraIdType = deployment.EntraIdType,
            RuntimeEndpoint = deployment.RuntimeEndpoint,
            PackageSource = "current-grs",
            ProjectionAcknowledged = true,
            PublicDeployOperationCreated = false,
            TargetLocalPackageStagingPerformed = false,
            RuntimeReadinessProven = false,
            CrossIslandDemonstrated = false,
        };

    private static string Require(string? value, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UsageException($"{option} is required for `msagent agent deploy`.");
        }

        return value.Trim();
    }

    private static string RequireGuid(string? value, string option, string operation = "agent deploy")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UsageException($"{option} is required for `msagent {operation}`.");
        }

        var result = value.Trim();
        if (!Guid.TryParse(result, out var parsed) || parsed == Guid.Empty)
        {
            throw new UsageException($"{option} must be a non-empty GUID.");
        }

        return parsed.ToString("D");
    }

    private static string RequireDeploymentName(string? value)
    {
        var result = Require(value, "--deployment-name");
        if (result.Length > AgentDeployRequest.DeploymentNameMaxLength)
        {
            throw new UsageException(
                $"--deployment-name must not exceed {AgentDeployRequest.DeploymentNameMaxLength} characters.");
        }

        return result;
    }

    private static string ParseDeploymentType(string? value) =>
        Require(value, "--deployment-type").ToLowerInvariant() switch
        {
            "dev" => "Dev",
            "test" => "Test",
            "prod" => "Prod",
            _ => throw new UsageException("--deployment-type must be dev, test, or prod."),
        };

    private static string ParseEntraIdType(string? value) =>
        Require(value, "--entra-id-type").ToLowerInvariant() switch
        {
            "app-reg" or "appreg" => "AppReg",
            "blueprint" => "Blueprint",
            "default" => "Default",
            _ => throw new UsageException(
                "--entra-id-type must be app-reg, blueprint, or default."),
        };

    private static bool EqualsId(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseNonEmptyGuid(string value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty;

    private static bool IsSafeRuntimeEndpoint(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var endpoint) &&
        string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(endpoint.Host) &&
        string.IsNullOrEmpty(endpoint.UserInfo) &&
        string.IsNullOrEmpty(endpoint.Query) &&
        string.IsNullOrEmpty(endpoint.Fragment);
}
