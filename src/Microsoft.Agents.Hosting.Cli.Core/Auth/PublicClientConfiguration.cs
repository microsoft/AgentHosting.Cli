using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// The platform- and flow-resolved configuration for an MSAL public client, captured as pure data. It
/// is the AgentHost adaptation of the decisions PAC's <c>AuthApplication</c> public-client constructor
/// bakes into <c>PublicClientApplicationBuilder</c>: redirect URI,
/// non-validated authority, and the platform-gated broker settings.
///
/// A separate adapter turns this record into a live <c>IPublicClientApplication</c>, including broker
/// wiring and cache registration, and performs token acquisition. Keeping the configuration separate
/// makes the "only place that constructs a public client" decision unit-testable without an MSAL
/// dependency or token acquisition.
/// </summary>
public sealed record PublicClientConfiguration
{
    /// <summary>The AgentHost public-client application id.</summary>
    public required Guid ClientId { get; init; }

    /// <summary>The redirect URI.</summary>
    public required Uri RedirectUri { get; init; }

    /// <summary>The resolved authority URL.</summary>
    public required Uri AuthorityUrl { get; init; }

    /// <summary>Whether MSAL should validate the authority. PAC passes <c>validateAuthority: false</c>.</summary>
    public bool ValidateAuthority { get; init; }

    /// <summary>Whether to enable the operating-system broker (WAM, the macOS broker, or the Linux identity broker) for this client.</summary>
    public bool UseBroker { get; init; }

    /// <summary>
    /// Whether MSAL instance discovery is disabled. PAC disables it when the Windows broker is enabled
    /// (<c>WithInstanceDiscovery(false)</c>).
    /// </summary>
    public bool DisableInstanceDiscovery { get; init; }

    /// <summary>
    /// Whether the client needs a parent window handle (Windows uses
    /// <c>WithParentActivityOrWindow</c> so interactive/broker flows attach to the console window).
    /// </summary>
    public bool RequiresParentWindow { get; init; }

    /// <summary>Whether the broker lists every account it holds, so sign-in can find a named account this app's token cache has never seen.</summary>
    public bool ListBrokerAccounts { get; init; }
}

/// <summary>Assembles the platform-gated <see cref="PublicClientConfiguration"/>, the single place that decides how a public client is configured.</summary>
public sealed class MsalPublicClientConfigurator
{
    private readonly IPlatformInfo _platform;

    /// <summary>Creates a configurator for <paramref name="platform"/>.</summary>
    public MsalPublicClientConfigurator(IPlatformInfo platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    /// <summary>Whether this platform has an MSAL broker, which every broker-enabled client then uses for silent and interactive requests alike.</summary>
    public bool IsBrokerSupported => BrokerCapability.IsSupported(_platform);

    /// <summary>Builds the configuration, honouring <paramref name="requestBroker"/> and <paramref name="listBrokerAccounts"/> only where the broker is supported and otherwise downgrading to a non-broker client.</summary>
    public PublicClientConfiguration Create(AgentHostClientAppConfiguration clientApp, AuthorityInfo authority, bool requestBroker, bool listBrokerAccounts = false)
    {
        ArgumentNullException.ThrowIfNull(clientApp);
        ArgumentNullException.ThrowIfNull(authority);

        var useBroker = requestBroker && IsBrokerSupported;
        return new PublicClientConfiguration
        {
            ClientId = clientApp.ClientId,
            RedirectUri = useBroker ? AgentHostClientAppConfiguration.PlatformRedirectUri(_platform, clientApp.ClientId) : clientApp.RedirectUri,
            AuthorityUrl = authority.AuthorityUrl,
            ValidateAuthority = false,
            UseBroker = useBroker,
            DisableInstanceDiscovery = _platform.IsWindows && useBroker,
            RequiresParentWindow = _platform.IsWindows,
            ListBrokerAccounts = useBroker && listBrokerAccounts,
        };
    }
}
