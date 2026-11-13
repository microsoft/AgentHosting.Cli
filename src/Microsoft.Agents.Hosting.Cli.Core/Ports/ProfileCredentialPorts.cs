using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

public interface IProfileCredentialProvider
{
    /// <summary>
    /// Metadata-only ownership check. Foreign or legacy references must not trigger private-cache
    /// reads or cleanup. Implementations with another owned reference scheme can override this.
    /// </summary>
    bool OwnsCredential(AuthProfile profile) =>
        profile.CredentialReference?.StartsWith(AuthCredentialReference.OwnedV1Prefix, StringComparison.Ordinal) == true;

    Task<AuthProfile> AuthenticateAsync(AuthCreateRequest request, CancellationToken ct);
    Task<TokenAcquisitionResult> AcquireAsync(AuthProfile profile, IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken ct);
    Task CompleteCreationAsync(AuthProfile profile, CancellationToken ct);
    Task RemoveOwnedCredentialAsync(AuthProfile profile, CancellationToken ct);
}

public interface IOwnedProfileCredentialCleanup
{
    Task ClearOwnedCredentialsAsync(CancellationToken ct);
}

/// <summary>
/// Reads token-free cached account metadata for an explicit profile snapshot. Implementations inspect
/// only that profile's owned nonbroker user cache and never renew tokens, observe authentication,
/// enumerate operating-system accounts, open identity UI, or activate a profile.
/// </summary>
public interface IProfileCachedAccountReader
{
    /// <summary>
    /// Returns accounts from the snapshot's private user cache, or an empty list for a principal or
    /// credential method without such a cache. Workload and DefaultAzureCredential profiles must not
    /// construct or invoke external credentials merely to report metadata status.
    /// </summary>
    Task<IReadOnlyList<AuthAccount>> GetAccountsAsync(
        AuthProfile profile,
        CancellationToken cancellationToken);
}
