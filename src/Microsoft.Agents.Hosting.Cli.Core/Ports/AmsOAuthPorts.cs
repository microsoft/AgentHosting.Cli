using Microsoft.Agents.Hosting.Cli.Core.Deployments;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>One OAuth connection of the bot AMS bound to a deployment.</summary>
public sealed record AmsOAuthConnectionTransportRequest
{
    public required AmsDeploymentReadTransportRequest Deployment { get; init; }
    public required string ConnectionName { get; init; }
}

/// <summary>The settings an OAuth connection request sends; on update, absent values stay as they are and an empty value clears an optional one.</summary>
public sealed record AmsOAuthSettings(string? ClientId, string? ClientSecret, string? Scopes, IReadOnlyDictionary<string, string?> Parameters);

/// <summary>An OAuth connection to change with the given settings.</summary>
public sealed record AmsOAuthUpdateTransportRequest
{
    public required AmsDeploymentReadTransportRequest Deployment { get; init; }
    public required string ConnectionName { get; init; }
    public required AmsOAuthSettings Settings { get; init; }
}

/// <summary>An OAuth connection to create with the named provider and the given settings.</summary>
public sealed record AmsOAuthCreateTransportRequest
{
    public required AmsDeploymentReadTransportRequest Deployment { get; init; }
    public required string ConnectionName { get; init; }
    public required string ServiceProviderName { get; init; }
    public required AmsOAuthSettings Settings { get; init; }
}

/// <summary>An OAuth connection AMS returned from a create, with whether the create made it.</summary>
public sealed record AmsOAuthCreateResult(DeploymentOAuthConnection Connection, bool IsCreated);

/// <summary>Manages the OAuth connections of the Azure Bot Service instance AMS bound to a code-agent deployment.</summary>
public interface IAmsOAuthTransport
{
    /// <summary>Lists the OAuth providers AMS can create connections for on the deployment.</summary>
    Task<IReadOnlyList<OAuthProviderDefinition>> ListProvidersAsync(AmsDeploymentReadTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Lists the OAuth connections of the deployment's bot.</summary>
    Task<IReadOnlyList<DeploymentOAuthConnection>> ListAsync(AmsDeploymentReadTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Reads one OAuth connection of the deployment's bot.</summary>
    Task<DeploymentOAuthConnection> GetAsync(AmsOAuthConnectionTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Creates an OAuth connection, or returns the existing connection unchanged.</summary>
    Task<AmsOAuthCreateResult> CreateAsync(AmsOAuthCreateTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Changes an OAuth connection and returns it as AMS wrote it.</summary>
    Task<DeploymentOAuthConnection> UpdateAsync(AmsOAuthUpdateTransportRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes an OAuth connection; an absent connection counts as deleted.</summary>
    Task DeleteAsync(AmsOAuthConnectionTransportRequest request, CancellationToken cancellationToken);
}
