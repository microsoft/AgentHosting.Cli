using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Supplies the sync library with bearer tokens for whichever audience it asks for, by delegating to
/// the CLI's existing silent token provider.
/// </summary>
public sealed class AgentHostSyncAuthProvider : ISyncAuthProvider
{
    private readonly ITokenProvider _tokenProvider;

    /// <summary>Creates a provider backed by <paramref name="tokenProvider"/>.</summary>
    /// <param name="tokenProvider">The CLI's silent, multi-audience token provider.</param>
    public AgentHostSyncAuthProvider(ITokenProvider tokenProvider) => _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));

    /// <summary>
    /// Acquires a bearer token for <paramref name="audience"/>, in the tenant the current operation
    /// targets.
    ///
    /// The audience alone does not say which tenant a token should come from. An operator signed in to
    /// several tenants would otherwise be served whichever the cache offered first, so a command aimed
    /// at one tenant's environment could carry another tenant's token. The tenant is read from the
    /// operation's own scope rather than from state on this provider, which is a singleton shared by
    /// every concurrent operation.
    /// </summary>
    /// <param name="audience">The audience the sync library needs a token for.</param>
    /// <param name="cancellationToken">Cancels the acquisition.</param>
    /// <returns>The bearer token value.</returns>
    public async Task<string> AcquireTokenAsync(Uri audience, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audience);

        var tenantId = DataverseOperationScope.TenantId;

        return AcquiredTokenTenant.Require(
            await _tokenProvider.AcquireTokenSilentAsync(audience.ToString(), tenantId, cancellationToken).ConfigureAwait(false),
            tenantId);
    }
}
