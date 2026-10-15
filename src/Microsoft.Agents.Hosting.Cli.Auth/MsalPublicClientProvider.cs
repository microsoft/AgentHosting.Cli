using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>The single place an MSAL public client is constructed, turning the pure client, authority, and platform-gated broker configuration into a cached live client with its secure token cache.</summary>
public sealed class MsalPublicClientProvider
{
    private readonly AgentHostAuthOptions _options;
    private readonly IPlatformInfo _platform;
    private readonly AgentHostTokenCache _tokenCache;
    private readonly MsalPublicClientConfigurator _configurator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IPublicClientApplication> _clients = new(StringComparer.Ordinal);

    public MsalPublicClientProvider(AgentHostAuthOptions options, IPlatformInfo platform, ISecureTokenCacheProbe probe)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        ArgumentNullException.ThrowIfNull(probe);
        _tokenCache = new AgentHostTokenCache(platform, probe);
        _configurator = new MsalPublicClientConfigurator(platform);
    }

    /// <summary>Whether this platform has an MSAL broker, which broker-enabled clients use for silent and interactive requests alike.</summary>
    public bool IsBrokerSupported => _configurator.IsBrokerSupported;

    /// <summary>Whether <paramref name="clientId"/> is the AgentHost first-party app this session is configured for, the only application whose broker reply URLs are registered.</summary>
    public bool IsAgentHostClient(Guid clientId) => TryResolveAgentHostClientId() is { } resolved && resolved == clientId;

    private Guid? TryResolveAgentHostClientId()
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId))
        {
            return AgentHostClientAppConfiguration.DefaultClientId;
        }

        return Guid.TryParse(_options.ClientId.Trim(), out var clientId) && clientId != Guid.Empty ? clientId : null;
    }

    /// <summary>Resolves, building on first use, the public client for the given broker preference, broker account listing, and optional target tenant.</summary>
    public async Task<IPublicClientApplication> GetClientAsync(
        bool useBroker,
        Guid? tenantId,
        CancellationToken cancellationToken,
        Guid? clientIdOverride = null,
        string? redirectUriOverride = null,
        bool listBrokerAccounts = false)
    {
        var clientApp = clientIdOverride is { } overrideId
            ? AgentHostClientAppConfiguration.Create(overrideId.ToString(), _platform, redirectUriOverride)
            : AgentHostClientAppConfiguration.Create(_options.ClientId, _platform);
        var authority = AgentHostAuthorityResolver.Resolve(_options.Cloud, tenantId);
        var configuration = _configurator.Create(
            clientApp,
            authority,
            requestBroker: useBroker && IsAgentHostClient(clientApp.ClientId),
            listBrokerAccounts);

        var key =
            $"{configuration.ClientId}|{configuration.RedirectUri}|{configuration.UseBroker}|{configuration.ListBrokerAccounts}|{configuration.AuthorityUrl}";

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_clients.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var client = Build(configuration);
            await RegisterCacheAsync(client, cancellationToken).ConfigureAwait(false);
            _clients[key] = client;
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }

    private IPublicClientApplication Build(PublicClientConfiguration configuration)
    {
        var builder = PublicClientApplicationBuilder
            .Create(configuration.ClientId.ToString())
            .WithRedirectUri(configuration.RedirectUri.OriginalString)
            .WithAuthority(configuration.AuthorityUrl, configuration.ValidateAuthority);

        if (configuration.RequiresParentWindow)
        {
            builder.WithParentActivityOrWindow(NativeMethods.GetConsoleOrTerminalWindow);
        }

        if (configuration.DisableInstanceDiscovery)
        {
            builder.WithInstanceDiscovery(false);
        }

        if (configuration.UseBroker)
        {
            builder.WithBroker(CreateBrokerOptions(_platform, configuration.ListBrokerAccounts));
        }

        if (_options.DebugOutput is { } debugOutput)
        {
            builder.WithLogging((_, message, _) => debugOutput.WriteLine(message), LogLevel.Info, enablePiiLogging: false, enableDefaultPlatformLogging: false);
        }

        return builder.Build();
    }

    internal static BrokerOptions CreateBrokerOptions(IPlatformInfo platform, bool listAccounts) =>
        new(platform.IsWindows ? BrokerOptions.OperatingSystems.Windows : platform.IsLinux ? BrokerOptions.OperatingSystems.Linux : BrokerOptions.OperatingSystems.OSX)
        {
            ListOperatingSystemAccounts = listAccounts,
        };

    private async Task RegisterCacheAsync(IPublicClientApplication client, CancellationToken cancellationToken)
    {
        var descriptor = AgentHostCacheDescriptor.Create(_options.ConfigDirectory);
        var protection = await _tokenCache
            .ResolveProtectionAsync(descriptor, _options.AllowUnprotectedCache, cancellationToken)
            .ConfigureAwait(false);

        Directory.CreateDirectory(descriptor.CacheDirectory);
        var properties = MsalCacheStorage.Build(descriptor, useUnprotectedFile: !protection.IsProtected);
        var helper = await MsalCacheHelper.CreateAsync(properties).ConfigureAwait(false);
        helper.RegisterCache(client.UserTokenCache);
    }
}
