using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>The identity a local MCS project declares for itself.</summary>
public sealed record ProjectIdentity
{
    /// <summary>The project's schema name.</summary>
    public required string SchemaName { get; init; }

    /// <summary>The project's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The project's description, when it declares one.</summary>
    public string? Description { get; init; }
}

/// <summary>The agent workspace <c>msagent agent init</c> registers or creates an agent from, with the project folder it was found in and the schema name it declares.</summary>
/// <param name="ProjectDirectory">The project folder the workspace was found in.</param>
/// <param name="AgentLocation">The workspace's location relative to the project folder.</param>
/// <param name="WorkspaceDirectory">The workspace folder.</param>
/// <param name="SchemaName">The schema name the workspace declares.</param>
public sealed record AgentInitWorkspace(string ProjectDirectory, string AgentLocation, string WorkspaceDirectory, string SchemaName);

/// <summary>Reads a workspace's identity from its settings file.</summary>
public sealed class CopilotProjectIdentityReader
{
    private readonly AgentConfigLocator _locator;
    private readonly IAgentWorkspaceIdentityPort _identities;

    /// <summary>Creates the reader.</summary>
    /// <param name="locator">Resolves workspace directories and their settings files.</param>
    /// <param name="identities">Reads the identity a workspace's settings file declares.</param>
    public CopilotProjectIdentityReader(AgentConfigLocator locator, IAgentWorkspaceIdentityPort identities)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _identities = identities ?? throw new ArgumentNullException(nameof(identities));
    }

    /// <summary>Reads the identity a workspace declares, failing when there is no readable project.</summary>
    /// <param name="workspaceDirectory">The directory holding the workspace.</param>
    /// <param name="agentId">The record the identity is being read for, named in the failure.</param>
    /// <returns>The workspace's declared identity.</returns>
    public ProjectIdentity Require(string workspaceDirectory, string agentId)
    {
        var resolved = _locator.ResolveProjectDirectory(workspaceDirectory);

        return _identities.Read(resolved) ?? throw (AgentConfigLocator.ProjectFile(resolved) is { } projectFile ? CopilotStudioException.ProjectIdentityUnreadable(agentId, projectFile) : ManagedAgentException.SchemaNameUnavailable(agentId, resolved));
    }

    /// <summary>Reads the identity a workspace declares, or <see langword="null"/> when there is none.</summary>
    /// <param name="workspaceDirectory">The directory holding the workspace.</param>
    /// <returns>The workspace's declared identity, or <see langword="null"/>.</returns>
    public ProjectIdentity? Find(string workspaceDirectory) => _identities.Read(_locator.ResolveProjectDirectory(workspaceDirectory));

    /// <summary>Finds the one agent workspace <c>msagent agent init</c> registers or creates an agent from, in the folder --mcs-agent-source names or else the enclosing project, refusing --mcs-workflow-source, a folder that holds no workspace init may take or several, and a workspace whose schema name cannot be read.</summary>
    /// <param name="agentSource">The --mcs-agent-source folder, or null for the enclosing project.</param>
    /// <param name="workflowSource">The --mcs-workflow-source folder, which applies to a standalone workflow only.</param>
    /// <param name="agentName">The --agent-name, named when the workspace is missing.</param>
    /// <param name="candidates">Narrows the workspace locations the folder holds to the ones init may take, refusing when none remains.</param>
    /// <returns>The workspace and the schema name it declares.</returns>
    public AgentInitWorkspace FindInitWorkspace(string? agentSource, string? workflowSource, string agentName, Func<string, IReadOnlyList<string>, IReadOnlyList<string>> candidates)
    {
        ArgumentNullException.ThrowIfNull(agentName);
        ArgumentNullException.ThrowIfNull(candidates);

        if (!string.IsNullOrWhiteSpace(workflowSource))
        {
            throw new UsageException("--mcs-workflow-source applies only to `--agent-type MCSWorkflow`. Use --mcs-agent-source, or add --agent-type MCSWorkflow.");
        }

        var projectDirectory = AgentConfigLocator.RequireExistingDirectory(_locator.ResolveProjectDirectory(agentSource));
        if (_locator.WorkspaceLocations(projectDirectory) is not { Count: > 0 } locations)
        {
            throw ManagedAgentException.ProjectNotFound(projectDirectory);
        }

        var available = candidates(projectDirectory, locations);
        if (available.Count > 1)
        {
            throw ManagedAgentException.WorkspaceAmbiguous(available);
        }

        var agentLocation = available[0];
        var workspaceDirectory = _locator.AgentWorkspaceDirectory(projectDirectory, agentLocation) ?? throw ManagedAgentException.WorkspaceMissing(agentName.Trim(), agentLocation, projectDirectory);
        var schemaName = Find(workspaceDirectory)?.SchemaName;
        return string.IsNullOrWhiteSpace(schemaName) ? throw ManagedAgentException.WorkspaceSchemaUnreadable(workspaceDirectory, agentLocation) : new AgentInitWorkspace(projectDirectory, agentLocation, workspaceDirectory, schemaName);
    }
}
