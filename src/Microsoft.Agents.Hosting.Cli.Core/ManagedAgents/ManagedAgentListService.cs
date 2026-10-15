using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent agent list</c>: lists the MCS agents in one environment or across the tenant.
/// </summary>
public sealed class ManagedAgentListService
{
    private const int DefaultLimit = 20;

    /// <summary>
    /// One row beyond the limit is requested so truncation stays detectable.
    ///
    /// The result reports <c>truncated</c> when more rows exist than were shown. Asking the service for
    /// exactly the limit would make a full page and an overflowing one indistinguishable.
    /// </summary>
    private const int TruncationProbe = 1;

    private static int Probe(int limit) => limit > int.MaxValue - TruncationProbe ? int.MaxValue : limit + TruncationProbe;

    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly ICopilotCatalogPort _catalog;
    private readonly IWorkflowStorePort _workflows;
    private readonly ManagedAgentResolver _resolver;
    private readonly AgentConfigLocator _locator;
    private readonly CopilotProjectIdentityReader _projectIdentities;
    private readonly IManagedAgentRegistry _agents;
    private readonly IAgentDeploymentRegistry _deployments;

    /// <summary>Creates the service.</summary>
    /// <param name="environmentResolver">Resolves one environment, or the whole tenant inventory.</param>
    /// <param name="catalog">Reads each environment's agents.</param>
    /// <param name="workflows">Reads each environment's cloud flows.</param>
    /// <param name="resolver">Counts an annotated record's deployments.</param>
    /// <param name="locator">Detects whether the current directory holds a configuration.</param>
    /// <param name="projectIdentities">Reads the current project's schema name, for the annotation join.</param>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="deployments">The deployment table.</param>
    public ManagedAgentListService(CopilotEnvironmentResolver environmentResolver, ICopilotCatalogPort catalog, IWorkflowStorePort workflows, ManagedAgentResolver resolver, AgentConfigLocator locator, CopilotProjectIdentityReader projectIdentities, IManagedAgentRegistry agents, IAgentDeploymentRegistry deployments)
    {
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _projectIdentities = projectIdentities ?? throw new ArgumentNullException(nameof(projectIdentities));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
    }

    /// <summary>Lists the agents in scope.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The agents found, and any per-environment failures.</returns>
    public async Task<ManagedAgentListResult> ListAsync(ManagedAgentListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Tenant && !string.IsNullOrWhiteSpace(request.EnvironmentId))
        {
            throw new UsageException("--environment-id and --tenant cannot be combined: each names a different scope.");
        }

        if (request.Limit is <= 0)
        {
            throw new UsageException("--limit must be a positive integer.");
        }

        var agentType = AgentTypes.IsMcsWorkflow(request.AgentType) ? AgentTypes.McsWorkflow : AgentTypes.McsAgent;

        if (agentType != AgentTypes.McsWorkflow && !string.IsNullOrWhiteSpace(request.Filter))
        {
            throw new UsageException("--filter applies to MCSWorkflow only. Add --agent-type MCSWorkflow.");
        }

        var limit = request.Limit ?? DefaultLimit;
        var read = ReaderFor(agentType, request, cancellationToken);
        var rows = new List<ManagedAgentListItem>();
        var diagnostics = new List<ManagedAgentListDiagnostic>();

        if (!request.Tenant)
        {
            var environment = await _environmentResolver.ResolveAsync(request.EnvironmentId, cancellationToken).ConfigureAwait(false);
            rows.AddRange(await read(environment, Probe(limit)).ConfigureAwait(false));

            return Project("environment", environment.EnvironmentId, agentType, limit, rows, diagnostics);
        }

        var inventory = await _environmentResolver.ListAsync(cancellationToken).ConfigureAwait(false);

        diagnostics.AddRange(inventory.Unreachable.Select(environmentId => new ManagedAgentListDiagnostic
        {
            EnvironmentId = environmentId,
            Message = "The environment carries no usable Dataverse organization url.",
        }));

        foreach (var environment in inventory.Reachable)
        {
            if (rows.Count > limit)
            {
                break;
            }

            try
            {
                rows.AddRange(await read(environment, Probe(limit) - rows.Count).ConfigureAwait(false));
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                diagnostics.Add(new ManagedAgentListDiagnostic { EnvironmentId = environment.EnvironmentId, Message = failure.Message });
            }
        }

