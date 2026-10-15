using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>
/// The live <see cref="IGraphConsentTokenProvider"/>: acquires Microsoft Graph tokens for App
/// Registration provisioning through the consent-capable public client, consenting on
/// first use instead of relying on central preauthorization of the CLI first-party app.
///
/// It resolves the consent client id from <c>AH_CLI_GRAPH_CLIENT_ID</c> (or
/// <see cref="GraphDelegatedConsent.DefaultGraphClientId"/> — the Microsoft Graph Command Line Tools
/// client) and delegates the silent-first / interactive-consent flow to
/// <see cref="MsalConsentTokenAcquisition"/>. Under <c>--non-interactive</c> a required interaction
/// becomes an <c>InteractionRequiredException</c>, which the provisioning service maps to a
/// <c>consent-required</c> report carrying the admin-consent URL.
/// </summary>
public sealed class MsalGraphConsentTokenProvider : IGraphConsentTokenProvider
{
    private readonly MsalConsentTokenAcquisition _acquisition;
    private readonly GraphConsentClientConfiguration _client;

    public MsalGraphConsentTokenProvider(
        MsalConsentTokenAcquisition acquisition,
        GraphConsentClientConfiguration client)
    {
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc />
    public Task<TokenAcquisitionResult> AcquireGraphTokenAsync(
        IReadOnlyList<string> scopes,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        // ValidateClientId (via ResolveClientId) rejects an empty/malformed id and the CLI first-party app
        // id: a first-party app can only be preauthorized for Graph, never consented.
        var clientId = Guid.Parse(_client.ClientId);

        return _acquisition.AcquireAsync(
            clientId,
            _client.RedirectUri,
            scopes,
            targetTenantId,
            interactionMessage: "Microsoft Graph consent is required for App Registration provisioning, but " +
                "interaction is disabled (--non-interactive).",
            interactionRemediation: "Rerun interactively, or have a tenant admin grant Microsoft Graph consent " +
                "using the admin-consent URL from a dry run, then retry.",
            interactionResource: InteractionRequiredResource.MicrosoftGraph,
            cancellationToken);
    }
}
