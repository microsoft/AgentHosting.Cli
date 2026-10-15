using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>
/// Shared silent-first / interactive-consent token acquisition for the consent-capable providers
/// (<see cref="MsalGraphConsentTokenProvider"/> and <see cref="MsalArmConsentTokenProvider"/>).
/// </summary>
public sealed class MsalConsentTokenAcquisition
{
    private readonly MsalPublicClientProvider _clientProvider;
    private readonly IAuthProfileStore _profileStore;
    private readonly IInteractionPolicy _interactionPolicy;
    private readonly IInteractiveMessagePresenter _messagePresenter;
    private readonly IAuthenticationResultObserver? _resultObserver;

    public MsalConsentTokenAcquisition(
        MsalPublicClientProvider clientProvider,
        IAuthProfileStore profileStore,
        IInteractionPolicy interactionPolicy,
        IInteractiveMessagePresenter messagePresenter,
        IAuthenticationResultObserver? resultObserver = null)
    {
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _interactionPolicy = interactionPolicy ?? throw new ArgumentNullException(nameof(interactionPolicy));
        _messagePresenter = messagePresenter ?? throw new ArgumentNullException(nameof(messagePresenter));
        _resultObserver = resultObserver;
    }

    /// <summary>Acquires a token for <paramref name="clientId"/> and <paramref name="scopes"/>, silently first unless <paramref name="trySilentFirst"/> is off and otherwise through interactive consent, raising an <see cref="InteractionRequiredException"/> built from the interaction arguments when consent is needed but interaction is disabled.</summary>
    public async Task<TokenAcquisitionResult> AcquireAsync(
        Guid clientId,
        string redirectUri,
        IReadOnlyList<string> scopes,
        string? targetTenantId,
        string interactionMessage,
        string interactionRemediation,
        InteractionRequiredResource interactionResource,
        CancellationToken cancellationToken,
        bool trySilentFirst = true)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        AuthenticationResultObservation? observation =
            AuthenticationResultTenantObserver.BeginObservation(
                _resultObserver);
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
                "No active AgentHost account is available for consent token acquisition.",
                remediation: "Run `msagent auth login` to sign in, then retry.");
        }

        var useBroker = ConsentInteractiveFlowSelector.UseBroker(
            active,
            _clientProvider.IsAgentHostClient(clientId),
            _clientProvider.IsBrokerSupported);

        var tenantRouting = SilentTokenTenantRouting.Resolve(useBroker, active.TenantId, targetTenantId);

        var client = await _clientProvider
            .GetClientAsync(
                useBroker,
                tenantRouting.ClientAuthorityTenantId,
                cancellationToken,
                clientIdOverride: clientId,
                redirectUriOverride: redirectUri)
            .ConfigureAwait(false);

        var account = await FindActiveAccountAsync(client, useBroker, active.AccountKey, cancellationToken).ConfigureAwait(false);
        var silent = trySilentFirst && account is not null
            ? await TryAcquireSilentAsync(
                client,
                requestedScopes,
                tenantRouting.RequestTenantId,
                account,
                clientId,
                observation,
                cancellationToken).ConfigureAwait(false)
            : null;
        if (silent is not null)
        {
            return silent;
        }

        if (!_interactionPolicy.IsInteractionAllowed)
        {
            throw new InteractionRequiredException(
                interactionMessage,
                remediation: interactionRemediation,
                resource: interactionResource);
        }

        var result = ConsentInteractiveFlowSelector.Select(active, useBroker, redirectUri) switch
        {
            ConsentInteractiveFlow.DeviceCode => await AcquireWithDeviceCodeAsync(client, requestedScopes, tenantRouting.RequestTenantId, cancellationToken).ConfigureAwait(false),
            ConsentInteractiveFlow.Broker => await AcquireWithBrokerAsync(client, requestedScopes, account, active.Username, tenantRouting.RequestTenantId, cancellationToken).ConfigureAwait(false),
            _ => await AcquireInteractiveAsync(client, requestedScopes, account, active.Username, tenantRouting.RequestTenantId, cancellationToken).ConfigureAwait(false),
        };

        ConsentAccountSelection.EnsureReturnedAccount(
            active.AccountKey,
            result.Account?.HomeAccountId?.Identifier);
        return Map(result, clientId, observation);
    }

    private Task<AuthenticationResult> AcquireWithBrokerAsync(IPublicClientApplication client, string[] scopes, IAccount? account, string? loginHint, string? targetTenantId, CancellationToken cancellationToken) =>
        FallBackToDeviceCodeAsync(
            () => MacOsMainThread.InvokeAsync(client, () => AcquireInteractiveAsync(client, scopes, account, loginHint, targetTenantId, cancellationToken)),
            () => AcquireWithDeviceCodeAsync(client, scopes, targetTenantId, cancellationToken),
            cancellationToken);

    internal async Task<AuthenticationResult> FallBackToDeviceCodeAsync(Func<Task<AuthenticationResult>> brokerSignIn, Func<Task<AuthenticationResult>> deviceCodeSignIn, CancellationToken cancellationToken)
    {
        try
        {
            return await BrokerFailure.TranslateAsync(brokerSignIn).ConfigureAwait(false);
        }
        catch (BrokerUnavailableException unavailable)
        {
            await _messagePresenter.PresentAsync(BrokerFallbackNotice.Create(unavailable.Reason), cancellationToken).ConfigureAwait(false);
            return await deviceCodeSignIn().ConfigureAwait(false);
        }
    }

    internal static async Task<IAccount?> FindActiveAccountAsync(IPublicClientApplication client, bool useBroker, string? activeAccountKey, CancellationToken cancellationToken)
    {
        try
        {
            return ConsentAccountSelection.Select((await client.GetAccountsAsync(cancellationToken).ConfigureAwait(false)).ToArray(), activeAccountKey, candidate => candidate.HomeAccountId?.Identifier);
        }
        catch (Exception failure) when (useBroker && BrokerFailure.IsBrokerFailure(failure))
        {
            return null;
        }
    }

    private Task<AuthenticationResult> AcquireWithDeviceCodeAsync(
        IPublicClientApplication client,
        string[] scopes,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        var builder = client.AcquireTokenWithDeviceCode(
            scopes,
            deviceCode => _messagePresenter.PresentAsync(deviceCode.Message, cancellationToken));
        if (!string.IsNullOrWhiteSpace(targetTenantId))
        {
            builder = builder.WithTenantId(targetTenantId);
        }

        return builder.ExecuteAsync(cancellationToken);
    }

    private static Task<AuthenticationResult> AcquireInteractiveAsync(
        IPublicClientApplication client,
        string[] scopes,
        IAccount? account,
        string? loginHint,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        var interactive = client.AcquireTokenInteractive(scopes);
        interactive = account is not null
            ? interactive.WithAccount(account)
            : string.IsNullOrWhiteSpace(loginHint)
                ? interactive.WithPrompt(Prompt.SelectAccount)
                : interactive.WithLoginHint(loginHint);
        if (!string.IsNullOrWhiteSpace(targetTenantId))
        {
            interactive = interactive.WithTenantId(targetTenantId);
        }

        return interactive.ExecuteAsync(cancellationToken);
    }

    private static async Task<TokenAcquisitionResult?> TryAcquireSilentAsync(
        IPublicClientApplication client,
        string[] scopes,
        string? targetTenantId,
        IAccount account,
        Guid clientId,
        AuthenticationResultObservation? observation,
        CancellationToken cancellationToken)
    {
        try
        {
            var builder = client.AcquireTokenSilent(scopes, account);
            if (!string.IsNullOrWhiteSpace(targetTenantId))
            {
                builder = builder.WithTenantId(targetTenantId);
            }

            var result = await builder.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return Map(result, clientId, observation);
        }
        catch (MsalUiRequiredException)
        {
            // A cached account exists but consent/interaction is required; fall back to interactive.
            return null;
        }
    }

    private static TokenAcquisitionResult Map(
        AuthenticationResult result,
        Guid? clientId,
        AuthenticationResultObservation? observation)
    {
        AuthenticationResultTenantObserver.Observe(
            result,
            observation,
            clientId?.ToString("D"));
        return new()
        {
            AccessToken = result.AccessToken,
            TenantId = result.TenantId,
            ExpiresOn = result.ExpiresOn,
        };
    }
}

internal static class ConsentAccountSelection
{
    public static T? Select<T>(
        IReadOnlyList<T> accounts,
        string? activeAccountKey,
        Func<T, string?> getAccountKey)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(getAccountKey);
        if (string.IsNullOrWhiteSpace(activeAccountKey))
        {
            return null;
        }

        return accounts.FirstOrDefault(account =>
            string.Equals(getAccountKey(account), activeAccountKey, StringComparison.Ordinal));
    }

    public static void EnsureReturnedAccount(string? activeAccountKey, string? returnedAccountKey)
    {
        if (string.IsNullOrWhiteSpace(activeAccountKey) ||
            !string.Equals(activeAccountKey, returnedAccountKey, StringComparison.Ordinal))
        {
            throw new ActiveAccountMismatchException();
        }
    }
}