        return Project("tenant", environmentId: null, agentType, limit, rows, diagnostics);
    }

    /// <summary>Selects the per-environment read the requested agent type needs.</summary>
    private Func<CopilotEnvironmentContext, int, Task<IReadOnlyList<ManagedAgentListItem>>> ReaderFor(string agentType, ManagedAgentListRequest request, CancellationToken cancellationToken)
    {
        if (agentType != AgentTypes.McsWorkflow)
        {
            var annotations = ReadAnnotations(request.Project);

            return (environment, maximumCount) => ReadEnvironmentAsync(environment, annotations, maximumCount, cancellationToken);
        }

        var workflowAnnotations = ReadWorkflowAnnotations(request.Project);

        return (environment, maximumCount) => ReadWorkflowsAsync(environment, workflowAnnotations, request.Filter, maximumCount, cancellationToken);
    }

    private static ManagedAgentListResult Project(string scope, string? environmentId, string agentType, int limit, List<ManagedAgentListItem> rows, List<ManagedAgentListDiagnostic> diagnostics) => new()
    {
        Scope = scope,
        EnvironmentId = environmentId,
        AgentType = agentType,
        Limit = limit,
        Truncated = rows.Count > limit,
        Agents = [.. rows.Take(limit)],
        Diagnostics = diagnostics,
    };

    private sealed record WorkflowAnnotation(ManagedAgentListItem Item, IReadOnlySet<string> Environments);

    private async Task<IReadOnlyList<ManagedAgentListItem>> ReadWorkflowsAsync(CopilotEnvironmentContext environment, IReadOnlyDictionary<string, WorkflowAnnotation> annotations, string? filter, int maximumCount, CancellationToken cancellationToken) =>
        [.. (await _workflows.ListAsync(environment, filter, maximumCount, cancellationToken).ConfigureAwait(false)).Select(workflow => Describe(workflow, environment, annotations))];

    private static ManagedAgentListItem Describe(WorkflowDocument workflow, CopilotEnvironmentContext environment, IReadOnlyDictionary<string, WorkflowAnnotation> annotations)
    {
        var annotation = annotations.TryGetValue(workflow.WorkflowId.ToString(), out var candidate) && candidate.Environments.Contains(environment.EnvironmentId)
            ? candidate.Item
            : null;

        return new ManagedAgentListItem
        {
            AgentId = annotation?.AgentId,
            McsWorkflowId = workflow.WorkflowId.ToString(),
            TenantId = environment.TenantId,
            EnvironmentId = environment.EnvironmentId,
            DisplayName = workflow.Name,
            AgentType = AgentTypes.McsWorkflow,
            WorkflowState = WorkflowStateProjection.ToPortalLabel(workflow.State),
            LastChangedOn = workflow.ModifiedOn,
            DeploymentSlotCount = annotation?.DeploymentSlotCount,
        };
    }

    /// <summary>The current project's workflow records, keyed by the workflow each one addresses.</summary>
    private Dictionary<string, WorkflowAnnotation> ReadWorkflowAnnotations(string? project)
    {
        var projectDirectory = _locator.ResolveProjectDirectory(project);

        if (!_locator.ConfigExists(projectDirectory))
        {
            return new Dictionary<string, WorkflowAnnotation>(StringComparer.OrdinalIgnoreCase);
        }

        var deploymentsByAgent = _deployments.List(projectDirectory)
            .Where(deployment => string.Equals(deployment.AgentType, AgentTypes.McsWorkflow, StringComparison.OrdinalIgnoreCase))
            .ToLookup(deployment => deployment.AgentId, StringComparer.OrdinalIgnoreCase);

        return _agents.List(projectDirectory)
            .Where(record => string.Equals(record.AgentType, AgentTypes.McsWorkflow, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(record.McsWorkflowId))
            .GroupBy(record => record.McsWorkflowId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => Annotate(group.First(), deploymentsByAgent[group.First().AgentId]),
                StringComparer.OrdinalIgnoreCase);
    }

    private static WorkflowAnnotation Annotate(ManagedAgentRecord record, IEnumerable<AgentDeploymentRecord> deployments)
    {
        var slots = deployments.ToArray();
        var environments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(record.EnvironmentId))
        {
            environments.Add(record.EnvironmentId!);
        }

        foreach (var slot in slots.Where(slot => !string.IsNullOrWhiteSpace(slot.EnvironmentId)))
        {
            environments.Add(slot.EnvironmentId!);
        }

        return new WorkflowAnnotation(
            new ManagedAgentListItem
            {
                AgentId = record.AgentId,
                TenantId = record.TenantId,
                EnvironmentId = record.EnvironmentId,
                DisplayName = record.DisplayName,
                AgentType = AgentTypes.McsWorkflow,
                DeploymentSlotCount = slots.Length,
            },
            environments);
    }

    private async Task<IReadOnlyList<ManagedAgentListItem>> ReadEnvironmentAsync(CopilotEnvironmentContext environment, IReadOnlyList<ManagedAgentAnnotation> annotations, int maximumCount, CancellationToken cancellationToken) =>
        [.. (await _catalog.ListAgentsAsync(environment, maximumCount, cancellationToken).ConfigureAwait(false)).Select(agent => Describe(agent, environment, annotations.FirstOrDefault(annotation => annotation.Matches(agent.SchemaName, environment.EnvironmentId))))];

    private static ManagedAgentListItem Describe(CopilotAgentSummary agent, CopilotEnvironmentContext environment, ManagedAgentAnnotation? annotation) => new()
    {
        AgentId = annotation?.AgentId,
        McsAgentId = agent.AgentId,
        TenantId = environment.TenantId,
        EnvironmentId = environment.EnvironmentId,
        DisplayName = agent.DisplayName,
        SchemaName = agent.SchemaName,
        AgentType = AgentTypes.McsAgent,
        LifecycleState = agent.State,
        LastChangedOn = agent.LastChangedOn,
        DeploymentSlotCount = annotation?.DeploymentCount,
    };

    /// <summary>The current project's records, keyed by the schema name each workspace declares.</summary>
    private IReadOnlyList<ManagedAgentAnnotation> ReadAnnotations(string? project)
    {
        var projectDirectory = _locator.ResolveProjectDirectory(project);

        if (!_locator.ConfigExists(projectDirectory))
        {
            return [];
        }

        var deploymentsByAgent = _deployments.List(projectDirectory)
            .Where(deployment => string.Equals(deployment.AgentType, AgentTypes.McsAgent, StringComparison.OrdinalIgnoreCase))
            .ToLookup(deployment => deployment.AgentId, StringComparer.OrdinalIgnoreCase);

        return [.. _agents.List(projectDirectory)
            .Where(record => string.Equals(record.AgentType, AgentTypes.McsAgent, StringComparison.OrdinalIgnoreCase))
            .Select(record => (Record: record, Identity: _projectIdentities.Find(_resolver.ResolveAgentWorkspaceDirectory(projectDirectory, record))))
            .Where(candidate => candidate.Identity is not null)
            .Select(candidate => new ManagedAgentAnnotation(
                candidate.Identity!.SchemaName,
                candidate.Record.AgentId,
                candidate.Record.EnvironmentId,
                [.. deploymentsByAgent[candidate.Record.AgentId].Select(deployment => deployment.McsEnvironmentId())],
                deploymentsByAgent[candidate.Record.AgentId].Count()))];
    }

    /// <summary>
    /// The local record a listed agent may correspond to.
    ///
    /// A schema name is unique within an environment but not across them, and promotion deliberately
    /// carries one name into several. Matching on the name alone would therefore annotate the same
    /// project onto its dev, test and production rows alike, each claiming the other's deployments.
    /// The environment is part of the match, and an environment the record reaches only through a
    /// deployment counts too.
    /// </summary>
    private sealed record ManagedAgentAnnotation(string SchemaName, string AgentId, string HomeEnvironmentId, IReadOnlyList<string> DeploymentEnvironmentIds, int DeploymentCount)
    {
        public bool Matches(string schemaName, string environmentId) =>
            string.Equals(SchemaName, schemaName, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(HomeEnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase)
                || DeploymentEnvironmentIds.Contains(environmentId, StringComparer.OrdinalIgnoreCase));
    }
}
