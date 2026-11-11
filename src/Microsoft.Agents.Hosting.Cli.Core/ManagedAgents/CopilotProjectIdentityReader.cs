using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
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

    /// <summary>The shape the project was authored as: <see cref="CopilotAuthoringModes.CliCopilot"/> or <see cref="CopilotAuthoringModes.Classic"/>.</summary>
    public required string AuthoringMode { get; init; }
}

/// <summary>Reads the identity a workspace declares in its settings.</summary>
public sealed class CopilotProjectIdentityReader
{
    private readonly AgentConfigLocator _locator;
    private readonly ICopilotProjectIdentityPort _identities;

    /// <summary>Creates the reader.</summary>
    /// <param name="locator">Resolves workspace directories.</param>
    /// <param name="identities">Reads the identity a project declares.</param>
    public CopilotProjectIdentityReader(AgentConfigLocator locator, ICopilotProjectIdentityPort identities)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _identities = identities ?? throw new ArgumentNullException(nameof(identities));
    }

    /// <summary>Reads the identity a workspace declares, failing when there is no readable project.</summary>
    /// <param name="workspaceDirectory">The directory holding the workspace.</param>
    /// <param name="agentId">The record the identity is being read for, named in the failure.</param>
    /// <returns>The workspace's declared identity.</returns>
    public ProjectIdentity Require(string workspaceDirectory, string agentId) => Find(workspaceDirectory) ?? throw ManagedAgentException.SchemaNameUnavailable(agentId, _locator.ResolveProjectDirectory(workspaceDirectory));

    /// <summary>Reads the identity a workspace declares, or <see langword="null"/> when there is none.</summary>
    /// <param name="workspaceDirectory">The directory holding the workspace.</param>
    /// <returns>The workspace's declared identity, or <see langword="null"/>.</returns>
    public ProjectIdentity? Find(string workspaceDirectory) => _identities.Read(_locator.ResolveProjectDirectory(workspaceDirectory));

    /// <summary>Finds the schema name an agent deploys under, reading its workspace only when its record names none.</summary>
    /// <param name="agent">The agent's record.</param>
    /// <param name="workspaceDirectory">The directory holding the agent's workspace, or <see langword="null"/> when it has none.</param>
    /// <returns>The schema name, or <see langword="null"/> when neither the record nor the workspace names one.</returns>
    public string? FindSchemaName(ManagedAgentRecord agent, string? workspaceDirectory)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (!string.IsNullOrWhiteSpace(agent.McsSchemaName))
        {
            return agent.McsSchemaName;
        }

        return workspaceDirectory is null ? null : Find(workspaceDirectory)?.SchemaName;
    }

    /// <summary>Finds what an agent is looked up by in an environment: its schema name, else the cloud agent identifier its record carries.</summary>
    /// <param name="agent">The agent's record.</param>
    /// <param name="workspaceDirectory">The directory holding the agent's workspace, or <see langword="null"/> when it has none.</param>
    /// <returns>The schema name or cloud agent identifier, or <see langword="null"/> when the agent has neither.</returns>
    public string? FindSelector(ManagedAgentRecord agent, string? workspaceDirectory) => FindSchemaName(agent, workspaceDirectory) ?? (string.IsNullOrWhiteSpace(agent.McsAgentId) ? null : agent.McsAgentId.Trim());
}
