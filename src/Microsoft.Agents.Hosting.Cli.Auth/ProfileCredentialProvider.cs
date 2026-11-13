using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Auth;

public sealed class ProfileCredentialProvider : IProfileCredentialProvider, IOwnedProfileCredentialCleanup, IProfileCachedAccountReader,
    IProfileSelectedAccountTokenAcquisition, IProfileConsentTokenAcquisition
{
    private readonly AgentHostAuthOptions _options;
    private readonly IOwnedProtectedStorageBackend _backend;
    private readonly IProfileTokenAcquisition _acquisition;
    private readonly IPlatformInfo _platform;
    private readonly IAuthenticationResultObserver? _observer;
    private readonly MsalPublicClientProvider? _legacyClients;
    private readonly IAuthProfileStore _profiles;
    private readonly OwnedProfileCredentialInventory _inventory;
    private readonly ConcurrentDictionary<string, Stage> _stages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _uses = new(StringComparer.Ordinal);

    public ProfileCredentialProvider(
        AgentHostAuthOptions options,
        IAuthProfileStore profiles,
        IOwnedProtectedStorageBackend backend,
        IProfileTokenAcquisition acquisition,
        IPlatformInfo platform,
        ProfileCredentialRuntimeOptions? runtime = null,
        IAuthenticationResultObserver? observer = null,
        MsalPublicClientProvider? legacyClients = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = profiles;
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _observer = observer;
        _legacyClients = legacyClients;
        _inventory = new(options.ConfigDirectory, profiles, backend);
    }

    public async Task<AuthProfile> AuthenticateAsync(AuthCreateRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var method = ProfileCredentialValidation.Method(request, new MsalPublicClientConfigurator(_platform).IsBrokerSupported, _options.InteractionAllowed);
        var scopes = ProfileCredentialValidation.Scopes(request);
        if (!ProfileCredentialValidation.IsPublicUser(method)) ProfileCredentialValidation.DefaultScopes(scopes);
        var client = request.ApplicationId is not null ? ProfileCredentialValidation.ParseGuid(request.ApplicationId, "application id")
            : method == AuthCredentialMethod.DefaultAzureCredential ? Guid.Empty
            : AgentHostClientAppConfiguration.Create(_options.ClientId, _platform).ClientId;
        var tenant = request.Tenant is null ? (Guid?)null : ProfileCredentialValidation.ParseGuid(request.Tenant, "tenant");
        var authority = AgentHostAuthorityResolver.Resolve(request.Target.Cloud, tenant);
        var references = OwnedProfileReferences.New(!ProfileCredentialValidation.IsPublicUser(method));
        var invocation = new ProfileTokenRequest
        {
            Method = method, ClientId = client, Authority = authority, Scopes = scopes, CacheReference = references.Value,
            IsCreation = true, InteractionAllowed = _options.InteractionAllowed && !request.NonInteractive,
            LoginHint = request.LoginHint, Username = request.Username, Password = request.Password,
            ClientSecret = request.ClientSecret, CertificatePath = request.CertificateDiskPath, CertificatePassword = request.CertificatePassword,
            Ring = request.Target.Ring, ConnectionUrl = request.Target.GlobalEndpoint.AbsoluteUri,
        };
        _acquisition.Validate(invocation);
        var stage = new Stage(_inventory.Lease(references, ct));
        _stages[references.Value] = stage;
        var secret = method == AuthCredentialMethod.ClientSecret ? request.ClientSecret
            : method == AuthCredentialMethod.Certificate ? request.CertificatePassword : null;
        try
        {
            await _inventory.TrackAsync(references, ct).ConfigureAwait(false);
            if (references.Credential is not null)
                await WriteCredentialAsync(references, new() { Secret = secret }, ct).ConfigureAwait(false);
            var observation = AuthenticationResultTenantObserver.BeginObservation(_observer);
            var result = await _acquisition.AcquireAsync(invocation, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result.ActualMethod is { } actual && actual != method)
            {
                if (method != AuthCredentialMethod.Broker || actual != AuthCredentialMethod.DeviceCode)
                    throw new AuthProfileStateException("The acquisition changed the requested credential method.");
                method = actual;
            }
            ProfileCredentialBinding? binding = null;
            if (ProfileCredentialValidation.IsPublicUser(method))
            {
                EnsurePublicIdentity(result, null, tenant);
                MicrosoftCorporateAccountPolicy.EnforceAfterLogin(method == AuthCredentialMethod.Browser ? AuthLoginFlow.BrowserInteractive : AuthLoginFlow.DeviceCode, new AuthAccount
                {
                    HomeAccountId = result.HomeAccountId!, Username = result.Username ?? "", TenantId = result.TenantId,
                });
            }
            else
            {
                binding = ProfileCredentialBinding.FromToken(result, invocation);
                await WriteCredentialAsync(references, new() { Secret = secret, Binding = binding }, ct).ConfigureAwait(false);
            }
            var profile = new AuthProfile
            {
                ProfileId = Guid.NewGuid().ToString("N"), Name = request.Name, CredentialReference = references.Value,
                CredentialMethod = method, PrincipalType = binding?.PrincipalType ?? AuthPrincipalType.User,
                HomeAccountId = ProfileCredentialValidation.IsPublicUser(method) ? result.HomeAccountId : null,
                AccountKey = binding is null ? result.HomeAccountId! : $"principal:{binding.TenantId:D}/{binding.ClientId:D}/{binding.ObjectId:D}",
                Username = result.Username ?? "", ClientId = (binding?.ClientId ?? client).ToString("D"),
                TenantId = result.TenantId, Authority = authority.AuthorityUrl.AbsoluteUri, Cloud = request.Target.Cloud, Ring = request.Target.Ring,
                ConnectionUrl = request.Target.GlobalEndpoint.AbsoluteUri, FlowType = FlowName(method),
                CertificatePath = request.CertificateDiskPath is null ? null : Path.GetFullPath(request.CertificateDiskPath),
                CertificateThumbprint = result.CertificateThumbprint,
            };
            Observe(result, observation, profile.ClientId);
            return profile;
        }
        catch (Exception primary)
        {
            try
            {
                // A native mutation may have committed despite cancellation. Cleanup gets its own
                // bounded native budget, while the reference lease remains held.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _inventory.RemoveWhileLeasedAsync(references, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure) when (cleanupFailure is AgentHostingException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                if (primary is OperationCanceledException)
                    primary.Data["msagent.owned-cleanup"] = "Owned cleanup is incomplete; reference inventory was retained.";
                else throw new ProfileCredentialCleanupException(false, primary as AgentHostingException);
            }
            finally
            {
                _stages.TryRemove(references.Value, out _);
                stage.Dispose();
            }
            throw;
        }
    }

    public async Task<TokenAcquisitionResult> AcquireAsync(AuthProfile profile, IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken ct)
        => await AcquireCoreAsync(profile, scopes, targetTenantId, observe: true, ct).ConfigureAwait(false);

    public async Task<TokenAcquisitionResult?> TryAcquireSelectedTokenAsync(AuthProfile profile, IReadOnlyList<string> scopes, string targetTenantId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.PrincipalType != AuthPrincipalType.User || !ProfileCredentialValidation.IsPublicUser(profile.CredentialMethod) ||
            profile.CredentialMethod == AuthCredentialMethod.Broker) return null;
        try { return await AcquireCoreAsync(profile, scopes, targetTenantId, observe: false, ct).ConfigureAwait(false); }
        catch (InteractionRequiredException) { return null; }
    }

    public async Task<TokenAcquisitionResult> AcquireConsentAsync(AuthProfile profile, Guid clientId, string redirectUri,
        IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken ct, bool trySilentFirst = true)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(profile);
        if (!ProfileCredentialValidation.IsPublicUser(profile.CredentialMethod))
            return await AcquireAsync(profile, scopes, targetTenantId, ct).ConfigureAwait(false);
        ProfileCredentialValidation.Target(profile);
        if (!trySilentFirst && !_options.InteractionAllowed)
            throw new InteractionRequiredException("Explicit profile consent is forbidden in this invocation.");
        var requestedScopes = ProfileCredentialValidation.Scopes(scopes);
        var references = OwnedProfileReferences.Parse(profile.CredentialReference);
        if (references.Credential is not null) throw new AuthProfileStateException("Public consent requires a private user cache reference.");
        var authority = AgentHostAuthorityResolver.Resolve(profile.Cloud,
            ProfileCredentialValidation.ParseGuid(targetTenantId ?? profile.TenantId, "consent tenant"));
        var firstParty = AgentHostClientAppConfiguration.Create(_options.ClientId, _platform).ClientId;
        var broker = profile.CredentialMethod == AuthCredentialMethod.Broker && clientId == firstParty &&
            new MsalPublicClientConfigurator(_platform).IsBrokerSupported;
        var method = broker ? AuthCredentialMethod.Broker :
            profile.CredentialMethod == AuthCredentialMethod.Browser && AgentHostClientAppConfiguration.IsLoopbackRedirect(redirectUri)
                ? AuthCredentialMethod.Browser : AuthCredentialMethod.DeviceCode;
        ProfileTokenRequest Request(bool interactive) => new()
        {
            Method = method, ClientId = clientId, Authority = authority, Scopes = requestedScopes, CacheReference = references.Value,
            HomeAccountId = profile.HomeAccountId ?? profile.AccountKey, LoginHint = profile.Username,
            InteractionAllowed = _options.InteractionAllowed, ForceInteractive = interactive, RedirectUri = broker ? null : redirectUri,
            Ring = profile.Ring, ConnectionUrl = profile.ConnectionUrl,
        };
        _acquisition.Validate(Request(!trySilentFirst));
        using var use = await UseAsync(references, ct).ConfigureAwait(false);
        var observation = AuthenticationResultTenantObserver.BeginObservation(_observer);
        ProfileTokenResult result;
        try { result = await _acquisition.AcquireAsync(Request(!trySilentFirst), ct).ConfigureAwait(false); }
        catch (InteractionRequiredException) when (trySilentFirst && _options.InteractionAllowed)
        { result = await _acquisition.AcquireAsync(Request(true), ct).ConfigureAwait(false); }
        ct.ThrowIfCancellationRequested();
        EnsurePublicIdentity(result, profile.HomeAccountId ?? profile.AccountKey, authority.TenantId);
        Observe(result, observation, clientId.ToString("D"));
        return new() { AccessToken = result.AccessToken, TenantId = result.TenantId, ExpiresOn = result.ExpiresOn };
    }

    private async Task<TokenAcquisitionResult> AcquireCoreAsync(AuthProfile profile, IReadOnlyList<string> scopes, string? targetTenantId, bool observe, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(profile);
        ProfileCredentialValidation.Target(profile);
        if (profile.CredentialReference is null)
        {
            var legacy = _legacyClients ?? throw new AuthProfileStateException("Captured legacy credential adapters are not configured.");
            return await new MsalSilentTokenProvider(legacy, _profiles, observe ? _observer : null)
                .AcquireForProfileAsync(profile, scopes, targetTenantId, ct).ConfigureAwait(false);
        }
        if (targetTenantId is not null)
            targetTenantId = ProfileCredentialValidation.ParseGuid(targetTenantId, "target tenant").ToString("D");
        var requestedScopes = ProfileCredentialValidation.Scopes(scopes);
        if (!ProfileCredentialValidation.IsPublicUser(profile.CredentialMethod)) ProfileCredentialValidation.DefaultScopes(requestedScopes);
        var references = OwnedProfileReferences.Parse(profile.CredentialReference);
        if (ProfileCredentialValidation.IsPublicUser(profile.CredentialMethod) != (references.Credential is null))
            throw new AuthProfileStateException("The owned reference kind differs from the supplied credential method.");
        using var use = await UseAsync(references, ct).ConfigureAwait(false);
        var credential = await ReadCredentialAsync(references, ct).ConfigureAwait(false);
        if (credential is not null)
        {
            var binding = credential.Binding ?? throw new AuthProfileStateException("The mandatory protected credential binding is missing.");
            binding.EnsureSnapshot(profile);
            if (binding.AzureSource is { } source && ProfileAzureCredentialSourceFactory.IsDeveloper(source) && !_options.InteractionAllowed)
                throw new InteractionRequiredException("This developer-source profile requires interaction permission, including application principals.");
        }
        if (profile.CredentialMethod == AuthCredentialMethod.DefaultAzureCredential)
        {
            if (!string.IsNullOrEmpty(targetTenantId) && !string.Equals(targetTenantId, profile.TenantId, StringComparison.OrdinalIgnoreCase))
                throw new AuthenticationException("This Azure credential profile cannot prove the same principal in another tenant.");
        }
        var invocation = RenewalRequest(profile, references, requestedScopes, targetTenantId, credential);
        _acquisition.Validate(invocation);
        var observation = observe ? AuthenticationResultTenantObserver.BeginObservation(_observer) : null;
        var result = await _acquisition.AcquireAsync(invocation, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (ProfileCredentialValidation.IsPublicUser(profile.CredentialMethod))
            EnsurePublicIdentity(result, profile.HomeAccountId ?? profile.AccountKey, invocation.Authority.TenantId);
        else
        {
            var bound = credential?.Binding ?? throw new AuthProfileStateException("The mandatory protected credential binding is missing.");
            bound.EnsureSame(ProfileCredentialBinding.FromToken(result, invocation));
        }
        Observe(result, observation, profile.ClientId);
        return new() { AccessToken = result.AccessToken, TenantId = result.TenantId, ExpiresOn = result.ExpiresOn };
    }

    public async Task CompleteCreationAsync(AuthProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var references = OwnedProfileReferences.Parse(profile.CredentialReference);
        if (_stages.TryGetValue(references.Value, out var stage))
        {
            stage.RequestCompletion();
            if (!stage.Gate.Wait(0, CancellationToken.None))
            {
                try { await stage.Gate.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    var canceled = CommittedCancellation(ct);
                    canceled.Data["msagent.owned-cleanup"] = "The in-use reference lease will be released automatically when acquisition exits.";
                    throw canceled;
                }
            }
            try
            {
                CloseStage(references.Value, stage);
            }
            finally { stage.Gate.Release(); }
        }
        if (ct.IsCancellationRequested) throw CommittedCancellation(ct);
    }

    public async Task RemoveOwnedCredentialAsync(AuthProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.CredentialReference is null) return;
        var references = OwnedProfileReferences.Parse(profile.CredentialReference);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_stages.TryGetValue(references.Value, out var stage))
            {
                await stage.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!stage.Closed)
                    {
                        _stages.TryRemove(references.Value, out _);
                        stage.Closed = true;
                        try { await _inventory.RemoveWhileLeasedAsync(references, ct).ConfigureAwait(false); }
                        finally { stage.Dispose(); }
                        return;
                    }
                }
                finally { stage.Gate.Release(); }
            }
            using var lease = _inventory.Lease(references, ct);
            await _inventory.RemoveWhileLeasedAsync(references, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            ex.Data["msagent.owned-cleanup"] = "Owned cleanup was interrupted; reference inventory and any in-use lease were retained for repair.";
            throw;
        }
        catch (Exception ex) when (ex is AgentHostingException or IOException or UnauthorizedAccessException)
        { throw new ProfileCredentialCleanupException(false, ex as AgentHostingException); }
    }

    public async Task ClearOwnedCredentialsAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await _inventory.ClearAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            ex.Data["msagent.owned-cleanup"] = "Owned cleanup was interrupted; reference inventory was retained for repair.";
            throw;
        }
    }

    public async Task<IReadOnlyList<AuthAccount>> GetAccountsAsync(AuthProfile profile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.PrincipalType != AuthPrincipalType.User || !ProfileCredentialValidation.IsPublicUser(profile.CredentialMethod) ||
            profile.CredentialMethod == AuthCredentialMethod.Broker || profile.CredentialReference is null) return [];
        ProfileCredentialValidation.Target(profile);
        var references = OwnedProfileReferences.Parse(profile.CredentialReference);
        using var use = await UseAsync(references, ct).ConfigureAwait(false);
        var request = RenewalRequest(profile, references, [], null, null);
        var accounts = await _acquisition.GetCachedAccountsAsync(request, ct).ConfigureAwait(false);
        return accounts.Where(a => string.Equals(a.HomeAccountId, profile.HomeAccountId ?? profile.AccountKey, StringComparison.Ordinal)).ToArray();
    }

    private ProfileTokenRequest RenewalRequest(AuthProfile p, OwnedProfileReferences references, IReadOnlyList<string> scopes, string? tenant, ProtectedProfileCredential? credential)
    {
        var authorityTenant = tenant ?? p.TenantId;
        return new()
        {
            Method = p.CredentialMethod, ClientId = ProfileCredentialValidation.ParseGuid(p.ClientId, "profile client id"),
            Authority = AgentHostAuthorityResolver.Resolve(p.Cloud, authorityTenant is null ? null : ProfileCredentialValidation.ParseGuid(authorityTenant, "target tenant")),
            Scopes = scopes, CacheReference = references.Value,
            HomeAccountId = ProfileCredentialValidation.IsPublicUser(p.CredentialMethod) ? p.HomeAccountId ?? p.AccountKey : null,
            InteractionAllowed = _options.InteractionAllowed, PinnedAzureSource = credential?.Binding?.AzureSource,
            Ring = p.Ring, ConnectionUrl = p.ConnectionUrl,
            CertificatePath = p.CertificatePath,
            ClientSecret = p.CredentialMethod == AuthCredentialMethod.ClientSecret ? credential?.Secret : null,
            CertificatePassword = p.CredentialMethod == AuthCredentialMethod.Certificate ? credential?.Secret : null,
        };
    }

    private async Task<IDisposable> UseAsync(OwnedProfileReferences references, CancellationToken ct)
    {
        var gate = _uses.GetOrAdd(references.Value, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return new ReferenceUse(await UseReferenceAsync(references, ct).ConfigureAwait(false), gate);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private async Task<IDisposable> UseReferenceAsync(OwnedProfileReferences references, CancellationToken ct)
    {
        if (_stages.TryGetValue(references.Value, out var stage))
        {
            await stage.Gate.WaitAsync(ct).ConfigureAwait(false);
            if (stage.Closed)
            {
                stage.Gate.Release();
                return _inventory.Lease(references, ct);
            }
            return new GateUse(stage, () => CloseStage(references.Value, stage));
        }
        return _inventory.Lease(references, ct);
    }

    private void CloseStage(string reference, Stage stage)
    {
        if (stage.Closed) return;
        _stages.TryRemove(reference, out _);
        stage.Closed = true;
        stage.Dispose();
    }

    private async Task WriteCredentialAsync(OwnedProfileReferences references, ProtectedProfileCredential credential, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(credential);
        try { await _backend.WriteAsync(references.Credential!, bytes, ct).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<ProtectedProfileCredential?> ReadCredentialAsync(OwnedProfileReferences references, CancellationToken ct)
    {
        if (references.Credential is null) return null;
        var bytes = await _backend.ReadAsync(references.Credential, ct).ConfigureAwait(false)
            ?? throw new AuthProfileStateException("The owned protected credential is missing.");
        try { return JsonSerializer.Deserialize<ProtectedProfileCredential>(bytes) ?? throw new JsonException(); }
        catch (JsonException) { throw new AuthProfileStateException("The owned protected credential payload is invalid."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void Observe(ProfileTokenResult result, AuthenticationResultObservation? observation, string? clientId)
    {
        if (result.MsalResult is { } msal) AuthenticationResultTenantObserver.Observe(msal, observation, clientId);
    }

    private static OperationCanceledException CommittedCancellation(CancellationToken ct)
    {
        var canceled = new OperationCanceledException(ct);
        canceled.Data["msagent.metadata-committed"] = true;
        return canceled;
    }

    private static void EnsurePublicIdentity(ProfileTokenResult result, string? home, Guid? tenant)
    {
        if (string.IsNullOrEmpty(result.HomeAccountId) || string.IsNullOrEmpty(result.TenantId))
            throw new AuthenticationException("The public credential did not provide a home account and tenant binding.");
        if (home is not null && !string.Equals(result.HomeAccountId, home, StringComparison.Ordinal))
            throw new AuthenticationException("The public credential returned a different home account.", errorCode: ErrorCodes.Authentication.ActiveAccountMismatch);
        if (tenant is { } expected && (!Guid.TryParse(result.TenantId, out var returned) || expected != returned))
            throw new AuthenticationException("The public credential returned a different tenant.", errorCode: ErrorCodes.Authentication.TokenClaimWrongTenant);
    }

    private static string FlowName(AuthCredentialMethod method) => method switch
    {
        AuthCredentialMethod.Broker => "broker", AuthCredentialMethod.Browser => "interactive",
        AuthCredentialMethod.DeviceCode => "device-code", AuthCredentialMethod.Password => "password", _ => method.ToString(),
    };
    private sealed class Stage(IDisposable lease) : IDisposable
    {
        internal object CompletionSync { get; } = new();
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal bool Closed { get; set; }
        internal bool CompletionRequested { get; private set; }
        internal void RequestCompletion()
        {
            lock (CompletionSync) CompletionRequested = true;
        }
        public void Dispose() => lease.Dispose();
    }
    private sealed class GateUse(Stage stage, Action complete) : IDisposable
    {
        public void Dispose()
        {
            // Pair completion requests with gate release so cancellation cannot strand the lease.
            lock (stage.CompletionSync)
            {
                try { if (stage.CompletionRequested) complete(); }
                finally { stage.Gate.Release(); }
            }
        }
    }
    private sealed class ReferenceUse(IDisposable lease, SemaphoreSlim gate) : IDisposable
    {
        public void Dispose()
        {
            try { lease.Dispose(); }
            finally { gate.Release(); }
        }
    }
}
