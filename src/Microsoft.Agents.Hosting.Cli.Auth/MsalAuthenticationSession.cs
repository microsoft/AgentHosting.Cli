using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>The live <see cref="IAuthenticationSession"/> that signs the operator in through the OS broker (silently as a requested account, or through the broker's account picker) or device code, and enumerates or clears the AgentHost MSAL cache.</summary>
public sealed class MsalAuthenticationSession : IAuthenticationSession
{
    private readonly MsalPublicClientProvider _clientProvider;
    private readonly AgentHostAuthOptions _options;
    private readonly IAuthenticationResultObserver? _resultObserver;
    private readonly IOwnedProfileCredentialCleanup? _ownedCleanup;
    private readonly PacResourceScopeNormalizer _scopeNormalizer = new();

    public MsalAuthenticationSession(
        MsalPublicClientProvider clientProvider,
        AgentHostAuthOptions options,
        IAuthenticationResultObserver? resultObserver = null,
        IOwnedProfileCredentialCleanup? ownedCleanup = null)
    {
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resultObserver = resultObserver;
        _ownedCleanup = ownedCleanup;
    }

    /// <inheritdoc />
    public Task<AuthAccount> LoginWithBrokerAsync(AuthLoginRequest request, CancellationToken cancellationToken)
    {
        EnsureInteraction(request, cancellationToken);
        return SignInAsync(request, useBroker: true, client => MacOsMainThread.InvokeAsync(client, () => AcquireInteractiveTokenAsync(client, BuildScopes(request), request.Account, cancellationToken)), cancellationToken);
    }

    /// <inheritdoc />
    public Task<AuthAccount> LoginWithBrokerSilentlyAsync(AuthLoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Account);
        return SignInAsync(request, useBroker: true, async client => EnsureIssuedByTheBroker(await AcquireSilentTokenAsync(client, BuildScopes(request), request.Account, cancellationToken).ConfigureAwait(false)), cancellationToken);
    }

    /// <inheritdoc />
    public Task<AuthAccount> LoginWithDeviceCodeAsync(AuthLoginRequest request, Func<string, Task> deviceCodeCallback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(deviceCodeCallback);
        EnsureInteraction(request, cancellationToken);
        return SignInAsync(request, useBroker: false, client => client.AcquireTokenWithDeviceCode(BuildScopes(request), deviceCodeResult => deviceCodeCallback(deviceCodeResult.Message)).ExecuteAsync(cancellationToken), cancellationToken);
    }

    private void EnsureInteraction(AuthLoginRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (request.NonInteractive || !_options.InteractionAllowed)
        {
            throw new InteractionRequiredException("Explicit interactive sign-in is not allowed in this invocation.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuthAccount>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        var client = await _clientProvider
            .GetClientAsync(useBroker: false, tenantId: null, cancellationToken)
            .ConfigureAwait(false);

        var accounts = await client.GetAccountsAsync().ConfigureAwait(false);
        return accounts.Select(AuthAccountMapper.FromAccount).ToList();
    }

    /// <inheritdoc />
    public async Task ClearCacheAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_ownedCleanup is not null) await _ownedCleanup.ClearOwnedCredentialsAsync(cancellationToken).ConfigureAwait(false);
        await _clientProvider.ClearLegacyCacheAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthAccount> SignInAsync(AuthLoginRequest request, bool useBroker, Func<IPublicClientApplication, Task<AuthenticationResult>> acquire, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var observation = AuthenticationResultTenantObserver.BeginObservation(_resultObserver);
        var client = await _clientProvider.GetClientAsync(useBroker, ResolveTenantId(request), cancellationToken, listBrokerAccounts: useBroker).ConfigureAwait(false);
        var result = useBroker
            ? await BrokerFailure.TranslateAsync(() => acquire(client)).ConfigureAwait(false)
            : await acquire(client).ConfigureAwait(false);
        return AuthAccountMapper.FromResult(result, observation, _options.ClientId);
    }

    internal static async Task<AuthenticationResult> AcquireSilentTokenAsync(IPublicClientApplication client, string[] scopes, string account, CancellationToken cancellationToken)
    {
        var held = (await client.GetAccountsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => AuthProfileSelection.NamesAccount(account, candidate.HomeAccountId?.Identifier, candidate.Username))
            ?? throw new BrokerUnavailableException("the operating-system broker does not hold this account");
        return await client.AcquireTokenSilent(scopes, held).ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static AuthenticationResult EnsureIssuedByTheBroker(AuthenticationResult result) =>
        result.AuthenticationResultMetadata?.TokenSource == TokenSource.Broker ? result : throw new BrokerUnavailableException("the operating-system broker does not hold this account");

    private static Task<AuthenticationResult> AcquireInteractiveTokenAsync(
        IPublicClientApplication client,
        IEnumerable<string> scopes,
        string? account,
        CancellationToken cancellationToken)
    {
        var interactive = client.AcquireTokenInteractive(scopes);
        interactive = string.IsNullOrWhiteSpace(account)
            ? interactive.WithPrompt(Prompt.SelectAccount)
            : interactive.WithLoginHint(account);

        return interactive.ExecuteAsync(cancellationToken);
    }

    private string[] BuildScopes(AuthLoginRequest request)
    {
        if (request.Scopes.Count > 0)
        {
            return request.Scopes
                .Where(scope => !string.IsNullOrWhiteSpace(scope))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var resource = string.IsNullOrWhiteSpace(request.Resource)
            ? _scopeNormalizer.DefaultResourceScope(request.Ring ?? PowerPlatformApiRing.Production)
            : _scopeNormalizer.ToDefaultScope(request.Resource);
        return [resource];
    }

    private static Guid? ResolveTenantId(AuthLoginRequest request) =>
        Guid.TryParse(request.Tenant, out var tenantId) ? tenantId : null;
}
