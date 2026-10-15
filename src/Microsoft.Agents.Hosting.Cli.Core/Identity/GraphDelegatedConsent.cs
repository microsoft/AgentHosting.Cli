using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

/// <summary>
/// Delegated Microsoft Graph consent for App Registration provisioning. Rather than depending on the
/// CLI's Power Platform first-party app id being centrally preauthorized for Microsoft Graph, a separate
/// consent-capable public client acquires Graph tokens and a tenant admin consents to it once on first
/// use (surfacing a <c>/v2.0/adminconsent</c> URL when consent is missing) — a standard delegated-consent
/// pattern. A365's DevTools CLI defaults its interactive client to
/// <c>AuthenticationConstants.PowershellClientId</c> (<c>1950a258…</c>), which is <b>Azure PowerShell</b>:
/// Microsoft preauthorizes it for ARM/AAD but <b>not</b> for modern Microsoft Graph app-management
/// scopes, so requesting Graph through it fails with <c>AADSTS65002</c>. We therefore default to the
/// <b>Microsoft Graph Command Line Tools</b> client (the Connect-MgGraph SDK client), which Microsoft
/// preauthorizes for exactly these Graph delegated scopes. The behavior is always on — there is no
/// opt-in flag — and <see cref="DefaultGraphClientId"/> is used unless an explicit override is supplied.
/// The logic is pure and deterministic (aside from the anti-replay <c>state</c>) so the dry-run plan and
/// the apply path share the same decisions.
/// </summary>
public static class GraphDelegatedConsent
{
    /// <summary>
    /// The default consent-capable Microsoft Graph client: the well-known <b>Microsoft Graph Command
    /// Line Tools</b> public client (the Microsoft Graph PowerShell SDK / <c>Connect-MgGraph</c> client).
    /// Unlike A365's Azure PowerShell client (<c>1950a258…</c>, which yields <c>AADSTS65002</c> against
    /// Microsoft Graph because it is not Graph-preauthorized), this client is Microsoft-preauthorized for
    /// Graph delegated scopes, so consent-on-first-use works in any tenant. It is a placeholder until a
    /// dedicated AgentHost multi-tenant client is provisioned (the open "which client to ship" decision).
    /// </summary>
    public const string DefaultGraphClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";
    public const string DefaultRedirectUri = "http://localhost";

    /// <summary>
    /// The delegated Microsoft Graph scopes App Registration provisioning needs. Consent to these is
    /// exactly what central preauthorization would otherwise grant to the CLI's first-party app.
    /// </summary>
    public static IReadOnlyList<string> GraphScopes { get; } =
    [
        IdentityProvisioningConfig.GraphApplicationReadWriteScope,
        IdentityProvisioningConfig.GraphApplicationReadScope,
        IdentityProvisioningConfig.GraphAppRoleAssignmentReadWriteScope,
        IdentityProvisioningConfig.GraphUserReadScope,
    ];

    /// <summary>
    /// Resolves the consent-capable Graph client: the supplied override when present and valid,
    /// otherwise <see cref="DefaultGraphClientId"/>.
    /// </summary>
    public static string ResolveClientId(string? graphClientIdOverride) =>
        string.IsNullOrWhiteSpace(graphClientIdOverride)
            ? DefaultGraphClientId
            : ValidateClientId(graphClientIdOverride);

