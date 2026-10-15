namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>
/// Exact opaque package bytes for the deployed v1 upload route. Repository and storage coordinates
/// remain service-owned and are absent from this contract.
/// </summary>
public sealed record AmsPackagePublicationTransportRequest
{
    public required Uri Endpoint { get; init; }
    public required string Audience { get; init; }
    public required string TenantId { get; init; }
    public required string EnvironmentId { get; init; }
    public required string AgentId { get; init; }
    public required Stream PackageStream { get; init; }
    public required long PackageLength { get; init; }
}

/// <summary>The terminal durable Package Upload Operation returned by the v1 route.</summary>
public sealed record AmsPackagePublicationResult
{
    public string? ClientRequestId { get; init; }
    public string? ServiceRequestId { get; init; }
    public required string OperationId { get; init; }
    public required string AgentId { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeInBytes { get; init; }
    public required string Status { get; init; }
    public required string? PackageRevisionId { get; init; }
    public required DateTimeOffset CreatedTime { get; init; }
    public required DateTimeOffset ChangedTime { get; init; }
}

public interface IAmsPackagePublicationTransport
{
    Task<AmsPackagePublicationResult> PublishAsync(
        AmsPackagePublicationTransportRequest request,
        CancellationToken cancellationToken);
}
