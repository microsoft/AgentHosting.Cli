using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Resolves the signed-in operator to the principal the registry records.
///
/// The service resolves these from the caller's token. Here the durable identity is the active auth
/// profile's account key, which MSAL forms as <c>{objectId}.{tenantId}</c> — the leading segment is the
/// principal's object id, and is what a later Agent Management Service row would carry.
/// </summary>
public sealed class ManagedAgentPrincipalResolver
{
    private readonly IAuthProfileStore _profiles;

    /// <summary>Creates the resolver.</summary>
    /// <param name="profiles">The signed-in profile store.</param>
    public ManagedAgentPrincipalResolver(IAuthProfileStore profiles)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    /// <summary>Resolves the caller, or <see langword="null"/> when nobody is signed in.</summary>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The caller's principal.</returns>
    public async Task<PrincipalReference?> ResolveAsync(CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);

        return profile is null ? null : new PrincipalReference
        {
            ObjectId = ObjectIdOf(profile.AccountKey),
            ObjectType = PrincipalObjectTypes.User,
        };
    }

    /// <summary>Resolves the caller's sign-in name, or <see langword="null"/> when nobody is signed in.</summary>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The caller's user principal name.</returns>
    public async Task<string?> ResolveUsernameAsync(CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(profile?.Username) ? null : profile.Username;
    }

    /// <summary>Extracts the object id from an MSAL home-account id.</summary>
    /// <param name="accountKey">The account key, normally <c>{objectId}.{tenantId}</c>.</param>
    /// <returns>The object id, or the whole key when it carries no tenant segment.</returns>
    public static string ObjectIdOf(string accountKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);

        var separator = accountKey.IndexOf('.', StringComparison.Ordinal);

        return separator > 0 ? accountKey[..separator] : accountKey;
    }
}
