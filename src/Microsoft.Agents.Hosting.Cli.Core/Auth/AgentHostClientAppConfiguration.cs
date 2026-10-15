using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// The AgentHost public-client application identity (client id + redirect URI) used to construct MSAL
/// clients. This is the AgentHost adaptation of PAC's <c>PacOAuth.ClientAppConfig</c>, which
/// hard-codes the first-party "Power Platform CLI - pac" registration.
///
/// The AgentHost public-client registration in <see cref="DefaultClientId"/> is used when nothing is
/// supplied, so a fresh clone signs in with <c>msagent auth login</c> and no further configuration.
/// <c>AH_CLI_CLIENT_ID</c> overrides it, and the guardrails still apply to whatever is supplied:
///
/// <list type="bullet">
///   <item>the all-zero GUID is the explicit <em>unconfigured</em> sentinel;</item>
///   <item>a non-GUID value is rejected as malformed;</item>
///   <item>PAC's own client id is forbidden, so the product identity can never be borrowed.</item>
/// </list>
///
/// Construction fails with a <see cref="ClientApplicationConfigurationException"/> (exit 1) that names
/// the required setting, so <c>auth login</c> stops before any interactive request is built.
/// </summary>
public sealed class AgentHostClientAppConfiguration
{
    /// <summary>
    /// PAC's first-party "Power Platform CLI - pac" client id (from <c>PacOAuth.ClientId</c>). Reusing
    /// it is forbidden: it is product identity, not reusable infrastructure.
    /// </summary>
    public static readonly Guid ForbiddenPacClientId = new("9cee029c-6210-4654-90bb-17e6e9d36617");

    /// <summary>The AgentHost public-client registration used when nothing overrides it.</summary>
    public static readonly Guid DefaultClientId = new("21fadfba-178a-47f0-8ce6-07c05eb346ca");

    /// <summary>
    /// The all-zero GUID sentinel that marks an unconfigured client id.
    /// </summary>
    public static readonly Guid UnconfiguredClientId = Guid.Empty;

    /// <summary>The macOS broker reply URL registered on the first-party application for unsigned executables.</summary>
    public static readonly Uri MacOsBrokerRedirectUri = new("msauth.com.msauth.unsignedapp://auth");

    /// <summary>The Linux broker reply URL registered on the first-party application.</summary>
    public static readonly Uri LinuxBrokerRedirectUri = new("https://login.microsoftonline.com/common/oauth2/nativeclient");

    /// <summary>Builds the Windows broker (WAM) reply URL for <paramref name="clientId"/>.</summary>
    public static Uri WindowsBrokerRedirectUri(Guid clientId) => new($"ms-appx-web://microsoft.aad.brokerplugin/{clientId:D}");

    /// <summary>Selects the broker reply URL registered for <paramref name="platform"/>: macOS and Linux use fixed URLs, and every other platform uses the Windows URL derived from <paramref name="clientId"/>.</summary>
    public static Uri PlatformRedirectUri(IPlatformInfo platform, Guid clientId) => platform switch
    {
        { IsMacOs: true } => MacOsBrokerRedirectUri,
        { IsLinux: true } => LinuxBrokerRedirectUri,
        _ => WindowsBrokerRedirectUri(clientId),
    };

    /// <summary>The name of the environment setting the host resolves the client id from.</summary>
    public const string ClientIdSettingName = "AH_CLI_CLIENT_ID";

    private AgentHostClientAppConfiguration(Guid clientId, Uri redirectUri)
    {
        ClientId = clientId;
        RedirectUri = redirectUri;
    }

    /// <summary>The validated AgentHost public-client application id.</summary>
    public Guid ClientId { get; }

    /// <summary>The redirect URI passed to MSAL.</summary>
    public Uri RedirectUri { get; }

    /// <summary>Whether <paramref name="redirectUri"/> is an HTTP loopback reply URL, the only shape MSAL's .NET system browser can listen on and so the only one that lets a Graph or ARM consent client use its browser flow.</summary>
    public static bool IsLoopbackRedirect(string? redirectUri) =>
        !string.IsNullOrWhiteSpace(redirectUri)
        && Uri.TryCreate(redirectUri.Trim(), UriKind.Absolute, out var parsed)
        && IsLoopbackRedirect(parsed);

    private static bool IsLoopbackRedirect(Uri redirectUri) =>
        redirectUri.IsLoopback
        && string.Equals(redirectUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates the raw client id (<see langword="null"/> meaning <see cref="DefaultClientId"/>) and builds the configuration with <paramref name="redirectUri"/> or, when none is given, the broker reply URL for <paramref name="platform"/>, throwing <see cref="ClientApplicationConfigurationException"/> for an unconfigured, malformed, or forbidden id.</summary>
    public static AgentHostClientAppConfiguration Create(string? clientId, IPlatformInfo platform, string? redirectUri = null)
    {
        ArgumentNullException.ThrowIfNull(platform);

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return Build(DefaultClientId, platform, redirectUri);
        }

        if (!Guid.TryParse(clientId.Trim(), out var parsedClientId))
        {
            throw new ClientApplicationConfigurationException(
                $"The configured {ClientIdSettingName} value '{clientId}' is not a valid GUID.",
                remediation: $"Set {ClientIdSettingName} to the AgentHost public-client application (GUID) id.");
        }

        if (parsedClientId == UnconfiguredClientId)
        {
            throw Unconfigured();
        }

        if (parsedClientId == ForbiddenPacClientId)
        {
            throw new ClientApplicationConfigurationException(
                "The Power Platform CLI (PAC) first-party client id must not be reused by the AgentHost " +
                "demonstrator; it is product identity, not shared infrastructure.",
                remediation: $"Set {ClientIdSettingName} to a dedicated AgentHost application registration id.");
        }

        return Build(parsedClientId, platform, redirectUri);
    }

    private static AgentHostClientAppConfiguration Build(Guid clientId, IPlatformInfo platform, string? redirectUri) =>
        new(clientId, string.IsNullOrWhiteSpace(redirectUri) ? PlatformRedirectUri(platform, clientId) : new Uri(redirectUri.Trim(), UriKind.Absolute));

    private static ClientApplicationConfigurationException Unconfigured() =>
        new(
            $"{ClientIdSettingName} is not configured. The production first-party AgentHost application " +
            "registration is unresolved, so interactive sign-in cannot proceed.",
            remediation: $"Set {ClientIdSettingName} to the AgentHost public-client application (GUID) id before running `msagent auth login`.");
}
