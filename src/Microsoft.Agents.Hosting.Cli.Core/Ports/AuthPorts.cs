using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>
/// A cached MSAL account, exposed without any token material.
/// </summary>
public sealed record AuthAccount
{
    /// <summary>Account UPN/email.</summary>
    public required string Username { get; init; }

    /// <summary>MSAL home account id (the stable switch key).</summary>
    public required string HomeAccountId { get; init; }

    /// <summary>Home tenant id, when known.</summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Issuer tenant returned by a normal authentication result after optional collection policy
    /// has enabled extraction. This is deliberately separate from the MSAL home tenant.
    /// </summary>
    public Guid? AuthenticatedTenantId { get; init; }

    /// <summary>
    /// Tenant-local Entra object id paired with <see cref="AuthenticatedTenantId"/>, when a normal
    /// user authentication result supplied one.
    /// </summary>
    public Guid? PrincipalObjectId { get; init; }

    /// <summary>
    /// Public-client application id that produced the optional authenticated context.
    /// </summary>
    public string? AuthenticatedClientId { get; init; }
}

/// <summary>
/// A persisted AgentHost auth profile. Holds only account metadata and selection state — never token
/// material (the MSAL cache is a separate, secure store). One AgentHost profile is active at a time
/// because the demonstrator has a single auth kind; PAC's per-<c>AuthKind</c> "current" dictionary is
/// intentionally collapsed.
/// </summary>
public sealed record AuthProfile
{
    /// <summary>
    /// Stable account key used for selection. This is the MSAL home-account id, not the mutable
    /// username; combined with the client application id, it forms the durable profile identity.
    /// </summary>
    public required string AccountKey { get; init; }

    /// <summary>Account UPN/email. Display and lookup data, not the durable key.</summary>
    public required string Username { get; init; }

    /// <summary>Home tenant id, when known.</summary>
    public string? TenantId { get; init; }

    /// <summary>Whether this profile is the single active AgentHost profile.</summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// The client application id under which this profile was created. Combined with
    /// <see cref="AccountKey"/> it forms the durable profile key; a profile created under a different
    /// client id is not reused.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>The authority URL the profile was homed against, when known.</summary>
    public string? Authority { get; init; }

    /// <summary>The cloud the profile belongs to.</summary>
    public CloudInstance Cloud { get; init; } = CloudInstance.Public;

    /// <summary>
    /// The Power Platform API ring every command run under this profile targets. Carried on the
    /// profile rather than supplied per command, so the selected sign-in and the ring it addresses
    /// cannot disagree.
    /// </summary>
    public PowerPlatformApiRing Ring { get; init; } = PowerPlatformApiRing.Production;

    /// <summary>The login flow that produced the profile (for example <c>broker</c>, <c>interactive</c>, <c>device-code</c>), when known.</summary>
    public string? FlowType { get; init; }

    /// <summary>
    /// Issuer tenant observed from normal authentication while optional telemetry collection was
    /// enabled. This is not inferred from <see cref="AccountKey"/> or <see cref="TenantId"/>.
    /// </summary>
    public string? AuthenticatedTenantId { get; init; }

    /// <summary>
    /// Tenant-local Entra object id paired with <see cref="AuthenticatedTenantId"/>, when verified.
    /// </summary>
    public string? PrincipalObjectId { get; init; }
}

/// <summary>
/// Narrow, token-free profile context available to optional command telemetry after collection
/// policy has enabled it. The account key is local matching state and is never emitted.
/// </summary>
public readonly record struct AuthProfileTelemetryContext(
    string AccountKey,
    string? ClientId,
    string? Authority,
    CloudInstance Cloud,
    Guid TenantId,
    Guid? PrincipalObjectId);

/// <summary>
/// Read-only optional metadata seam over the existing profile store. It never acquires a token,
/// creates or updates a profile, changes the active pointer, or exposes usernames.
/// </summary>
public interface IAuthProfileTelemetryStore
{
    /// <summary>Returns verified metadata for the active profile, or no context when unavailable.</summary>
    ValueTask<AuthProfileTelemetryContext?> GetActiveTelemetryContextAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Process-local, metadata-free revision of committed profile mutations. It is neither persisted
/// nor emitted and exists only to order authentication observations against profile lifecycle.
/// </summary>
public sealed class AuthProfileMutationRevision
{
    private long _current;

