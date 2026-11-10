using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent deployment show connection</c>: reports a deployment's connection and workflow state without
/// changing anything.
/// </summary>
public sealed class AgentDeploymentConnectionReader
{
    private readonly AgentDeploymentConnectionResolver _targets;
    private readonly ICopilotConnectionPort _connections;
    private readonly WorkflowStateCache _workflowStates;
    private readonly Func<CoreServicesClusterCategory> _cluster;

    /// <summary>Creates the reader.</summary>
    /// <param name="targets">Resolves the deployment, environment and workspace the verb acts on.</param>
    /// <param name="connections">Reads references, candidates and workflows.</param>
    /// <param name="workflowStates">Reports the workflow states last observed in the environment.</param>
    /// <param name="cluster">The cluster category the environment belongs to.</param>
    public AgentDeploymentConnectionReader(AgentDeploymentConnectionResolver targets, ICopilotConnectionPort connections, WorkflowStateCache workflowStates, CoreServicesClusterCategory cluster)
        : this(targets, connections, workflowStates, () => cluster)
    {
    }

    /// <summary>Creates a reader that resolves its legacy cluster only when reporting connections.</summary>
    public static AgentDeploymentConnectionReader CreateDeferred(AgentDeploymentConnectionResolver targets, ICopilotConnectionPort connections, WorkflowStateCache workflowStates, Func<CoreServicesClusterCategory> cluster) =>
        new(targets, connections, workflowStates, cluster);

    private AgentDeploymentConnectionReader(AgentDeploymentConnectionResolver targets, ICopilotConnectionPort connections, WorkflowStateCache workflowStates, Func<CoreServicesClusterCategory> cluster)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _workflowStates = workflowStates ?? throw new ArgumentNullException(nameof(workflowStates));
        _cluster = cluster ?? throw new ArgumentNullException(nameof(cluster));
    }

    /// <summary>Reports the deployment's connection and workflow state.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Readiness for every reference and workflow the workspace has.</returns>
    public async Task<AgentDeploymentConnectionResult> ShowAsync(AgentDeploymentConnectionShowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await _targets.ResolveAsync(request, "msagent deployment connection list", cancellationToken).ConfigureAwait(false);
        var environment = target.Environment;

        var snapshot = await _connections.ReadConnectionsAsync(environment, target.WorkspaceDirectory, cancellationToken).ConfigureAwait(false);
        var plan = snapshot.References.Select(ConnectionReadinessPlanner.Observe).ToList();
        var workflows = WorkflowStateProjection.FromCache(snapshot.Workflows, _workflowStates.Read(target.WorkspaceDirectory, environment.EnvironmentId));
        var connectionsReady = ConnectionReadinessPlanner.IsReady(plan);
        var workflowsReady = WorkflowStateProjection.AllActivated(workflows);

        return new AgentDeploymentConnectionResult
        {
            Status = "connections-previewed",
            AgentId = target.Agent.AgentId,
            DeploymentId = target.Deployment.DeploymentId,
            DeploymentName = target.Deployment.DeploymentName,
            EnvironmentId = environment.EnvironmentId,
            ConnectionsReady = connectionsReady,
            WorkflowsReady = workflowsReady,
            Ready = ConnectionReport.IsReady(connectionsReady, workflowsReady),
            Connections = ConnectionReport.Connections(plan, environment.Context is { } context ? PowerAppsServiceTarget.ClusterFor(context) : _cluster(), environment.EnvironmentId),
            Workflows = workflows,
            Diagnostics = [],
            ReferenceFirst = request.ByReference,
        };
    }
}
