using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Identifies whether a live-test target came from local records or caller-supplied values.</summary>
public enum LiveTestTargetSource
{
    /// <summary>The target was resolved from the read-only managed-agent configuration.</summary>
    Configured,

    /// <summary>The target was described completely by the caller.</summary>
    Explicit,
}

/// <summary>The explicitly selected runtime behind a live agent target.</summary>
public enum LiveTestAgentType
{
    /// <summary>A standard published Copilot Studio agent.</summary>
    Standard,

    /// <summary>A GitHub Copilot Harness agent served by the agentic runtime endpoint.</summary>
    GitHubCopilot,
}

/// <summary>Selectors and assertions used to resolve one live-test destination.</summary>
public sealed record LiveTestTargetRequest
{
    /// <summary>The project whose managed-agent configuration should be read.</summary>
    public string? Project { get; init; }

    /// <summary>The managed-agent record identifier.</summary>
    public string? AgentId { get; init; }

    /// <summary>The managed-agent record display name.</summary>
    public string? AgentName { get; init; }

    /// <summary>The Copilot Studio schema name.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The deployment record identifier or explicit operator label.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The deployment record name or explicit operator label.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>The destination environment identifier.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>The destination tenant identifier.</summary>
    public string? TenantId { get; init; }

    /// <summary>The runtime selected for the target.</summary>
    public LiveTestAgentType AgentType { get; init; } = LiveTestAgentType.Standard;

    /// <summary>The Power Platform cloud. Live MCS targets currently support public cloud only.</summary>
    public CloudInstance Cloud { get; init; } = CloudInstance.Public;
}

/// <summary>
/// One immutable, invocation-owned MCS target. A configured target retains its source records only
/// for in-memory orchestration; the explicit route never consults or claims a registry association.
/// </summary>
public sealed record ResolvedMcsTarget
{
    /// <summary>How this target was selected.</summary>
    public required LiveTestTargetSource Source { get; init; }

    /// <summary>The public cloud selected for the target.</summary>
    public CloudInstance Cloud { get; init; } = CloudInstance.Public;

    /// <summary>The destination tenant.</summary>
    public required string TenantId { get; init; }

    /// <summary>The destination environment.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The MCS schema name.</summary>
    public required string SchemaName { get; init; }

    /// <summary>The explicitly selected runtime.</summary>
    public LiveTestAgentType AgentType { get; init; } = LiveTestAgentType.Standard;

    /// <summary>The start/query conversations endpoint derived from the validated target.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>The selected managed-agent record, or <see langword="null"/> for an explicit target.</summary>
    public ManagedAgentRecord? Agent { get; init; }

    /// <summary>The selected deployment record, or <see langword="null"/> for an explicit target.</summary>
    public AgentDeploymentRecord? Deployment { get; init; }

    /// <summary>The explicit deployment identifier/label, when one was supplied.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The explicit deployment name/label, when one was supplied.</summary>
    public string? DeploymentName { get; init; }
}
