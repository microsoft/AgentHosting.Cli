using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent deployment create</c>: records a deployment slot for a managed agent.
/// </summary>
public sealed class AgentDeploymentCreateService
{
    private readonly ManagedAgentResolver _resolver;
    private readonly IAgentDeploymentRegistry _deployments;
    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly CopilotProjectIdentityReader _projectIdentities;
    private readonly ManagedAgentPrincipalResolver _principals;

    /// <summary>Creates the service.</summary>
    /// <param name="resolver">Resolves records from selectors.</param>
    /// <param name="deployments">The deployment table.</param>
    /// <param name="environmentResolver">Resolves the target and source environments.</param>
    /// <param name="projectIdentities">Reads the project's declared schema name.</param>
    /// <param name="principals">Resolves the signed-in operator recorded as the creator.</param>
    public AgentDeploymentCreateService(ManagedAgentResolver resolver, IAgentDeploymentRegistry deployments, CopilotEnvironmentResolver environmentResolver, CopilotProjectIdentityReader projectIdentities, ManagedAgentPrincipalResolver principals)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _projectIdentities = projectIdentities ?? throw new ArgumentNullException(nameof(projectIdentities));
        _principals = principals ?? throw new ArgumentNullException(nameof(principals));
    }

    /// <summary>Creates a deployment slot.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The created slot.</returns>
    public async Task<AgentDeploymentCreateResult> CreateAsync(AgentDeploymentCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DeploymentName))
        {
            throw new UsageException("`msagent deployment create` requires --deployment-name.");
        }

        if (!DeploymentStages.TryParse(request.DeploymentType, out var stage))
        {
            throw new UsageException($"--deployment-type must be one of {string.Join(", ", DeploymentStages.All)}.");
        }

        var projectDirectory = _resolver.ResolveProjectDirectory(request.Project);
        var agent = ManagedAgentResolver.RequireMcsAgentOrWorkflow(_resolver.ResolveAgent(projectDirectory, request.AgentId, request.AgentName), "msagent deployment create");
        var deploymentName = DeploymentNameRule.Validate("--deployment-name", request.DeploymentName);

        if (stage != DeploymentStages.Dev && string.IsNullOrWhiteSpace(request.EnvironmentId))
        {
            throw new UsageException($"A {stage} deployment requires --environment-id.");
        }

        var targetEnvironmentId = string.IsNullOrWhiteSpace(request.EnvironmentId) ? ManagedAgentResolver.TargetEnvironmentId(agent, null) : request.EnvironmentId.Trim();
        var target = await _environmentResolver.ResolveAsync(targetEnvironmentId, cancellationToken).ConfigureAwait(false);
        var schemaName = ResolveSchemaName(projectDirectory, agent);

        var stored = _deployments.Add(projectDirectory, new AgentDeploymentRecord
        {
            DeploymentId = Guid.NewGuid().ToString(),
            AgentId = agent.AgentId,
            AgentType = agent.AgentType,
            DeploymentName = deploymentName,
            DeploymentType = stage,
            TenantId = agent.TenantId,
            HomeEnvironmentId = agent.EnvironmentId,
            EnvironmentId = targetEnvironmentId,
            CreatedBy = await _principals.ResolveAsync(cancellationToken).ConfigureAwait(false),
            IsQuarantined = false,
            IsEnabled = true,
            EntityTag = string.Empty,
        });

        return new AgentDeploymentCreateResult
        {
            AgentId = agent.AgentId,
            DeploymentId = stored.DeploymentId,
            DeploymentName = stored.DeploymentName,
            DeploymentType = stored.DeploymentType,
            EnvironmentId = stored.EnvironmentId,
            TargetEnvironmentId = target.EnvironmentId,
            SchemaName = schemaName,
        };
    }

    /// <summary>Resolves the schema name the deployment will use, which only an agent project declares.</summary>
    private string? ResolveSchemaName(string projectDirectory, ManagedAgentRecord agent) =>
        agent.AgentType == AgentTypes.McsWorkflow
            ? null
            : _projectIdentities.Require(_resolver.ResolveAgentWorkspaceDirectory(projectDirectory, agent), agent.AgentId).SchemaName;
}
