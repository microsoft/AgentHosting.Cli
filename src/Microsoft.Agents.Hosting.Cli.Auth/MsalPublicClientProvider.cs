using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>The single place an MSAL public client is constructed, turning the pure client, authority, and platform-gated broker configuration into a cached live client with its secure token cache.</summary>
public sealed class MsalPublicClientProvider
{
    private readonly AgentHostAuthOptions _options;
    private readonly IPlatformInfo _platform;
    private ILegacyMsalTokenCacheBackend? _legacyCache;
    private readonly MsalPublicClientConfigurator _configurator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IPublicClientApplication> _clients = new(StringComparer.Ordinal);

    public MsalPublicClientProvider(AgentHostAuthOptions options, IPlatformInfo platform, ISecureTokenCacheProbe probe, ILegacyMsalTokenCacheBackend? legacyCache = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        ArgumentNullException.ThrowIfNull(probe);
        _legacyCache = legacyCache;
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
        bool listBrokerAccounts = false,
        CloudInstance? cloudOverride = null)
    {
        var clientApp = clientIdOverride is { } overrideId
            ? AgentHostClientAppConfiguration.Create(overrideId.ToString(), _platform, redirectUriOverride)
            : AgentHostClientAppConfiguration.Create(_options.ClientId, _platform);
        var authority = AgentHostAuthorityResolver.Resolve(cloudOverride ?? _options.Cloud, tenantId);
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
            LegacyMsalTokenCache.Attach(client.UserTokenCache, GetLegacyCache);
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

    internal Task ClearLegacyCacheAsync(CancellationToken ct) => GetLegacyCache().ClearAsync(ct);

    private ILegacyMsalTokenCacheBackend GetLegacyCache()
    {
        if (_legacyCache is { } existing) return existing;
        var created = OperatingSystem.IsLinux() && _options.AllowUnprotectedCache
            ? LegacyMsalTokenCacheBackendFactory.CreateWithUnprotectedTokenCacheFallback(_options.ConfigDirectory)
            : LegacyMsalTokenCacheBackendFactory.Create(_options.ConfigDirectory);
        return Interlocked.CompareExchange(ref _legacyCache, created, null) ?? created;
    }
}
