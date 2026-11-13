using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

/// <summary>
/// Delegated Azure Resource Manager consent for managed-identity provisioning, the ARM counterpart of
/// <see cref="GraphDelegatedConsent"/>. The <c>FederatedCredentials</c> and <c>UserManagedIdentity</c>
/// auth types create a user-assigned managed identity over ARM, which the CLI's Power Platform
/// first-party app is not preauthorized for (first-party-to-first-party access can only be
/// preauthorized, never consented). Instead a separate consent-capable public client acquires the ARM
/// token and the operator consents to it on first use. (A365's DevTools CLI solves the same problem
/// differently: it shells out to the <c>az</c> CLI for ARM tokens rather than acquiring them through a
/// consent-capable client of its own.) The behavior is always on and <see cref="DefaultArmClientId"/>
/// is used unless an explicit override is supplied.
/// </summary>
public static class ArmDelegatedConsent
{
    /// <summary>
    /// The default consent-capable ARM client: the well-known Azure CLI public client
    /// (<c>04b07795-8ddb-461a-bbee-02f9e1bf7b46</c>), which Microsoft preauthorizes for ARM delegated
    /// access so consent-on-first-use works in any tenant. It is a placeholder until a dedicated
    /// AgentHost client is provisioned (the open "which client to ship" decision). A365 also knows this
    /// id as <c>AuthenticationConstants.AzureCliClientId</c>, but only references it in a unit test — it
    /// does not acquire ARM tokens through it (it shells out to the <c>az</c> CLI instead).
    /// </summary>
    public const string DefaultArmClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
    public const string DefaultRedirectUri = "http://localhost";

    /// <summary>
    /// Resolves the consent-capable ARM client: the supplied override when present and valid, otherwise
    /// <see cref="DefaultArmClientId"/>. Mirrors <see cref="GraphDelegatedConsent.ResolveClientId"/>.
    /// </summary>
    public static string ResolveClientId(string? armClientIdOverride) =>
        string.IsNullOrWhiteSpace(armClientIdOverride)
            ? DefaultArmClientId
            : ValidateClientId(armClientIdOverride);

    /// <summary>
    /// Validates an explicit ARM client id override and returns it. It must be a non-empty GUID and must
    /// not be the CLI's Power Platform first-party app id: a first-party app can only be preauthorized for
    /// ARM, never consented, so pointing the consent client at that app can never work.
    /// </summary>
    public static string ValidateClientId(string? armClientId)
    {
        if (string.IsNullOrWhiteSpace(armClientId) ||
            !Guid.TryParse(armClientId, out var clientId) ||
            clientId == Guid.Empty)
        {
            throw new UsageException("The Azure Resource Manager consent client id must be a non-empty GUID.");
        }

        if (string.Equals(
                armClientId,
                PowerPlatformApiPermissions.AgentHostCliApplicationId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException(
                "The Azure Resource Manager consent client must be a consent-capable client, not the CLI " +
                $"first-party app id {PowerPlatformApiPermissions.AgentHostCliApplicationId}: a " +
                "first-party app can only be preauthorized for ARM, never consented.");
        }

        return armClientId;
    }
}

/// <summary>The resolved ARM consent client and its registered interactive redirect URI.</summary>
public sealed class ArmConsentClientConfiguration
{
    private ArmConsentClientConfiguration(string clientId, string redirectUri)
    {
        _clientId = clientId;
        _redirectUri = redirectUri;
    }

    private readonly string? _clientId;
    private readonly string? _redirectUri;
    private readonly Lazy<ArmConsentClientConfiguration>? _deferred;

    private ArmConsentClientConfiguration(Func<ArmConsentClientConfiguration> resolve) => _deferred = new(resolve);

    public string ClientId => _deferred?.Value.ClientId ?? _clientId!;

    public string RedirectUri => _deferred?.Value.RedirectUri ?? _redirectUri!;

    /// <summary>Captures resolved host inputs, validating only when a consent operation uses them.</summary>
    public static ArmConsentClientConfiguration ResolveDeferred(string? clientIdOverride, string? redirectUriOverride = null) =>
        new(() => Resolve(clientIdOverride, redirectUriOverride));

    public static ArmConsentClientConfiguration Default { get; } =
        new(ArmDelegatedConsent.DefaultArmClientId, ArmDelegatedConsent.DefaultRedirectUri);

    public static ArmConsentClientConfiguration Resolve(
        string? clientIdOverride,
        string? redirectUriOverride = null) =>
        new(
            ArmDelegatedConsent.ResolveClientId(clientIdOverride),
            ConsentClientRedirectUri.Resolve(
                redirectUriOverride,
                clientIdOverride,
                ArmDelegatedConsent.DefaultRedirectUri,
                "AH_CLI_ARM_REDIRECT_URI"));
}
