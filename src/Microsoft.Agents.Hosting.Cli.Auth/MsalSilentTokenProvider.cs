using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>
/// The live <see cref="ITokenProvider"/>: silent-only token acquisition for the active AgentHost
/// account. It adapts PAC's <c>AuthTokenStore</c> silent paths
/// (<c>AcquireUserTokenSilentAsync</c> and <c>AcquireTokenSilentForTenantAsync</c>):
///
/// <list type="bullet">
///   <item>it resolves the <em>exact</em> active-profile account by home-account id and never falls back
///   to a different cached account;</item>
///   <item>a target tenant uses a tenant-specific client authority for broker flows and MSAL's
///   <c>WithTenantId</c> override otherwise, leaving the stored home-account profile untouched;</item>
///   <item>MSAL's <see cref="MsalUiRequiredException"/> becomes a typed
///   <see cref="InteractionRequiredException"/> (exit 3) — it never opens interactive UI.</item>
/// </list>
/// </summary>
public sealed class MsalSilentTokenProvider :
    ITokenProvider,
    ISelectedAccountTokenProvider
{
    private readonly MsalPublicClientProvider _clientProvider;
    private readonly IAuthProfileStore _profileStore;
    private readonly IAuthenticationResultObserver? _resultObserver;
    private readonly IProfileCredentialProvider? _profileCredentials;
    private readonly IProfileSelectedAccountTokenAcquisition? _selectedCredentials;
    private readonly PacResourceScopeNormalizer _scopeNormalizer = new();

    public MsalSilentTokenProvider(
        MsalPublicClientProvider clientProvider,
        IAuthProfileStore profileStore,
        IAuthenticationResultObserver? resultObserver = null,
        IProfileCredentialProvider? profileCredentials = null,
        IProfileSelectedAccountTokenAcquisition? selectedCredentials = null)
    {
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _resultObserver = resultObserver;
        _profileCredentials = profileCredentials;
        _selectedCredentials = selectedCredentials;
    }

    private static TimeSpan? RetryAfterOf(MsalServiceException failure) =>
        failure.Headers?.RetryAfter?.Delta
        ?? (failure.Headers?.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

    /// <inheritdoc />
    public async Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
        string resource,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            throw new ArgumentException("A resource is required for token acquisition.", nameof(resource));
        }

        return await AcquireTokenSilentAsync(
            [_scopeNormalizer.ToDefaultScope(resource)],
            targetTenantId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
        IReadOnlyList<string> scopes,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var requestedScopes = scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requestedScopes.Length == 0)
        {
            throw new ArgumentException("At least one scope is required for token acquisition.", nameof(scopes));
        }

        var active = await _profileStore.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
        if (active is null)
        {
            throw new NotSignedInException(
                "No active AgentHost account is available for silent token acquisition.",
                remediation: "Run `msagent auth create` to sign in, then retry.");
        }
        return await AcquireForProfileAsync(active, requestedScopes, targetTenantId, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<TokenAcquisitionResult> AcquireForProfileAsync(
        AuthProfile active, IReadOnlyList<string> requestedScopes, string? targetTenantId, CancellationToken cancellationToken)
    {
        var observation = AuthenticationResultTenantObserver.BeginObservation(_resultObserver);
        if (active.CredentialReference is not null)
            return await (_profileCredentials ?? throw new AuthProfileStateException("Owned profile credential adapters are not configured."))
                .AcquireAsync(active, requestedScopes, targetTenantId, cancellationToken).ConfigureAwait(false);
        EnsureLegacyUser(active);

        var useBroker = string.Equals(active.FlowType, "broker", StringComparison.OrdinalIgnoreCase);
        // WAM's operating-system account must use a tenant-specific client authority. Applying the
        // tenant only through WithTenantId can surface WAM IncorrectConfiguration instead.
        var tenantRouting = SilentTokenTenantRouting.Resolve(
            useBroker,
            active.TenantId,
            targetTenantId);

        var client = await _clientProvider
            .GetClientAsync(useBroker, tenantRouting.ClientAuthorityTenantId, cancellationToken,
                clientIdOverride: active.ClientId is null ? null : ProfileCredentialValidation.ParseGuid(active.ClientId, "profile client id"),
                cloudOverride: active.Cloud)
            .ConfigureAwait(false);

        try
        {
            var account = FindExactAccount(await client.GetAccountsAsync().ConfigureAwait(false), active.AccountKey);

            AcquireTokenSilentParameterBuilder builder;
            if (account is not null)
            {
                builder = client.AcquireTokenSilent(requestedScopes, account);
            }
            else if (useBroker)
            {
                builder = client.AcquireTokenSilent(requestedScopes, PublicClientApplication.OperatingSystemAccount);
            }
            else
            {
                // The exact active account is gone and there is no OS account to fall back to; do not
                // silently use a different cached account.
                throw new InteractionRequiredException(
                    "The active AgentHost account is no longer present in the token cache.");
            }

            if (tenantRouting.RequestTenantId is not null)
            {
                builder = builder.WithTenantId(tenantRouting.RequestTenantId);
            }

            var result = await builder.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            if (account is null)
            {
                EnsureActiveAccount(result.Account, active.AccountKey);
            }

            AuthenticationResultTenantObserver.Observe(
                result,
                observation,
                active.ClientId);
            return new TokenAcquisitionResult
            {
                AccessToken = result.AccessToken,
                TenantId = result.TenantId,
                ExpiresOn = result.ExpiresOn,
            };
        }
        catch (MsalServiceException throttled) when (throttled.StatusCode == 429 || throttled is MsalThrottledServiceException)
        {
            throw new TokenThrottledException(
                "Microsoft Entra ID is throttling token requests from this client.",
                RetryAfterOf(throttled));
        }
        catch (MsalUiRequiredException)
        {
            throw CreateInteractionRequiredException(targetTenantId);
        }
        catch (Exception failure) when (useBroker && BrokerFailure.IsBrokerFailure(failure))
        {
            throw BrokerFailure.Unavailable(failure);
        }
    }

    internal static InteractionRequiredException CreateInteractionRequiredException(
        string? targetTenantId)
    {
        // A cached session exists but MSAL requires interaction (consent, Conditional Access, a
        // guest tenant that has not been redeemed, or an expired refresh token) — this is NOT a
        // permanent denial. Name an explicit target tenant so the operator can retry an interactive
        // login scoped to it (or correct the flag) rather than assuming they lack access.
        return !string.IsNullOrWhiteSpace(targetTenantId)
            ? new InteractionRequiredException(
                $"Could not silently obtain the requested access token for tenant {targetTenantId}.",
                remediation: $"Run `msagent auth create --tenant {targetTenantId}` for the intended cloud with an account that can access that tenant, or correct `--tenant`.")
            : new InteractionRequiredException(
                "A cached AgentHost session exists but interaction is required to obtain a fresh token.");
    }

    /// <inheritdoc />
    public async Task<TokenAcquisitionResult?> TryAcquireTokenSilentAsync(
        string resource,
        string targetTenantId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            throw new ArgumentException(
                "A resource is required for token acquisition.",
                nameof(resource));
        }

        if (string.IsNullOrWhiteSpace(targetTenantId))
        {
            throw new ArgumentException(
                "A target tenant is required for selected-account token acquisition.",
                nameof(targetTenantId));
        }

        AuthProfile? active = await _profileStore
            .GetActiveProfileAsync(cancellationToken)
            .ConfigureAwait(false);
        if (active is null)
        {
            return null;
        }
        return await TryAcquireForProfileAsync(active, resource, targetTenantId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Selected-cache-only acquisition for already captured authentication coordinates.</summary>
    public async Task<TokenAcquisitionResult?> TryAcquireForProfileAsync(
        AuthProfile active, string resource, string targetTenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTenantId);
        cancellationToken.ThrowIfCancellationRequested();
        if (active.PrincipalType != AuthPrincipalType.User || !ProfileCredentialValidation.IsPublicUser(active.CredentialMethod) ||
            active.CredentialMethod == AuthCredentialMethod.Broker) return null;
        if (active.CredentialReference is not null)
            return _selectedCredentials is null ? null : await _selectedCredentials.TryAcquireSelectedTokenAsync(
                active, [_scopeNormalizer.ToDefaultScope(resource)], targetTenantId, cancellationToken).ConfigureAwait(false);

        // Metadata-only authentication deliberately uses the non-broker client. It may refresh the
        // exact selected account from the application cache, but it never asks WAM for the operating
        // system account, enumerates OS accounts, or substitutes another cached account.
        IPublicClientApplication client = await _clientProvider
            .GetClientAsync(
                useBroker: false,
                tenantId: null,
                cancellationToken,
                clientIdOverride: active.ClientId is null ? null : ProfileCredentialValidation.ParseGuid(active.ClientId, "profile client id"),
                cloudOverride: active.Cloud)
            .ConfigureAwait(false);
        IAccount? account = FindExactAccount(
            await client.GetAccountsAsync().ConfigureAwait(false),
            active.AccountKey);
        if (account is null)
        {
            return null;
        }

        try
        {
            AuthenticationResult result = await client
                .AcquireTokenSilent(
                    [_scopeNormalizer.ToDefaultScope(resource)],
                    account)
                .WithTenantId(targetTenantId)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            // This acquisition exists only to choose a collector. It must not create normal product
            // tenant attribution, so it intentionally does not call AuthenticationResultTenantObserver.
            return new TokenAcquisitionResult
            {
                AccessToken = result.AccessToken,
                TenantId = result.TenantId,
                ExpiresOn = result.ExpiresOn,
            };
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
    }

    internal static IAccount? FindExactAccount(
        IEnumerable<IAccount> accounts,
        string selectedAccountKey)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        return string.IsNullOrWhiteSpace(selectedAccountKey)
            ? null
            : accounts.FirstOrDefault(account => IsExactAccount(account, selectedAccountKey));
    }

    internal static void EnsureLegacyUser(AuthProfile profile)
    {
        if (profile.PrincipalType != AuthPrincipalType.User || !ProfileCredentialValidation.IsPublicUser(profile.CredentialMethod))
            throw new AuthProfileStateException("A workload or Azure credential profile requires its protected owned reference.");
    }

    internal static void EnsureActiveAccount(IAccount? account, string activeAccountKey)
    {
        if (!IsExactAccount(account, activeAccountKey))
        {
            throw new InteractionRequiredException(
                "The operating-system broker no longer holds the active AgentHost account and offered a different account instead.",
                remediation: "Run `msagent auth create` for the intended cloud to sign in again with the account you want to use.");
        }
    }

    private static bool IsExactAccount(IAccount? account, string accountKey) =>
        string.Equals(account?.HomeAccountId?.Identifier, accountKey, StringComparison.Ordinal);
}

internal readonly record struct SilentTokenTenantRouting(
    Guid? ClientAuthorityTenantId,
    string? RequestTenantId)
{
    public static SilentTokenTenantRouting Resolve(
        bool useBroker,
        string? activeTenantId,
        string? targetTenantId)
    {
        var authorityTenantId = string.IsNullOrWhiteSpace(targetTenantId)
            ? activeTenantId
            : targetTenantId;
        if (useBroker &&
            Guid.TryParse(authorityTenantId, out var parsedAuthorityTenantId) &&
            parsedAuthorityTenantId != Guid.Empty)
        {
            return new(
                ClientAuthorityTenantId: parsedAuthorityTenantId,
                RequestTenantId: null);
        }

        return new(
            ClientAuthorityTenantId: null,
            RequestTenantId: string.IsNullOrWhiteSpace(targetTenantId) ? null : targetTenantId);
    }
}
