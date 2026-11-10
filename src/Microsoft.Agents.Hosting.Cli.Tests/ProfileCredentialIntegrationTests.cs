using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ProfileCredentialIntegrationTests
{
    [Fact]
    public async Task LegacyClientRegistrationIsLazyAndNeverUsesSdkPersistenceProbe()
    {
        using var h = new ProfileCredentialTests.Harness();
        var probe = new FakeSecureTokenCacheProbe(false);
        var cache = new LegacyCache();
        var provider = new MsalPublicClientProvider(new() { ConfigDirectory = h.Directory, AllowUnprotectedCache = true }, FakePlatformInfo.Linux(), probe, cache);
        _ = await provider.GetClientAsync(false, null, default);
        Assert.Equal(0, probe.CallCount);
        Assert.Equal(0, cache.Calls);
    }

    [Fact]
    public async Task ReusedRealMsalLegacyClientRehydratesOnlyDedicatedCacheAndCannotRetainClearedAccounts()
    {
        using var h = new ProfileCredentialTests.Harness();
        var seed = PublicClientApplicationBuilder.Create(AgentHostClientAppConfiguration.DefaultClientId.ToString()).WithAuthority("https://login.microsoftonline.com/common", false).Build();
        MsalTokenCacheSeed.Seed(seed, ProfileCredentialTests.Tenant, "https://example.test/.default", "alice@example.test");
        var cache = new LegacyCache();
        seed.UserTokenCache.SetAfterAccessAsync(args => { cache.Value = args.TokenCache.SerializeMsalV3(); return Task.CompletedTask; });
        _ = await seed.GetAccountsAsync();
        var provider = new MsalPublicClientProvider(new() { ConfigDirectory = h.Directory, AllowUnprotectedCache = true }, FakePlatformInfo.Linux(), new FakeSecureTokenCacheProbe(false), cache);
        var client = await provider.GetClientAsync(false, null, default);
        Assert.Single(await client.GetAccountsAsync());
        await cache.ClearAsync(default);
        Assert.Empty(await client.GetAccountsAsync());
        Assert.Equal(cache.Leases, cache.Releases);
    }

    [Fact]
    public async Task CapturedLegacyResourceProfileUsesDedicatedCacheWithoutOwnedReferenceParsing()
    {
        using var h = new ProfileCredentialTests.Harness();
        var services = new ServiceCollection().AddSingleton<IClock>(new FixedClock(DateTimeOffset.UnixEpoch));
        services.AddAgentHostAuthentication(new() { ConfigDirectory = h.Directory, InteractionAllowed = false })
            .AddAgentHostProfileCredentials(h.Backend, new(), new LegacyCache());
        using var provider = services.BuildServiceProvider();
        var profile = new AuthProfile
        {
            ProfileId = "legacy", AccountKey = "missing-account", Username = "alice", ClientId = ProfileCredentialTests.App,
            TenantId = ProfileCredentialTests.Tenant, Cloud = CloudInstance.China,
        };
        await Assert.ThrowsAsync<InteractionRequiredException>(() =>
            provider.GetRequiredService<IProfileCredentialProvider>().AcquireAsync(profile,
                ["https://api.powerplatform.partner.microsoftonline.cn/.default"], profile.TenantId, default));
        Assert.Equal(0, h.Backend.Calls);
    }

    [Fact]
    public async Task SilentOwnedProfileUsesOneSnapshotAndNeverConstructsGlobalLegacyClient()
    {
        using var h = new ProfileCredentialTests.Harness();
        var profile = Profile(AuthCredentialMethod.ClientSecret);
        h.Profiles.Items.Add(profile);
        var credentials = new Credentials();
        var probe = new FakeSecureTokenCacheProbe(false);
        var provider = new MsalSilentTokenProvider(Public(h, probe), h.Profiles, profileCredentials: credentials);
        var result = await provider.AcquireTokenSilentAsync(["https://example.test/.default"], ProfileCredentialTests.Tenant, default);
        Assert.Equal("bound-token", result.AccessToken);
        Assert.Same(profile, credentials.Profile);
        Assert.Equal(["https://example.test/.default"], credentials.Scopes);
        Assert.Equal(0, probe.CallCount);
    }

    [Theory]
    [InlineData(AuthCredentialMethod.DefaultAzureCredential)]
    [InlineData(AuthCredentialMethod.ClientSecret)]
    [InlineData(AuthCredentialMethod.Certificate)]
    [InlineData(AuthCredentialMethod.Broker)]
    public async Task MetadataSelectedTokenSkipsWorkloadDacAndBrokerWithoutConstructingAnyCredential(AuthCredentialMethod method)
    {
        using var h = new ProfileCredentialTests.Harness();
        h.Profiles.Items.Add(Profile(method));
        var probe = new FakeSecureTokenCacheProbe(false);
        var credentials = new Credentials();
        var provider = new MsalSilentTokenProvider(Public(h, probe), h.Profiles, selectedCredentials: credentials);
        Assert.Null(await provider.TryAcquireTokenSilentAsync("https://example.test", ProfileCredentialTests.Tenant, default));
        Assert.Equal(0, probe.CallCount);
        Assert.Null(credentials.Profile);
    }

    [Fact]
    public async Task ConsentRoutesOwnedSnapshotWithoutSubstitutingConfiguredHelperIdentity()
    {
        using var h = new ProfileCredentialTests.Harness();
        var profile = Profile(AuthCredentialMethod.ClientSecret);
        h.Profiles.Items.Add(profile);
        var credentials = new Credentials();
        var probe = new FakeSecureTokenCacheProbe(false);
        var consent = new MsalConsentTokenAcquisition(Public(h, probe), h.Profiles, new StaticInteractionPolicy(false), new Messages(), profileCredentials: credentials);
        Assert.Equal("bound-token", (await consent.AcquireAsync(Guid.NewGuid(), "http://localhost", ["https://example.test/.default"],
            ProfileCredentialTests.Tenant, "interaction", "remediation", default, default)).AccessToken);
        Assert.Same(profile, credentials.Profile);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task SessionClearPreservesOwnedCleanupFailureAndNeverCallsSharedAccountRemoval()
    {
        using var h = new ProfileCredentialTests.Harness();
        var cache = new LegacyCache();
        var credentials = new Credentials { CleanupFails = true };
        var options = new AgentHostAuthOptions { ConfigDirectory = h.Directory };
        var client = new MsalPublicClientProvider(options, FakePlatformInfo.Linux(), new FakeSecureTokenCacheProbe(false), cache);
        var session = new MsalAuthenticationSession(client, options, ownedCleanup: credentials);
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => session.ClearCacheAsync(default));
        Assert.Equal(0, cache.Calls);
    }

    [Fact]
    public void AuthRegistrationSharesOneProviderAndManagementStoreWithoutNativeStatusConstruction()
    {
        using var h = new ProfileCredentialTests.Harness();
        using var services = new ServiceCollection().AddAgentHostAuthentication(new() { ConfigDirectory = h.Directory, InteractionAllowed = false })
            .AddAgentHostProfileCredentials(h.Backend, new(), new LegacyCache()).BuildServiceProvider();
        Assert.Same(services.GetRequiredService<IAuthProfileStore>(), services.GetRequiredService<IAuthProfileManagementStore>());
        Assert.Same(services.GetRequiredService<IProfileCredentialProvider>(), services.GetRequiredService<IOwnedProfileCredentialCleanup>());
        Assert.Same(services.GetRequiredService<IProfileCredentialProvider>(), services.GetRequiredService<IProfileCachedAccountReader>());
        Assert.Equal(0, h.Backend.Calls);
    }

    private static MsalPublicClientProvider Public(ProfileCredentialTests.Harness h, FakeSecureTokenCacheProbe probe) =>
        new(new() { ConfigDirectory = h.Directory, AllowUnprotectedCache = true }, FakePlatformInfo.Linux(), probe, new LegacyCache());

    private static AuthProfile Profile(AuthCredentialMethod method) => new()
    {
        ProfileId = "selected", AccountKey = ProfileCredentialTests.Home, Username = "alice", IsActive = true,
        CredentialReference = OwnedProfileReferences.New(method is not (AuthCredentialMethod.Broker or AuthCredentialMethod.Browser)).Value,
        CredentialMethod = method, PrincipalType = method == AuthCredentialMethod.Broker ? AuthPrincipalType.User : AuthPrincipalType.ServicePrincipal,
        ClientId = ProfileCredentialTests.App, TenantId = ProfileCredentialTests.Tenant,
    };
    private sealed class Credentials : IProfileCredentialProvider, IProfileSelectedAccountTokenAcquisition, IProfileConsentTokenAcquisition, IOwnedProfileCredentialCleanup
    {
        internal AuthProfile? Profile;
        internal IReadOnlyList<string>? Scopes;
        internal bool CleanupFails;
        public Task<AuthProfile> AuthenticateAsync(Microsoft.Agents.Hosting.Cli.Core.Operations.AuthCreateRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<TokenAcquisitionResult> AcquireAsync(AuthProfile profile, IReadOnlyList<string> scopes, string? tenant, CancellationToken ct)
        { Profile = profile; Scopes = scopes; return Task.FromResult(new TokenAcquisitionResult { AccessToken = "bound-token", TenantId = tenant, ExpiresOn = DateTimeOffset.UtcNow.AddHours(1) }); }
        public Task<TokenAcquisitionResult?> TryAcquireSelectedTokenAsync(AuthProfile profile, IReadOnlyList<string> scopes, string tenant, CancellationToken ct) => throw new InvalidOperationException("No source should be constructed.");
        public Task<TokenAcquisitionResult> AcquireConsentAsync(AuthProfile profile, Guid client, string redirect, IReadOnlyList<string> scopes, string? tenant, CancellationToken ct, bool trySilentFirst = true) => AcquireAsync(profile, scopes, tenant, ct);
        public Task CompleteCreationAsync(AuthProfile profile, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveOwnedCredentialAsync(AuthProfile profile, CancellationToken ct) => Task.CompletedTask;
        public Task ClearOwnedCredentialsAsync(CancellationToken ct) => CleanupFails ? Task.FromException(new CacheProtectionUnavailableException("Owned cleanup failed.")) : Task.CompletedTask;
    }
    private sealed class Messages : IInteractiveMessagePresenter
    {
        public Task PresentAsync(string message, CancellationToken ct) => throw new InvalidOperationException("UI is forbidden.");
    }
    private sealed class LegacyCache : ILegacyMsalTokenCacheBackend
    {
        internal byte[]? Value;
        internal int Calls, Leases, Releases;
        public IDisposable AcquireLease(CancellationToken ct) { Leases++; return new Lease(() => Releases++); }
        public Task<byte[]?> ReadAsync(CancellationToken ct) { Calls++; return Task.FromResult(Value?.ToArray()); }
        public Task WriteAsync(ReadOnlyMemory<byte> value, CancellationToken ct) { Calls++; Value = value.ToArray(); return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken ct) { Calls++; Value = null; return Task.CompletedTask; }
        private sealed class Lease(Action release) : IDisposable { public void Dispose() => release(); }
    }
}
