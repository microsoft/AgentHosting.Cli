using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>
/// Acquires the delegated user token used by the live-test integration.
///
/// This adapter deliberately uses the configured AgentHost public client and delegates the
/// silent-first acquisition to <see cref="ITokenProvider"/> and interactive consent fallback to
/// <see cref="MsalConsentTokenAcquisition"/>.
/// </summary>
public sealed class MsalLiveTestTokenProvider
{
    /// <summary>The delegated permission required to invoke Copilot Studio agents.</summary>
    public const string CopilotStudioInvokeScope = "https://api.powerplatform.com/CopilotStudio.Copilots.Invoke";

    private readonly ITokenProvider _silentTokenProvider;
    private readonly ILiveTestConsentTokenAcquisition _acquisition;
    private readonly AgentHostClientAppConfiguration _client;

    /// <summary>
    /// Creates an adapter backed by the CLI's silent token provider and shared MSAL consent acquisition.
    /// </summary>
    public MsalLiveTestTokenProvider(
        ITokenProvider silentTokenProvider,
        MsalConsentTokenAcquisition acquisition,
        AgentHostAuthOptions options,
        IPlatformInfo platform)
        : this(
            silentTokenProvider,
            new SharedConsentTokenAcquisition(acquisition),
            options,
            platform)
    {
    }

    internal MsalLiveTestTokenProvider(
        ITokenProvider silentTokenProvider,
        ILiveTestConsentTokenAcquisition acquisition,
        AgentHostAuthOptions options,
        IPlatformInfo platform)
    {
        _silentTokenProvider = silentTokenProvider ?? throw new ArgumentNullException(nameof(silentTokenProvider));
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        ArgumentNullException.ThrowIfNull(options);
        _client = AgentHostClientAppConfiguration.Create(options.ClientId, platform);
    }

    /// <summary>
    /// Acquires the delegated Power Platform bearer token for the resolved destination tenant.
    /// The selected active account is used by the CLI's silent provider. When
    /// <paramref name="nonInteractive"/> is <see langword="true"/>, required interaction is surfaced
    /// without opening UI; otherwise consent acquisition may fall back to the configured interactive flow.
    /// </summary>
    public async Task<string> AcquireDelegatedTokenAsync(
        string targetTenantId,
        bool nonInteractive,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetTenantId))
        {
            throw new ArgumentException(
                "A resolved destination tenant is required for live-test token acquisition.",
                nameof(targetTenantId));
        }

        TokenAcquisitionResult result;
        try
        {
            result = await _silentTokenProvider
                .AcquireTokenSilentAsync([CopilotStudioInvokeScope], targetTenantId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InteractionRequiredException) when (nonInteractive)
        {
            throw new InteractionRequiredException(
                "Copilot Studio delegated consent is required, but interaction is disabled (--non-interactive).",
                remediation: "Rerun interactively so you can sign in or consent to Copilot Studio.",
                resource: InteractionRequiredResource.Unspecified);
        }
        catch (Exception failure) when (!nonInteractive && failure is InteractionRequiredException or BrokerUnavailableException)
        {
            result = await _acquisition
                .AcquireAsync(
                    nonInteractive: false,
                    _client.ClientId,
                    _client.RedirectUri.OriginalString,
                    [CopilotStudioInvokeScope],
                    targetTenantId,
                    interactionMessage:
                        "Copilot Studio delegated consent is required, but interaction is disabled (--non-interactive).",
                    interactionRemediation:
                        "Rerun interactively so you can sign in or consent to Copilot Studio, then retry.",
                    interactionResource: InteractionRequiredResource.Unspecified,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // Keep the bearer opaque and validate only the tenant metadata returned by MSAL before the
        // caller can send the token to the live-test endpoint.
        if (string.IsNullOrWhiteSpace(result.TenantId))
        {
            throw new TokenClaimException(
                "The access token did not report the tenant required by the live-test destination.",
                TokenClaimFailure.WrongTenant,
                remediation: $"Run `msagent auth create --tenant {targetTenantId}` for the intended cloud with an account in that tenant.");
        }

        return AcquiredTokenTenant.Require(result, targetTenantId);
    }

    internal interface ILiveTestConsentTokenAcquisition
    {
        Task<TokenAcquisitionResult> AcquireAsync(
            bool nonInteractive,
            Guid clientId,
            string redirectUri,
            IReadOnlyList<string> scopes,
            string? targetTenantId,
            string interactionMessage,
            string interactionRemediation,
            InteractionRequiredResource interactionResource,
            CancellationToken cancellationToken);
    }

    private sealed class SharedConsentTokenAcquisition(
        MsalConsentTokenAcquisition acquisition) : ILiveTestConsentTokenAcquisition
    {
        private readonly MsalConsentTokenAcquisition _acquisition =
            acquisition ?? throw new ArgumentNullException(nameof(acquisition));

        public Task<TokenAcquisitionResult> AcquireAsync(
            bool nonInteractive,
            Guid clientId,
            string redirectUri,
            IReadOnlyList<string> scopes,
            string? targetTenantId,
            string interactionMessage,
            string interactionRemediation,
            InteractionRequiredResource interactionResource,
            CancellationToken cancellationToken) =>
            _acquisition.AcquireAsync(
                clientId,
                redirectUri,
                scopes,
                targetTenantId,
                interactionMessage,
                interactionRemediation,
                interactionResource,
                cancellationToken,
                trySilentFirst: false);
    }
}
