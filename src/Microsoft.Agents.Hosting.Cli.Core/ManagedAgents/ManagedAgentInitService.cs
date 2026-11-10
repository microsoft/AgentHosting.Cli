using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// <c>msagent agent init</c>: registers an MCS project that already exists.
/// </summary>
public sealed class ManagedAgentInitService
{
    private readonly IManagedAgentRegistry _agents;
    private readonly AgentConfigLocator _locator;
    private readonly CopilotWorkspaceLocator _workspaceLocator;
    private readonly CopilotEnvironmentResolver _environmentResolver;
    private readonly CopilotProjectIdentityReader _projectIdentities;

    /// <summary>Creates the service.</summary>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="locator">Resolves project and configuration directories.</param>
    /// <param name="workspaceLocator">Reads the project's cloud baseline when it has one.</param>
    /// <param name="environmentResolver">Resolves the environment for an unconnected project.</param>
    /// <param name="projectIdentities">Reads the schema name the workspace declares.</param>
    public ManagedAgentInitService(IManagedAgentRegistry agents, AgentConfigLocator locator, CopilotWorkspaceLocator workspaceLocator, CopilotEnvironmentResolver environmentResolver, CopilotProjectIdentityReader projectIdentities)
    {
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _workspaceLocator = workspaceLocator ?? throw new ArgumentNullException(nameof(workspaceLocator));
        _environmentResolver = environmentResolver ?? throw new ArgumentNullException(nameof(environmentResolver));
        _projectIdentities = projectIdentities ?? throw new ArgumentNullException(nameof(projectIdentities));
    }

    /// <summary>Registers an existing project.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The registered record.</returns>
    public async Task<ManagedAgentInitResult> InitAsync(ManagedAgentInitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new UsageException("`msagent agent init` requires --agent-name.");
        }

        if (!string.IsNullOrWhiteSpace(request.AgentType) && !AgentTypes.IsMcsAgent(request.AgentType))
        {
            throw new UsageException($"`msagent agent init` applies only to MCS agent types, and '{request.AgentType}' is not one.");
        }

        var (projectDirectory, agentLocation, workspaceDirectory, schemaName) = _projectIdentities.FindInitWorkspace(request.McsAgentSource, request.McsWorkflowSource, request.Name, Unregistered);
        var workspace = await _workspaceLocator.FindAsync(workspaceDirectory, cancellationToken).ConfigureAwait(false);
        var environment = await _environmentResolver.ResolveAsync(workspace?.EnvironmentId, cancellationToken).ConfigureAwait(false);

        var stored = _agents.Add(projectDirectory, new ManagedAgentRecord
        {
            AgentId = Guid.NewGuid().ToString(),
            DisplayName = request.Name.Trim(),
            AgentType = AgentTypes.McsAgent,
            AgentLocation = agentLocation,
            McsAgentId = workspace?.AgentId,
            McsSchemaName = schemaName,
            TenantId = environment.TenantId,
            EnvironmentId = environment.EnvironmentId,
            Origin = ManagedAgentOrigins.AgentHostCli,
            EntityTag = string.Empty,
            AgentState = EntityStates.Saved,
        });

        return new ManagedAgentInitResult
        {
            AgentId = stored.AgentId,
            DisplayName = stored.DisplayName,
            AgentType = stored.AgentType,
            TenantId = stored.TenantId,
            EnvironmentId = stored.EnvironmentId,
            Connected = workspace is not null,
            ProjectDirectory = projectDirectory,
            ConfigPath = _locator.ConfigFilePath(projectDirectory),
        };
    }

    private IReadOnlyList<string> Unregistered(string projectDirectory, IReadOnlyList<string> locations)
    {
        var registered = _agents.List(projectDirectory).Select(agent => agent.AgentLocation).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unregistered = locations.Where(location => !registered.Contains(location)).ToArray();

        if (unregistered.Length == 0)
        {
            throw ManagedAgentException.AlreadyRegistered(_agents.List(projectDirectory)[0].DisplayName, _agents.List(projectDirectory)[0].AgentId, _locator.ConfigFilePath(projectDirectory));
        }

        return unregistered;
    }
}

