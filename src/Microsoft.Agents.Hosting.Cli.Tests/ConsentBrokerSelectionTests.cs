using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ConsentBrokerSelectionTests
{
    private const string MacOsReplyUrl = "msauth.com.msauth.unsignedapp://auth";
    private const string LinuxReplyUrl = "https://login.microsoftonline.com/common/oauth2/nativeclient";
    private const string Tenant = "33333333-3333-3333-3333-333333333333";

    private static readonly Guid FirstPartyClientId = AgentHostClientAppConfiguration.DefaultClientId;

    private static readonly string WindowsBrokerRedirectUri =
        AgentHostClientAppConfiguration.WindowsBrokerRedirectUri(FirstPartyClientId).ToString();

    [Theory]
    [InlineData("windows", true)]
    [InlineData("macos", true)]
    [InlineData("linux", true)]
    [InlineData("unknown", false)]
    public void TheBrokerIsSupportedOnWindowsMacOsAndLinux(string platform, bool expected)
    {
        var provider = FakeClientProviders.Create(FakePlatformInfo.Named(platform));

        Assert.Equal(expected, new MsalPublicClientConfigurator(FakePlatformInfo.Named(platform)).IsBrokerSupported);
        Assert.Equal(expected, provider.IsBrokerSupported);
        Assert.True(provider.IsAgentHostClient(FirstPartyClientId));
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("macos")]
    [InlineData("linux")]
    public void FirstPartyConsentUsesTheBroker_OnEveryBrokerPlatform(string platform)
    {
        var useBroker = UseBroker(FakePlatformInfo.Named(platform), "broker");

        Assert.True(useBroker);
        Assert.Equal(
            ConsentInteractiveFlow.Broker,
            ConsentInteractiveFlowSelector.Select(Profile("broker"), useBroker, AgentHostClientAppConfiguration.PlatformRedirectUri(FakePlatformInfo.Named(platform), FirstPartyClientId).OriginalString));
    }

    [Theory]
    [InlineData("windows", "device-code")]
    [InlineData("windows", "interactive")]
    [InlineData("windows", null)]
    [InlineData("macos", "device-code")]
    [InlineData("macos", null)]
    [InlineData("linux", "device-code")]
    [InlineData("linux", "interactive")]
    [InlineData("linux", null)]
    public void ConsentHonoursANonBrokerLoginFlow_InsteadOfForcingTheBroker(string platform, string? flowType)
    {
        var useBroker = UseBroker(FakePlatformInfo.Named(platform), flowType);

        Assert.False(useBroker);
        Assert.Equal(
            ConsentInteractiveFlow.DeviceCode,
            ConsentInteractiveFlowSelector.Select(Profile(flowType), useBroker, WindowsBrokerRedirectUri));
    }

    [Fact]
    public void BrokerProfileFlowTypeIsMatchedCaseInsensitively()
    {
        Assert.True(UseBroker(FakePlatformInfo.Windows(), "Broker"));
        Assert.True(UseBroker(FakePlatformInfo.Linux(), "BROKER"));
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("macos")]
    [InlineData("linux")]
    public void ConsentClientsNeverUseTheBroker_EvenForABrokerProfile(string platform)
    {
        var provider = FakeClientProviders.Create(FakePlatformInfo.Named(platform));

        Assert.False(ConsentInteractiveFlowSelector.UseBroker(
            Profile("broker"),
            provider.IsAgentHostClient(Guid.Parse(GraphConsentClientConfiguration.Default.ClientId)),
            provider.IsBrokerSupported));
        Assert.False(ConsentInteractiveFlowSelector.UseBroker(
            Profile("broker"),
            provider.IsAgentHostClient(Guid.Parse(ArmConsentClientConfiguration.Default.ClientId)),
            provider.IsBrokerSupported));
    }

    [Fact]
    public void FirstPartyConsentFallsBackToDeviceCode_OnAPlatformWithoutABroker()
    {
        var useBroker = UseBroker(FakePlatformInfo.Unknown(), "broker");

        Assert.False(useBroker);
        Assert.Equal(
            ConsentInteractiveFlow.DeviceCode,
            ConsentInteractiveFlowSelector.Select(Profile("broker"), useBroker, WindowsBrokerRedirectUri));
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:1234")]
    [InlineData("http://127.0.0.1:65000/callback")]
    public void LoopbackConsentClientsStillUseTheSystemBrowser(string redirectUri) =>
        Assert.Equal(
            ConsentInteractiveFlow.BrowserInteractive,
            ConsentInteractiveFlowSelector.Select(Profile("interactive"), useBroker: false, redirectUri));

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:1234")]
    public void ADeviceCodeProfileKeepsDeviceCode_EvenForALoopbackConsentClient(string redirectUri) =>
        Assert.Equal(
            ConsentInteractiveFlow.DeviceCode,
            ConsentInteractiveFlowSelector.Select(Profile("device-code"), useBroker: false, redirectUri));

    [Theory]
    [InlineData("Device-Code")]
    [InlineData("DEVICE-CODE")]
    public void DeviceCodeProfileFlowTypeIsMatchedCaseInsensitively(string flowType) =>
        Assert.Equal(
            ConsentInteractiveFlow.DeviceCode,
            ConsentInteractiveFlowSelector.Select(Profile(flowType), useBroker: false, "http://localhost"));

    [Theory]
    [InlineData("https://localhost")]
    [InlineData("https://127.0.0.1:5001/callback")]
    public void HttpsLoopbackIsNotABrowserRedirect_AndFallsBackToDeviceCode(string redirectUri)
    {
        Assert.False(AgentHostClientAppConfiguration.IsLoopbackRedirect(redirectUri));
        Assert.Equal(
            ConsentInteractiveFlow.DeviceCode,
            ConsentInteractiveFlowSelector.Select(Profile("interactive"), useBroker: false, redirectUri));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-uri")]
    [InlineData("https://contoso.example/callback")]
    [InlineData(MacOsReplyUrl)]
    [InlineData(LinuxReplyUrl)]
    public void NonLoopbackNonBrokerRedirectsUseDeviceCode(string? redirectUri) =>
        Assert.Equal(
            ConsentInteractiveFlow.DeviceCode,
            ConsentInteractiveFlowSelector.Select(Profile("interactive"), useBroker: false, redirectUri));

    [Fact]
    public void GraphAndArmConsentClientsAreNeverTheFirstPartyApp_AndKeepTheBrowserFlow()
    {
        var provider = FakeClientProviders.Create(FakePlatformInfo.Windows());
        var graph = GraphConsentClientConfiguration.Default;
        var arm = ArmConsentClientConfiguration.Default;

        Assert.False(provider.IsAgentHostClient(Guid.Parse(graph.ClientId)));
        Assert.False(provider.IsAgentHostClient(Guid.Parse(arm.ClientId)));

        Assert.Equal(
            ConsentInteractiveFlow.BrowserInteractive,
            ConsentInteractiveFlowSelector.Select(Profile("interactive"), useBroker: false, graph.RedirectUri));
        Assert.Equal(
            ConsentInteractiveFlow.BrowserInteractive,
            ConsentInteractiveFlowSelector.Select(Profile("interactive"), useBroker: false, arm.RedirectUri));
    }

    [Fact]
    public void GraphAndArmConsentUseDeviceCodeForADeviceCodeProfile()
    {
        var graph = GraphConsentClientConfiguration.Default;
        var arm = ArmConsentClientConfiguration.Default;

        Assert.Equal(
            ConsentInteractiveFlow.DeviceCode,
            ConsentInteractiveFlowSelector.Select(Profile("device-code"), useBroker: false, graph.RedirectUri));
        Assert.Equal(
            ConsentInteractiveFlow.DeviceCode,
            ConsentInteractiveFlowSelector.Select(Profile("device-code"), useBroker: false, arm.RedirectUri));
    }

    [Fact]
    public async Task BrokerConsentFallsBackToDeviceCode_NamingTheFailure()
    {
        var presenter = new RecordingMessagePresenter();
        var deviceCodeResult = Result();

        var result = await Acquisition(presenter).FallBackToDeviceCodeAsync(
            () => throw new MsalServiceException("WAM_provider_error_3399614476", "WAM Error") { CorrelationId = "00000000-0000-0000-0000-000000000001" },
            () => Task.FromResult(deviceCodeResult),
            CancellationToken.None);

        Assert.Same(deviceCodeResult, result);
        Assert.Equal(
            "Broker sign-in failed (WAM_provider_error_3399614476, correlation ID 00000000-0000-0000-0000-000000000001), so msagent will use a device code.",
            Assert.Single(presenter.Messages));
    }

    [Fact]
    public async Task BrokerConsentThatSucceeds_NeverUsesDeviceCode()
    {
        var presenter = new RecordingMessagePresenter();
        var brokerResult = Result();
        var deviceCodeCalls = 0;

        var result = await Acquisition(presenter).FallBackToDeviceCodeAsync(
            () => Task.FromResult(brokerResult),
            () => { deviceCodeCalls++; return Task.FromResult(Result()); },
            CancellationToken.None);

        Assert.Same(brokerResult, result);
        Assert.Equal(0, deviceCodeCalls);
        Assert.Empty(presenter.Messages);
    }

    [Fact]
    public async Task BrokerConsentStops_WhenTheOperatorCancels()
    {
        var presenter = new RecordingMessagePresenter();
        var deviceCodeCalls = 0;

        await Assert.ThrowsAsync<SignInCanceledException>(() => Acquisition(presenter).FallBackToDeviceCodeAsync(
            () => throw new MsalClientException(MsalError.AuthenticationCanceledError, "User canceled authentication."),
            () => { deviceCodeCalls++; return Task.FromResult(Result()); },
            CancellationToken.None));

        Assert.Equal(0, deviceCodeCalls);
        Assert.Empty(presenter.Messages);
    }

    [Fact]
    public async Task ConsentFindsTheActiveAccount_AmongEveryAccountTheClientLists()
    {
        var client = PublicClientApplicationBuilder.Create(Guid.NewGuid().ToString()).Build();
        MsalTokenCacheSeed.Seed(client, Tenant, "scope", "alice@contoso.com", "bob@contoso.com");

        var account = await MsalConsentTokenAcquisition.FindActiveAccountAsync(client, useBroker: true, MsalTokenCacheSeed.HomeAccountId("bob@contoso.com", Tenant), CancellationToken.None);

        Assert.Equal("bob@contoso.com", account?.Username);
    }

    [Fact]
    public async Task ABrokerThatCannotStartWhileListingAccounts_FindsNoAccount_SoConsentReachesItsDeviceCodeFallback() =>
        Assert.Null(await MsalConsentTokenAcquisition.FindActiveAccountAsync(ClientFailingToListAccounts(new MsalClientException("wam_runtime_init_failed", "Unable to load shared library 'msalruntime'.")), useBroker: true, "home-account-id", CancellationToken.None));

    [Fact]
    public async Task AListingFailureWithoutTheBroker_Propagates()
    {
        var failure = new MsalClientException("wam_runtime_init_failed", "Unable to load shared library 'msalruntime'.");

        Assert.Same(failure, await Assert.ThrowsAsync<MsalClientException>(() => MsalConsentTokenAcquisition.FindActiveAccountAsync(ClientFailingToListAccounts(failure), useBroker: false, "home-account-id", CancellationToken.None)));
    }

    [Fact]
    public async Task ACanceledCommand_StopsListingTheBrokerAccounts()
    {
        var client = PublicClientApplicationBuilder.Create(Guid.NewGuid().ToString()).Build();
        MsalTokenCacheSeed.Seed(client, Tenant, "scope", "alice@contoso.com");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MsalConsentTokenAcquisition.FindActiveAccountAsync(client, useBroker: true, MsalTokenCacheSeed.HomeAccountId("alice@contoso.com", Tenant), cancellation.Token));
    }

    [Fact]
    public void ConsentAcquisitionResolvesWithADeviceCodeMessagePresenter()
    {
        var services = new ServiceCollection();
        services.AddAgentHostAuthentication(new AgentHostAuthOptions
        {
            ClientId = FirstPartyClientId.ToString("D"),
            ConfigDirectory = Path.Combine(Path.GetTempPath(), "ah-consent-" + Guid.NewGuid().ToString("N")),
        });

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<MsalConsentTokenAcquisition>());
        Assert.NotNull(provider.GetRequiredService<MsalLiveTestTokenProvider>());
    }

    [Fact]
    public async Task InteractiveMessagesGoToTheConfiguredWriter()
    {
        var output = new StringWriter();
        var services = new ServiceCollection();
        services.AddAgentHostAuthentication(new AgentHostAuthOptions
        {
            ClientId = FirstPartyClientId.ToString("D"),
            ConfigDirectory = Path.Combine(Path.GetTempPath(), "ah-consent-" + Guid.NewGuid().ToString("N")),
            MessageOutput = output,
        });

        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IInteractiveMessagePresenter>().PresentAsync("Open https://microsoft.com/devicelogin to sign in.", CancellationToken.None);

        Assert.Equal($"Open https://microsoft.com/devicelogin to sign in.{Environment.NewLine}", output.ToString());
    }

    private static bool UseBroker(FakePlatformInfo platform, string? flowType)
    {
        var provider = FakeClientProviders.Create(platform);
        return ConsentInteractiveFlowSelector.UseBroker(
            Profile(flowType),
            provider.IsAgentHostClient(FirstPartyClientId),
            provider.IsBrokerSupported);
    }

    private static AuthProfile Profile(string? flowType) =>
        new()
        {
            AccountKey = "home-account-id",
            Username = "user@contoso.example",
            TenantId = "11111111-1111-1111-1111-111111111111",
            FlowType = flowType,
        };

    private static MsalConsentTokenAcquisition Acquisition(RecordingMessagePresenter presenter) =>
        new(FakeClientProviders.Create(FakePlatformInfo.MacOs()), InMemoryAuthProfileStore.SignedOut(), new StaticInteractionPolicy(true), presenter);

    private static IPublicClientApplication ClientFailingToListAccounts(Exception failure)
    {
        var client = PublicClientApplicationBuilder.Create(Guid.NewGuid().ToString()).Build();
        client.UserTokenCache.SetBeforeAccessAsync(_ => throw failure);
        return client;
    }

    private static AuthenticationResult Result() =>
        new("access-token", false, "unique-id", DateTimeOffset.MaxValue, DateTimeOffset.MaxValue, "tenant-id", null, null, ["scope"], Guid.Empty, "Bearer");
}
