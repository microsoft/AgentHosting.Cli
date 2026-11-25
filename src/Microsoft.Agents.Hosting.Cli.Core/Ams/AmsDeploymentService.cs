using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Writes, lists, reads, changes and removes the deployment records of AMS code agents, never putting a deployment live.</summary>
public sealed class AmsDeploymentService :
    ICommandHandler<AgentDeploymentAmsCreateRequest, AgentDeploymentAmsResult>,
    ICommandHandler<AgentDeploymentAmsListRequest, AgentDeploymentAmsListResult>,
    ICommandHandler<AgentDeploymentAmsShowRequest, AgentDeploymentAmsResult>,
    ICommandHandler<AgentDeploymentAmsUpdateRequest, AgentDeploymentAmsResult>,
    ICommandHandler<AgentDeploymentAmsDeleteRequest, AgentDeploymentAmsDeleteResult>
{
    private readonly AmsAddressResolver _addresses;
    private readonly IAmsAgentReadTransport _agents;
    private readonly IAmsDeploymentTransport _deployments;
    private readonly IConfirmationPrompt _confirmations;

    /// <summary>Creates the service over the AMS agent and deployment transports and the prompt that confirms deleting a live deployment's record.</summary>
    public AmsDeploymentService(AmsAddressResolver addresses, IAmsAgentReadTransport agents, IAmsDeploymentTransport deployments, IConfirmationPrompt confirmations)
    {
        _addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _confirmations = confirmations ?? throw new ArgumentNullException(nameof(confirmations));
    }

    /// <summary>Writes a deployment record with the agent's language, and no package, deployed endpoint or bot.</summary>
    public async Task<AgentDeploymentAmsResult> CreateAsync(AgentDeploymentAmsCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deploymentName = RequireName(request.DeploymentName, "--deployment-name", "deployment create");
        var deploymentType = ParseDeploymentType(request.DeploymentType);
        var entraId = AmsAddressResolver.RequireGuid(Require(request.EntraId, "--entra-id", "deployment create"), "--entra-id");
        var entraIdType = ParseEntraIdType(request.EntraIdType);
        var targetEnvironmentId = Require(request.TargetEnvironmentId, "--target-environment-id", "deployment create");
        var requestedEndpoint = EndpointOption(request.EndpointUrl);
        RequireDevEndpointStage(requestedEndpoint, deploymentType);
        var agent = await _addresses.ResolveAgentAsync(request, "deployment create", cancellationToken).ConfigureAwait(false);
        var definition = await _agents.GetAsync(agent.OnDataPlane, cancellationToken).ConfigureAwait(false);
        AmsAgentResponseValidator.ValidateBoundCodeAgent(definition, agent.Home.Environment, agent.AgentId);
        var target = await _addresses.ResolveTargetEnvironmentAsync(agent.Home, targetEnvironmentId, cancellationToken).ConfigureAwait(false);
        var created = await _deployments.CreateAsync(new AmsDeploymentCreateTransportRequest { ServiceTarget = agent.Home.ControlPlane, TargetEnvironmentId = target.EnvironmentId, AgentId = agent.AgentId, DeploymentName = deploymentName, DeploymentType = deploymentType, EntraId = entraId, EntraIdType = entraIdType, Language = definition.Language!, LanguageVersion = definition.LanguageVersion!, RequestedEndpoint = requestedEndpoint }, cancellationToken).ConfigureAwait(false);
        if (FirstCreateMismatch(created, agent, target.EnvironmentId, deploymentType, entraId, entraIdType) is { } field)
        {
            throw AgentDeployException.ResponseMismatch("Deployment create", field, created.ClientRequestId, created.ServiceRequestId);
        }

        return Result(AgentDeploymentAmsResult.CreatedStatus, created);
    }

    /// <summary>Lists the agent's deployments with the bots kept from deleted records.</summary>
    public async Task<AgentDeploymentAmsListResult> ListAsync(AgentDeploymentAmsListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agent = await _addresses.ResolveAgentAsync(request, "deployment list", cancellationToken).ConfigureAwait(false);
        var list = await _addresses.ListDeploymentsAsync(agent, cancellationToken).ConfigureAwait(false);
        return new AgentDeploymentAmsListResult { AgentId = agent.AgentId, HomeEnvironmentId = agent.Home.Environment.EnvironmentId, Deployments = [.. list.Deployments.Select(AmsDeploymentView.ToRecord)], KeptBots = list.KeptBots };
    }

    /// <summary>Reads one deployment, from its list entry when it is named.</summary>
    public async Task<AgentDeploymentAmsResult> ShowAsync(AgentDeploymentAmsShowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var address = await _addresses.ResolveDeploymentAsync(request, "deployment show", cancellationToken).ConfigureAwait(false);
        return Result(AgentDeploymentAmsResult.ReadStatus, await _addresses.ReadDeploymentAsync(address, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Renames a deployment record or changes its requested dev endpoint, guarded by the entity tag of the record read.</summary>
    public async Task<AgentDeploymentAmsResult> UpdateAsync(AgentDeploymentAmsUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var newName = string.IsNullOrWhiteSpace(request.NewName) ? null : RequireName(request.NewName, "--new-name", "deployment update");
        var requestedEndpoint = EndpointOption(request.EndpointUrl);
        if (newName is null && requestedEndpoint is null)
        {
            throw new UsageException("--new-name or --endpoint-url is required for `msagent deployment update`.");
        }

        var address = await _addresses.ResolveDeploymentAsync(request, "deployment update", cancellationToken).ConfigureAwait(false);
        var current = await _addresses.ReadDeploymentAsync(address, cancellationToken).ConfigureAwait(false);
        AmsDeploymentView.RequireNotDeleting(current);
        RequireDevEndpointStage(requestedEndpoint, current.DeploymentType);
        var updated = await _deployments.UpdateAsync(new AmsDeploymentUpdateTransportRequest { ServiceTarget = address.Reference.ServiceTarget, AgentId = address.Reference.AgentId, DeploymentId = address.Reference.DeploymentId, ETag = current.ETag, DeploymentName = newName, RequestedEndpoint = requestedEndpoint }, cancellationToken).ConfigureAwait(false);
        return Result(AgentDeploymentAmsResult.UpdatedStatus, AmsDeploymentView.RequireAddressed(address.Reference, updated, "Deployment update"));
    }

    /// <summary>Removes a deployment record in any state, first asking for confirmation when the deployment is live.</summary>
    public async Task<AgentDeploymentAmsDeleteResult> DeleteAsync(AgentDeploymentAmsDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var address = await _addresses.ResolveDeploymentAsync(request, "deployment delete", cancellationToken).ConfigureAwait(false);
        var current = await _addresses.ReadDeploymentAsync(address, cancellationToken).ConfigureAwait(false);
        var record = AmsDeploymentView.ToRecord(current);
        if (current.IsLive && await AmsConfirmation.StoppedStatusAsync(_confirmations, request.NonInteractive, [$"About to delete the record of live deployment {current.DeploymentName} ({current.DeploymentId}).", .. AgentAmsConfirmation.LiveDeploymentKeptLines(record)], current.DeploymentName, cancellationToken).ConfigureAwait(false) is { } stopped)
        {
            return new AgentDeploymentAmsDeleteResult { Status = stopped, Deployment = record, WasLive = true };
        }

        await _deployments.DeleteAsync(new AmsDeploymentDeleteTransportRequest { ServiceTarget = address.Reference.ServiceTarget, AgentId = address.Reference.AgentId, DeploymentId = address.Reference.DeploymentId, ETag = current.ETag }, cancellationToken).ConfigureAwait(false);
        return new AgentDeploymentAmsDeleteResult { Status = AgentDeploymentAmsDeleteResult.DeletedStatus, Deployment = record, WasLive = current.IsLive };
    }

    Task<AgentDeploymentAmsResult> ICommandHandler<AgentDeploymentAmsCreateRequest, AgentDeploymentAmsResult>.HandleAsync(AgentDeploymentAmsCreateRequest request, CancellationToken cancellationToken) => CreateAsync(request, cancellationToken);

    Task<AgentDeploymentAmsListResult> ICommandHandler<AgentDeploymentAmsListRequest, AgentDeploymentAmsListResult>.HandleAsync(AgentDeploymentAmsListRequest request, CancellationToken cancellationToken) => ListAsync(request, cancellationToken);

    Task<AgentDeploymentAmsResult> ICommandHandler<AgentDeploymentAmsShowRequest, AgentDeploymentAmsResult>.HandleAsync(AgentDeploymentAmsShowRequest request, CancellationToken cancellationToken) => ShowAsync(request, cancellationToken);

    Task<AgentDeploymentAmsResult> ICommandHandler<AgentDeploymentAmsUpdateRequest, AgentDeploymentAmsResult>.HandleAsync(AgentDeploymentAmsUpdateRequest request, CancellationToken cancellationToken) => UpdateAsync(request, cancellationToken);

    Task<AgentDeploymentAmsDeleteResult> ICommandHandler<AgentDeploymentAmsDeleteRequest, AgentDeploymentAmsDeleteResult>.HandleAsync(AgentDeploymentAmsDeleteRequest request, CancellationToken cancellationToken) => DeleteAsync(request, cancellationToken);

    private static AgentDeploymentAmsResult Result(string status, AmsDeploymentResult deployment) => new() { Status = status, Deployment = AmsDeploymentView.ToRecord(deployment) };

    private static string? FirstCreateMismatch(AmsDeploymentResult created, AmsAgentAddress agent, string targetEnvironmentId, string deploymentType, string entraId, string entraIdType) =>
        !Guid.TryParse(created.DeploymentId, out var deploymentId) || deploymentId == Guid.Empty ? "id"
        : !EqualsId(created.TenantId, agent.Home.ControlPlane.TenantId) ? "tenantId"
        : !EqualsId(created.HomeEnvironmentId, agent.Home.ControlPlane.EnvironmentId) ? "homeEnvironmentId"
        : !EqualsId(created.AgentId, agent.AgentId) ? "agentId"
        : !EqualsId(created.TargetEnvironmentId, targetEnvironmentId) ? "environmentId"
        : !string.Equals(created.DeploymentType, deploymentType, StringComparison.Ordinal) ? "deploymentType"
        : !EqualsId(created.EntraId, entraId) ? "entraAppId"
        : !string.Equals(created.EntraIdType, entraIdType, StringComparison.Ordinal) ? "entraAppIdType"
        : null;

    private static string Require(string? value, string option, string operation) => string.IsNullOrWhiteSpace(value) ? throw new UsageException($"{option} is required for `msagent {operation}`.") : value.Trim();

    private static string RequireName(string? value, string option, string operation) => Require(value, option, operation) is { Length: <= AgentDeploymentAmsCreateRequest.DeploymentNameMaxLength } name ? name : throw new UsageException($"{option} must not exceed {AgentDeploymentAmsCreateRequest.DeploymentNameMaxLength} characters.");

    private static string ParseDeploymentType(string? value) => Require(value, "--deployment-type", "deployment create").ToLowerInvariant() switch
    {
        "dev" => "Dev",
        "test" => "Test",
        "prod" => "Prod",
        _ => throw new UsageException("--deployment-type must be dev, test, or prod."),
    };

    private static string ParseEntraIdType(string? value) => Require(value, "--entra-id-type", "deployment create").ToLowerInvariant() switch
    {
        "app-reg" or "appreg" => "AppReg",
        "blueprint" => "Blueprint",
        "default" => "Default",
        _ => throw new UsageException("--entra-id-type must be app-reg, blueprint, or default."),
    };

    private static string? EndpointOption(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
        : IsSafeEndpoint(value.Trim()) ? value.Trim()
        : throw new UsageException("--endpoint-url must be an absolute https URL without user info, query, or fragment.");

    private static void RequireDevEndpointStage(string? requestedEndpoint, string deploymentType)
    {
        if (requestedEndpoint is not null && !string.Equals(deploymentType, "Dev", StringComparison.Ordinal))
        {
            throw new UsageException($"--endpoint-url is supported only for Dev deployments; a {deploymentType} deployment always runs a package.");
        }
    }

    private static bool IsSafeEndpoint(string value) => Uri.TryCreate(value, UriKind.Absolute, out var endpoint) && endpoint.IsWellFormedOriginalString() && string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(endpoint.Host) && string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment);

    private static bool EqualsId(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
