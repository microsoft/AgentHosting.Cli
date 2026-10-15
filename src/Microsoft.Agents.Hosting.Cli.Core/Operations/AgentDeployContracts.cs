namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>
/// Explicit desired Deployment state for the current fN13j target-routed projection path. It consumes
/// an existing immutable Package Revision and carries no package bytes.
/// </summary>
public sealed record AgentDeployRequest
{
    public const int DeploymentNameMaxLength = 64;

    public string? AgentId { get; init; }
    public string? PackageRevisionId { get; init; }
    public string? EnvironmentId { get; init; }
    public required string TargetEnvironmentId { get; init; }
    public string? DeploymentName { get; init; }
    public string? DeploymentType { get; init; }
    public string? EntraId { get; init; }
    public string? EntraIdType { get; init; }
}

/// <summary>Replaces the package selected by an existing AMS deployment.</summary>
public sealed record AgentDeploymentPackageUpdateRequest
{
    public string? AgentId { get; init; }
    public string? DeploymentId { get; init; }
    public string? PackageRevisionId { get; init; }
    public string? EnvironmentId { get; init; }
}

/// <summary>
/// Result of current Deployment creation or package update and its awaited target-routed AHRG
/// projection. This is transitional evidence and never claims a durable public Deploy Operation or
/// cross-island proof.
/// </summary>
public sealed record AgentDeployResult : ITextGridRenderable
{
    public int SchemaVersion { get; init; } = 1;
    public required string Status { get; init; }
    public required string TenantId { get; init; }
    public required string HomeEnvironmentId { get; init; }
    public required string TargetEnvironmentId { get; init; }
    public required string AgentId { get; init; }
    public required string PackageRevisionId { get; init; }
    public required string PackageDigest { get; init; }
    public required long PackageSizeInBytes { get; init; }

    public required string DeploymentId { get; init; }

    public required string DeploymentName { get; init; }

    public required string DeploymentType { get; init; }

    public required string EntraId { get; init; }

    public required string EntraIdType { get; init; }

    public required string RuntimeEndpoint { get; init; }

    public required string PackageSource { get; init; }

    public bool ProjectionAcknowledged { get; init; }

    public bool PublicDeployOperationCreated { get; init; }

    public bool TargetLocalPackageStagingPerformed { get; init; }

    public bool RuntimeReadinessProven { get; init; }

    public bool CrossIslandDemonstrated { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        Status == "agent-deployment-updated"
            ? "Deployment package updated and target-routed AHRG projection acknowledged."
            : "Deployment created and target-routed AHRG projection acknowledged.",
        $"  agent: {AgentId}",
        $"  home environment: {HomeEnvironmentId} (tenant {TenantId})",
        $"  target environment: {TargetEnvironmentId}",
        $"  Package Revision: {PackageRevisionId}, {PackageSizeInBytes} bytes, sha256 {PackageDigest}",
        $"  Deployment: {DeploymentName} ({DeploymentId}, {DeploymentType})",
        $"  Entra identity: {EntraId} ({EntraIdType})",
        $"  runtime endpoint: {RuntimeEndpoint}",
        "Package source: current GRS; target-local package staging was not performed.",
        "Public Deploy Operation: not created by the current PPRP implementation.",
        "Cross-island call: not demonstrated. That requires an explicit distinct-island Test deployment.",
        "Runtime readiness: not proven.",
    ];
}
