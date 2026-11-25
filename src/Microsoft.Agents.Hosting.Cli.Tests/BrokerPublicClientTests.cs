using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class BrokerPublicClientTests
{
    [Theory]
    [InlineData("macos", true, "msauth.com.msauth.unsignedapp://auth")]
    [InlineData("macos", false, "msauth.com.msauth.unsignedapp://auth")]
    [InlineData("linux", true, "https://login.microsoftonline.com/common/oauth2/nativeclient")]
    [InlineData("linux", false, "https://login.microsoftonline.com/common/oauth2/nativeclient")]
    public async Task TheClient_CarriesTheExactRegisteredReplyUrl_WithOrWithoutTheBroker(string platform, bool useBroker, string expectedRedirectUri)
    {
        using var directory = new TempDir();

        var client = await ClientAsync(directory.Path, FakePlatformInfo.Named(platform), useBroker);

        Assert.Equal(expectedRedirectUri, client.AppConfig.RedirectUri);
    }

    [Theory]
    [InlineData("windows", true, true)]
    [InlineData("windows", false, true)]
    [InlineData("linux", true, false)]
    [InlineData("linux", false, false)]
    [InlineData("macos", true, false)]
    [InlineData("macos", false, false)]
    public async Task OnlyTheWindowsClient_NamesAParentWindow(string platform, bool useBroker, bool expectParentWindow)
    {
        using var directory = new TempDir();

        var client = await ClientAsync(directory.Path, FakePlatformInfo.Named(platform), useBroker);

        Assert.Equal(expectParentWindow, client.AppConfig.ParentActivityOrWindowFunc is not null);
    }

    [Fact]
    public async Task WithDebugOutput_TheClientForwardsMsalsPiiFreeLog()
    {
        using var directory = new TempDir();
        var debugOutput = new StringWriter();

        var client = await ClientAsync(directory.Path, FakePlatformInfo.Linux(), useBroker: true, debugOutput);
        client.AppConfig.LoggingCallback!(LogLevel.Warning,
            "False MSAL 4.92.0.0 MSAL.NetCore runtime OS [2026-11-20 04:18:15Z - 59de06fd-5856-40d0-acd4-c53c1f44781e] broker handshake", false);
        client.AppConfig.LoggingCallback!(LogLevel.Warning, "broker warning", false);

        Assert.False(client.AppConfig.EnablePiiLogging);
        Assert.Equal(LogLevel.Warning, client.AppConfig.LogLevel);
        Assert.Contains("2026-11-20 04:18:15.000 - 59de06fd-5856-40d0-acd4-c53c1f44781e [MSAL] broker handshake", debugOutput.ToString());
        Assert.Contains("2026-11-20 04:18:15.000 [MSAL] False MSAL 4.92.0.0 MSAL.NetCore runtime OS", debugOutput.ToString());
        Assert.EndsWith("broker warning" + Environment.NewLine, debugOutput.ToString());
        Assert.DoesNotContain("PII logging:", debugOutput.ToString());
        Assert.DoesNotContain("\u001b", debugOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutDebugOutput_TheClientLogsNothing()
    {
        using var directory = new TempDir();

        var client = await ClientAsync(directory.Path, FakePlatformInfo.Linux(), useBroker: true);

        Assert.Null(client.AppConfig.LoggingCallback);
    }

    [Fact]
    public async Task RealOfflineMsalCallbacksShareContextAcrossClientConfigurations()
    {
        using var directory = new TempDir();
        var debugOutput = new StringWriter();
        var cache = new EmptyLegacyCache();
        using var provider = FakeClientProviders.Create(FakePlatformInfo.Linux(), directory.Path, debugOutput, legacyCache: cache);
        var first = await provider.GetClientAsync(useBroker: false, tenantId: null, CancellationToken.None);
        var second = await provider.GetClientAsync(useBroker: false, Guid.Parse("11111111-2222-3333-4444-555555555555"), CancellationToken.None);

        Assert.Empty(await first.GetAccountsAsync());
        Assert.Empty(await second.GetAccountsAsync());
        Assert.Equal(2, cache.ReadCount);

        var text = debugOutput.ToString();
        Assert.Equal(1, text.Split("[MSAL] False MSAL ").Length - 1);
        Assert.DoesNotContain("correlation established", text);
        Assert.DoesNotContain("==== GetAccounts started", text);
        Assert.Contains("[Internal cache]", text);
        Assert.DoesNotContain("MSAL operation:", text);
        Assert.DoesNotContain("PII logging:", text);
        Assert.DoesNotContain("MSAL SDK timestamp", text);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.000 \[MSAL\] False MSAL ", text);
    }

    [Fact]
    public async Task RealCachedTokenAcquisitionPreservesSdkCallbacksWithoutLoggingTheToken()
    {
        using var directory = new TempDir();
        var debugOutput = new StringWriter();
        using var provider = FakeClientProviders.Create(FakePlatformInfo.Linux(), directory.Path, debugOutput);
        const string tenant = "11111111-2222-3333-4444-555555555555";
        const string scope = "https://api.powerplatform.com/.default";
        const string username = "offline@example.invalid";
        var client = await provider.GetClientAsync(useBroker: false, tenantId: null, CancellationToken.None);
        MsalTokenCacheSeed.Seed(client, tenant, scope, username);
        var account = Assert.Single(await client.GetAccountsAsync());

        var result = await client.AcquireTokenSilent([scope], account).ExecuteAsync(CancellationToken.None);

        Assert.Equal(MsalTokenCacheSeed.AccessToken(username), result.AccessToken);
        var text = debugOutput.ToString();
        Assert.DoesNotContain("Returning 1 accounts", text);
        Assert.DoesNotContain("MSAL operation:", text);
        Assert.DoesNotContain("request context created", text);
        Assert.DoesNotContain("=== AcquireTokenSilent Parameters ===", text);
        Assert.DoesNotContain("=== Request Data ===", text);
        Assert.DoesNotContain("=== Token Acquisition (SilentRequest) started:", text);
        Assert.Contains("[Internal cache]", text);
        Assert.DoesNotContain("Returning access token found in cache", text);
        Assert.DoesNotContain(result.AccessToken, text);
        Assert.DoesNotContain("refresh-token", text);
    }

    [Theory]
    [InlineData("windows", false, BrokerOptions.OperatingSystems.Windows)]
    [InlineData("windows", true, BrokerOptions.OperatingSystems.Windows)]
    [InlineData("macos", false, BrokerOptions.OperatingSystems.OSX)]
    [InlineData("macos", true, BrokerOptions.OperatingSystems.OSX)]
    [InlineData("linux", false, BrokerOptions.OperatingSystems.Linux)]
    [InlineData("linux", true, BrokerOptions.OperatingSystems.Linux)]
    public void TheBrokerOptions_EnableThisPlatformsBroker_AndListItsAccountsOnlyWhenAsked(string platform, bool listAccounts, BrokerOptions.OperatingSystems enabledOn)
    {
        var options = MsalPublicClientProvider.CreateBrokerOptions(FakePlatformInfo.Named(platform), listAccounts);

        Assert.Equal(enabledOn, options.EnabledOn);
        Assert.Equal(listAccounts, options.ListOperatingSystemAccounts);
    }

    [Fact]
    public async Task TheAccountListingBrokerClient_IsCachedApartFromTheOrdinaryBrokerClient()
    {
        using var directory = new TempDir();
        var provider = FakeClientProviders.Create(FakePlatformInfo.Linux(), directory.Path);

        var listing = await provider.GetClientAsync(useBroker: true, tenantId: null, CancellationToken.None, listBrokerAccounts: true);
        var ordinary = await provider.GetClientAsync(useBroker: true, tenantId: null, CancellationToken.None);

        Assert.NotSame(listing, ordinary);
        Assert.Same(listing, await provider.GetClientAsync(useBroker: true, tenantId: null, CancellationToken.None, listBrokerAccounts: true));
        Assert.Same(ordinary, await provider.GetClientAsync(useBroker: true, tenantId: null, CancellationToken.None));
    }

    [Fact]
    public async Task AClientWithoutTheBroker_IgnoresTheRequestToListBrokerAccounts()
    {
        using var directory = new TempDir();
        var provider = FakeClientProviders.Create(FakePlatformInfo.Linux(), directory.Path);

        Assert.Same(
            await provider.GetClientAsync(useBroker: false, tenantId: null, CancellationToken.None),
            await provider.GetClientAsync(useBroker: false, tenantId: null, CancellationToken.None, listBrokerAccounts: true));
    }

    private sealed class EmptyLegacyCache : ILegacyMsalTokenCacheBackend
    {
        public int ReadCount { get; private set; }

        public IDisposable AcquireLease(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return new Lease();
        }

        public Task<byte[]?> ReadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult<byte[]?>(null);
        }

        public Task WriteAsync(ReadOnlyMemory<byte> value, CancellationToken ct) => throw new InvalidOperationException("Account listing must not write the cache.");
        public Task ClearAsync(CancellationToken ct) => throw new InvalidOperationException("Account listing must not clear the cache.");

        private sealed class Lease : IDisposable
        {
            public void Dispose() { }
        }
    }

    private static Task<IPublicClientApplication> ClientAsync(string configDirectory, FakePlatformInfo platform, bool useBroker, TextWriter? debugOutput = null) =>
        FakeClientProviders.Create(platform, configDirectory, debugOutput: debugOutput).GetClientAsync(useBroker, tenantId: null, CancellationToken.None);
}
