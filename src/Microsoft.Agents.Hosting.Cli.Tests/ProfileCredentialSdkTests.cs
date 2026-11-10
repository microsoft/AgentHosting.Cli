using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Core.Pipeline;
using Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;
using Azure.Identity;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ProfileCredentialSdkTests
{
    private const string GraphAudience = "00000003-0000-0000-c000-000000000000";

    [Theory]
    [InlineData(CloudInstance.UsNat, false, "login.microsoftonline.eaglex.ic.gov")]
    [InlineData(CloudInstance.UsSec, false, "login.microsoftonline.microsoft.scloud")]
    [InlineData(CloudInstance.UsNat, true, "login.microsoftonline.eaglex.ic.gov")]
    [InlineData(CloudInstance.UsSec, true, "login.microsoftonline.microsoft.scloud")]
    public async Task NationalWorkloadSdkUsesOnlyTheSelectedAuthority(CloudInstance cloud, bool dac, string loginHost)
    {
        using var h = new Harness(new()
        {
            AzureTenantId = ProfileCredentialTests.Tenant, AzureClientId = ProfileCredentialTests.App,
            AzureClientSecret = "synthetic-environment-secret",
        }, interaction: false, realAzure: dac);
        var provider = h.Provider();
        var profile = await provider.AuthenticateAsync(new()
        {
            Target = PowerPlatformApiTargetSelection.TargetFor(cloud), ManagedIdentity = dac,
            Tenant = ProfileCredentialTests.Tenant, ApplicationId = dac ? null : ProfileCredentialTests.App,
            ClientSecret = dac ? null : "synthetic-profile-secret", Scopes = ["https://service.test/.default"],
        }, default);
        try
        {
            Assert.Equal(cloud, profile.Cloud);
            Assert.Single(h.Transport.Posts);
            Assert.NotEmpty(h.Transport.RequestUris);
            Assert.All(h.Transport.RequestUris, uri => Assert.Equal(loginHost, uri.Host));
        }
        finally { await provider.RemoveOwnedCredentialAsync(profile, default); }
    }

    [Theory]
    [InlineData(CloudInstance.UsNat, "login.microsoftonline.eaglex.ic.gov")]
    [InlineData(CloudInstance.UsSec, "login.microsoftonline.microsoft.scloud")]
    public async Task NationalPublicSdkDoesNotDiscoverAnotherCloud(CloudInstance cloud, string loginHost)
    {
        using var h = new Harness();
        var request = Request(AuthCredentialMethod.Password, secret: "synthetic-once", cloud: cloud);
        var result = await h.Adapter.AcquireAsync(request, default);
        Assert.Equal(ProfileCredentialTests.Tenant, result.TenantId);
        Assert.NotEmpty(h.Transport.RequestUris);
        Assert.All(h.Transport.RequestUris, uri => Assert.Equal(loginHost, uri.Host));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealWorkloadAndAzureInitialGraphV2BindingPreservesRequestedScopes(bool dac)
    {
        using var h = new Harness(new()
        {
            AzureTenantId = ProfileCredentialTests.Tenant, AzureClientId = ProfileCredentialTests.App,
            AzureClientSecret = "synthetic-environment-secret",
        }, interaction: false, realAzure: dac);
        h.Transport.Audience = GraphAudience;
        h.Transport.V2Tokens = true;
        string[] scopes = [dac ? "HTTPS://GRAPH.MICROSOFT.COM//.default" : "https://graph.microsoft.com/.default"];
        var provider = h.Provider();
        var profile = await provider.AuthenticateAsync(new()
        {
            ManagedIdentity = dac, Tenant = ProfileCredentialTests.Tenant,
            ApplicationId = dac ? null : ProfileCredentialTests.App,
            ClientSecret = dac ? null : "synthetic-profile-secret", Scopes = scopes,
        }, default);
        try
        {
            Assert.Equal(AuthPrincipalType.ServicePrincipal, profile.PrincipalType);
            Assert.Equal(ProfileCredentialTests.App, profile.ClientId);
            Assert.Equal(ProfileCredentialTests.Tenant, profile.TenantId);
            Assert.Null(profile.HomeAccountId);
            var post = Assert.Single(h.Transport.Posts);
            Assert.Equal(scopes[0], post["scope"]);
            Assert.Equal(ProfileCredentialTests.App, post["client_id"]);
            Assert.All(h.Transport.TokenUris, uri => Assert.Equal("login.microsoftonline.com", uri.Host));
            if (dac) Assert.Equal(ProfileAzureSourceId.EnvironmentClientSecret, Assert.Single(h.Azure.Constructions).Source);
            else Assert.Equal(scopes, Assert.Single(h.Msal.ConfidentialRequests).Scopes);
        }
        finally { await provider.RemoveOwnedCredentialAsync(profile, default); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealWorkloadAndAzureGraphV2RenewalKeepsProtectedIdentityAndSource(bool dac)
    {
        using var h = new Harness(new()
        {
            AzureTenantId = ProfileCredentialTests.Tenant, AzureClientId = ProfileCredentialTests.App,
            AzureClientSecret = "synthetic-environment-secret",
        }, interaction: false, realAzure: dac);
        var provider = h.Provider();
        var profile = await provider.AuthenticateAsync(new()
        {
            ManagedIdentity = dac, Tenant = ProfileCredentialTests.Tenant,
            ApplicationId = dac ? null : ProfileCredentialTests.App,
            ClientSecret = dac ? null : "synthetic-profile-secret", Scopes = ["https://service.test/.default"],
        }, default);
        try
        {
            h.Files.Profiles.Items.Add(profile);
            await provider.CompleteCreationAsync(profile, default);
            var discoveries = h.Azure.Discoveries;
            var binding = h.Backend.Values.Single(p => p.Key.StartsWith(OwnedProtectedStorageKind.Credential + ":", StringComparison.Ordinal)).Value.ToArray();
            h.Transport.Audience = GraphAudience;
            h.Transport.V2Tokens = true;
            string[] scopes = [dac ? "HTTPS://GRAPH.MICROSOFT.COM//.default" : "https://graph.microsoft.com/.default"];
            var token = await h.Provider().AcquireAsync(profile, scopes, ProfileCredentialTests.Tenant, default);
            Assert.Equal(ProfileCredentialTests.Tenant, token.TenantId);
            Assert.Equal(2, h.Transport.Posts.Count);
            Assert.Equal(scopes[0], h.Transport.Posts.Last()["scope"]);
            Assert.All(h.Transport.Posts, post => Assert.Equal(ProfileCredentialTests.App, post["client_id"]));
            Assert.All(h.Transport.TokenUris, uri => Assert.Equal("login.microsoftonline.com", uri.Host));
            Assert.All(h.Transport.TokenUris, uri => Assert.Contains(ProfileCredentialTests.Tenant, uri.AbsolutePath));
            Assert.Equal(binding, h.Backend.Values.Single(p => p.Key.StartsWith(OwnedProtectedStorageKind.Credential + ":", StringComparison.Ordinal)).Value);
            if (dac)
            {
                Assert.Equal(discoveries, h.Azure.Discoveries);
                Assert.Equal(2, h.Azure.Constructions.Count);
                Assert.All(h.Azure.Constructions, construction => Assert.Equal(ProfileAzureSourceId.EnvironmentClientSecret, construction.Source));
            }
            else Assert.Equal(scopes, h.Msal.ConfidentialRequests.Last().Scopes);
        }
        finally
        {
            h.Files.Profiles.Items.Clear();
            await provider.RemoveOwnedCredentialAsync(profile, default);
        }
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/.default", "66666666-6666-6666-6666-666666666666")]
    [InlineData("https://graph.microsoft.com.lookalike.test/.default", GraphAudience)]
    [InlineData("https://graph.microsoft.com/lookalike/.default", GraphAudience)]
    [InlineData("https://graph.microsoft.us/.default", GraphAudience)]
    public async Task RealGraphV2BindingRejectsWrongAudienceAndUnmappedResources(string scope, string audience)
    {
        using var h = new Harness();
        h.Transport.Audience = audience;
        h.Transport.V2Tokens = true;
        var error = await Assert.ThrowsAsync<Microsoft.Agents.Hosting.Cli.Core.Errors.AuthenticationException>(() => h.Provider().AuthenticateAsync(new()
        {
            Tenant = ProfileCredentialTests.Tenant, ApplicationId = ProfileCredentialTests.App,
            ClientSecret = "synthetic-profile-secret", Scopes = [scope],
        }, default));
        Assert.Equal(ErrorCodes.Authentication.TokenClaimWrongAudience, error.ErrorCode);
        Assert.Equal(scope, Assert.Single(h.Transport.Posts)["scope"]);
        Assert.Empty(h.Backend.Values);
        Assert.Empty(Directory.GetFiles(h.Files.Directory, "*.marker", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(AuthCredentialMethod.Password, "not-a-guid")]
    [InlineData(AuthCredentialMethod.ClientSecret, "not-a-guid")]
    [InlineData(AuthCredentialMethod.DefaultAzureCredential, "not-a-guid")]
    [InlineData(AuthCredentialMethod.Password, "")]
    [InlineData(AuthCredentialMethod.ClientSecret, "")]
    [InlineData(AuthCredentialMethod.DefaultAzureCredential, "")]
    [InlineData(AuthCredentialMethod.Password, "00000000-0000-0000-0000-000000000000")]
    [InlineData(AuthCredentialMethod.ClientSecret, "00000000-0000-0000-0000-000000000000")]
    [InlineData(AuthCredentialMethod.DefaultAzureCredential, "00000000-0000-0000-0000-000000000000")]
    public async Task InvalidRenewalTenantFailsBeforeFilesProtectedStorageSdkSourcesOrHttp(AuthCredentialMethod method, string target)
    {
        using var h = new Harness();
        var request = Request(method, creation: false);
        var profile = UserProfile(request, new() { AccessToken = "unused", TenantId = ProfileCredentialTests.Tenant, HomeAccountId = "user.home" });
        await Assert.ThrowsAsync<UsageException>(() => h.Provider().AcquireAsync(profile, request.Scopes, target, default));
        Assert.Equal(0, h.Backend.Calls);
        Assert.Empty(h.Msal.PublicRequests);
        Assert.Empty(h.Msal.ConfidentialRequests);
        Assert.Empty(h.Azure.Constructions);
        Assert.Equal(0, h.Azure.Discoveries);
        Assert.Equal(0, h.Azure.TokenCalls);
        Assert.Empty(h.Transport.RequestUris);
        Assert.Empty(Directory.GetFileSystemEntries(h.Files.Directory));
    }

    [Fact]
    public async Task RealPublicCrossTenantRenewalKeepsPrivateHomeClientCloudAndProfileSnapshot()
    {
        using var h = new Harness();
        var create = Request(AuthCredentialMethod.Password, secret: "initial-only");
        var first = await h.Adapter.AcquireAsync(create, default);
        var profile = UserProfile(create, first);
        const string target = "66666666-6666-6666-6666-666666666666";
        var result = await h.Provider().AcquireAsync(profile, create.Scopes, target, default);
        Assert.Equal(target, result.TenantId);
        Assert.Equal(ProfileCredentialTests.Tenant, profile.TenantId);
        Assert.Equal(first.HomeAccountId, profile.HomeAccountId);
        var renewal = h.Msal.PublicRequests.Last().Request;
        Assert.Equal(first.HomeAccountId, renewal.HomeAccountId);
        Assert.Equal(create.ClientId, renewal.ClientId);
        Assert.Equal(create.Scopes, renewal.Scopes);
        Assert.Equal(create.CacheReference, renewal.CacheReference);
        Assert.Equal(create.Authority.Cloud, renewal.Authority.Cloud);
        Assert.Equal(Guid.Parse(target), renewal.Authority.TenantId);
        Assert.Equal("refresh_token", h.Transport.Posts.Last()["grant_type"]);
        Assert.DoesNotContain("password", h.Transport.Posts.Last().Keys);
        Assert.Contains(target, h.Transport.TokenUris.Last().AbsolutePath);
        Assert.All(h.Transport.TokenUris, uri => Assert.Equal("login.partner.microsoftonline.cn", uri.Host));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualMsalCacheMutationFailureOrCancellationCleansItsStage(bool canceled)
    {
        using var h = new Harness();
        using var ct = new CancellationTokenSource();
        h.Backend.AfterWrite = reference =>
        {
            if (reference.Kind != OwnedProtectedStorageKind.TokenCache) return;
            if (canceled) { ct.Cancel(); throw new OperationCanceledException(ct.Token); }
            throw new CacheProtectionUnavailableException("synthetic-native-cache-failure");
        };
        var error = await Record.ExceptionAsync(() => h.Provider().AuthenticateAsync(new()
        {
            ClientSecret = "ephemeral-secret", Tenant = ProfileCredentialTests.Tenant, ApplicationId = ProfileCredentialTests.App,
            Scopes = ["https://service.test/.default"],
        }, ct.Token));
        if (canceled) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal(ErrorCodes.Authentication.CacheProtectionUnavailable, Assert.IsAssignableFrom<AgentHostingException>(error).ErrorCode);
        Assert.Single(h.Transport.Posts);
        Assert.Empty(h.Backend.Values);
        Assert.Empty(Directory.GetFiles(h.Files.Directory, "*.marker", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RealAzureEnvironmentSecretSdkHonorsExplicitTenantCloudClientAndScopes()
    {
        using var handler = new OAuthTransport();
        using var http = new HttpClient(handler, disposeHandler: false);
        using var transport = new HttpClientTransport(http);
        var factory = new ProfileAzureCredentialSourceFactory(new StaticInteractionPolicy(false), transport);
        var source = factory.Create(ProfileAzureSourceId.EnvironmentClientSecret, new()
        {
            AzureTenantId = "66666666-6666-6666-6666-666666666666", AzureClientId = ProfileCredentialTests.App,
            AzureClientSecret = "ephemeral-environment-secret",
        }, AgentHostAuthorityResolver.Resolve(CloudInstance.China, Guid.Parse(ProfileCredentialTests.Tenant)));
        _ = await source.GetTokenAsync(new TokenRequestContext(["https://service.test/.default"]), default);
        var request = Assert.Single(handler.Posts);
        Assert.Equal(ProfileCredentialTests.App, request["client_id"]);
        Assert.Equal("ephemeral-environment-secret", request["client_secret"]);
        Assert.Equal("https://service.test/.default", request["scope"]);
        var uri = Assert.Single(handler.TokenUris);
        Assert.Equal("login.partner.microsoftonline.cn", uri.Host);
        Assert.Contains(ProfileCredentialTests.Tenant, uri.AbsolutePath);
    }

    [Fact]
    public async Task ChangedKnownAzureClientFailsPinnedRenewalBeforeSdkConstructorOrExchange()
    {
        using var h = new Harness(new() { AzureClientId = "66666666-6666-6666-6666-666666666666" });
        await Assert.ThrowsAsync<Microsoft.Agents.Hosting.Cli.Core.Errors.AuthenticationException>(() => h.Adapter.AcquireAsync(
            Request(AuthCredentialMethod.DefaultAzureCredential, creation: false, source: ProfileAzureSourceId.EnvironmentClientSecret), default));
        Assert.Empty(h.Azure.Constructions);
        Assert.Empty(h.Transport.Posts);
    }
    [Fact]
    public async Task PrivateSelectedMetadataTokenUsesRealSilentCacheWithoutObserver()
    {
        using var h = new Harness();
        var request = Request(AuthCredentialMethod.Password, secret: "once");
        var result = await h.Adapter.AcquireAsync(request, default);
        var observer = new Observer();
        var provider = h.Provider(observer: observer);
        var token = await provider.TryAcquireSelectedTokenAsync(UserProfile(request, result),
            request.Scopes, ProfileCredentialTests.Tenant, default);
        Assert.Equal(result.AccessToken, token!.AccessToken);
        Assert.Equal(0, observer.Checks);
        Assert.Single(h.Transport.Posts);
        Assert.All(h.Msal.PublicRequests, p => Assert.False(p.Broker));
    }

    [Fact]
    public async Task PublicConsentHelperUsesSameOwnedCacheAndExactHelperClientScopesAndCloud()
    {
        using var h = new Harness();
        var request = Request(AuthCredentialMethod.Password, secret: "once");
        var result = await h.Adapter.AcquireAsync(request, default);
        var helper = Guid.Parse("66666666-6666-6666-6666-666666666666");
        await h.Provider().AcquireConsentAsync(UserProfile(request, result), helper, "https://login.microsoftonline.com/common/oauth2/nativeclient",
            ["https://graph.microsoft.com/User.Read"], ProfileCredentialTests.Tenant, default);
        Assert.Equal(helper.ToString(), h.Transport.Posts.Last()["client_id"]);
        Assert.Equal(["https://graph.microsoft.com/User.Read"], h.Msal.PublicRequests.Last().Request.Scopes);
        Assert.All(h.Transport.Posts.Last()["scope"].Split(' '), scope =>
            Assert.True(scope is "https://graph.microsoft.com/User.Read" or "openid" or "profile" or "offline_access"));
        Assert.Single(h.Backend.Values);
        Assert.All(h.Msal.PublicRequests, r => Assert.Equal(request.CacheReference, r.Request.CacheReference));
        Assert.All(h.Transport.TokenUris, uri => Assert.Equal("login.partner.microsoftonline.cn", uri.Host));
        Assert.Single(h.Messages);
    }

    [Fact]
    public async Task ExplicitPrivateConsentHeadlessFailsBeforeCacheClientOrSdkCallback()
    {
        using var h = new Harness(interaction: false);
        var request = Request(AuthCredentialMethod.Password, creation: false, home: "user.home");
        var profile = UserProfile(request, new() { AccessToken = "unused", TenantId = ProfileCredentialTests.Tenant, HomeAccountId = "user.home" });
        await Assert.ThrowsAsync<InteractionRequiredException>(() => h.Provider().AcquireConsentAsync(profile,
            request.ClientId, "http://localhost", request.Scopes, ProfileCredentialTests.Tenant, default, trySilentFirst: false));
        Assert.Equal(0, h.Backend.Calls);
        Assert.Empty(h.Msal.PublicRequests);
        Assert.Empty(h.Messages);
    }

    [Fact]
    public async Task RealDeviceCodeProtocolPresentsSdkMessageAndStoresOnlyPrivateMsalState()
    {
        using var h = new Harness();
        var token = await h.Adapter.AcquireAsync(Request(AuthCredentialMethod.DeviceCode), default);
        Assert.Equal("user.home", token.HomeAccountId);
        Assert.Equal("offline device-code message", Assert.Single(h.Messages));
        Assert.Equal("device_code", h.Transport.Posts.Last()["grant_type"]);
        Assert.Single(h.Backend.Values);
    }

    [Theory]
    [InlineData(AuthCredentialMethod.GitHubFederated)]
    [InlineData(AuthCredentialMethod.AzureDevOpsFederated)]
    public async Task RealConfidentialAssertionCallbackIsFreshOnExchangeButNotCacheHit(AuthCredentialMethod method)
    {
        var assertions = new AssertionTransport();
        using var h = new Harness(new()
        {
            GitHubRequestUrl = "https://runtime.test/oidc", GitHubRequestToken = "runtime-only",
            AzureDevOpsRequestUrl = "https://runtime.test/oidc", AzureDevOpsRequestToken = "runtime-only",
        }, assertions);
        var request = Request(method);
        await h.Adapter.AcquireAsync(request, default);
        await h.Adapter.AcquireAsync(request, default);
        Assert.Equal(1, assertions.Calls);
        var cache = OwnedProfileReferences.Parse(request.CacheReference).Cache;
        var key = $"{cache.Kind}:{cache.OpaqueId}";
        var document = JsonNode.Parse(h.Backend.Values[key])!.AsObject();
        foreach (var entry in document["AccessToken"]!.AsObject().Select(p => p.Value!.AsObject()))
        { entry["expires_on"] = "0"; entry["extended_expires_on"] = "0"; }
        h.Backend.Values[key] = JsonSerializer.SerializeToUtf8Bytes(document);
        await h.Adapter.AcquireAsync(request, default);
        Assert.Equal(2, assertions.Calls);
        Assert.Equal(["fresh-assertion-1", "fresh-assertion-2"], h.Transport.Posts.Select(p => p["client_assertion"]));
        Assert.DoesNotContain("fresh-assertion", string.Join("", h.Backend.Values.Values.Select(Encoding.UTF8.GetString)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingFederationOrInvalidBrowserConfigurationRejectsBeforeStageStorage(bool browser)
    {
        using var h = new Harness(browser ? new() { BrowserRedirectUri = "https://unregistered.test/redirect" } : new());
        await Assert.ThrowsAnyAsync<AgentHostingException>(() => h.Provider().AuthenticateAsync(browser
            ? new Microsoft.Agents.Hosting.Cli.Core.Operations.AuthCreateRequest { Scopes = ["https://service.test/.default"] }
            : new Microsoft.Agents.Hosting.Cli.Core.Operations.AuthCreateRequest
            {
                GitHubFederated = true, Tenant = ProfileCredentialTests.Tenant, ApplicationId = ProfileCredentialTests.App,
                Scopes = ["https://service.test/.default"],
            }, default));
        Assert.Equal(0, h.Backend.Calls);
        Assert.Empty(h.Transport.Posts);
        Assert.Empty(Directory.GetFileSystemEntries(h.Files.Directory));
    }

    private static AuthProfile UserProfile(ProfileTokenRequest request, ProfileTokenResult result) => new()
    {
        AccountKey = result.HomeAccountId!, HomeAccountId = result.HomeAccountId, Username = result.Username ?? "",
        ProfileId = "owned-user", CredentialReference = request.CacheReference, CredentialMethod = request.Method,
        ClientId = request.ClientId.ToString(), TenantId = result.TenantId, Cloud = request.Authority.Cloud,
        Authority = request.Authority.AuthorityUrl.AbsoluteUri,
    };
    [Fact]
    public async Task RealConfidentialClientPrivateAppCacheIsPartitionedByOwnedReference()
    {
        using var h = new Harness();
        var one = Request(AuthCredentialMethod.ClientSecret, secret: "one-secret");
        var two = Request(AuthCredentialMethod.ClientSecret, secret: "two-secret");
        var first = await h.Adapter.AcquireAsync(one, default);
        var second = await h.Adapter.AcquireAsync(two, default);
        Assert.NotEqual(first.AccessToken, second.AccessToken);
        Assert.Equal(2, h.Transport.Posts.Count);
        Assert.Equal("one-secret", h.Transport.Posts[0]["client_secret"]);
        Assert.Equal("two-secret", h.Transport.Posts[1]["client_secret"]);
        Assert.Equal(ProfileCredentialTests.App, h.Transport.Posts[0]["client_id"]);
        Assert.Equal("https://service.test/.default", h.Transport.Posts[0]["scope"]);
        Assert.All(h.Transport.TokenUris, uri => Assert.Equal("login.partner.microsoftonline.cn", uri.Host));
        Assert.All(h.Transport.TokenUris, uri => Assert.Contains(ProfileCredentialTests.Tenant, uri.AbsolutePath));
        Assert.Equal(first.AccessToken, (await h.Adapter.AcquireAsync(one, default)).AccessToken);
        Assert.Equal(second.AccessToken, (await h.Adapter.AcquireAsync(two, default)).AccessToken);
        Assert.Equal(2, h.Transport.Posts.Count);
        Assert.Equal(2, h.Backend.Values.Count);
        Assert.DoesNotContain("one-secret", string.Join("", h.Backend.Values.Values.Select(Encoding.UTF8.GetString)));
    }

    [Fact]
    public async Task RealRopcCachesRefreshStateAndMetadataReaderAndRenewalNeverReplayPassword()
    {
        using var h = new Harness();
        var create = Request(AuthCredentialMethod.Password, secret: "ropc-once");
        var first = await h.Adapter.AcquireAsync(create, default);
        Assert.Equal("user.home", first.HomeAccountId);
        var renew = Request(AuthCredentialMethod.Password, creation: false, reference: create.CacheReference, home: first.HomeAccountId);
        Assert.Equal(first.AccessToken, (await h.Adapter.AcquireAsync(renew, default)).AccessToken);
        var networkCalls = h.Transport.RequestUris.Count;
        var accounts = await h.Adapter.GetCachedAccountsAsync(renew, default);
        Assert.Equal(networkCalls, h.Transport.RequestUris.Count);
        Assert.Equal("user.home", Assert.Single(accounts).HomeAccountId);
        Assert.Equal("password", Assert.Single(h.Transport.Posts)["grant_type"]);
        Assert.DoesNotContain("ropc-once", string.Join("", h.Backend.Values.Values.Select(Encoding.UTF8.GetString)));
        Assert.Equal(3, h.Msal.PublicRequests.Count);
        Assert.All(h.Msal.PublicRequests, p => Assert.False(p.Broker));
    }

    [Fact]
    public async Task ExactPrivateAccountCannotRenewASecondHomeIdentity()
    {
        using var h = new Harness();
        var create = Request(AuthCredentialMethod.Password, secret: "once");
        await h.Adapter.AcquireAsync(create, default);
        await Assert.ThrowsAsync<InteractionRequiredException>(() => h.Adapter.AcquireAsync(
            Request(AuthCredentialMethod.Password, creation: false, reference: create.CacheReference, home: "different.home"), default));
        Assert.Single(h.Transport.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokerRenewalUsesOnlyTheExactProfileAccount(bool selectedAccountAvailable)
    {
        using var h = new Harness();
        const string selected = "selected@example.test";
        const string operatingSystemUser = "windows@example.test";
        var request = Request(AuthCredentialMethod.Broker, creation: false,
            home: MsalTokenCacheSeed.HomeAccountId(selected, ProfileCredentialTests.Tenant), cloud: CloudInstance.Public);
        var brokerClient = h.Msal.CreatePublic(request, broker: false);
        MsalTokenCacheSeed.Seed(brokerClient, ProfileCredentialTests.Tenant, request.Scopes[0],
            selectedAccountAvailable ? [operatingSystemUser, selected] : [operatingSystemUser]);
        _ = await brokerClient.GetAccountsAsync();
        // Model broker-owned accounts without invoking the native broker or writing a private refresh token.
        h.Msal.BrokerClient = brokerClient;
        h.Msal.PublicRequests.Clear();
        var profile = UserProfile(request, new()
        {
            AccessToken = "unused", HomeAccountId = request.HomeAccountId,
            Username = selected, TenantId = ProfileCredentialTests.Tenant,
        });

        if (selectedAccountAvailable)
        {
            var result = await h.Provider().AcquireAsync(profile, request.Scopes, null, default);
            Assert.Equal(MsalTokenCacheSeed.AccessToken(selected), result.AccessToken);
        }
        else
        {
            await Assert.ThrowsAsync<InteractionRequiredException>(() =>
                h.Provider().AcquireAsync(profile, request.Scopes, null, default));
        }

        Assert.True(Assert.Single(h.Msal.PublicRequests).Broker);
        Assert.Empty(h.Transport.RequestUris);
        Assert.Empty(h.Messages);
    }

    [Theory]
    [InlineData(AuthCredentialMethod.Broker)]
    [InlineData(AuthCredentialMethod.Browser)]
    [InlineData(AuthCredentialMethod.DeviceCode)]
    public async Task ExplicitInteractiveSdkEntryRejectsBeforeClientCacheOrCallback(AuthCredentialMethod method)
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<InteractionRequiredException>(() => h.Adapter.AcquireAsync(Request(method, interaction: false), default));
        Assert.Empty(h.Msal.PublicRequests);
        Assert.Equal(0, h.Backend.Calls);
        Assert.Empty(h.Transport.Posts);
    }

    [Fact]
    public async Task PinnedDeveloperSourceHeadlessFailsBeforeAnyFactoryConstruction()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<InteractionRequiredException>(() => h.Adapter.AcquireAsync(
            Request(AuthCredentialMethod.DefaultAzureCredential, creation: false, interaction: false, source: ProfileAzureSourceId.AzureCli), default));
        Assert.Empty(h.Azure.Constructions);
        Assert.Equal(0, h.Azure.TokenCalls);
        Assert.Equal(0, h.Backend.Calls);
    }

    [Fact]
    public async Task AzureDiscoveryIsLazyAndRenewalConstructsOnlyPinnedSourceWithExactInputs()
    {
        using var h = new Harness();
        h.Azure.Sources = [ProfileAzureSourceId.EnvironmentClientSecret, ProfileAzureSourceId.ManagedIdentity, ProfileAzureSourceId.AzureCli];
        h.Azure.Unavailable.Add(ProfileAzureSourceId.EnvironmentClientSecret);
        var initial = await h.Adapter.AcquireAsync(Request(AuthCredentialMethod.DefaultAzureCredential), default);
        Assert.Equal(ProfileAzureSourceId.ManagedIdentity, initial.AzureSource);
        Assert.Equal([ProfileAzureSourceId.EnvironmentClientSecret, ProfileAzureSourceId.ManagedIdentity], h.Azure.Constructions.Select(c => c.Source));
        h.Azure.Constructions.Clear();
        h.Azure.Sources = [ProfileAzureSourceId.AzureCli];
        await h.Adapter.AcquireAsync(Request(AuthCredentialMethod.DefaultAzureCredential, creation: false, source: initial.AzureSource), default);
        var invocation = Assert.Single(h.Azure.Constructions);
        Assert.Equal(ProfileAzureSourceId.ManagedIdentity, invocation.Source);
        Assert.Equal(CloudInstance.China, invocation.Authority.Cloud);
        Assert.Equal(Guid.Parse(ProfileCredentialTests.Tenant), invocation.Authority.TenantId);
        Assert.Equal(["https://service.test/.default"], h.Azure.TokenScopes.Last());
    }

    [Fact]
    public async Task UnavailablePinnedSourceNeverRediscoversOrFallsBack()
    {
        using var h = new Harness();
        h.Azure.Unavailable.Add(ProfileAzureSourceId.ManagedIdentity);
        await Assert.ThrowsAsync<Microsoft.Agents.Hosting.Cli.Core.Errors.AuthenticationException>(() => h.Adapter.AcquireAsync(
            Request(AuthCredentialMethod.DefaultAzureCredential, creation: false, source: ProfileAzureSourceId.ManagedIdentity), default));
        Assert.Equal(ProfileAzureSourceId.ManagedIdentity, Assert.Single(h.Azure.Constructions).Source);
        Assert.Equal(0, h.Azure.Discoveries);
    }

    [Theory]
    [InlineData(AuthCredentialMethod.DefaultAzureCredential)]
    [InlineData(AuthCredentialMethod.ClientSecret)]
    [InlineData(AuthCredentialMethod.Broker)]
    public async Task MetadataOnlyReaderCannotConstructExternalSourcesOrBroker(AuthCredentialMethod method)
    {
        using var h = new Harness();
        Assert.Empty(await h.Adapter.GetCachedAccountsAsync(Request(method), default));
        Assert.Empty(h.Msal.PublicRequests);
        Assert.Empty(h.Azure.Constructions);
        Assert.Equal(0, h.Backend.Calls);
    }

    [Fact]
    public async Task RealCertificateClientUsesNonpersistentPfxAndNeverDeletesOriginal()
    {
        using var h = new Harness();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=offline-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.Combine(h.Files.Directory, "source.pfx");
        await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx, "pfx-password"));
        var tokenRequest = Request(AuthCredentialMethod.Certificate, secret: "pfx-password", certificatePath: path);
        var result = await h.Adapter.AcquireAsync(tokenRequest, default);
        Assert.Equal(certificate.Thumbprint, result.CertificateThumbprint);
        Assert.True(File.Exists(path));
        var posted = Assert.Single(h.Transport.Posts);
        Assert.Contains("client_assertion", posted.Keys);
        Assert.DoesNotContain("client_secret", posted.Keys);
        Assert.DoesNotContain("pfx-password", string.Join("", h.Backend.Values.Values.Select(Encoding.UTF8.GetString)));
    }

    [Theory]
    [InlineData("macos")]
    [InlineData("windows")]
    [InlineData("linux")]
    public void CertificateImportUsesSupportedNonpersistentPlatformSemantics(string platformName)
    {
        using var h = new Harness();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=offline-platform-import", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.Combine(h.Files.Directory, "platform-source.pfx");
        var pfx = certificate.Export(X509ContentType.Pfx, "generated-test-password");
        try
        {
            File.WriteAllBytes(path, pfx);
            var platform = FakePlatformInfo.Named(platformName);
            using (var loaded = ProfileCertificateLoader.Load(path, "generated-test-password", platform, (file, password, flags) =>
            {
                Assert.Equal(path, file);
                Assert.Equal("generated-test-password", password);
                if (platform.IsMacOs && flags.HasFlag(X509KeyStorageFlags.EphemeralKeySet))
                    throw new PlatformNotSupportedException("The .NET macOS importer rejects EphemeralKeySet.");
                Assert.False(flags.HasFlag(X509KeyStorageFlags.PersistKeySet));
                Assert.Equal(platform.IsMacOs ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet, flags);
                // Exercise generated keys without importing into this host's user certificate store.
                return X509CertificateLoader.LoadPkcs12FromFile(file, password,
                    OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
            }))
            {
                using var privateKey = loaded.GetRSAPrivateKey();
                Assert.NotNull(privateKey);
                byte[] data = [1, 2, 3];
                Assert.True(rsa.VerifyData(data, privateKey.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            }
            Assert.Equal(pfx, File.ReadAllBytes(path));
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    internal static ProfileTokenRequest Request(AuthCredentialMethod method, bool creation = true, bool interaction = true,
        string? reference = null, string? home = null, string? secret = null, ProfileAzureSourceId? source = null, string? certificatePath = null,
        CloudInstance cloud = CloudInstance.China) => new()
    {
        Method = method, ClientId = Guid.Parse(ProfileCredentialTests.App),
        Authority = AgentHostAuthorityResolver.Resolve(cloud, Guid.Parse(ProfileCredentialTests.Tenant)),
        Scopes = ["https://service.test/.default"], CacheReference = reference ?? OwnedProfileReferences.New(!ProfileCredentialValidation.IsPublicUser(method)).Value,
        IsCreation = creation, InteractionAllowed = interaction, HomeAccountId = home,
        ClientSecret = method == AuthCredentialMethod.ClientSecret ? secret : null,
        Password = method == AuthCredentialMethod.Password ? secret : null, Username = method == AuthCredentialMethod.Password ? "alice@example.test" : null,
        CertificatePath = certificatePath, CertificatePassword = method == AuthCredentialMethod.Certificate ? secret : null, PinnedAzureSource = source,
    };

    private sealed class Harness : IDisposable
    {
        internal ProfileCredentialTests.Harness Files { get; } = new();
        internal ProfileCredentialTests.Backend Backend => Files.Backend;
        internal OAuthTransport Transport { get; } = new();
        internal AzureFactory Azure { get; } = new();
        private readonly HttpClientTransport? _azureTransport;
        private readonly ProfileFederationAssertionProvider _federation;
        internal MsalFactory Msal { get; }
        internal ProfileTokenAcquisition Adapter { get; }
        private readonly AgentHostAuthOptions _options;
        internal List<string> Messages { get; } = [];
        internal ProfileCredentialProvider Provider(IAuthenticationResultObserver? observer = null) =>
            new(_options, Files.Profiles, Backend, Adapter, FakePlatformInfo.Unknown(), observer: observer);
        internal Harness(ProfileCredentialRuntimeOptions? runtime = null, HttpMessageHandler? assertions = null, bool interaction = true, bool realAzure = false)
        {
            runtime ??= new ProfileCredentialRuntimeOptions();
            _options = new() { ConfigDirectory = Files.Directory, InteractionAllowed = interaction };
            _federation = new(runtime, assertions);
            Msal = new(new ProfileMsalClientFactory(FakePlatformInfo.Unknown(), runtime, _federation, Transport));
            if (realAzure)
            {
                _azureTransport = new HttpClientTransport(Transport.GetHttpClient());
                Azure.Inner = new ProfileAzureCredentialSourceFactory(new StaticInteractionPolicy(interaction), _azureTransport);
            }
            Adapter = new(_options, runtime, Backend, Msal, Azure, new MessagesPresenter(Messages));
        }
        public void Dispose() { _azureTransport?.Dispose(); _federation.Dispose(); Transport.Dispose(); Files.Dispose(); }
    }

    private sealed class MsalFactory(IProfileMsalClientFactory inner) : IProfileMsalClientFactory
    {
        internal List<(ProfileTokenRequest Request, bool Broker)> PublicRequests { get; } = [];
        internal List<ProfileTokenRequest> ConfidentialRequests { get; } = [];
        internal IPublicClientApplication? BrokerClient { get; set; }
        public IPublicClientApplication CreatePublic(ProfileTokenRequest request, bool broker)
        { PublicRequests.Add((request, broker)); return broker && BrokerClient is not null ? BrokerClient : inner.CreatePublic(request, broker); }
        public IConfidentialClientApplication CreateConfidential(ProfileTokenRequest request, X509Certificate2? certificate)
        { ConfidentialRequests.Add(request); return inner.CreateConfidential(request, certificate); }
    }

    private sealed class AzureFactory : IProfileAzureCredentialSourceFactory
    {
        internal ProfileAzureCredentialSourceFactory? Inner;
        internal IReadOnlyList<ProfileAzureSourceId> Sources = [ProfileAzureSourceId.ManagedIdentity];
        internal HashSet<ProfileAzureSourceId> Unavailable { get; } = [];
        internal List<(ProfileAzureSourceId Source, AuthorityInfo Authority)> Constructions { get; } = [];
        internal List<string[]> TokenScopes { get; } = [];
        internal int Discoveries, TokenCalls;
        public IReadOnlyList<ProfileAzureSourceId> GetInitialSources(ProfileCredentialRuntimeOptions settings, AuthorityInfo authority) { Discoveries++; return Inner?.GetInitialSources(settings, authority) ?? Sources; }
        public TokenCredential Create(ProfileAzureSourceId source, ProfileCredentialRuntimeOptions settings, AuthorityInfo authority)
        {
            Constructions.Add((source, authority));
            return Inner?.Create(source, settings, authority) ?? new Credential(this, source);
        }
        private sealed class Credential(AzureFactory owner, ProfileAzureSourceId source) : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new NotSupportedException();
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                owner.TokenCalls++; owner.TokenScopes.Add(requestContext.Scopes);
                if (owner.Unavailable.Contains(source)) throw new CredentialUnavailableException("unavailable-secret-diagnostic");
                return ValueTask.FromResult(new AccessToken(ProfileCredentialTests.WorkloadToken().AccessToken, DateTimeOffset.UtcNow.AddHours(1)));
            }
        }
    }

    internal sealed class OAuthTransport : HttpMessageHandler, IMsalHttpClientFactory
    {
        private readonly HttpClient _client;
        internal List<Dictionary<string, string>> Posts { get; } = [];
        internal List<Uri> TokenUris { get; } = [];
        internal List<Uri> RequestUris { get; } = [];
        internal string Audience { get; set; } = "https://service.test";
        internal bool V2Tokens { get; set; }
        internal OAuthTransport() => _client = new(this, disposeHandler: false);
        public HttpClient GetHttpClient() => _client;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestUris.Add(request.RequestUri!);
            var requestedTenant = request.RequestUri!.Segments.ElementAtOrDefault(1)?.Trim('/');
            var tenant = Guid.TryParse(requestedTenant, out var tenantId) ? tenantId.ToString("D") : ProfileCredentialTests.Tenant;
            object payload;
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/userrealm/", StringComparison.OrdinalIgnoreCase))
                payload = new { account_type = "Managed" };
            else if (request.Method == HttpMethod.Get)
            {
                var origin = request.RequestUri!.GetLeftPart(UriPartial.Authority) + "/" + tenant;
                payload = new { authorization_endpoint = origin + "/oauth2/v2.0/authorize", token_endpoint = origin + "/oauth2/v2.0/token", issuer = origin + "/v2.0", jwks_uri = origin + "/discovery/v2.0/keys" };
            }
            else
            {
                var body = (await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(p => p.Split('=', 2))
                    .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace("+", " ")));
                Posts.Add(body); TokenUris.Add(request.RequestUri!);
                if (request.RequestUri!.AbsolutePath.EndsWith("/devicecode", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        device_code = "offline-code", user_code = "OFFLINE", verification_uri = "https://device.test",
                        expires_in = 60, interval = 1, message = "offline device-code message",
                    }), Encoding.UTF8, "application/json") };
                var response = new Dictionary<string, object>
                {
                    ["token_type"] = "Bearer", ["scope"] = body["scope"], ["expires_in"] = 3600,
                    ["access_token"] = ProfileCredentialTests.Jwt(V2Tokens
                        ? new { tid = tenant, azp = body["client_id"], oid = ProfileCredentialTests.ObjectId, aud = Audience, idtyp = "app", ver = "2.0", sequence = Posts.Count }
                        : new { tid = tenant, appid = body["client_id"], oid = ProfileCredentialTests.ObjectId, aud = Audience, idtyp = "app", sequence = Posts.Count }),
                };
                if (body["grant_type"] != "client_credentials")
                {
                    response["refresh_token"] = "private-refresh";
                    response["client_info"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { uid = "user", utid = "home" })).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                    response["id_token"] = ProfileCredentialTests.Jwt(new { tid = tenant, oid = "user", preferred_username = "alice@example.test", sub = "user" });
                }
                payload = response;
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        }
        protected override void Dispose(bool disposing) { if (disposing) _client.Dispose(); base.Dispose(disposing); }
    }
    private sealed class MessagesPresenter(List<string> messages) : IInteractiveMessagePresenter
    {
        public Task PresentAsync(string message, CancellationToken ct) { messages.Add(message); return Task.CompletedTask; }
    }
    private sealed class Observer : IAuthenticationResultObserver
    {
        internal int Checks;
        public bool IsObservationEnabled { get { Checks++; return true; } }
        public void Observe(AuthenticatedTenantContext context) => throw new InvalidOperationException("Metadata acquisition cannot attribute a tenant.");
    }
    private sealed class AssertionTransport : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { value = "fresh-assertion-" + Calls, oidcToken = "fresh-assertion-" + Calls })) });
        }
    }
}
