using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class SelectedAccountTokenProviderTests
{
    private const string Tenant = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public void ExactSelectedAccountIsUsedWithoutChoosingAnotherAccount()
    {
        var selected = new SyntheticAccount("selected");
        var other = new SyntheticAccount("other");

        IAccount? actual = MsalSilentTokenProvider.FindExactAccount(
            [other, selected],
            "selected");

        Assert.Same(selected, actual);
    }

    [Fact]
    public void MissingSelectedAccountDoesNotFallBackToAnyCachedOrOsAccount()
    {
        var other = new SyntheticAccount("other");

        IAccount? actual = MsalSilentTokenProvider.FindExactAccount(
            [other],
            "selected");

        Assert.Null(actual);
        Assert.NotSame(PublicClientApplication.OperatingSystemAccount, actual);
    }

    [Fact]
    public async Task MissingActiveProfileNamesTheRenamedLoginCommand()
    {
        using var directory = new TempDir();
        var cacheProbe =
            new FakeSecureTokenCacheProbe(protectedAvailable: false);
        var clientProvider = new MsalPublicClientProvider(
            new AgentHostAuthOptions
            {
                ConfigDirectory = directory.Path,
                Cloud = CloudInstance.Public,
            },
            FakePlatformInfo.Unknown(),
            cacheProbe);
        var provider = new MsalSilentTokenProvider(
            clientProvider,
            InMemoryAuthProfileStore.SignedOut());

        NotSignedInException failure =
            await Assert.ThrowsAsync<NotSignedInException>(() =>
                provider.AcquireTokenSilentAsync(
                    "https://api.powerplatform.com/",
                    targetTenantId: null,
                    CancellationToken.None));

        Assert.Equal(
            "Run `msagent auth create` to sign in, then retry.",
            failure.Remediation);
        Assert.Equal(0, cacheProbe.CallCount);
    }

    [Fact]
    public void TheOperatingSystemAccountIsUsed_OnlyWhenItIsTheActiveAccount()
    {
        MsalSilentTokenProvider.EnsureActiveAccount(new SyntheticAccount("selected"), "selected");

        var otherAccount = Assert.Throws<InteractionRequiredException>(() => MsalSilentTokenProvider.EnsureActiveAccount(new SyntheticAccount("other"), "selected"));
        var noAccount = Assert.Throws<InteractionRequiredException>(() => MsalSilentTokenProvider.EnsureActiveAccount(null, "selected"));

        Assert.Equal("The operating-system broker no longer holds the active AgentHost account and offered a different account instead.", otherAccount.Message);
        Assert.Equal("Run `msagent auth create` for the intended cloud to sign in again with the account you want to use.", otherAccount.Remediation);
        Assert.Equal(otherAccount.Message, noAccount.Message);
    }

    [Fact]
    public void TenantInteractionRequiredNamesTheAvailableCreateCommand()
    {
        const string tenantId =
            "11111111-1111-1111-1111-111111111111";

        InteractionRequiredException failure =
            MsalSilentTokenProvider.CreateInteractionRequiredException(
                tenantId);

        Assert.Equal(
            $"Run `msagent auth create --tenant {tenantId}` for the intended cloud with an account that can access that tenant, or correct `--tenant`.",
            failure.Remediation);
    }

    [Fact]
    public async Task ABrokerThatCannotStart_IsReportedAsUnavailable_SoAnInteractiveCallerCanFallBack()
    {
        var unavailable = await Assert.ThrowsAsync<BrokerUnavailableException>(() => AcquireWhileListingAccountsFailsAsync("broker", new MsalClientException("wam_runtime_init_failed", "Unable to load shared library 'msalruntime'.")));

        Assert.Equal("wam_runtime_init_failed", unavailable.Reason);
    }

    [Fact]
    public async Task AFailureWithoutTheBroker_PropagatesUnchanged()
    {
        var failure = new MsalClientException("wam_runtime_init_failed", "Unable to load shared library 'msalruntime'.");

        Assert.Same(failure, await Assert.ThrowsAsync<MsalClientException>(() => AcquireWhileListingAccountsFailsAsync("device-code", failure)));
    }

    [Fact]
    public Task ABrokerThatNeedsInteraction_StillAsksForIt() =>
        Assert.ThrowsAsync<InteractionRequiredException>(() => AcquireWhileListingAccountsFailsAsync("broker", new MsalUiRequiredException(MsalError.InvalidGrantError, "Consent is required.")));

    [Fact]
    public Task AThrottledBroker_IsStillReportedAsThrottled() =>
        Assert.ThrowsAsync<TokenThrottledException>(() => AcquireWhileListingAccountsFailsAsync("broker", new MsalThrottledServiceException(new MsalServiceException("throttled", "Too many requests."))));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyMetadataBrokerUsesOnlyExactAccountAndExplicitTenant(bool selectedAvailable)
    {
        using var directory = new TempDir();
        const string selected = "selected@example.test";
        const string resource = "https://api.powerplatform.com/";
        using var http = new ForbiddenAuthHttp();
        var clients = new MsalPublicClientProvider(
            new() { ConfigDirectory = directory.Path, AllowUnprotectedCache = true },
            FakePlatformInfo.BrokerPlatformOtherThanThisHost(), new FakeSecureTokenCacheProbe(false), legacyCache: null,
            createClient: configuration =>
            {
                Assert.True(configuration.UseBroker);
                Assert.False(configuration.ListBrokerAccounts);
                Assert.Equal("/" + Tenant, configuration.AuthorityUrl.AbsolutePath);
                return PublicClientApplicationBuilder.Create(configuration.ClientId.ToString())
                    .WithAuthority(configuration.AuthorityUrl, validateAuthority: false)
                    .WithHttpClientFactory(http).Build();
            });
        var client = await clients.GetClientAsync(true, Guid.Parse(Tenant), default);
        // Model broker cache results; the injected transport rejects any real token exchange.
        MsalTokenCacheSeed.Seed(client, Tenant, resource + "/.default",
            selectedAvailable ? ["other@example.test", selected] : ["other@example.test"]);
        var profile = new AuthProfile
        {
            AccountKey = MsalTokenCacheSeed.HomeAccountId(selected, Tenant),
            Username = selected, CredentialMethod = AuthCredentialMethod.Broker,
            TenantId = EnvironmentFixtures.FabrikamTenant,
        };
        var diagnostics = new GeographyDiagnostics();
        var provider = new MsalSilentTokenProvider(clients, InMemoryAuthProfileStore.SignedOut());

        var result = await provider.TryAcquireForProfileAsync(profile, resource, Tenant, default, diagnostics);

        if (selectedAvailable)
        {
            Assert.True(result is not null, string.Join(",", diagnostics.Stages));
            Assert.Equal(MsalTokenCacheSeed.AccessToken(selected), result!.AccessToken);
            Assert.Equal(Tenant, result.TenantId);
        }
        else
        {
            Assert.Null(result);
            Assert.Contains(EnvironmentGeographyStage.TokenAccountUnavailable, diagnostics.Stages);
        }
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task LegacyMetadataBrokerInitializationFailureHasFixedReason()
    {
        using var directory = new TempDir();
        var clients = FakeClientProviders.Create(FakePlatformInfo.BrokerPlatformOtherThanThisHost(), directory.Path);
        var client = await clients.GetClientAsync(true, Guid.Parse(Tenant), default);
        client.UserTokenCache.SetBeforeAccessAsync(_ =>
            throw new MsalClientException("wam_runtime_init_failed", "private-failure-details"));
        var diagnostics = new GeographyDiagnostics();
        var profile = new AuthProfile { AccountKey = "selected", Username = "selected@example.test", TenantId = Tenant };

        Assert.Null(await new MsalSilentTokenProvider(clients, InMemoryAuthProfileStore.SignedOut())
            .TryAcquireForProfileAsync(profile, "https://api.powerplatform.com/", Tenant, default, diagnostics));
        Assert.Equal([EnvironmentGeographyStage.TokenBrokerUnavailable], diagnostics.Stages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyBrokerCannotDowngradeToNonBrokerClient(bool differentClient)
    {
        using var directory = new TempDir();
        var clients = new MsalPublicClientProvider(
            new() { ConfigDirectory = directory.Path },
            differentClient ? FakePlatformInfo.BrokerPlatformOtherThanThisHost() : FakePlatformInfo.Unknown(),
            new FakeSecureTokenCacheProbe(false), legacyCache: null,
            createClient: _ => throw new InvalidOperationException("No client should be constructed."));
        var profile = new AuthProfile
        {
            AccountKey = "selected", Username = "selected@example.test", TenantId = Tenant,
            ClientId = differentClient ? "55555555-5555-5555-5555-555555555555" : null,
        };
        var diagnostics = new GeographyDiagnostics();

        Assert.Null(await new MsalSilentTokenProvider(clients, InMemoryAuthProfileStore.SignedOut())
            .TryAcquireForProfileAsync(profile, "https://api.powerplatform.com/", Tenant, default, diagnostics));
        Assert.Equal([EnvironmentGeographyStage.TokenBrokerUnavailable], diagnostics.Stages);
    }

    private sealed class GeographyDiagnostics : IEnvironmentGeographyDiagnostics
    {
        internal List<EnvironmentGeographyStage> Stages { get; } = [];
        public void Report(EnvironmentGeographyStage stage, int? httpStatusCode = null) => Stages.Add(stage);
    }

    private sealed class ForbiddenAuthHttp : HttpMessageHandler, IMsalHttpClientFactory
    {
        internal int Calls { get; private set; }
        public HttpClient GetHttpClient() => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Network access is forbidden.");
        }
    }

    private static async Task<TokenAcquisitionResult> AcquireWhileListingAccountsFailsAsync(string flowType, Exception failure)
    {
        using var directory = new TempDir();
        var clientProvider = FakeClientProviders.Create(FakePlatformInfo.BrokerPlatformOtherThanThisHost(), directory.Path);
        var useBroker = flowType == "broker";
        var client = await clientProvider.GetClientAsync(useBroker, useBroker ? Guid.Parse(Tenant) : null, CancellationToken.None);
        client.UserTokenCache.SetBeforeAccessAsync(_ => throw failure);
        var profiles = InMemoryAuthProfileStore.SignedOut();
        await profiles.UpsertAsync(new AuthProfile { AccountKey = "home-1", Username = "a@contoso.com", TenantId = Tenant, FlowType = flowType }, CancellationToken.None);
        return await new MsalSilentTokenProvider(clientProvider, profiles).AcquireTokenSilentAsync("https://api.powerplatform.com/", targetTenantId: null, CancellationToken.None);
    }

    private sealed class SyntheticAccount(string key) : IAccount
    {
        public string Username => $"{key}@example.invalid";

        public string Environment => "login.microsoftonline.com";

        public AccountId HomeAccountId { get; } =
            new(key, "11111111-1111-1111-1111-111111111111", key);

    }
}
