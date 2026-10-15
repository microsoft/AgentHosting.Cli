namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

public sealed record AmsAgentReadTransportRequest
{
    public required Uri Endpoint { get; init; }
    public required string Audience { get; init; }
    public required string TenantId { get; init; }
    public required string EnvironmentId { get; init; }
    public required string AgentId { get; init; }
}

public sealed record AmsAgentReadResult
{
    public required string AgentId { get; init; }
    public required string TenantId { get; init; }
    public required string EnvironmentId { get; init; }
    public required string RepositoryId { get; init; }
    public required string RepositoryBindingStatus { get; init; }
    public required string AgentType { get; init; }
    public required string Language { get; init; }
    public required string LanguageVersion { get; init; }
}

public interface IAmsAgentReadTransport
{
    Task<AmsAgentReadResult> GetAsync(
        AmsAgentReadTransportRequest request,
        CancellationToken cancellationToken);
}

public sealed record AmsDeploymentCreateTransportRequest
{
    public required Uri Endpoint { get; init; }
    public required string Audience { get; init; }
    public required string TenantId { get; init; }
    public required string HomeEnvironmentId { get; init; }
    public required string TargetEnvironmentId { get; init; }
    public required string AgentId { get; init; }
    public required string PackageRevisionId { get; init; }
    public required string DeploymentName { get; init; }
    public required string DeploymentType { get; init; }
    public required string EntraId { get; init; }
    public required string EntraIdType { get; init; }
    public required string Language { get; init; }
    public required string LanguageVersion { get; init; }
}

public sealed record AmsDeploymentCreateResult
{
    public string? ClientRequestId { get; init; }
    public string? ServiceRequestId { get; init; }
    public required string DeploymentId { get; init; }
    public required string AgentId { get; init; }
    public required string TenantId { get; init; }
    public required string HomeEnvironmentId { get; init; }
    public required string TargetEnvironmentId { get; init; }
    public required string PackageRevisionId { get; init; }
    public required string PackageSha256 { get; init; }
    public required long PackageSizeInBytes { get; init; }
    public required string AgentType { get; init; }
    public required string DeploymentName { get; init; }
    public required string DeploymentType { get; init; }
    public required string EntraId { get; init; }
    public required string EntraIdType { get; init; }
    public required string Language { get; init; }
    public required string LanguageVersion { get; init; }
    public required string RuntimeEndpoint { get; init; }
    public bool IsCustomEndpoint { get; init; }
    public bool IsQuarantined { get; init; }
}

/// <summary>Coordinates for reading an existing deployment through the public control plane.</summary>
public record AmsDeploymentReadTransportRequest
{
    public required Uri Endpoint { get; init; }
    public required string Audience { get; init; }
    public required string TenantId { get; init; }
    public required string HomeEnvironmentId { get; init; }
    public required string AgentId { get; init; }
    public required string DeploymentId { get; init; }
}

/// <summary>A conditional package replacement using the package revision observed by the preceding read.</summary>
public sealed record AmsDeploymentUpdateTransportRequest : AmsDeploymentReadTransportRequest
{
    public required string PackageRevisionId { get; init; }
    public required string ExpectedPackageRevisionId { get; init; }
}

/// <summary>Authoritative deployment identity and revision needed before a conditional update.</summary>
public sealed record AmsDeploymentCurrentResult
{
    public string? ClientRequestId { get; init; }
    public string? ServiceRequestId { get; init; }
    public required string DeploymentId { get; init; }
    public required string TenantId { get; init; }
    public required string HomeEnvironmentId { get; init; }
    public required string AgentId { get; init; }
    public required string PackageRevisionId { get; init; }
}

public interface IAmsDeploymentTransport
{
    Task<AmsDeploymentCurrentResult> GetAsync(
        AmsDeploymentReadTransportRequest request,
        CancellationToken cancellationToken);

    Task<AmsDeploymentCreateResult> CreateAsync(
        AmsDeploymentCreateTransportRequest request,
        CancellationToken cancellationToken);

    Task<AmsDeploymentCreateResult> UpdateAsync(
        AmsDeploymentUpdateTransportRequest request,
        CancellationToken cancellationToken);
}
