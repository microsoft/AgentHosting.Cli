using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Resolves the read-only targeting contract for one live MCS test invocation.
///
/// This resolver deliberately does not use <see cref="ManagedAgentResolver"/>'s deployment fallback:
/// live testing must select an explicit deployment or exactly one configured default, and it must use
/// the deployment's destination environment rather than the agent's home environment.
/// </summary>
public sealed class LiveTestTargetResolver
{
    private readonly IManagedAgentRegistry _agents;
    private readonly IAgentDeploymentRegistry _deployments;
    private readonly AgentConfigLocator _locator;
    private readonly CopilotProjectIdentityReader _projectIdentities;

    /// <summary>Creates a resolver over the existing read-only registry ports.</summary>
    public LiveTestTargetResolver(
        IManagedAgentRegistry agents,
        IAgentDeploymentRegistry deployments,
        AgentConfigLocator locator)
    {
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _projectIdentities = new CopilotProjectIdentityReader(locator);
    }

    /// <summary>Resolves one target without authentication, network access or configuration writes.</summary>
    public ResolvedMcsTarget Resolve(LiveTestTargetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequestShape(request);

        if (request.Cloud != CloudInstance.Public)
        {
            throw LiveTestTargetResolutionException.UnsupportedTarget(
                "The live MCS test target must use public cloud.",
                "Choose a public-cloud environment; sovereign and custom destinations are not supported.");
        }

        return IsFullyExplicit(request)
            ? ResolveExplicit(request)
            : ResolveConfigured(request);
    }

    /// <summary>Resolves an operation request into the minimal live-test destination contract.</summary>
    public LiveTestDestination Resolve(LiveTestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = Resolve(new LiveTestTargetRequest
        {
            Project = request.Project,
            AgentId = request.AgentId,
            AgentName = request.AgentName,
            SchemaName = request.SchemaName,
            DeploymentId = request.DeploymentId,
            DeploymentName = request.DeploymentName,
            EnvironmentId = request.EnvironmentId,
            TenantId = request.Tenant,
        });

        return new LiveTestDestination(
            resolved.EnvironmentId,
            resolved.TenantId,
            resolved.SchemaName,
            resolved.Source == LiveTestTargetSource.Configured ? "configured" : "explicit");
    }

    private static ResolvedMcsTarget ResolveExplicit(LiveTestTargetRequest request)
    {
        var schema = RequireSafeSegment(request.SchemaName!, "--schema-name");
        var environment = RequireSafeSegment(request.EnvironmentId!, "--environment-id");
        var tenant = RequireExplicitTenant(request.TenantId!);

        return CreateTarget(
            LiveTestTargetSource.Explicit,
            tenant,
            environment,
            schema,
            agent: null,
            deployment: null,
            request.DeploymentId,
            request.DeploymentName);
    }

    private ResolvedMcsTarget ResolveConfigured(LiveTestTargetRequest request)
    {
        var project = _locator.ResolveProjectDirectory(request.Project);

        if (!_locator.ConfigExists(project))
        {
            throw ManagedAgentException.ConfigNotFound(_locator.ConfigFilePath(project));
        }

        var agents = _agents.List(project);
        var deployments = _deployments.List(project);
        ValidateRegistryShape(agents, deployments);

        var selectedAgent = ResolveAgent(project, agents, deployments, request);
        var selectedDeployment = ResolveDeployment(agents, deployments, selectedAgent, request);
        var owner = selectedAgent ?? FindOwner(agents, selectedDeployment);

        RequireMcsAgent(owner);
        RequireMcsDeployment(owner, selectedDeployment);

        var schema = EffectiveSchema(project, owner);
        if (!string.IsNullOrWhiteSpace(request.SchemaName))
        {
            var requestedSchema = RequireSafeSegment(request.SchemaName, "--schema-name");
            if (schema is not null && !string.Equals(schema, requestedSchema, StringComparison.OrdinalIgnoreCase))
            {
                throw LiveTestTargetResolutionException.InvalidConfiguration(
                    "The supplied --schema-name does not match the selected managed-agent record.",
                    "Use the record's MCS schema name, or select the matching managed agent.");
            }

            schema ??= requestedSchema;
        }

        if (string.IsNullOrWhiteSpace(schema))
        {
            throw LiveTestTargetResolutionException.InvalidConfiguration(
                "The selected managed agent has no MCS schema name.",
                "Supply --schema-name or restore schemaName in the managed agent project.");
        }

        var environment = RequireSafeSegment(selectedDeployment.EnvironmentId!, "the deployment environment");
        var tenant = RequireConfiguredTenant(selectedDeployment.TenantId);

        if (!string.IsNullOrWhiteSpace(request.EnvironmentId) &&
            !string.Equals(environment, RequireSafeSegment(request.EnvironmentId, "--environment-id"), StringComparison.OrdinalIgnoreCase))
        {
            throw LiveTestTargetResolutionException.InvalidConfiguration(
                "The supplied --environment-id does not match the selected deployment.",
                "Use the deployment's destination environment or select the matching deployment.");
        }

        if (!string.IsNullOrWhiteSpace(request.TenantId) &&
            !string.Equals(tenant, RequireExplicitTenant(request.TenantId), StringComparison.OrdinalIgnoreCase))
        {
            throw LiveTestTargetResolutionException.InvalidConfiguration(
                "The supplied --tenant does not match the selected deployment.",
                "Use the deployment's tenant or select the matching deployment.");
        }

        return CreateTarget(
            LiveTestTargetSource.Configured,
            tenant,
            environment,
            schema,
            owner,
            selectedDeployment,
            selectedDeployment.DeploymentId,
            selectedDeployment.DeploymentName);
    }

