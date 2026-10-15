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
        client.AppConfig.LoggingCallback!(LogLevel.Info, "broker handshake", false);

        Assert.False(client.AppConfig.EnablePiiLogging);
        Assert.Equal(LogLevel.Info, client.AppConfig.LogLevel);
        Assert.Contains("broker handshake", debugOutput.ToString());
    }

    [Fact]
    public async Task WithoutDebugOutput_TheClientLogsNothing()
    {
        using var directory = new TempDir();

        var client = await ClientAsync(directory.Path, FakePlatformInfo.Linux(), useBroker: true);

        Assert.Null(client.AppConfig.LoggingCallback);
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

    private static Task<IPublicClientApplication> ClientAsync(string configDirectory, FakePlatformInfo platform, bool useBroker, TextWriter? debugOutput = null) =>
        FakeClientProviders.Create(platform, configDirectory, debugOutput: debugOutput).GetClientAsync(useBroker, tenantId: null, CancellationToken.None);
}
