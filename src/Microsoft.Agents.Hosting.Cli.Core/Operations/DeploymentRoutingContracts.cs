using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Selects the deployment or agent whose type a unified <c>msagent deployment</c> verb must resolve.</summary>
public sealed record DeploymentAgentTypeQuery
{
    /// <summary>The project directory holding the configuration, or null for the current directory.</summary>
    public string? Project { get; init; }

    /// <summary>The deployment id, when the verb addresses an existing deployment.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The deployment name, when the verb addresses an existing deployment by name.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>The owning agent id.</summary>
    public string? AgentId { get; init; }

    /// <summary>The owning agent name.</summary>
    public string? AgentName { get; init; }

    /// <summary>Whether any Copilot Studio option was supplied, used only when no record resolves.</summary>
    public bool HasMcsOptions { get; init; }

    /// <summary>Whether any Azure option was supplied, used only when no record resolves.</summary>
    public bool HasCodeAgentOptions { get; init; }
}

/// <summary>Where a resolved agent type came from, so callers can explain an ambiguous selection.</summary>
public enum DeploymentAgentTypeSource
{
    DeploymentRecord,
    AgentRecord,
    OptionShape,
    Default,
    Unresolved,
}

/// <summary>The agent type a unified <c>msagent deployment</c> verb resolved, and how it was determined.</summary>
public sealed record DeploymentAgentTypeResult
{
    /// <summary>The resolved agent type, one of <see cref="AgentTypes"/>.</summary>
    public required string AgentType { get; init; }

    /// <summary>How the type was determined.</summary>
    public required DeploymentAgentTypeSource Source { get; init; }

    /// <summary>Whether the resolved type names a Copilot Studio agent or a standalone workflow.</summary>
    public bool IsMcs => AgentTypes.IsMcs(AgentType);
}