    private ManagedAgentRecord? ResolveAgent(
        string project,
        IReadOnlyList<ManagedAgentRecord> agents,
        IReadOnlyList<AgentDeploymentRecord> deployments,
        LiveTestTargetRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.AgentId))
        {
            var matches = agents.Where(agent => EqualsOrdinalIgnoreCase(agent.AgentId, request.AgentId)).ToArray();
            var selected = RequireSingleAgent(matches, "agent id");

            if (!string.IsNullOrWhiteSpace(request.AgentName) &&
                !EqualsOrdinalIgnoreCase(selected.DisplayName, request.AgentName))
            {
                throw LiveTestTargetResolutionException.InvalidConfiguration(
                    "The supplied --agent-id and --agent-name identify different records.",
                    "Use selectors that name the same managed agent.");
            }

            return selected;
        }

        if (!string.IsNullOrWhiteSpace(request.AgentName))
        {
            var matches = agents.Where(agent => EqualsOrdinalIgnoreCase(agent.DisplayName, request.AgentName)).ToArray();
            return RequireSingleAgent(matches, "agent name");
        }

        var hasDeploymentSelector = HasDeploymentSelector(request);
        if (!hasDeploymentSelector && !string.IsNullOrWhiteSpace(request.SchemaName))
        {
            var schema = RequireSafeSegment(request.SchemaName, "--schema-name");
            var matches = agents.Where(agent => EqualsOrdinalIgnoreCase(EffectiveSchema(project, agent), schema)).ToArray();
            return RequireSingleAgent(matches, "schema name");
        }

        if (hasDeploymentSelector)
        {
            return null;
        }

        return agents.Count switch
        {
            1 => agents[0],
            0 => throw LiveTestTargetResolutionException.InvalidConfiguration(
                "The project has no managed-agent records.",
                "Register a managed agent or use the fully explicit schema, environment, tenant and deployment route."),
            _ => throw LiveTestTargetResolutionException.InvalidConfiguration(
                "The project has more than one managed-agent record.",
                "Supply --agent-id, --agent-name or a unique --schema-name."),
        };
    }

    private static AgentDeploymentRecord ResolveDeployment(
        IReadOnlyList<ManagedAgentRecord> agents,
        IReadOnlyList<AgentDeploymentRecord> deployments,
        ManagedAgentRecord? selectedAgent,
        LiveTestTargetRequest request)
    {
        if (HasDeploymentSelector(request))
        {
            var candidates = selectedAgent is null
                ? deployments
                : deployments.Where(deployment => EqualsOrdinalIgnoreCase(deployment.AgentId, selectedAgent.AgentId)).ToArray();

            AgentDeploymentRecord selected;
            if (!string.IsNullOrWhiteSpace(request.DeploymentId))
            {
                var idMatches = deployments.Where(deployment => EqualsOrdinalIgnoreCase(deployment.DeploymentId, request.DeploymentId)).ToArray();
                selected = RequireSingleDeployment(idMatches, "deployment id");
            }
            else
            {
                var nameMatches = candidates.Where(deployment => EqualsOrdinalIgnoreCase(deployment.DeploymentName, request.DeploymentName)).ToArray();
                selected = RequireSingleDeployment(nameMatches, "deployment name");
            }

            if (selectedAgent is not null && !EqualsOrdinalIgnoreCase(selected.AgentId, selectedAgent.AgentId))
            {
                throw LiveTestTargetResolutionException.InvalidConfiguration(
                    "The deployment selector belongs to a different managed agent.",
                    "Select a deployment owned by the selected managed agent.");
            }

            if (!string.IsNullOrWhiteSpace(request.DeploymentName) &&
                !EqualsOrdinalIgnoreCase(selected.DeploymentName, request.DeploymentName))
            {
                throw LiveTestTargetResolutionException.InvalidConfiguration(
                    "The supplied --deployment-id and --deployment-name identify different deployments.",
                    "Use selectors that name the same deployment.");
            }

            return selected;
        }

        if (selectedAgent is null)
        {
            throw LiveTestTargetResolutionException.InvalidConfiguration(
                "A deployment selector is required when no managed-agent selector is supplied.",
                "Supply --deployment-id or --deployment-name, or select a managed agent.");
        }

        var owned = deployments.Where(deployment => EqualsOrdinalIgnoreCase(deployment.AgentId, selectedAgent.AgentId)).ToArray();
        var defaults = owned.Where(deployment => deployment.IsDefault == true).ToArray();

        return defaults.Length switch
        {
            1 => defaults[0],
            0 => throw LiveTestTargetResolutionException.InvalidConfiguration(
                "The selected managed agent has no configured default deployment.",
                "Supply --deployment-id or --deployment-name; a singleton non-default deployment is not used."),
            _ => throw LiveTestTargetResolutionException.InvalidConfiguration(
                "The selected managed agent has more than one configured default deployment.",
                "Mark exactly one deployment as the default before running a live test."),
        };
    }

    private static ManagedAgentRecord FindOwner(IReadOnlyList<ManagedAgentRecord> agents, AgentDeploymentRecord deployment)
    {
        var matches = agents.Where(agent => EqualsOrdinalIgnoreCase(agent.AgentId, deployment.AgentId)).ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw LiveTestTargetResolutionException.InvalidConfiguration(
                "The selected deployment has no owning managed-agent record.",
                "Repair the deployment ownership in agent.config.json."),
            _ => throw LiveTestTargetResolutionException.InvalidConfiguration(
                "The selected deployment has ambiguous ownership.",
                "Repair duplicate managed-agent identifiers in agent.config.json."),
        };
    }

    private static void RequireMcsAgent(ManagedAgentRecord agent)
    {
        if (!AgentTypes.IsMcsAgent(agent.AgentType))
        {
            throw LiveTestTargetResolutionException.UnsupportedTarget(
                "The selected managed agent is not an MCS agent.",
                "Select an MCS managed agent; CodeAgent and AHRG targets are not supported.");
        }
    }

    private static void RequireMcsDeployment(ManagedAgentRecord owner, AgentDeploymentRecord deployment)
    {
        if (!AgentTypes.IsMcsAgent(deployment.AgentType))
        {
            throw LiveTestTargetResolutionException.UnsupportedTarget(
                "The selected deployment is not an MCS deployment.",
                "Select an MCS deployment; CodeAgent and AHRG targets are not supported.");
        }

        if (!EqualsOrdinalIgnoreCase(owner.AgentType, deployment.AgentType))
        {
            throw LiveTestTargetResolutionException.InvalidConfiguration(
                "The selected deployment type does not match its managed-agent record.",
                "Repair the deployment type in agent.config.json.");
        }

        if (!EqualsOrdinalIgnoreCase(owner.TenantId, deployment.TenantId))
        {
            throw LiveTestTargetResolutionException.InvalidConfiguration(
                "The selected deployment tenant does not match its managed-agent record.",
                "Repair the deployment tenant in agent.config.json.");
        }

        if (string.IsNullOrWhiteSpace(deployment.EnvironmentId) || string.IsNullOrWhiteSpace(deployment.TenantId))
        {
            throw LiveTestTargetResolutionException.InvalidConfiguration(
                "The selected deployment is missing destination environment or tenant metadata.",
                "Repair the deployment destination metadata in agent.config.json.");
        }

        if (!deployment.IsEnabled || deployment.IsQuarantined)
        {
            throw LiveTestTargetResolutionException.InvalidConfiguration(
                "The selected deployment is disabled or quarantined.",
                "Select an enabled, non-quarantined deployment.");
        }
    }

    private string? EffectiveSchema(string project, ManagedAgentRecord agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.McsSchemaName))
        {
            return agent.McsSchemaName.Trim();
        }

        var workspace = _locator.AgentWorkspaceDirectory(project, agent.AgentLocation);
        return workspace is null ? null : _projectIdentities.Find(workspace)?.SchemaName?.Trim();
    }

    private static void ValidateRegistryShape(
        IReadOnlyList<ManagedAgentRecord> agents,
        IReadOnlyList<AgentDeploymentRecord> deployments)
    {
        RequireNoDuplicate(agents.Select(agent => agent.AgentId), "managed-agent identifiers");
        RequireNoDuplicate(agents.Select(agent => agent.DisplayName), "managed-agent names");
        RequireNoDuplicate(deployments.Select(deployment => deployment.DeploymentId), "deployment identifiers");
    }

    private static void RequireNoDuplicate(IEnumerable<string> values, string description)
    {
        if (values.GroupBy(value => value, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            throw LiveTestTargetResolutionException.InvalidConfiguration(
                $"The configuration contains duplicate {description}.",
                "Repair the duplicate records in agent.config.json.");
        }
    }

    private static ManagedAgentRecord RequireSingleAgent(ManagedAgentRecord[] matches, string selector)
    {
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw LiveTestTargetResolutionException.InvalidConfiguration(
                $"No managed-agent record matched the supplied {selector}.",
                "Choose a selector shown by `msagent agent list`."),
            _ => throw LiveTestTargetResolutionException.InvalidConfiguration(
                $"More than one managed-agent record matched the supplied {selector}.",
                "Narrow the selection with --agent-id."),
        };
    }

    private static AgentDeploymentRecord RequireSingleDeployment(AgentDeploymentRecord[] matches, string selector)
    {
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw LiveTestTargetResolutionException.InvalidConfiguration(
                $"No deployment record matched the supplied {selector}.",
                "Choose a deployment shown by `msagent deployment list`."),
            _ => throw LiveTestTargetResolutionException.InvalidConfiguration(
                $"More than one deployment record matched the supplied {selector}.",
                "Narrow the selection with --deployment-id and --agent-id."),
        };
    }

    private static bool IsFullyExplicit(LiveTestTargetRequest request) =>
        string.IsNullOrWhiteSpace(request.Project)
        && string.IsNullOrWhiteSpace(request.AgentId)
        && string.IsNullOrWhiteSpace(request.AgentName)
        && !string.IsNullOrWhiteSpace(request.SchemaName)
        && !string.IsNullOrWhiteSpace(request.EnvironmentId)
        && !string.IsNullOrWhiteSpace(request.TenantId)
        && HasDeploymentSelector(request);

    private static bool HasDeploymentSelector(LiveTestTargetRequest request) =>
        !string.IsNullOrWhiteSpace(request.DeploymentId) || !string.IsNullOrWhiteSpace(request.DeploymentName);

    private static void ValidateRequestShape(LiveTestTargetRequest request)
    {
        foreach (var (value, option) in new[]
        {
            (request.Project, "--project"),
            (request.AgentId, "--agent-id"),
            (request.AgentName, "--agent-name"),
            (request.SchemaName, "--schema-name"),
            (request.DeploymentId, "--deployment-id"),
            (request.DeploymentName, "--deployment-name"),
            (request.EnvironmentId, "--environment-id"),
            (request.TenantId, "--tenant"),
        })
        {
            if (value is not null && string.IsNullOrWhiteSpace(value))
            {
                throw LiveTestTargetResolutionException.InvalidInput(
                    $"{option} cannot be blank.",
                    $"Supply a nonblank value for {option}, or omit it.");
            }
        }
    }

    private static string RequireSafeSegment(string value, string label)
    {
        var normalized = value.Trim();
        if (!ServiceIdentifier.IsWellFormed(normalized) || normalized is "." or ".." || normalized.Contains("..", StringComparison.Ordinal))
        {
            throw LiveTestTargetResolutionException.InvalidInput(
                $"{label} is not a valid bounded identifier.",
                $"Supply a Power Platform identifier without path, query, fragment or traversal characters for {label}.");
        }

        return normalized;
    }

    private static string RequireExplicitTenant(string value)
    {
        var normalized = RequireSafeSegment(value, "--tenant");
        if (!Guid.TryParse(normalized, out _))
        {
            throw LiveTestTargetResolutionException.InvalidInput(
                "The --tenant value must be a GUID.",
                "Supply a tenant GUID for --tenant.");
        }

        return normalized;
    }

    private static string RequireConfiguredTenant(string value)
    {
        var normalized = RequireSafeSegment(value, "the deployment tenant");
        if (!Guid.TryParse(normalized, out _))
        {
            throw LiveTestTargetResolutionException.InvalidConfiguration(
                "The selected deployment tenant is not a GUID.",
                "Repair the deployment tenant metadata in agent.config.json.");
        }

        return normalized;
    }

    private static ResolvedMcsTarget CreateTarget(
        LiveTestTargetSource source,
        string tenant,
        string environment,
        string schema,
        ManagedAgentRecord? agent,
        AgentDeploymentRecord? deployment,
        string? deploymentId,
        string? deploymentName)
    {
        return new ResolvedMcsTarget
        {
            Source = source,
            Cloud = CloudInstance.Public,
            TenantId = tenant,
            EnvironmentId = environment,
            SchemaName = schema,
            Agent = agent,
            Deployment = deployment,
            DeploymentId = source == LiveTestTargetSource.Explicit ? deploymentId?.Trim() : deployment?.DeploymentId,
            DeploymentName = source == LiveTestTargetSource.Explicit ? deploymentName?.Trim() : deployment?.DeploymentName,
        };
    }

    private static bool EqualsOrdinalIgnoreCase(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
