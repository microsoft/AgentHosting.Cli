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
    private const string ApplicationAudienceScheme = "api";

    private const string DelegatedPermission = "user_impersonation";

    private static readonly PpapiScopeBuilder Scopes = new();

    private readonly ITokenProvider _tokenProvider;

    /// <summary>Creates a provider backed by <paramref name="tokenProvider"/>.</summary>
    /// <param name="tokenProvider">The CLI's silent, multi-audience token provider.</param>
    public AgentHostSyncAuthProvider(ITokenProvider tokenProvider) => _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));

    /// <summary>Acquires a bearer token for <paramref name="audience"/> in the tenant the current operation targets rather than whichever the cache offers first, asking for an application audience's user_impersonation permission and for a resource's default scope.</summary>
    /// <param name="audience">The audience the sync library needs a token for.</param>
    /// <param name="cancellationToken">Cancels the acquisition.</param>
    /// <returns>The bearer token value.</returns>
    public async Task<string> AcquireTokenAsync(Uri audience, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audience);

        var tenantId = DataverseOperationScope.TenantId;
        var token = string.Equals(audience.Scheme, ApplicationAudienceScheme, StringComparison.OrdinalIgnoreCase)
            ? await _tokenProvider.AcquireTokenSilentAsync(ApplicationScopes(audience), tenantId, cancellationToken).ConfigureAwait(false)
            : await _tokenProvider.AcquireTokenSilentAsync(audience.ToString(), tenantId, cancellationToken).ConfigureAwait(false);

        return AcquiredTokenTenant.Require(token, tenantId);
    }

    internal static IReadOnlyList<string> ApplicationScopes(Uri audience) => Scopes.ExplicitScopes(audience.ToString(), DelegatedPermission);
}
