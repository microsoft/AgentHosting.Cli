using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>An agent AMS registered, with its .config record carrying the repository folder.</summary>
/// <param name="Record">AMS's answer.</param>
/// <param name="Agent">The .config record, carrying its repository folder.</param>
public sealed record AmsRegisteredAgent(AmsMcsAgentRecord Record, ManagedAgentRecord Agent);

/// <summary>Registers an agent of .config in AMS with its repository folder and deployments, then records the folder in .config, removing AMS's records again when .config can't be written.</summary>
public sealed class AmsAgentRegistrar
{
    private readonly IAmsMcsRegistrationTransport _registrations;
    private readonly AmsAgentDeletion _deletion;
    private readonly IManagedAgentRegistry _agents;
    private readonly CopilotProjectIdentityReader _identities;
    private readonly IAgentWorkspaceIdentityPort _workspaces;
    private readonly AgentConfigLocator _locator;
    private readonly AmsTargets _targets;

    /// <summary>Creates the registrar.</summary>
    /// <param name="registrations">Registers the agent.</param>
    /// <param name="deletion">Removes AMS's records when .config can't be written.</param>
    /// <param name="agents">The managed-agent table.</param>
    /// <param name="identities">Reads a workspace's schema name.</param>
    /// <param name="workspaces">Detects a workspace's authoring shape.</param>
    /// <param name="locator">Resolves the agent's workspace.</param>
    /// <param name="targets">Resolves the endpoints of the agent's home environment.</param>
    public AmsAgentRegistrar(IAmsMcsRegistrationTransport registrations, AmsAgentDeletion deletion, IManagedAgentRegistry agents, CopilotProjectIdentityReader identities, IAgentWorkspaceIdentityPort workspaces, AgentConfigLocator locator, AmsTargets targets)
    {
        _registrations = registrations ?? throw new ArgumentNullException(nameof(registrations));
        _deletion = deletion ?? throw new ArgumentNullException(nameof(deletion));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _identities = identities ?? throw new ArgumentNullException(nameof(identities));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    /// <summary>Registers an agent, then records its repository folder in .config.</summary>
    /// <param name="projectDirectory">The project folder.</param>
    /// <param name="agent">The agent's .config record.</param>
    /// <param name="deployments">The deployments AMS creates with the agent.</param>
    /// <param name="repositoryId">The existing repository, or null for a new one.</param>
    /// <param name="claimedBotId">The Copilot Studio agent the home deployment claims, or null.</param>
    /// <param name="claimedWorkflowId">The workflow the home deployment claims, or null.</param>
    /// <param name="home">The agent's home environment.</param>
    /// <param name="command">The command, named when the on-behalf-of endpoint isn't usable.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task<AmsRegisteredAgent> RegisterAsync(string projectDirectory, ManagedAgentRecord agent, IReadOnlyList<AgentDeploymentRecord> deployments, string? repositoryId, string? claimedBotId, string? claimedWorkflowId, PowerPlatformEnvironment home, string command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(deployments);

        var isWorkflow = agent.AgentType == AgentTypes.McsWorkflow;
        var workspace = _locator.ContainedLocation(projectDirectory, agent.AgentLocation) ?? Path.Combine(projectDirectory, agent.AgentLocation);
        var record = await _registrations.RegisterAsync(new AmsMcsRegistration
        {
            Target = _targets.OnBehalfOf(home, command),
            AgentId = agent.AgentId,
            DisplayName = agent.DisplayName,
            Description = agent.Description,
            AgentType = agent.AgentType,
            SchemaName = isWorkflow ? null : agent.McsSchemaName ?? _identities.Find(workspace)?.SchemaName,
            AuthoringShape = isWorkflow ? null : AmsMcsWire.AuthoringShape(_workspaces.DetectAuthoringShape(workspace)),
            RepositoryId = repositoryId,
            Branch = AmsProjectLink.Branch,
            AgentFolder = agent.AgentLocation,
            Deployments = [.. deployments.Select(deployment => new AmsMcsRegistrationDeployment(deployment.DeploymentId, DeploymentStages.ServiceName(deployment.DeploymentType), deployment.DeploymentName, deployment.McsEnvironmentId(), deployment.DeploymentType == DeploymentStages.Dev ? claimedBotId : null, deployment.DeploymentType == DeploymentStages.Dev ? claimedWorkflowId : null))],
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            return new(record, RecordFolder(projectDirectory, agent, new AgentSourceControlMetadata(record.SourceControl?.RepositoryId ?? record.RepositoryId ?? throw new FormatException("The register answer names no repository."), record.SourceControl?.Branch ?? AmsProjectLink.Branch, record.SourceControl?.Directory ?? agent.AgentLocation)));
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            await _deletion.DeleteAsync(home, agent, record.ETag, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Records in .config the repository folder of an agent AMS holds, with the repository id in .config's form.</summary>
    /// <param name="projectDirectory">The project folder.</param>
    /// <param name="agent">The agent's .config record.</param>
    /// <param name="folder">The agent's repository folder, as AMS names it.</param>
    public ManagedAgentRecord RecordFolder(string projectDirectory, ManagedAgentRecord agent, AgentSourceControlMetadata folder)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(folder);

        return _agents.Update(projectDirectory, agent with { SourceControlMetadata = folder with { RepositoryId = AmsIds.Local(folder.RepositoryId) } });
    }
}
