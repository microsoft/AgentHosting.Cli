using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>What a connection verb resolved its selectors to.</summary>
/// <param name="Agent">The managed-agent record the deployment belongs to.</param>
/// <param name="Deployment">The deployment the verb addresses.</param>
/// <param name="Environment">The environment the deployment targets.</param>
/// <param name="WorkspaceDirectory">The agent workspace on disk.</param>
/// <param name="TargetAgentId">The deployed agent in the target environment.</param>
public sealed record AgentDeploymentConnectionTarget(
    ManagedAgentRecord Agent,
    AgentDeploymentRecord Deployment,
    CopilotEnvironmentContext Environment,
    string WorkspaceDirectory,
    string TargetAgentId);

/// <summary>The deployment, environment and workspace a verb acts on, without a deployed agent.</summary>
/// <param name="Agent">The registered agent the deployment belongs to.</param>
/// <param name="Deployment">The deployment slot the verb addresses.</param>
/// <param name="Environment">The environment the deployment targets.</param>
/// <param name="WorkspaceDirectory">The workspace holding the agent's content.</param>
public sealed record AgentDeploymentWorkspaceTarget(
    ManagedAgentRecord Agent,
    AgentDeploymentRecord Deployment,
    CopilotEnvironmentContext Environment,
    string WorkspaceDirectory);

/// <summary>
/// Resolves the selectors every <c>connection</c> verb shares, so the read and write paths cannot
/// disagree about which deployment, environment or workspace they are acting on.
/// </summary>
public sealed class AgentDeploymentConnectionResolver
{
    private readonly ManagedAgentResolver _resolver;
    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly CopilotProjectIdentityReader _projectIdentities;
    private readonly ICopilotCatalogPort _catalog;
    private readonly IWorkflowWorkspacePort _workflowWorkspace;

    /// <summary>Creates the resolver.</summary>
    /// <param name="resolver">Resolves records and deployments from selectors.</param>
    /// <param name="environmentResolver">Resolves the deployment's environment.</param>
    /// <param name="projectIdentities">Reads the project's declared schema name.</param>
    /// <param name="catalog">Resolves the deployed agent in the target environment.</param>
    /// <param name="workflowWorkspace">Finds the workflows a workspace holds.</param>
    public AgentDeploymentConnectionResolver(ManagedAgentResolver resolver, CopilotEnvironmentResolver environmentResolver, CopilotProjectIdentityReader projectIdentities, ICopilotCatalogPort catalog, IWorkflowWorkspacePort workflowWorkspace)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _projectIdentities = projectIdentities ?? throw new ArgumentNullException(nameof(projectIdentities));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _workflowWorkspace = workflowWorkspace ?? throw new ArgumentNullException(nameof(workflowWorkspace));
    }

    /// <summary>Resolves one request's selectors.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="verb">The verb named in usage and remediation messages.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>The deployment, environment, workspace and deployed agent the verb acts on.</returns>
    public async Task<AgentDeploymentConnectionTarget> ResolveAsync(IAgentDeploymentConnectionSelector request, string verb, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workspace = await ResolveWorkspaceAsync(request, verb, cancellationToken).ConfigureAwait(false);

        if (workspace.Agent.AgentType == AgentTypes.McsWorkflow)
        {
            return new AgentDeploymentConnectionTarget(workspace.Agent, workspace.Deployment, workspace.Environment, workspace.WorkspaceDirectory, WorkflowProjectResolver.RequireMatchingWorkflow(_workflowWorkspace, workspace.WorkspaceDirectory, workspace.Agent).WorkflowId.ToString());
        }

        var schemaName = string.IsNullOrWhiteSpace(request.SchemaName)
            ? _projectIdentities.Require(workspace.WorkspaceDirectory, workspace.Agent.AgentId).SchemaName
            : request.SchemaName.Trim();
        var targetAgentId = await _catalog.ResolveAgentIdAsync(workspace.Environment, schemaName, cancellationToken).ConfigureAwait(false)
            ?? throw NoTargetAgent(workspace.Agent, workspace.Deployment, schemaName, workspace.Environment.EnvironmentId);

        return new AgentDeploymentConnectionTarget(workspace.Agent, workspace.Deployment, workspace.Environment, workspace.WorkspaceDirectory, targetAgentId);
    }

    /// <summary>
    /// Resolves the deployment, environment and workspace, without looking up the deployed agent.
    /// </summary>
    /// <param name="request">The command input.</param>
    /// <param name="verb">The verb named in usage and remediation messages.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>The deployment, environment and workspace the verb acts on.</returns>
    /// <exception cref="UsageException">No deployment selector was supplied.</exception>
    public async Task<AgentDeploymentWorkspaceTarget> ResolveWorkspaceAsync(IAgentDeploymentConnectionSelector request, string verb, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DeploymentId) && string.IsNullOrWhiteSpace(request.DeploymentName))
        {
            throw new UsageException($"`{verb}` requires --deployment-id or --deployment-name. Connections are bound per deployment, so there is no active-environment fallback.");
        }

        var projectDirectory = _resolver.ResolveProjectDirectory(request.Project);
        var (owner, deployment) = _resolver.ResolveAgentDeployment(projectDirectory, request.AgentId, request.AgentName, request.DeploymentId, request.DeploymentName);
        var agent = ManagedAgentResolver.RequireMcsAgentOrWorkflow(owner, verb);

        var environment = await _environmentResolver.ResolveAsync(ManagedAgentResolver.TargetEnvironmentId(agent, deployment), cancellationToken).ConfigureAwait(false);
        var workspaceDirectory = _resolver.ResolveWorkspaceDirectory(projectDirectory, agent);

        return new AgentDeploymentWorkspaceTarget(agent, deployment, environment, workspaceDirectory);
    }

    /// <summary>Finds the record identifier a request's agent selector stands for, looking a name up in the project without resolving anything else.</summary>
    /// <param name="request">The command input.</param>
    /// <returns>The identifier the request supplied or its name resolves to, or <see langword="null"/> when there is neither.</returns>
    public string? FindAgentId(IAgentDeploymentConnectionSelector request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.IsNullOrWhiteSpace(request.AgentName) ? request.AgentId : _resolver.FindAgent(_resolver.ResolveProjectDirectory(request.Project), request.AgentId, request.AgentName)?.AgentId;
    }

    /// <summary>
    /// Distinguishes an agent that was never deployed from one whose schema no longer resolves.
    /// </summary>
    private static ManagedAgentException NoTargetAgent(ManagedAgentRecord agent, AgentDeploymentRecord deployment, string schemaName, string environmentId) =>
        deployment.LastDeployedTime is null
            ? ManagedAgentException.NotDeployedYet(agent.DisplayName, agent.AgentId)
            : ManagedAgentException.TargetAgentMissing(schemaName, environmentId);
}
