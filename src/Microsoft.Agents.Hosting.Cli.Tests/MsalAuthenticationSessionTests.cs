using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class MsalAuthenticationSessionTests
{
    private const string Tenant = "33333333-3333-3333-3333-333333333333";

    private static readonly string DefaultScope = new PacResourceScopeNormalizer().DefaultResourceScope(PowerPlatformApiRing.Production);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitInteractiveSignInRejectsHeadlessBeforeClientConfigurationOrCallback(bool broker)
    {
        var options = new AgentHostAuthOptions
        {
            ConfigDirectory = "unused",
            ClientId = Guid.Empty.ToString(),
            InteractionAllowed = false,
        };
        var session = new MsalAuthenticationSession(
            new MsalPublicClientProvider(options, FakePlatformInfo.Linux(), new FakeSecureTokenCacheProbe(false)),
            options);
        var callbacks = 0;
        var failure = await Assert.ThrowsAsync<InteractionRequiredException>(() => broker
            ? session.LoginWithBrokerAsync(new AuthLoginRequest(), default)
            : session.LoginWithDeviceCodeAsync(new AuthLoginRequest(), _ =>
            {
                callbacks++;
                return Task.CompletedTask;
            }, default));
        Assert.Equal(3, failure.ExitCode);
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task SilentBrokerSignIn_RequiresARequest() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => Session().LoginWithBrokerSilentlyAsync(null!, CancellationToken.None));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SilentBrokerSignIn_RequiresANamedAccount(string? account) =>
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Session().LoginWithBrokerSilentlyAsync(new AuthLoginRequest { Account = account }, CancellationToken.None));

    [Fact]
    public async Task SilentBrokerSignIn_ReportsTheBrokerUnavailable_WhenItDoesNotHoldTheAccount()
    {
        var client = PublicClientApplicationBuilder.Create(Guid.NewGuid().ToString()).Build();

        var unavailable = await Assert.ThrowsAsync<BrokerUnavailableException>(() => MsalAuthenticationSession.AcquireSilentTokenAsync(client, ["scope"], "alice@contoso.com", CancellationToken.None));

        Assert.Equal("the operating-system broker does not hold this account", unavailable.Reason);
    }

    [Fact]
    public async Task SilentBrokerSignIn_AcquiresATokenForTheNamedAccount_AmongEveryAccountTheListingBrokerClientReturns()
    {
        using var directory = new TempDir();
        var listingBrokerClient = await ListingBrokerClientHoldingAsync(BrokerClientProvider(directory.Path), "alice@contoso.com", "bob@contoso.com");

        var result = await MsalAuthenticationSession.AcquireSilentTokenAsync(listingBrokerClient, [DefaultScope], "BOB@contoso.com", CancellationToken.None);

        Assert.Equal(MsalTokenCacheSeed.AccessToken("bob@contoso.com"), result.AccessToken);
        Assert.Equal(MsalTokenCacheSeed.HomeAccountId("bob@contoso.com", Tenant), result.Account.HomeAccountId.Identifier);
    }

    [Fact]
    public async Task SilentBrokerSignIn_ReportsTheBrokerUnavailable_WhenTheNamedAccountsTokenComesFromTheCacheRatherThanTheBroker()
    {
        using var directory = new TempDir();
        var session = await SessionHoldingAsync(directory.Path, "alice@contoso.com", "bob@contoso.com");

        var unavailable = await Assert.ThrowsAsync<BrokerUnavailableException>(() => session.LoginWithBrokerSilentlyAsync(new AuthLoginRequest { Account = "BOB@contoso.com" }, CancellationToken.None));

        Assert.Equal("the operating-system broker does not hold this account", unavailable.Reason);
    }

    [Fact]
    public void SilentBrokerSignIn_AcceptsATokenTheBrokerIssued()
    {
        var issued = Issued(TokenSource.Broker);

        Assert.Same(issued, MsalAuthenticationSession.EnsureIssuedByTheBroker(issued));
    }

    [Theory]
    [InlineData(TokenSource.Cache)]
    [InlineData(TokenSource.IdentityProvider)]
    [InlineData(null)]
    public void SilentBrokerSignIn_ReportsTheBrokerUnavailable_ForATokenTheBrokerDidNotIssue(TokenSource? source)
    {
        var unavailable = Assert.Throws<BrokerUnavailableException>(() => MsalAuthenticationSession.EnsureIssuedByTheBroker(Issued(source)));

        Assert.Equal("the operating-system broker does not hold this account", unavailable.Reason);
    }

    [Fact]
    public async Task SilentBrokerSignIn_NeverSignsInAnotherAccountTheBrokerLists()
    {
        using var directory = new TempDir();
        var listingBrokerClient = await ListingBrokerClientHoldingAsync(BrokerClientProvider(directory.Path), "alice@contoso.com");

        var unavailable = await Assert.ThrowsAsync<BrokerUnavailableException>(() => MsalAuthenticationSession.AcquireSilentTokenAsync(listingBrokerClient, [DefaultScope], "bob@contoso.com", CancellationToken.None));

        Assert.Equal("the operating-system broker does not hold this account", unavailable.Reason);
    }

    [Fact]
    public async Task SilentBrokerSignIn_StopsListingAccounts_WhenTheCommandIsCanceled()
    {
        using var directory = new TempDir();
        var listingBrokerClient = await ListingBrokerClientHoldingAsync(BrokerClientProvider(directory.Path), "alice@contoso.com");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MsalAuthenticationSession.AcquireSilentTokenAsync(listingBrokerClient, [DefaultScope], "bob@contoso.com", cancellation.Token));
    }

    private static MsalAuthenticationSession Session() =>
        new(FakeClientProviders.Create(FakePlatformInfo.Linux()), new AgentHostAuthOptions { ConfigDirectory = Path.GetTempPath() });

    private static MsalPublicClientProvider BrokerClientProvider(string configDirectory) =>
        FakeClientProviders.Create(FakePlatformInfo.BrokerPlatformOtherThanThisHost(), configDirectory);

    private static async Task<IPublicClientApplication> ListingBrokerClientHoldingAsync(MsalPublicClientProvider provider, params string[] usernames)
    {
        var listingBrokerClient = await provider.GetClientAsync(useBroker: true, tenantId: null, CancellationToken.None, listBrokerAccounts: true);
        MsalTokenCacheSeed.Seed(listingBrokerClient, Tenant, DefaultScope, usernames);
        return listingBrokerClient;
    }

    private static async Task<MsalAuthenticationSession> SessionHoldingAsync(string configDirectory, params string[] usernames)
    {
        var provider = BrokerClientProvider(configDirectory);
        await ListingBrokerClientHoldingAsync(provider, usernames);
        return new(provider, new AgentHostAuthOptions { ConfigDirectory = configDirectory });
    }

    private static AuthenticationResult Issued(TokenSource? source) => new(
        accessToken: "access-token",
        isExtendedLifeTimeToken: false,
        uniqueId: null,
        expiresOn: DateTimeOffset.UtcNow.AddHours(1),
        extendedExpiresOn: DateTimeOffset.UtcNow.AddHours(1),
        tenantId: Tenant,
        account: null,
        idToken: null,
        scopes: [DefaultScope],
        correlationId: Guid.NewGuid(),
        authenticationResultMetadata: source is { } issuer ? new AuthenticationResultMetadata(issuer) : null,
        claimsPrincipal: null);
}
