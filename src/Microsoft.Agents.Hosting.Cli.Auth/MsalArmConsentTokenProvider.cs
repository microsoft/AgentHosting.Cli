using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>
/// The live <see cref="IArmConsentTokenProvider"/>: acquires Azure Resource Manager tokens for
/// managed-identity provisioning (the <c>FederatedCredentials</c> and <c>UserManagedIdentity</c> auth
/// types) through the consent-capable public client, consenting on first use instead of
/// relying on preauthorization of the CLI first-party app for ARM.
///
/// It resolves the consent client id from <c>AH_CLI_ARM_CLIENT_ID</c> (or
/// <see cref="ArmDelegatedConsent.DefaultArmClientId"/> — the well-known Azure CLI public client),
/// builds the ARM <c>/.default</c> scope through the same <see cref="PacResourceScopeNormalizer"/> the
/// silent path uses, and delegates the silent-first / interactive-consent flow to
/// <see cref="MsalConsentTokenAcquisition"/>. ARM consent is user-consentable, so — unlike Graph — no
/// admin-consent URL is required; the non-interactive remediation simply asks to rerun interactively.
/// </summary>
public sealed class MsalArmConsentTokenProvider : IArmConsentTokenProvider
{
    private static readonly PacResourceScopeNormalizer ScopeNormalizer = new();

    private readonly MsalConsentTokenAcquisition _acquisition;
    private readonly ArmConsentClientConfiguration _client;

    public MsalArmConsentTokenProvider(
        MsalConsentTokenAcquisition acquisition,
        ArmConsentClientConfiguration client)
    {
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc />
    public Task<TokenAcquisitionResult> AcquireArmTokenAsync(
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        // ValidateClientId (via ResolveClientId) rejects an empty/malformed id and the CLI first-party app
        // id: a first-party app can only be preauthorized for ARM, never consented.
        var clientId = Guid.Parse(_client.ClientId);
        var armScope = ScopeNormalizer.ToDefaultScope(IdentityProvisioningConfig.ArmResource);

        return _acquisition.AcquireAsync(
            clientId,
            _client.RedirectUri,
            new[] { armScope },
            targetTenantId,
            interactionMessage: "Azure Resource Manager consent is required for managed-identity provisioning, " +
                "but interaction is disabled (--non-interactive).",
            interactionRemediation: "Rerun interactively so you can consent to Azure Resource Manager on first " +
                "use, then retry.",
            interactionResource: InteractionRequiredResource.AzureResourceManager,
            cancellationToken);
    }
}