    public long Current => Interlocked.Read(ref _current);

    internal long Advance() => Interlocked.Increment(ref _current);
}

/// <summary>
/// Optional observer for active-profile mutations already required by product authentication.
/// Implementations must not initiate authentication or make profile mutations fail.
/// </summary>
public interface IAuthProfileStateObserver
{
    bool IsObservationEnabled => true;

    void ObserveProfileMutation(long revision);

    void ObserveActiveProfile(AuthProfile? profile, long revision);
}

/// <summary>
/// The non-secret metadata of an acquired token. The bearer value itself is returned only by
/// <see cref="ITokenProvider"/> and never crosses into a report, log, comparison, or metadata model.
/// </summary>
public sealed record TokenAcquisitionResult
{
    /// <summary>The bearer token value. Consumed by transport only; never serialized.</summary>
    public required string AccessToken { get; init; }

    /// <summary>The tenant the token was issued for.</summary>
    public string? TenantId { get; init; }

    /// <summary>Token expiry.</summary>
    public DateTimeOffset ExpiresOn { get; init; }
}

/// <summary>
/// Silent, multi-audience token acquisition for a resource and optional target tenant. Producing a
/// token here never opens interactive UI.
/// </summary>
public interface ITokenProvider
{
    /// <summary>Acquires a token silently for <paramref name="resource"/> and optional target tenant.</summary>
    Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
        string resource,
        string? targetTenantId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Acquires a token silently for explicit delegated scopes. Scopes are passed to the identity
    /// provider unchanged and are not converted to a resource <c>/.default</c> scope.
    /// </summary>
    Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
        IReadOnlyList<string> scopes,
        string? targetTenantId,
        CancellationToken cancellationToken);

    /// <summary>Acquires a newly issued token silently for <paramref name="resource"/> instead of a cached one, which a provider that keeps no token cache acquires as usual.</summary>
    Task<TokenAcquisitionResult> RenewTokenSilentAsync(string resource, string? targetTenantId, CancellationToken cancellationToken) => AcquireTokenSilentAsync(resource, targetTenantId, cancellationToken);
}