    /// <summary>
    /// Validates an explicit Graph client id override and returns it. It must be a non-empty GUID and
    /// must not be the CLI's Power Platform first-party app id: first-party-to-first-party Graph access
    /// can only be granted by preauthorization, never by consent, so pointing the consent client at
    /// that app can never work.
    /// </summary>
    public static string ValidateClientId(string? graphClientId)
    {
        if (string.IsNullOrWhiteSpace(graphClientId) ||
            !Guid.TryParse(graphClientId, out var clientId) ||
            clientId == Guid.Empty)
        {
            throw new UsageException("The Microsoft Graph consent client id must be a non-empty GUID.");
        }

        if (string.Equals(
                graphClientId,
                PowerPlatformApiPermissions.AgentHostCliApplicationId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException(
                "The Microsoft Graph consent client must be a consent-capable client, not the CLI " +
                $"first-party app id {PowerPlatformApiPermissions.AgentHostCliApplicationId}: a " +
                "first-party app can only be preauthorized for Microsoft Graph, never consented.");
        }

        return graphClientId;
    }

    /// <summary>
    /// Builds the tenant admin-consent URL a Global Administrator opens once so the consent-capable
    /// client may call Microsoft Graph on the CLI's behalf.
    /// </summary>
    public static string BuildAdminConsentUrl(
        string tenantId,
        GraphConsentClientConfiguration? configuration = null,
        bool includeState = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        configuration ??= GraphConsentClientConfiguration.Default;
        var scope = string.Join("%20", GraphScopes.Select(Uri.EscapeDataString));
        var url = $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent" +
            $"?client_id={Uri.EscapeDataString(configuration.ClientId)}" +
            $"&scope={scope}" +
            $"&redirect_uri={Uri.EscapeDataString(configuration.RedirectUri)}";
        return includeState ? $"{url}&state={Guid.NewGuid():N}" : url;
    }

    /// <summary>Builds the delegated-consent plan (resolved client id plus its admin-consent URL).</summary>
    public static GraphDelegatedConsentPlan Create(
        string tenantId,
        GraphConsentClientConfiguration configuration,
        bool includeState = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new GraphDelegatedConsentPlan(
            configuration.ClientId,
            BuildAdminConsentUrl(tenantId, configuration, includeState));
    }
}

/// <summary>The consent-capable Microsoft Graph client selected for a run and its admin-consent URL.</summary>
public sealed record GraphDelegatedConsentPlan(string GraphClientId, string AdminConsentUrl);

/// <summary>
/// The single resolved Graph consent client used by both token acquisition and provisioning reports.
/// Resolve it once at composition time so the apply path and its remediation URL cannot diverge.
/// </summary>
public sealed class GraphConsentClientConfiguration
{
    private GraphConsentClientConfiguration(string clientId, string redirectUri)
    {
        ClientId = clientId;
        RedirectUri = redirectUri;
    }

    public string ClientId { get; }

    public string RedirectUri { get; }

    public static GraphConsentClientConfiguration Default { get; } =
        new(GraphDelegatedConsent.DefaultGraphClientId, GraphDelegatedConsent.DefaultRedirectUri);

    public static GraphConsentClientConfiguration Resolve(
        string? clientIdOverride,
        string? redirectUriOverride = null) =>
        new(
            GraphDelegatedConsent.ResolveClientId(clientIdOverride),
            ConsentClientRedirectUri.Resolve(
                redirectUriOverride,
                clientIdOverride,
                GraphDelegatedConsent.DefaultRedirectUri,
                "AH_CLI_GRAPH_REDIRECT_URI"));
}

internal static class ConsentClientRedirectUri
{
    public static string Resolve(
        string? redirectUriOverride,
        string? clientIdOverride,
        string defaultRedirectUri,
        string settingName)
    {
        if (!string.IsNullOrWhiteSpace(clientIdOverride) &&
            string.IsNullOrWhiteSpace(redirectUriOverride))
        {
            throw new UsageException(
                $"{settingName} must be configured when overriding the consent client id.");
        }

        var value = string.IsNullOrWhiteSpace(redirectUriOverride)
            ? defaultRedirectUri
            : redirectUriOverride.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var redirectUri) ||
            !string.Equals(redirectUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !redirectUri.IsLoopback ||
            !string.IsNullOrEmpty(redirectUri.Fragment) ||
            !string.IsNullOrEmpty(redirectUri.UserInfo))
        {
            throw new UsageException(
                $"{settingName} must be an absolute HTTP loopback URI without user info or a fragment.");
        }

        return value;
    }
}
