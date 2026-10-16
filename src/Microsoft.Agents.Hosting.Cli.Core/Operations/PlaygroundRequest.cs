namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>One invocation's playground selectors and launch preferences.</summary>
public sealed record PlaygroundRequest
{
    /// <summary>The connection transport: directengine (default) or directline.</summary>
    public string Target { get; init; } = "directengine";

    /// <summary>The local project directory holding agent.config.json.</summary>
    public string? Project { get; init; }

    /// <summary>The managed-agent record id.</summary>
    public string? AgentId { get; init; }

    /// <summary>The managed-agent record name.</summary>
    public string? AgentName { get; init; }

    /// <summary>The MCS schema name.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The deployment record id.</summary>
    public string? DeploymentId { get; init; }

    /// <summary>The deployment name.</summary>
    public string? DeploymentName { get; init; }

    /// <summary>The destination environment id.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>The destination tenant id.</summary>
    public string? Tenant { get; init; }

    /// <summary>The explicitly selected DirectEngine agent harness, absent for Direct Line.</summary>
    public string? AgentType { get; init; }

    /// <summary>Whether machine-readable output was requested.</summary>
    public bool Json { get; init; }

    /// <summary>Whether transient progress should be suppressed.</summary>
    public bool Plain { get; init; }

    /// <summary>Whether prompting is forbidden.</summary>
    public bool NonInteractive { get; init; }

    /// <summary>Whether required unattended playground installation is authorized; interactive installation still requires explicit user consent.</summary>
    public bool InstallPlayground { get; init; }

    /// <summary>Projects only the target selectors needed by the existing live-test resolver.</summary>
    public LiveTestRequest ToLiveTestRequest() => new()
    {
        Project = Project,
        AgentId = AgentId,
        AgentName = AgentName,
        SchemaName = SchemaName,
        DeploymentId = DeploymentId,
        DeploymentName = DeploymentName,
        EnvironmentId = EnvironmentId,
        Tenant = Tenant,
        AgentType = AgentType ?? string.Empty,
    };
}