/// <summary>
/// Optional-measurement token acquisition for the explicitly selected AgentHost account. This seam
/// never discovers or selects an operating-system account and never opens interactive UI.
/// </summary>
public interface ISelectedAccountTokenProvider
{
    /// <summary>
    /// Silently acquires a token for the exact active profile, or returns <see langword="null"/> when
    /// that selected account is absent from the application token cache or requires interaction.
    /// </summary>
    Task<TokenAcquisitionResult?> TryAcquireTokenSilentAsync(
        string resource,
        string targetTenantId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Consent-capable Graph token acquisition for identity provisioning. Unlike <see cref="ITokenProvider"/>
/// (which is silent-only and never opens UI), this port may open interactive consent UI on first use:
/// it acquires a Microsoft Graph token through a dedicated consent-capable public client (delegated
/// consent — see <c>GraphDelegatedConsent</c>), so a tenant admin can grant the CLI's Graph permissions on
/// first use instead of relying on central preauthorization of the CLI first-party app.
///
/// Silent acquisition is attempted first; interactive consent is used only as a fallback and only when
/// interaction is permitted. Under <c>--non-interactive</c> a required interaction surfaces as
/// <c>InteractionRequiredException</c>, which the provisioning service maps to a <c>consent-required</c>
/// report carrying the admin-consent URL.
/// </summary>
public interface IGraphConsentTokenProvider
{
    /// <summary>
    /// Acquires a Microsoft Graph token for the explicit delegated <paramref name="scopes"/> and optional
    /// target tenant, consenting on first use. Scopes are passed to the identity provider unchanged.
    /// </summary>
    Task<TokenAcquisitionResult> AcquireGraphTokenAsync(
        IReadOnlyList<string> scopes,
        string? targetTenantId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Consent-capable Azure Resource Manager token acquisition for identity provisioning (the ARM
/// counterpart of <see cref="IGraphConsentTokenProvider"/>). Managed-identity provisioning for the
/// <c>FederatedCredentials</c> and <c>UserManagedIdentity</c> auth types calls ARM, which — like Graph —
/// the CLI first-party app is not preauthorized for. This port acquires the ARM token through a
/// consent-capable public client (delegated consent — see <c>ArmDelegatedConsent</c>, which defaults
/// to the well-known Azure CLI public client), consenting on first use. It may open interactive
/// UI on first use; under <c>--non-interactive</c> a required interaction surfaces as
/// <c>InteractionRequiredException</c>.
/// </summary>
public interface IArmConsentTokenProvider
{
    /// <summary>
    /// Acquires an Azure Resource Manager token for the optional target tenant, consenting on first use.
    /// </summary>
    Task<TokenAcquisitionResult> AcquireArmTokenAsync(
        string? targetTenantId,
        CancellationToken cancellationToken);
}

/// <summary>Explicit broker and device-code login plus cache account enumeration, the single port through which the operation core performs an operator sign-in.</summary>
public interface IAuthenticationSession
{
    /// <summary>Signs in through the OS broker's account picker, pre-filled with <see cref="AuthLoginRequest.Account"/> when one is named, throwing <see cref="Errors.BrokerUnavailableException"/> when the broker cannot sign in and <see cref="Errors.SignInCanceledException"/> when the operator cancels.</summary>
    Task<AuthAccount> LoginWithBrokerAsync(AuthLoginRequest request, CancellationToken cancellationToken);

    /// <summary>Signs in without any UI as exactly the account named by <see cref="AuthLoginRequest.Account"/>, throwing <see cref="Errors.BrokerUnavailableException"/> when the OS broker does not hold that account or cannot sign it in.</summary>
    Task<AuthAccount> LoginWithBrokerSilentlyAsync(AuthLoginRequest request, CancellationToken cancellationToken);

    /// <summary>Device-code sign-in; <paramref name="deviceCodeCallback"/> surfaces the code/instructions.</summary>
    Task<AuthAccount> LoginWithDeviceCodeAsync(
        AuthLoginRequest request,
        Func<string, Task> deviceCodeCallback,
        CancellationToken cancellationToken);

    /// <summary>Enumerates cached accounts without acquiring a token.</summary>
    Task<IReadOnlyList<AuthAccount>> GetAccountsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Clears the AgentHost MSAL token cache (removes all cached accounts). Best-effort and never opens
    /// identity UI — the cache-side half of <c>msagent auth logout</c> (PAC's <c>auth clear</c> deletes the
    /// profiles file and the token cache).
    /// </summary>
    Task ClearCacheAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Profile persistence and active-profile selection. Contains no token material and uses atomic file
/// persistence with explicit corruption errors.
/// </summary>
public interface IAuthProfileStore
{
    /// <summary>The single active profile, or <see langword="null"/> when none is selected.</summary>
    Task<AuthProfile?> GetActiveProfileAsync(CancellationToken cancellationToken);

    /// <summary>All persisted profiles.</summary>
    Task<IReadOnlyList<AuthProfile>> GetProfilesAsync(CancellationToken cancellationToken);

    /// <summary>Inserts or updates a profile.</summary>
    Task UpsertAsync(AuthProfile profile, CancellationToken cancellationToken);

    /// <summary>Marks <paramref name="accountKey"/> as the single active profile.</summary>
    Task SetActiveAsync(string accountKey, CancellationToken cancellationToken);

    /// <summary>Clears all persisted profile metadata.</summary>
    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reports whether an interactive identity flow is permitted (for example, false under
/// <c>--non-interactive</c>). It never performs a prompt itself; the auth flows consult it.
/// </summary>
public interface IInteractionPolicy
{
    /// <summary>Whether interactive identity UI is currently allowed.</summary>
    bool IsInteractionAllowed { get; }
}
