namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Allowlisted environment metadata without internal ownership or credential references.</summary>
public sealed record AuthEnvironmentInfo(
    string EnvironmentId, string DisplayName, string TenantId, string ApiEndpoint, string? InstanceUrl);

/// <summary>Allowlisted profile metadata. The index is one-based and transient.</summary>
public sealed record AuthProfileInfo
{
    public required string ProfileId { get; init; }
    public int? Index { get; init; }
    public bool IsActive { get; init; }
    public string? Name { get; init; }
    public required string Account { get; init; }
    public string? ClientId { get; init; }
    public Ports.AuthPrincipalType PrincipalType { get; init; }
    public Ports.AuthCredentialMethod CredentialMethod { get; init; }
    public string? TenantId { get; init; }
    public required string Cloud { get; init; }
    public string? ConnectionUrl { get; init; }
    public AuthEnvironmentInfo? DefaultEnvironment { get; init; }
}

public sealed record AuthProfileListResult(IReadOnlyList<AuthProfileInfo> Profiles);
public sealed record AuthProfileOperationResult(string Status, AuthProfileInfo Profile, AuthProfileInfo? Active = null);
public sealed record AuthWhoResult(string Status, AuthProfileInfo? Profile, AuthEnvironmentInfo? Environment, bool? EnvironmentBound);

public sealed record AuthProfileSelectRequest
{
    public Ports.AuthProfileSelector Selector { get; init; } = new();
    public bool NonInteractive { get; init; }
}

/// <summary>Optional lifecycle surface, independent of the legacy embedding facade.</summary>
public interface IAuthProfileOperations
{
    Task<AuthProfileOperationResult> CreateAsync(AuthCreateRequest request, CancellationToken cancellationToken);
    Task<AuthProfileListResult> ListAsync(CancellationToken cancellationToken);
    Task<AuthProfileOperationResult> SelectAsync(AuthProfileSelectRequest request, CancellationToken cancellationToken);
    Task<AuthProfileOperationResult> DeleteAsync(Ports.AuthProfileSelector selector, CancellationToken cancellationToken);
    Task<AuthProfileOperationResult> NameAsync(Ports.AuthProfileSelector selector, string name, CancellationToken cancellationToken);
    Task<AuthWhoResult> WhoAsync(CancellationToken cancellationToken);
    Task<AuthLogoutResult> ClearAsync(CancellationToken cancellationToken);
}

/// <summary>Metadata-only picker. Returns an opaque profile ID from the supplied snapshot, or cancellation.</summary>
public interface IAuthProfilePicker
{
    Task<string?> PickAsync(IReadOnlyList<AuthProfileInfo> profiles, CancellationToken cancellationToken);
}
