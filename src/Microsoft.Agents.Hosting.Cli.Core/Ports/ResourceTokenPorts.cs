using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Auth;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>Acquires and checks a resource token for explicit immutable authentication coordinates.</summary>
public interface IProfileResourceTokenProvider
{
    Task<ValidatedResourceToken> AcquireAsync(
        EnvironmentResolutionContext context, string audience, string tenantId,
        IReadOnlyList<string> requiredPermissions, CancellationToken cancellationToken,
        IReadOnlyList<string>? explicitScopes = null);
}

/// <summary>Resource-token claims checked against the same context used for acquisition.</summary>
public sealed record ValidatedResourceToken
{
    internal ValidatedResourceToken(TokenAcquisitionResult token, PpapiTokenClaims claims,
        EnvironmentResolutionContext context, string tenantId)
    {
        Token = token;
        Claims = claims;
        Context = context;
        TenantId = tenantId;
    }

    [JsonIgnore] public TokenAcquisitionResult Token { get; }
    public PpapiTokenClaims Claims { get; }
    [JsonIgnore] public EnvironmentResolutionContext Context { get; }
    public string TenantId { get; }
}

/// <summary>A validated tenant-local user identity reusable within one operation.</summary>
public sealed record ResourceTenantPrincipal
{
    internal ResourceTenantPrincipal(EnvironmentResolutionContext context, string tenantId, string objectId, string? username)
    {
        Context = context;
        TenantId = tenantId;
        ObjectId = objectId;
        Username = username;
    }

    [JsonIgnore] public EnvironmentResolutionContext Context { get; }
    public string TenantId { get; }
    public string ObjectId { get; }
    public string? Username { get; }
}
