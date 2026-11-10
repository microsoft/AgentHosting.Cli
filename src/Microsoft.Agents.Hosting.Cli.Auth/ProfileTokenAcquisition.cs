using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Auth;

public sealed class ProfileTokenAcquisition(
    AgentHostAuthOptions options,
    ProfileCredentialRuntimeOptions runtime,
    IOwnedProtectedStorageBackend backend,
    IProfileMsalClientFactory msal,
    IProfileAzureCredentialSourceFactory azure,
    IInteractiveMessagePresenter messages) : IProfileTokenAcquisition
{
    public void Validate(ProfileTokenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((request.IsCreation || request.ForceInteractive) && request.Method is AuthCredentialMethod.Broker or AuthCredentialMethod.Browser or AuthCredentialMethod.DeviceCode &&
            (!options.InteractionAllowed || !request.InteractionAllowed))
            throw new InteractionRequiredException("Explicit interactive sign-in is not permitted in this invocation.");
        if (!ProfileCredentialValidation.IsPublicUser(request.Method))
            ProfileCredentialValidation.DefaultScopes(request.Scopes);
        if (request.Method == AuthCredentialMethod.DefaultAzureCredential)
        {
            _ = AzureSources(request);
            return;
        }
        if (request.ClientId == Guid.Empty) throw new UsageException("A credential client id is required.");
        if (ProfileCredentialValidation.IsPublicUser(request.Method))
        {
            var redirect = request.Method == AuthCredentialMethod.Broker ? null : request.RedirectUri ?? runtime.BrowserRedirectUri ?? "http://localhost";
            var app = AgentHostClientAppConfiguration.Create(request.ClientId.ToString("D"), new RuntimePlatformInfo(), redirect);
            if (request.Method == AuthCredentialMethod.Browser && !AgentHostClientAppConfiguration.IsLoopbackRedirect(app.RedirectUri.AbsoluteUri))
                throw new ClientApplicationConfigurationException("Private browser authentication requires an HTTP loopback redirect URI.");
        }
        if (request.Method is AuthCredentialMethod.GitHubFederated or AuthCredentialMethod.AzureDevOpsFederated)
            ProfileFederationAssertionProvider.Validate(runtime, request.Method);
        if (request.Method == AuthCredentialMethod.ClientSecret && string.IsNullOrEmpty(request.ClientSecret))
            throw new AuthProfileStateException("The protected application credential is missing.");
        if (request.Method == AuthCredentialMethod.Certificate)
        {
            if (request.CertificatePath is null) throw new UsageException("A certificate path is required.");
            using var certificate = ProfileCertificateLoader.Load(request.CertificatePath, request.CertificatePassword);
        }
        if (request.IsCreation && request.Method == AuthCredentialMethod.Password &&
            (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password)))
            throw new UsageException("Password sign-in requires both a username and a password.");
    }

    public async Task<ProfileTokenResult> AcquireAsync(ProfileTokenRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Validate(request);
        try
        {
            if (request.Method == AuthCredentialMethod.DefaultAzureCredential)
                return await AcquireAzureAsync(request, ct).ConfigureAwait(false);
            var reference = OwnedProfileReferences.Parse(request.CacheReference).Cache;
            if (!ProfileCredentialValidation.IsPublicUser(request.Method))
            {
                using var certificate = request.Method == AuthCredentialMethod.Certificate
                    ? ProfileCertificateLoader.Load(request.CertificatePath!, request.CertificatePassword) : null;
                var client = msal.CreateConfidential(request, certificate);
                ProfileMsalTokenCache.Attach(client.AppTokenCache, backend, reference, ct);
                var result = await client.AcquireTokenForClient(request.Scopes).ExecuteAsync(ct).ConfigureAwait(false);
                return Map(result, request.Method, certificate?.Thumbprint);
            }
            try { return await AcquirePublicAsync(request, reference, request.Method == AuthCredentialMethod.Broker, ct).ConfigureAwait(false); }
            catch (BrokerUnavailableException unavailable) when ((request.IsCreation || request.ForceInteractive) && request.Method == AuthCredentialMethod.Broker)
            {
                await messages.PresentAsync(BrokerFallbackNotice.Create(unavailable.Reason), ct).ConfigureAwait(false);
                return await AcquirePublicAsync(request, reference, broker: false, ct, deviceFallback: true).ConfigureAwait(false);
            }
        }
        catch (MsalUiRequiredException) { throw new InteractionRequiredException("The profile requires explicit sign-in to renew its token."); }
        catch (MsalServiceException ex) when (ex.StatusCode == 429 || ex is MsalThrottledServiceException)
        { throw new TokenThrottledException("Microsoft Entra ID is throttling credential token acquisition.", ex.Headers?.RetryAfter?.Delta); }
        catch (MsalException) { throw new AuthenticationException("Profile token acquisition failed; check the credential, tenant and requested permissions."); }
    }

    public async Task<IReadOnlyList<AuthAccount>> GetCachedAccountsAsync(ProfileTokenRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ProfileCredentialValidation.IsPublicUser(request.Method) || request.Method == AuthCredentialMethod.Broker) return [];
        var reference = OwnedProfileReferences.Parse(request.CacheReference).Cache;
        var client = msal.CreatePublic(request, broker: false);
        ProfileMsalTokenCache.Attach(client.UserTokenCache, backend, reference, ct);
        try
        {
            var accounts = await client.GetAccountsAsync(ct).ConfigureAwait(false);
            return accounts.Where(a => request.HomeAccountId is null || a.HomeAccountId?.Identifier == request.HomeAccountId)
                .Select(AuthAccountMapper.FromAccount).ToArray();
        }
        catch (MsalException) { throw new AuthProfileStateException("The private account cache could not be read."); }
    }

    private async Task<ProfileTokenResult> AcquirePublicAsync(ProfileTokenRequest request, OwnedProtectedStorageReference reference, bool broker, CancellationToken ct, bool deviceFallback = false)
    {
        async Task<ProfileTokenResult> ExecuteAsync()
        {
            var client = msal.CreatePublic(request, broker);
            ProfileMsalTokenCache.Attach(client.UserTokenCache, backend, reference, ct);
            AuthenticationResult result;
            var method = deviceFallback ? AuthCredentialMethod.DeviceCode : request.Method;
            if (!request.IsCreation && !request.ForceInteractive)
            {
                // Broker-only accounts without local refresh tokens require a broker-enabled cache reader.
                var account = MsalSilentTokenProvider.FindExactAccount(await client.GetAccountsAsync(ct).ConfigureAwait(false), request.HomeAccountId ?? "");
                if (account is null) throw new InteractionRequiredException(broker
                    ? "The operating-system broker no longer holds the selected profile account."
                    : "The exact profile account is absent from its private cache.");
                result = await client.AcquireTokenSilent(request.Scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
                if (result.Account?.HomeAccountId?.Identifier != request.HomeAccountId) throw new ActiveAccountMismatchException();
            }
            else if (method == AuthCredentialMethod.Password)
            {
#pragma warning disable CS0618 // PAC parity uses MSAL's ROPC overload; password is never retained or replayed on renewal.
                result = await client.AcquireTokenByUsernamePassword(request.Scopes, request.Username, request.Password).ExecuteAsync(ct).ConfigureAwait(false);
#pragma warning restore CS0618
            }
            else if (method == AuthCredentialMethod.DeviceCode)
                result = await client.AcquireTokenWithDeviceCode(request.Scopes, code => messages.PresentAsync(code.Message, ct)).ExecuteAsync(ct).ConfigureAwait(false);
            else
            {
                var interactive = client.AcquireTokenInteractive(request.Scopes);
                interactive = string.IsNullOrEmpty(request.LoginHint) ? interactive.WithPrompt(Prompt.SelectAccount) : interactive.WithLoginHint(request.LoginHint);
                result = broker
                    ? await MacOsMainThread.InvokeAsync(client, () => interactive.ExecuteAsync(ct)).ConfigureAwait(false)
                    : await interactive.WithUseEmbeddedWebView(false).ExecuteAsync(ct).ConfigureAwait(false);
            }
            return Map(result, method);
        }
        return broker ? await BrokerFailure.TranslateAsync(ExecuteAsync).ConfigureAwait(false) : await ExecuteAsync().ConfigureAwait(false);
    }

    private ProfileAzureSourceId[] AzureSources(ProfileTokenRequest request)
    {
        var allowed = options.InteractionAllowed && request.InteractionAllowed;
        if (request.PinnedAzureSource is { } pinned)
        {
            EnsureAzureSource(pinned, allowed);
            var configuredClient = pinned == ProfileAzureSourceId.ManagedIdentity
                ? runtime.ManagedIdentityClientId ?? runtime.AzureClientId
                : pinned is ProfileAzureSourceId.EnvironmentClientSecret or ProfileAzureSourceId.EnvironmentCertificate or ProfileAzureSourceId.WorkloadIdentity
                    ? runtime.AzureClientId : null;
            if (!request.IsCreation && configuredClient is not null &&
                ProfileCredentialValidation.ParseGuid(configuredClient, "Azure client") != request.ClientId)
                throw new AuthenticationException("The configured Azure client differs from this profile's protected identity.",
                    ExitCodes.Failure, "Restore the same client identity or explicitly create a different profile.",
                    ErrorCodes.Authentication.ActiveAccountMismatch);
            return [pinned];
        }
        if (!request.IsCreation) throw new AuthProfileStateException("The protected Azure source pin is missing.");
        var candidates = azure.GetInitialSources(runtime, request.Authority);
        var selection = runtime.AzureCredentialSelection?.Trim().ToLowerInvariant();
        if (!allowed && selection is not null and not ("" or "prod") && candidates.Any(ProfileAzureCredentialSourceFactory.IsDeveloper))
            throw new InteractionRequiredException("The explicitly selected developer source requires interaction permission.");
        foreach (var source in candidates)
            if (!Enum.IsDefined(source)) throw new UsageException("The Azure source is not allowlisted.");
        return candidates.Where(s => allowed || !ProfileAzureCredentialSourceFactory.IsDeveloper(s)).ToArray();
    }

    private static void EnsureAzureSource(ProfileAzureSourceId source, bool allowed)
    {
        if (!Enum.IsDefined(source)) throw new AuthProfileStateException("The protected Azure source pin is invalid.");
        if (ProfileAzureCredentialSourceFactory.IsDeveloper(source) && !allowed)
            throw new InteractionRequiredException("The pinned developer source is forbidden in this invocation, including application principals.");
    }

    private async Task<ProfileTokenResult> AcquireAzureAsync(ProfileTokenRequest request, CancellationToken ct)
    {
        foreach (var source in AzureSources(request))
        {
            TokenCredential? credential = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                credential = azure.Create(source, runtime, request.Authority);
                var token = await credential.GetTokenAsync(new TokenRequestContext(request.Scopes.ToArray()), ct).ConfigureAwait(false);
                return new()
                {
                    AccessToken = token.Token, TenantId = ProfileCredentialBinding.ReadTenantId(token.Token),
                    ExpiresOn = token.ExpiresOn, AzureSource = source,
                };
            }
            catch (CredentialUnavailableException) when (request.IsCreation) { }
            catch (AuthenticationFailedException) when (request.IsCreation && ProfileAzureCredentialSourceFactory.IsDeveloper(source)) { }
            catch (AuthenticationFailedException) { throw new AuthenticationException("The pinned Azure credential source failed; no source fallback was performed."); }
            finally { (credential as IDisposable)?.Dispose(); }
        }
        throw new AuthenticationException("No eligible Azure credential source was available.");
    }

    private static ProfileTokenResult Map(AuthenticationResult result, AuthCredentialMethod method, string? thumbprint = null) => new()
    {
        AccessToken = result.AccessToken, TenantId = result.TenantId, ExpiresOn = result.ExpiresOn,
        HomeAccountId = result.Account?.HomeAccountId?.Identifier, Username = result.Account?.Username,
        MsalResult = result, ActualMethod = method, CertificateThumbprint = thumbprint,
    };
}
