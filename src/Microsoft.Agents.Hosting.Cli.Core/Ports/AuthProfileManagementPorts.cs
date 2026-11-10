namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>Persisted credential intent; these values do not implement credential acquisition.</summary>
public enum AuthCredentialMethod
{
    Broker,
    Browser,
    DeviceCode,
    Password,
    ClientSecret,
    Certificate,
    DefaultAzureCredential,
    GitHubFederated,
    AzureDevOpsFederated,
}

/// <summary>The principal represented by an authentication profile.</summary>
public enum AuthPrincipalType
{
    User,
    ServicePrincipal,
}

/// <summary>Exactly one one-based transient index or ordinal, case-sensitive name.</summary>
public sealed record AuthProfileSelector(int? Index = null, string? Name = null);

/// <summary>Null preserves a field; an empty name clears it. Updating never selects a profile.</summary>
public sealed record AuthProfileUpdate(string? Name = null, ActiveEnvironment? DefaultEnvironment = null);

/// <summary>The deleted profile and resulting active profile, from the same committed mutation.</summary>
public sealed record AuthProfileDeletion(AuthProfile Deleted, AuthProfile? Active);

/// <summary>Committed create and its replaced snapshot from the same metadata transaction.</summary>
public sealed record AuthProfileCreation(AuthProfile Committed, AuthProfile? Replaced);

/// <summary>
/// Optional atomic replacement inventory. Lifecycle create requires this extension; existing
/// session-only stores and facade constructors remain compatible.
/// </summary>
public interface IAuthProfileCreationStore
{
    Task<AuthProfileCreation> CreateWithReplacementAsync(AuthProfile profile, CancellationToken cancellationToken);
}

/// <summary>Token-free Universal profile lifecycle and optimistic environment selection.</summary>
public interface IAuthProfileManagementStore : IAuthProfileStore
{
    /// <summary>
    /// Creates or removes an identity-matched profile and appends its replacement, retaining its
    /// opaque identity and advancing its revision. Activates the appended profile.
    /// </summary>
    Task<AuthProfile> CreateAsync(AuthProfile profile, CancellationToken cancellationToken);

    /// <summary>Selects exactly the addressed profile.</summary>
    Task<AuthProfile> SelectAsync(AuthProfileSelector selector, CancellationToken cancellationToken);

    /// <summary>Updates only the addressed metadata without changing active ownership.</summary>
    Task<AuthProfile> UpdateAsync(AuthProfileSelector selector, AuthProfileUpdate update, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically updates caller-validated metadata for the exact captured profile and revision,
    /// without activation. Unlike selector updates, this cannot retarget after asynchronous work.
    /// </summary>
    Task<AuthProfile> UpdateAsync(string profileId, long expectedRevision, AuthProfileUpdate update, CancellationToken cancellationToken);

    /// <summary>Deletes the addressed profile; deleting active selects the first remaining profile.</summary>
    Task<AuthProfileDeletion> DeleteAsync(AuthProfileSelector selector, CancellationToken cancellationToken);

    /// <summary>
    /// Persists caller-validated environment metadata only if the profile remains active at the
    /// expected revision. Environment validation must occur outside the store transaction.
    /// </summary>
    Task<AuthProfile> SetDefaultEnvironmentAsync(string profileId, long expectedRevision, ActiveEnvironment environment, CancellationToken cancellationToken);
}
