namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Exact opaque package bytes to upload for one existing Agent.</summary>
public sealed record AgentPackageUploadRequest
{
    public string? AgentId { get; init; }
    public string? PackagePath { get; init; }
    public string? EnvironmentId { get; init; }
}

/// <summary>The validated terminal Package Upload Operation and immutable Package Revision.</summary>
public sealed record AgentPackageUploadResult : ITextGridRenderable
{
    public int SchemaVersion { get; init; } = 1;
    public required string Status { get; init; }
    public required string TenantId { get; init; }
    public required string EnvironmentId { get; init; }
    public required string AgentId { get; init; }
    public required string PackageOperationId { get; init; }
    public required string PackageRevisionId { get; init; }
    public required string PackageDigest { get; init; }
    public required long PackageSizeInBytes { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        "Agent package uploaded.",
        $"  agent: {AgentId}",
        $"  environment: {EnvironmentId} (tenant {TenantId})",
        $"  package operation: {PackageOperationId}",
        $"  Package Revision: {PackageRevisionId}, {PackageSizeInBytes} bytes, sha256 {PackageDigest}",
        "This confirms package upload only. It does not create or project a Deployment.",
    ];
}

/// <summary>The confirmed durable operation identity from an unsuccessful package upload response.</summary>
public sealed record AgentPackageUploadPartialResult : ITextGridRenderable
{
    public int SchemaVersion { get; init; } = 1;
    public required string PackageOperationId { get; init; }
    public required string OperationStatus { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() =>
    [
        $"Package upload operation: {PackageOperationId}",
        $"  confirmed status: {OperationStatus}",
    ];
}
