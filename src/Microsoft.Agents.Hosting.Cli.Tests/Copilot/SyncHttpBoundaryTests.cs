using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Extensions.DependencyInjection;
using CoreCluster = Microsoft.Agents.Hosting.Cli.Core.Environments.CoreServicesClusterCategory;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncHttpBoundaryTests
{
    private const string Tenant = "33333333-3333-3333-3333-333333333333";
    private const string IslandResource = "api://96ff4394-9197-43aa-b393-6a41652e21f8";
    private const string Gateway = "https://gateway.island.powerapps.com/";

    [Fact]
    public async Task DefaultSdkComponentPathRejectsRedirectWithoutFollowingIt()
    {
        var credentials = new ProfileSyncResourceTests.Credentials();
        var legacy = new RecordingTokenProvider();
        var transport = new Transport(HttpStatusCode.Redirect);
        var hostFactory = new HostFactory(transport);
        using var services = Services(credentials, legacy, transport, hostFactory);
        var environment = Environment();
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));
        var sdk = services.GetRequiredService<IIslandControlPlaneService>();

        await Assert.ThrowsAsync<EndpointTrustException>(() => sdk.GetComponentsAsync(Context(environment), null!, default));

        // The SDK partitions component queries into independent, parallel $batch requests.
        Assert.NotEmpty(transport.Requests);
        Assert.All(transport.Requests, sent =>
        {
            Assert.Equal(environment.DataverseOrganization.Host, sent.Destination.Host);
            Assert.Equal("profile-a", sent.Token);
            Assert.True(Guid.TryParse(sent.ClientRequestId, out _));
        });
        Assert.Equal(transport.Requests.Count, transport.Requests.Select(sent => sent.ClientRequestId).Distinct().Count());
        Assert.Equal(transport.Requests.Count, credentials.Requests.Count);
        Assert.All(credentials.Requests, acquired =>
        {
            Assert.Same(environment.Context!.Profile, acquired.Profile);
            Assert.Equal(Tenant, acquired.Tenant);
            Assert.Equal(["https://a.crm.dynamics.com//.default"], acquired.Scopes);
        });
        Assert.Equal(0, hostFactory.Calls);
        Assert.Empty(legacy.RequestedResources);
    }

    [Fact]
    public async Task DefaultSdkComponentPathRejectsDifferentOrganizationBeforeAcquisition()
    {
        var credentials = new ProfileSyncResourceTests.Credentials();
        var legacy = new RecordingTokenProvider();
        var transport = new Transport();
        var hostFactory = new HostFactory(transport);
        using var services = Services(credentials, legacy, transport, hostFactory);
        var environment = Environment();
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));
        var other = environment with { DataverseOrganization = new Uri("https://b.crm.dynamics.com/") };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() =>
            services.GetRequiredService<IIslandControlPlaneService>().GetComponentsAsync(Context(other), null!, default));

        Assert.Equal(CopilotErrorKinds.UntrustedDataverseOrigin, failure.ErrorKind);
        Assert.Empty(credentials.Requests);
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(transport.Requests);
        Assert.Equal(0, hostFactory.Calls);
    }

    [Fact]
    public async Task DefaultSdkComponentPathRejectsFragmentBearingRequestBeforeAcquisition()
    {
        var credentials = new ProfileSyncResourceTests.Credentials();
        var legacy = new RecordingTokenProvider();
        var transport = new Transport();
        var hostFactory = new HostFactory(transport);
        using var services = Services(credentials, legacy, transport, hostFactory);
        var environment = Environment();
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));
        var malformed = environment with { DataverseOrganization = new Uri("https://a.crm.dynamics.com/#fragment") };

        await Assert.ThrowsAsync<EndpointTrustException>(() =>
            services.GetRequiredService<IIslandControlPlaneService>().GetComponentsAsync(Context(malformed), null!, default));

        Assert.Empty(credentials.Requests);
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(transport.Requests);
        Assert.Equal(0, hostFactory.Calls);
    }

    [Theory]
    [InlineData("https://a.crm.dynamics.com/#fragment", false)]
    [InlineData("https://a.crm.dynamics.com/?query=value", false)]
    [InlineData("https://a.crm.dynamics.com/#fragment", true)]
    [InlineData("https://a.crm.dynamics.com/?query=value", true)]
    public async Task DefaultSdkComponentPathRejectsMalformedOrganizationBeforeAcquisition(string organization, bool legacyContext)
    {
        var credentials = new ProfileSyncResourceTests.Credentials();
        var legacy = new RecordingTokenProvider();
        var transport = new Transport();
        var hostFactory = new HostFactory(transport);
        using var services = Services(credentials, legacy, transport, hostFactory);
        var environment = Environment() with { DataverseOrganization = new Uri(organization) };
        if (legacyContext)
            environment = environment with { Context = null };
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));

        await Assert.ThrowsAsync<EndpointTrustException>(() =>
            services.GetRequiredService<IIslandControlPlaneService>().GetComponentsAsync(Context(environment), null!, default));

        Assert.Empty(credentials.Requests);
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(transport.Requests);
        Assert.Equal(0, hostFactory.Calls);
    }

    [Fact]
    public async Task DefaultSdkComponentPostBatchesAreNotReplayedWhenThrottled()
    {
        var credentials = new ProfileSyncResourceTests.Credentials();
        var transport = new Transport(HttpStatusCode.TooManyRequests);
        using var services = Services(credentials, new RecordingTokenProvider(), transport, new HostFactory(transport));
        var environment = Environment();
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));

        var failure = await Record.ExceptionAsync(() =>
            services.GetRequiredService<IIslandControlPlaneService>().GetComponentsAsync(Context(environment), null!, default));

        Assert.NotNull(failure);
        Assert.NotEmpty(transport.Requests);
        Assert.All(transport.Requests, sent => Assert.Equal(HttpMethod.Post, sent.Method));
        Assert.Equal(transport.Requests.Count, transport.Requests.Select(sent => sent.ClientRequestId).Distinct().Count());
        Assert.Equal(transport.Requests.Count, credentials.Requests.Count);
    }

    [Fact]
    public async Task ActualSdkComponentCollectionWriteIsNotReplayedWhenThrottled()
    {
        var credentials = new ProfileSyncResourceTests.Credentials();
        var transport = new Transport(HttpStatusCode.TooManyRequests);
        using var services = Services(credentials, new RecordingTokenProvider(), transport, new HostFactory(transport));
        var environment = Environment();
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));
        var sdk = services.GetRequiredService<ISyncDataverseClient>();
        sdk.SetDataverseUrl(environment.DataverseOrganization.ToString());
        var collections = (ISyncComponentCollectionDataverseClient)sdk;

        var failure = await Assert.ThrowsAsync<DataverseRequestException>(() =>
            collections.CreateComponentCollectionAsync("collection", "collection_schema", default));

        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Equal(HttpMethod.Post, Assert.Single(transport.Requests).Method);
        Assert.Single(credentials.Requests);
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":\"0x8004fac4\",\"message\":\"Request rejected.\",\"extra\":\"unrelated-body-canary\"}}", "0x8004fac4")]
    [InlineData("{\"unrelated\":\"unrelated-body-canary\"}", null)]
    [InlineData("unrelated-body-canary", null)]
    public async Task SdkUnsafeFailureUsesRedactedErrorEnvelope(string body, string? expectedCode)
    {
        var credentials = new ProfileSyncResourceTests.Credentials();
        var transport = new Transport(HttpStatusCode.TooManyRequests, body);
        using var services = Services(credentials, new RecordingTokenProvider(), transport, new HostFactory(transport));
        var environment = Environment();
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));
        var sdk = services.GetRequiredService<ISyncDataverseClient>();
        sdk.SetDataverseUrl(environment.DataverseOrganization.ToString());

        var failure = await Assert.ThrowsAsync<DataverseRequestException>(() =>
            ((ISyncComponentCollectionDataverseClient)sdk).CreateComponentCollectionAsync("collection", "collection_schema", default));

        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.DoesNotContain("unrelated-body-canary", failure.Message, StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(failure.ResponseBody);
        var error = payload.RootElement.GetProperty("error");
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.Contains("HTTP 429", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Single(transport.Requests);
        Assert.Single(credentials.Requests);
    }

    [Theory]
    [InlineData(AuthPrincipalType.User)]
    [InlineData(AuthPrincipalType.ServicePrincipal)]
    public async Task SdkFactoryBranchAuthenticatesExactResourceAndDoesNotReplayPost(AuthPrincipalType mode)
    {
        var credentials = new ProfileSyncResourceTests.Credentials();
        var legacy = new RecordingTokenProvider();
        var transport = new Transport();
        var hostFactory = new HostFactory(transport);
        using var services = Services(credentials, legacy, transport, hostFactory, islandEnabled: true);
        var environment = Environment(mode: mode);
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));
        var sdk = services.GetRequiredService<IIslandControlPlaneService>();
        sdk.SetConnectionContext(Gateway, SyncClusterCategory.For(CoreCluster.Prod));

        await Assert.ThrowsAsync<HttpRequestException>(() => sdk.GetComponentsAsync(Context(environment), null!, default));

        var acquired = Assert.Single(credentials.Requests);
        Assert.Same(environment.Context!.Profile, acquired.Profile);
        Assert.Equal(Tenant, acquired.Tenant);
        Assert.Equal([IslandResource + (mode == AuthPrincipalType.User ? "//.default" : "/.default")], acquired.Scopes);
        var sent = Assert.Single(transport.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("gateway.island.powerapps.com", sent.Destination.Host);
        Assert.Equal("profile-a", sent.Token);
        Assert.Equal(0, hostFactory.Calls);
        Assert.Same(hostFactory, services.GetRequiredService<IHttpClientFactory>());
        Assert.Empty(legacy.RequestedResources);
    }

    [Theory]
    [InlineData("https://attacker.example/")]
    [InlineData("https://gateway.island.powerapps.com.attacker.example/")]
    [InlineData("https://user@gateway.island.powerapps.com/")]
    [InlineData("https://gateway.island.powerapps.com:444/")]
    [InlineData("http://gateway.island.powerapps.com/")]
    [InlineData("https://gateway.island.powerapps.com/#fragment")]
    [InlineData("https://gateway.island.powerapps.com/?query=value")]
    public async Task SdkFactoryBranchRejectsUntrustedDestinationBeforeAcquisition(string destination)
    {
        var credentials = new ProfileSyncResourceTests.Credentials();
        var legacy = new RecordingTokenProvider();
        var transport = new Transport();
        var hostFactory = new HostFactory(transport);
        using var services = Services(credentials, legacy, transport, hostFactory, islandEnabled: true);
        var environment = Environment();
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));
        var sdk = services.GetRequiredService<IIslandControlPlaneService>();
        sdk.SetConnectionContext(destination, SyncClusterCategory.For(CoreCluster.Prod));

        await Assert.ThrowsAsync<EndpointTrustException>(() => sdk.GetComponentsAsync(Context(environment), null!, default));

        Assert.Empty(credentials.Requests);
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(transport.Requests);
        Assert.Equal(0, hostFactory.Calls);
    }

    [Fact]
    public async Task SdkFactoryBranchRejectsRedirectWithoutFollowingOrReplayingPost()
    {
        var credentials = new ProfileSyncResourceTests.Credentials();
        var transport = new Transport(HttpStatusCode.RedirectKeepVerb);
        var hostFactory = new HostFactory(transport);
        using var services = Services(credentials, new RecordingTokenProvider(), transport, hostFactory, islandEnabled: true);
        var environment = Environment();
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));
        var sdk = services.GetRequiredService<IIslandControlPlaneService>();
        sdk.SetConnectionContext(Gateway, SyncClusterCategory.For(CoreCluster.Prod));

        await Assert.ThrowsAsync<EndpointTrustException>(() => sdk.GetComponentsAsync(Context(environment), null!, default));

        Assert.Single(credentials.Requests);
        Assert.Single(transport.Requests);
        Assert.Equal(0, hostFactory.Calls);
    }

    [Theory]
    [InlineData(CloudInstance.UsNat)]
    [InlineData(CloudInstance.UsSec)]
    public async Task NationalBootstrapDoesNotReplaceASupportedCapturedSdkTarget(CloudInstance cloud)
    {
        var credentials = new ProfileSyncResourceTests.Credentials();
        var legacy = new RecordingTokenProvider();
        var transport = new Transport(HttpStatusCode.RedirectKeepVerb);
        var hostFactory = new HostFactory(transport);
        using var services = Services(credentials, legacy, transport, hostFactory, islandEnabled: true,
            apiTarget: PowerPlatformApiTargetSelection.TargetFor(cloud, PowerPlatformApiRing.Production));
        var environment = Environment();
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));
        var sdk = services.GetRequiredService<IIslandControlPlaneService>();
        sdk.SetConnectionContext(Gateway, SyncClusterCategory.For(CoreCluster.Prod));

        await Assert.ThrowsAsync<EndpointTrustException>(() => sdk.GetComponentsAsync(Context(environment), null!, default));

        Assert.Same(environment.Context!.Profile, Assert.Single(credentials.Requests).Profile);
        Assert.Equal(new Uri(Gateway).Host, Assert.Single(transport.Requests).Destination.Host);
        Assert.Empty(legacy.RequestedResources);
        Assert.Equal(0, hostFactory.Calls);
    }

    [Fact]
    public async Task SdkFactoryClientKeepsCapturedProfileDuringLaterAmbientSelection()
    {
        var credentials = new ProfileSyncResourceTests.Credentials { Pause = true };
        var transport = new Transport();
        var hostFactory = new HostFactory(transport);
        using var services = Services(credentials, new RecordingTokenProvider(), transport, hostFactory, islandEnabled: true);
        var environment = Environment();
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));
        var sdk = services.GetRequiredService<IIslandControlPlaneService>();
        sdk.SetConnectionContext(Gateway, SyncClusterCategory.For(CoreCluster.Prod));
        var request = sdk.GetComponentsAsync(Context(environment), null!, default);
        try
        {
            Assert.Single(credentials.Requests);
            var other = Environment("b") with { TenantId = "77777777-7777-7777-7777-777777777777" };
            DataverseOperationScope.Set(other.DataverseOrganization, other.TenantId, other.Context);
        }
        finally
        {
            credentials.Release.TrySetResult();
        }

        await Assert.ThrowsAsync<HttpRequestException>(() => request);

        var acquired = Assert.Single(credentials.Requests);
        Assert.Same(environment.Context!.Profile, acquired.Profile);
        Assert.Equal(Tenant, acquired.Tenant);
        Assert.Equal("profile-a", Assert.Single(transport.Requests).Token);
        Assert.Equal("b", DataverseOperationScope.Context!.Profile.ProfileId);
        Assert.Equal(0, hostFactory.Calls);
    }

    [Fact]
    public async Task FreshGatewayClientsKeepIndependentProfilesWhenSentUnderAnotherScope()
    {
        var credentials = new ProfileSyncResourceTests.Credentials { Pause = true };
        var transport = new Transport();
        var legacy = new RecordingTokenProvider();
        using var services = Services(credentials, legacy, transport, new HostFactory(transport));
        var accessor = services.GetRequiredService<AgentHostDataverseHttpClientAccessor>();
        var environmentA = Environment();
        var environmentB = Environment("b") with { TenantId = "77777777-7777-7777-7777-777777777777" };
        using var scope = DataverseOperationScope.Push(new(environmentA.DataverseOrganization, Tenant, environmentA.Context));
        using var clientA = accessor.CreateGatewayClient(PowerPlatformCloudMap.ForCloud(CloudInstance.Public), CoreCluster.Prod);
        DataverseOperationScope.Set(environmentB.DataverseOrganization, environmentB.TenantId, environmentB.Context);
        using var clientB = accessor.CreateGatewayClient(PowerPlatformCloudMap.ForCloud(CloudInstance.Public), CoreCluster.Prod);
        var requestA = clientA.PostAsync(Gateway, null);
        var requestB = clientB.PostAsync(Gateway, null);
        try
        {
            Assert.Equal(2, credentials.Requests.Count);
        }
        finally
        {
            credentials.Release.TrySetResult();
        }
        using var responseA = await requestA;
        using var responseB = await requestB;

        Assert.Contains(credentials.Requests, acquired => ReferenceEquals(acquired.Profile, environmentA.Context!.Profile) && acquired.Tenant == Tenant);
        Assert.Contains(credentials.Requests, acquired => ReferenceEquals(acquired.Profile, environmentB.Context!.Profile) && acquired.Tenant == environmentB.TenantId);
        Assert.Contains(transport.Requests, sent => sent.Token == "profile-a");
        Assert.Contains(transport.Requests, sent => sent.Token == "profile-b");
        Assert.Equal("b", DataverseOperationScope.Context!.Profile.ProfileId);
        Assert.Empty(legacy.RequestedResources);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SdkFactoryUsesCapturedSovereignTrustInsteadOfHostPublicConfiguration(bool trusted)
    {
        var credentials = new ProfileSyncResourceTests.Credentials();
        var transport = new Transport();
        var legacy = new RecordingTokenProvider();
        using var services = Services(credentials, legacy, transport, new HostFactory(transport), islandEnabled: true);
        var environment = Environment(cloud: CloudInstance.UsGov);
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));
        var sdk = services.GetRequiredService<IIslandControlPlaneService>();
        sdk.SetConnectionContext(trusted ? "https://gateway.island.powerapps.us/" : Gateway, SyncClusterCategory.For(CoreCluster.Gov));

        if (trusted)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => sdk.GetComponentsAsync(Context(environment), null!, default));
            var acquired = Assert.Single(credentials.Requests);
            Assert.Same(environment.Context!.Profile, acquired.Profile);
            Assert.Equal(["api://9315aedd-209b-43b3-b149-2abff6a95d59//.default"], acquired.Scopes);
            Assert.Equal("gateway.island.powerapps.us", Assert.Single(transport.Requests).Destination.Host);
        }
        else
        {
            await Assert.ThrowsAsync<EndpointTrustException>(() => sdk.GetComponentsAsync(Context(environment), null!, default));
            Assert.Empty(credentials.Requests);
            Assert.Empty(transport.Requests);
        }
        Assert.Empty(legacy.RequestedResources);
    }

    [Fact]
    public async Task SdkFactoryRejectsForeignTenantBeforeAnyRequest()
    {
        var credentials = new ProfileSyncResourceTests.Credentials { AcquiredTenant = "77777777-7777-7777-7777-777777777777" };
        var transport = new Transport();
        using var services = Services(credentials, new RecordingTokenProvider(), transport, new HostFactory(transport), islandEnabled: true);
        var environment = Environment();
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));
        var sdk = services.GetRequiredService<IIslandControlPlaneService>();
        sdk.SetConnectionContext(Gateway, SyncClusterCategory.For(CoreCluster.Prod));

        await Assert.ThrowsAsync<TokenClaimException>(() => sdk.GetComponentsAsync(Context(environment), null!, default));

        Assert.Single(credentials.Requests);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task LegacyGatewayClientDoesNotAdoptALaterCapturedProfile()
    {
        var credentials = new ProfileSyncResourceTests.Credentials();
        var legacy = new RecordingTokenProvider();
        var transport = new Transport();
        using var services = Services(credentials, legacy, transport, new HostFactory(transport));
        using var scope = DataverseOperationScope.Push(null);
        using var client = services.GetRequiredService<AgentHostDataverseHttpClientAccessor>()
            .CreateGatewayClient(PowerPlatformCloudMap.ForCloud(CloudInstance.Public), CoreCluster.Prod);
        var environment = Environment();
        DataverseOperationScope.Set(environment.DataverseOrganization, Tenant, environment.Context);

        using var response = await client.PostAsync(Gateway, null);

        Assert.Equal(IslandResource + "/", Assert.Single(legacy.RequestedResources));
        Assert.Null(Assert.Single(legacy.RequestedTargetTenants));
        Assert.Empty(credentials.Requests);
        Assert.Same(environment.Context, DataverseOperationScope.Context);
        Assert.Equal(legacy.AccessToken, Assert.Single(transport.Requests).Token);
    }

    private static CopilotEnvironmentContext Environment(string name = "a", AuthPrincipalType mode = AuthPrincipalType.User,
        CloudInstance cloud = CloudInstance.Public) => new()
    {
        EnvironmentId = name,
        TenantId = Tenant,
        DataverseOrganization = new Uri(cloud == CloudInstance.Public
            ? $"https://{name}.crm.dynamics.com/" : $"https://{name}.crm.microsoftdynamics.us/"),
        AgentManagementUrl = new Uri(Gateway),
        Context = new(new AuthProfile
        {
            ProfileId = name, Revision = 17, AccountKey = "home-" + name, Username = name,
            ClientId = PowerPlatformApiPermissions.AgentHostCliApplicationId, TenantId = Tenant,
            PrincipalType = mode,
            CredentialMethod = mode == AuthPrincipalType.User ? AuthCredentialMethod.Browser : AuthCredentialMethod.ClientSecret,
            Cloud = cloud, Ring = PowerPlatformApiRing.Production,
        }, PowerPlatformApiTargetSelection.TargetFor(cloud, PowerPlatformApiRing.Production)),
    };

    private static AuthoringOperationContext Context(CopilotEnvironmentContext environment) => new(
        new PrincipalObjectReference(Guid.Parse(environment.TenantId), Guid.Empty),
        new CdsOrganizationInfo(Guid.Parse(environment.TenantId), environment.DataverseOrganization,
            pvaSolutionVersion: new Version(1, 0),
            dvTableSearchGlossaryAndSynonymsSolutionVersion: new Version(1, 0),
            dvTableSearchSolutionVersion: new Version(1, 0)),
        new AgentSyncInfo { AgentId = Guid.Parse("55555555-5555-5555-5555-555555555555") }.BotReference,
        null, false);

    private static ServiceProvider Services(ProfileSyncResourceTests.Credentials credentials, RecordingTokenProvider legacy,
        Transport transport, HostFactory hostFactory, bool islandEnabled = false, PowerPlatformApiTargetConfig? apiTarget = null)
    {
        IServiceCollection services = islandEnabled ? new IslandEnabledServices() : new ServiceCollection();
        services.AddSingleton<IProfileCredentialProvider>(credentials);
        services.AddSingleton<ITokenProvider>(legacy);
        services.AddSingleton<IHttpClientFactory>(hostFactory);
        services.AddAgentHostCopilotStudio(TextWriter.Null, apiTarget ?? PowerPlatformApiTargetConfig.Public);
        services.AddSingleton<AgentHostDataverseHttpClientAccessor>(provider =>
            new(provider.GetRequiredService<ISyncAuthProvider>(), () => transport));
        return services.BuildServiceProvider();
    }

    // Substitute only the original SDK registration with its publicly supported preauthorized
    // registration, before the production adapter wraps it. Default-path tests do not substitute it.
    private sealed class IslandEnabledServices : Collection<ServiceDescriptor>, IServiceCollection
    {
        private bool _substituted;
        protected override void InsertItem(int index, ServiceDescriptor item)
        {
            if (!_substituted && item.ServiceType == typeof(IIslandControlPlaneService))
            {
                var sdkServices = new ServiceCollection();
                sdkServices.AddSyncServices(isIslandPreauthorized: true);
                item = sdkServices.Last(descriptor => descriptor.ServiceType == typeof(IIslandControlPlaneService));
                _substituted = true;
            }
            base.InsertItem(index, item);
        }
    }

    private sealed class HostFactory(Transport transport) : IHttpClientFactory
    {
        internal int Calls { get; private set; }
        public HttpClient CreateClient(string name)
        {
            Calls++;
            return new(transport, disposeHandler: false);
        }
    }

    private sealed class Transport(HttpStatusCode status = HttpStatusCode.ServiceUnavailable, string body = "{}") : HttpMessageHandler
    {
        internal ConcurrentQueue<(Uri Destination, HttpMethod Method, string? Token, string? ClientRequestId)> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.RequestUri!, request.Method, request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("x-ms-client-request-id", out var values) ? values.Single() : null));
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (status == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            if ((int)status is >= 300 and < 400)
                response.Headers.Location = new Uri("https://attacker.example/");
            return Task.FromResult(response);
        }
    }
}
