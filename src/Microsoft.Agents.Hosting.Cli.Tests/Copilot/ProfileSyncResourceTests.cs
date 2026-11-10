using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using CoreServicesClusterCategory = Microsoft.Agents.Hosting.Cli.Core.Environments.CoreServicesClusterCategory;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class ProfileSyncResourceTests
{
    private const string Tenant = "33333333-3333-3333-3333-333333333333";
    private const string DelegatedScope = "api://96ff4394-9197-43aa-b393-6a41652e21f8/user_impersonation";

    private static CopilotEnvironmentContext Environment(string name = "a", CloudInstance cloud = CloudInstance.Public,
        AuthPrincipalType mode = AuthPrincipalType.User, AuthCredentialMethod method = AuthCredentialMethod.Browser,
        PowerPlatformApiRing ring = PowerPlatformApiRing.Production)
    {
        var target = PowerPlatformApiTargetSelection.TargetFor(cloud, ring);
        return new()
        {
            EnvironmentId = name, TenantId = Tenant,
            DataverseOrganization = new Uri(cloud == CloudInstance.Public
                ? $"https://{name}.crm.dynamics.com/" : $"https://{name}.crm.microsoftdynamics.us/"),
            AgentManagementUrl = new Uri("https://gateway.island.powerapps.com/"),
            Context = new(new AuthProfile
            {
                ProfileId = name, Revision = 17, AccountKey = "home-" + name, Username = name,
                ClientId = PowerPlatformApiPermissions.AgentHostCliApplicationId, TenantId = Tenant,
                PrincipalType = mode, CredentialMethod = method, Cloud = cloud, Ring = ring,
            }, target),
        };
    }

    private static ServiceProvider Services(Credentials credentials, RecordingTokenProvider legacy, HttpMessageHandler handler) =>
        new ServiceCollection()
            .AddSingleton<IProfileCredentialProvider>(credentials)
            .AddSingleton<ITokenProvider>(legacy)
            .AddSingleton(PowerPlatformCloudMap.ForCloud(CloudInstance.Public))
            .AddSingleton(typeof(CoreServicesClusterCategory), CoreServicesClusterCategory.Prod)
            .AddSingleton<Func<HttpMessageHandler>>(() => handler)
            .BuildServiceProvider();

    [Theory]
    [InlineData(CloudInstance.Public, PowerPlatformApiRing.Production, AuthPrincipalType.User, "api://96ff4394-9197-43aa-b393-6a41652e21f8")]
    [InlineData(CloudInstance.Public, PowerPlatformApiRing.Production, AuthPrincipalType.ServicePrincipal, "api://96ff4394-9197-43aa-b393-6a41652e21f8")]
    [InlineData(CloudInstance.Public, PowerPlatformApiRing.Preprod, AuthPrincipalType.User, "api://a522f059-bb65-47c0-8934-7db6e5286414")]
    [InlineData(CloudInstance.Public, PowerPlatformApiRing.Test, AuthPrincipalType.User, "api://a522f059-bb65-47c0-8934-7db6e5286414")]
    [InlineData(CloudInstance.UsGov, PowerPlatformApiRing.Production, AuthPrincipalType.User, "api://9315aedd-209b-43b3-b149-2abff6a95d59")]
    [InlineData(CloudInstance.UsGovHigh, PowerPlatformApiRing.Production, AuthPrincipalType.User, "api://69c6e40c-465f-4154-987d-da5cba10734e")]
    [InlineData(CloudInstance.UsGovDod, PowerPlatformApiRing.Production, AuthPrincipalType.User, "api://bd4a9f18-e349-4c74-a6b7-65dd465ea9ab")]
    public async Task SdkIslandAudienceAcquiresExactCapturedResourceProfileAndTenant(
        CloudInstance cloud, PowerPlatformApiRing ring, AuthPrincipalType mode, string expectedResource)
    {
        var credentials = new Credentials();
        var legacy = new RecordingTokenProvider();
        var environment = Environment(cloud: cloud, ring: ring, mode: mode,
            method: mode == AuthPrincipalType.User ? AuthCredentialMethod.Browser : AuthCredentialMethod.ClientSecret);
        var audienceMethod = typeof(ISyncAuthProvider).Assembly
            .GetType("Microsoft.CopilotStudio.Sync.IslandControlPlaneService", throwOnError: true)!
            .GetMethod("GetTokenAudience", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var resource = (string)audienceMethod.Invoke(null,
            [SyncClusterCategory.For(CoreServicesCluster.For(environment.Context!.Target))])!;
        Assert.Equal(expectedResource, resource);
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));
        var auth = new AgentHostSyncAuthProvider(legacy, credentials);

        var failure = await Record.ExceptionAsync(() => auth.AcquireTokenAsync(new Uri(resource)));

        Assert.Null(failure);
        var request = Assert.Single(credentials.Requests);
        Assert.Same(environment.Context.Profile, request.Profile);
        Assert.Equal(Tenant, request.Tenant);
        Assert.Equal([resource + (mode == AuthPrincipalType.User ? "//.default" : "/.default")], request.Scopes);
        Assert.Empty(legacy.RequestedResources);
    }

    [Theory]
    [InlineData(CloudInstance.Public, PowerPlatformApiRing.Production, "api://arbitrary-resource")]
    [InlineData(CloudInstance.Public, PowerPlatformApiRing.Production, "api://96ff4394-9197-43aa-b393-6a41652e21f8/other-resource")]
    [InlineData(CloudInstance.Public, PowerPlatformApiRing.Production, "96ff4394-9197-43aa-b393-6a41652e21f8")]
    [InlineData(CloudInstance.Public, PowerPlatformApiRing.Production, "api://9315aedd-209b-43b3-b149-2abff6a95d59")]
    [InlineData(CloudInstance.UsGov, PowerPlatformApiRing.Production, "api://96ff4394-9197-43aa-b393-6a41652e21f8")]
    [InlineData(CloudInstance.Public, PowerPlatformApiRing.Preprod, "api://96ff4394-9197-43aa-b393-6a41652e21f8")]
    [InlineData(CloudInstance.Public, PowerPlatformApiRing.Production, "api://a522f059-bb65-47c0-8934-7db6e5286414")]
    [InlineData(CloudInstance.Public, PowerPlatformApiRing.Production, "https://gateway.island.powerapps.com/")]
    [InlineData(CloudInstance.China, PowerPlatformApiRing.Production, "api://96ff4394-9197-43aa-b393-6a41652e21f8")]
    [InlineData(CloudInstance.UsNat, PowerPlatformApiRing.Production, "api://96ff4394-9197-43aa-b393-6a41652e21f8")]
    [InlineData(CloudInstance.UsSec, PowerPlatformApiRing.Production, "api://96ff4394-9197-43aa-b393-6a41652e21f8")]
    public async Task SdkIslandRejectsUnverifiedOrDifferentTargetResourceBeforeAcquisition(
        CloudInstance cloud, PowerPlatformApiRing ring, string resource)
    {
        var credentials = new Credentials();
        var legacy = new RecordingTokenProvider();
        var environment = Environment(cloud: cloud, ring: ring);
        var organization = cloud switch
        {
            CloudInstance.China => new Uri("https://a.crm.dynamics.cn/"),
            CloudInstance.UsNat => new Uri("https://a.crm.dynamics.eaglex.ic.gov/"),
            CloudInstance.UsSec => new Uri("https://a.crm.dynamics.microsoft.scloud/"),
            _ => environment.DataverseOrganization,
        };
        using var scope = DataverseOperationScope.Push(new(organization, Tenant, environment.Context));

        await Assert.ThrowsAsync<EndpointTrustException>(() =>
            new AgentHostSyncAuthProvider(legacy, credentials).AcquireTokenAsync(new Uri(resource, UriKind.RelativeOrAbsolute)));

        Assert.Empty(credentials.Requests);
        Assert.Empty(legacy.RequestedResources);
    }

    [Fact]
    public async Task OverlappingHttpClientsKeepCapturedProfilesAndTenantsOnOneTransport()
    {
        var credentials = new Credentials { Pause = true };
        var legacy = new RecordingTokenProvider();
        using var handler = new TokenHandler();
        using var services = Services(credentials, legacy, handler);
        var auth = ActivatorUtilities.CreateInstance<AgentHostSyncAuthProvider>(services);
        using var accessor = new AgentHostDataverseHttpClientAccessor(auth, () => handler);
        Microsoft.CopilotStudio.Sync.Dataverse.ISyncDataverseClient sdk = new StubSolutionVersionsClient();
        var environmentA = Environment();
        var environmentB = Environment("b") with { TenantId = "77777777-7777-7777-7777-777777777777" };
        DataverseTargeting.Connect(sdk, accessor, environmentA);
        using var a = accessor.CreateClient();
        DataverseTargeting.Connect(sdk, accessor, environmentB);
        using var b = accessor.CreateClient();

        var requestA = a.GetAsync(environmentA.DataverseOrganization);
        var requestB = b.GetAsync(environmentB.DataverseOrganization);
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

        Assert.Empty(legacy.RequestedResources);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(credentials.Requests, r => ReferenceEquals(r.Profile, environmentA.Context!.Profile) && r.Tenant == environmentA.TenantId);
        Assert.Contains(credentials.Requests, r => ReferenceEquals(r.Profile, environmentB.Context!.Profile) && r.Tenant == environmentB.TenantId);
        Assert.Contains(handler.Requests, r => r.Host == "a.crm.dynamics.com" && r.Token == "profile-a");
        Assert.Contains(handler.Requests, r => r.Host == "b.crm.dynamics.com" && r.Token == "profile-b");
    }

    [Fact]
    public async Task OverlappingDataverseRequestsShareTheRealProfileProviderWithoutCleanupFailure()
    {
        using var h = new ProfileCredentialTests.Harness();
        var profile = await h.Provider.AuthenticateAsync(new(), default);
        h.Profiles.Items.Add(profile);
        await h.Provider.CompleteCreationAsync(profile, default);
        var environment = Environment() with { Context = new(profile, PowerPlatformApiTargetConfig.Public) };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Acquisition.BeforeAcquire = _ => { entered.TrySetResult(); return release.Task; };
        var legacy = new RecordingTokenProvider();
        using var handler = new TokenHandler();
        using var accessor = new AgentHostDataverseHttpClientAccessor(new AgentHostSyncAuthProvider(legacy, h.Provider), () => handler);
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));
        using var client = accessor.CreateClient();
        var first = client.GetAsync(environment.DataverseOrganization);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var second = client.GetAsync(environment.DataverseOrganization);
            release.TrySetResult();
            using var secondResponse = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        }
        finally
        {
            release.TrySetResult();
            using var firstResponse = await first;
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        }
        Assert.Equal(2, handler.Requests.Count);
        Assert.Empty(legacy.RequestedResources);
        Assert.Equal(0, h.Backend.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedSovereignContextRejectsPublicGatewayBeforeAnyAcquisition(bool evaluation)
    {
        var credentials = new Credentials();
        var legacy = new RecordingTokenProvider();
        using var handler = new TokenHandler();
        using var services = Services(credentials, legacy, handler);
        var environment = Environment(cloud: CloudInstance.UsGov);

        await Assert.ThrowsAsync<EndpointTrustException>(() => evaluation
            ? ActivatorUtilities.CreateInstance<MakerEvaluationAdapter>(services).ListRunsAsync(environment, "agent", 1, default)
            : (Task)ActivatorUtilities.CreateInstance<IslandSharingAdapter>(services).ReadAsync(environment, "agent", default));

        Assert.Empty(credentials.Requests);
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChinaIslandUserActionsAreRejectedBeforeAcquisitionOrTransport(bool evaluation)
    {
        var credentials = new Credentials();
        var legacy = new RecordingTokenProvider();
        using var handler = new TokenHandler();
        using var services = Services(credentials, legacy, handler);
        var environment = Environment(cloud: CloudInstance.China) with
        {
            DataverseOrganization = new Uri("https://a.crm.dynamics.cn/"),
            AgentManagementUrl = new Uri("https://gateway.island.powerapps.cn/"),
        };

        await Assert.ThrowsAsync<EndpointTrustException>(() => evaluation
            ? ActivatorUtilities.CreateInstance<MakerEvaluationAdapter>(services).ListRunsAsync(environment, "agent", 1, default)
            : (Task)ActivatorUtilities.CreateInstance<IslandSharingAdapter>(services).ReadAsync(environment, "agent", default));

        Assert.Empty(credentials.Requests);
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false, CloudInstance.Public)]
    [InlineData(true, CloudInstance.Public)]
    [InlineData(false, CloudInstance.China)]
    [InlineData(true, CloudInstance.China)]
    public async Task AppOnlyUserActionsAreRejectedBeforeAcquisitionOrTransport(bool evaluation, CloudInstance cloud)
    {
        var credentials = new Credentials();
        var legacy = new RecordingTokenProvider();
        using var handler = new TokenHandler();
        using var services = Services(credentials, legacy, handler);
        var environment = Environment(cloud: cloud, mode: AuthPrincipalType.ServicePrincipal, method: AuthCredentialMethod.ClientSecret);

        await Assert.ThrowsAsync<UserPrincipalRequiredException>(() => evaluation
            ? ActivatorUtilities.CreateInstance<MakerEvaluationAdapter>(services).ListRunsAsync(environment, "agent", 1, default)
            : (Task)ActivatorUtilities.CreateInstance<IslandSharingAdapter>(services).ReadAsync(environment, "agent", default));

        Assert.Empty(credentials.Requests);
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false, "https://gateway.island.powerapps.com/#fragment", false)]
    [InlineData(true, "https://gateway.island.powerapps.com/#fragment", false)]
    [InlineData(false, "https://gateway.island.powerapps.com/?query=value", false)]
    [InlineData(true, "https://gateway.island.powerapps.com/?query=value", false)]
    [InlineData(false, "https://gateway.island.powerapps.com/#fragment", true)]
    [InlineData(true, "https://gateway.island.powerapps.com/#fragment", true)]
    [InlineData(false, "https://gateway.island.powerapps.com/?query=value", true)]
    [InlineData(true, "https://gateway.island.powerapps.com/?query=value", true)]
    public async Task IslandRejectsMalformedAdvertisedGatewayBeforeAcquisition(bool evaluation, string destination, bool legacyContext)
    {
        var credentials = new Credentials();
        var legacy = new RecordingTokenProvider();
        using var handler = new TokenHandler();
        using var services = Services(credentials, legacy, handler);
        var environment = Environment() with { AgentManagementUrl = new Uri(destination) };
        if (legacyContext)
            environment = environment with { Context = null };

        await Assert.ThrowsAsync<EndpointTrustException>(() => evaluation
            ? ActivatorUtilities.CreateInstance<MakerEvaluationAdapter>(services).ListRunsAsync(environment, "agent", 1, default)
            : (Task)ActivatorUtilities.CreateInstance<IslandSharingAdapter>(services).ReadAsync(environment, "agent", default));

        Assert.Empty(credentials.Requests);
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CapturedDataverseRejectsRequestFragmentBeforeAcquisition()
    {
        var credentials = new Credentials();
        var legacy = new RecordingTokenProvider();
        using var handler = new TokenHandler();
        using var accessor = new AgentHostDataverseHttpClientAccessor(new AgentHostSyncAuthProvider(legacy, credentials), () => handler);
        var environment = Environment();
        using var client = accessor.CreateClient(environment);

        await Assert.ThrowsAsync<EndpointTrustException>(() =>
            client.GetAsync(new Uri(environment.DataverseOrganization, "api/data/v9.2/items#fragment")));

        Assert.Empty(credentials.Requests);
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CapturedChinaDataverseKeepsItsSupportedBoundCredentialPath()
    {
        var credentials = new Credentials();
        var legacy = new RecordingTokenProvider();
        using var handler = new TokenHandler();
        using var accessor = new AgentHostDataverseHttpClientAccessor(new AgentHostSyncAuthProvider(legacy, credentials), () => handler);
        var environment = Environment(cloud: CloudInstance.China) with
        {
            DataverseOrganization = new Uri("https://a.crm.dynamics.cn/"),
        };
        using var client = accessor.CreateClient(environment);

        using var response = await client.GetAsync(new Uri(environment.DataverseOrganization, "api/data/v9.2/items?$select=name"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var acquisition = Assert.Single(credentials.Requests);
        Assert.Same(environment.Context!.Profile, acquisition.Profile);
        Assert.Equal(Tenant, acquisition.Tenant);
        Assert.Equal(["https://a.crm.dynamics.cn//.default"], acquisition.Scopes);
        Assert.Equal("a.crm.dynamics.cn", Assert.Single(handler.Requests).Host);
        Assert.Empty(legacy.RequestedResources);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SovereignUserRequestKeepsExactDelegatedScopeAndProfile(bool evaluation)
    {
        var credentials = new Credentials();
        var legacy = new RecordingTokenProvider();
        using var handler = new TokenHandler();
        using var services = Services(credentials, legacy, handler);
        var environment = Environment(cloud: CloudInstance.UsGov) with
        {
            AgentManagementUrl = new Uri("https://gateway.island.powerapps.us/"),
        };
        if (evaluation)
            await ActivatorUtilities.CreateInstance<MakerEvaluationAdapter>(services).ListRunsAsync(environment, "agent", 1, default);
        else
            await ActivatorUtilities.CreateInstance<IslandSharingAdapter>(services).ReadAsync(environment, "agent", default);

        var request = Assert.Single(credentials.Requests);
        Assert.Same(environment.Context!.Profile, request.Profile);
        Assert.Equal([DelegatedScope], request.Scopes);
        Assert.Empty(legacy.RequestedResources);
        Assert.Equal("gateway.island.powerapps.us", Assert.Single(handler.Requests).Host);
    }

    [Theory]
    [InlineData(CloudInstance.Public, PowerPlatformApiRing.Production, "https://api.bap.microsoft.com//.default", "api.powerapps.com")]
    [InlineData(CloudInstance.Public, PowerPlatformApiRing.Preprod, "https://api.bap.microsoft.com//.default", "api.powerapps.com")]
    [InlineData(CloudInstance.Public, PowerPlatformApiRing.Test, "https://api.bap.microsoft.com//.default", "api.powerapps.com")]
    [InlineData(CloudInstance.UsGov, PowerPlatformApiRing.Production, "https://gov.service.powerapps.us//.default", "gov.api.powerapps.us")]
    [InlineData(CloudInstance.UsGovHigh, PowerPlatformApiRing.Production, "https://high.service.powerapps.us//.default", "high.api.powerapps.us")]
    [InlineData(CloudInstance.UsGovDod, PowerPlatformApiRing.Production, "https://service.apps.appsplatform.us//.default", "api.apps.appsplatform.us")]
    [InlineData(CloudInstance.China, PowerPlatformApiRing.Production, "https://service.powerapps.cn//.default", "api.powerapps.cn")]
    public async Task PowerAppsDeleteSeparatesCapturedServiceAudienceFromApiDestination(
        CloudInstance cloud, PowerPlatformApiRing ring, string expectedScope, string expectedHost)
    {
        var credentials = new Credentials();
        var legacy = new RecordingTokenProvider();
        using var handler = new TokenHandler();
        using var services = Services(credentials, legacy, handler);
        using var adapter = ActivatorUtilities.CreateInstance<PowerAppsConnectionAdapter>(services);
        var environment = Environment(cloud: cloud, ring: ring);

        await adapter.DeleteConnectionAsync(environment, "connector", "connection", default);

        var request = Assert.Single(credentials.Requests);
        Assert.Same(environment.Context!.Profile, request.Profile);
        Assert.Equal([expectedScope], request.Scopes);
        Assert.Empty(legacy.RequestedResources);
        Assert.Equal(expectedHost, Assert.Single(handler.Requests).Host);
    }

    [Fact]
    public async Task DataverseRejectsWrongCapturedCloudBeforeAcquisition()
    {
        var credentials = new Credentials();
        var legacy = new RecordingTokenProvider();
        using var handler = new TokenHandler();
        using var services = Services(credentials, legacy, handler);
        using var accessor = new AgentHostDataverseHttpClientAccessor(
            ActivatorUtilities.CreateInstance<AgentHostSyncAuthProvider>(services), () => handler);
        var environment = Environment(cloud: CloudInstance.UsGov) with
        {
            DataverseOrganization = new Uri("https://a.crm.dynamics.com/"),
        };
        await Assert.ThrowsAsync<EndpointTrustException>(() => new DataverseWebApiClient(new StubSolutionVersionsClient(), accessor)
            .SendAsync(environment, HttpMethod.Get, "api/data/v9.2/bots", default));
        Assert.Empty(credentials.Requests);
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(CloudInstance.UsNat)]
    [InlineData(CloudInstance.UsSec)]
    public async Task UnverifiedPowerAppsConsentDestinationFailsBeforeAcquisition(CloudInstance cloud)
    {
        var credentials = new Credentials();
        var legacy = new RecordingTokenProvider();
        using var handler = new TokenHandler();
        using var client = new PowerAppsConnectionClient(legacy, CoreServicesClusterCategory.Prod, () => handler,
            profileCredentials: credentials);

        await Assert.ThrowsAsync<EndpointTrustException>(() =>
            client.ConfirmConsentCodeAsync(Environment(cloud: cloud), "connector", "connection", "synthetic-code", default));

        Assert.Empty(credentials.Requests);
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(CloudInstance.UsNat)]
    [InlineData(CloudInstance.UsSec)]
    public async Task UnverifiedComposedClusterFailsOnUseWithTypedTrustError(CloudInstance cloud)
    {
        var legacy = new RecordingTokenProvider();
        var credentials = new Credentials();
        using var services = new ServiceCollection()
            .AddSingleton<ITokenProvider>(legacy)
            .AddSingleton<IProfileCredentialProvider>(credentials)
            .AddAgentHostCopilotStudio(TextWriter.Null, PowerPlatformApiTargetSelection.TargetFor(cloud))
            .BuildServiceProvider();
        await Assert.ThrowsAsync<EndpointTrustException>(() => services.GetRequiredService<IConnectorCatalogPort>()
            .ListConnectorsAsync(Environment(cloud: cloud), default));
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(credentials.Requests);
    }

    [Fact]
    public async Task CopilotCompositionUsesOptionalCapturedCredentialsAndOneSdkResourceAcrossAliases()
    {
        var credentials = new Credentials();
        var legacy = new RecordingTokenProvider();
        using var services = new ServiceCollection()
            .AddSingleton<ITokenProvider>(legacy)
            .AddSingleton<IProfileCredentialProvider>(credentials)
            .AddAgentHostCopilotStudio(TextWriter.Null, PowerPlatformApiTargetConfig.Public)
            .BuildServiceProvider();
        var environment = Environment();
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, environment.TenantId, environment.Context));

        await services.GetRequiredService<ISyncAuthProvider>().AcquireTokenAsync(environment.DataverseOrganization);
        Assert.Same(environment.Context!.Profile, Assert.Single(credentials.Requests).Profile);
        Assert.Empty(legacy.RequestedResources);
        var sdk = services.GetRequiredService<ISyncDataverseClient>();
        var workflowSdk = services.GetRequiredService<IStandaloneWorkflowDataverseClient>();
        Assert.Same(sdk, workflowSdk);
        Assert.Same(sdk, services.GetRequiredService<ISyncComponentCollectionDataverseClient>());
        using var owner = await DataverseTargeting.EnterAsync(sdk, default);
        using var cancelWaiter = new CancellationTokenSource();
        var waiter = DataverseTargeting.EnterAsync(workflowSdk, cancelWaiter.Token);
        Assert.False(waiter.IsCompleted);
        cancelWaiter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
    }

    [Fact]
    public async Task CapturedContextWithoutCredentialServiceNeverFallsBackToLegacyTokens()
    {
        var legacy = new RecordingTokenProvider();
        using var handler = new TokenHandler();
        using var sharing = new IslandSharingAdapter(legacy, PowerPlatformCloudMap.ForCloud(CloudInstance.Public), () => handler);
        using var powerApps = new PowerAppsConnectionAdapter(legacy, CoreServicesClusterCategory.Prod, () => handler);
        using var accessor = new AgentHostDataverseHttpClientAccessor(new AgentHostSyncAuthProvider(legacy), () => handler);
        var environment = Environment();
        using var http = accessor.CreateClient(environment);

        await Assert.ThrowsAsync<TokenClaimException>(() => sharing.ReadAsync(environment, "agent", default));
        await Assert.ThrowsAsync<TokenClaimException>(() => powerApps.DeleteConnectionAsync(environment, "connector", "connection", default));
        await Assert.ThrowsAsync<TokenClaimException>(() => http.GetAsync(environment.DataverseOrganization));

        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("dataverse")]
    [InlineData("sharing")]
    [InlineData("powerapps")]
    [InlineData("island")]
    public async Task CapturedAcquisitionRejectsForeignTenantBeforeSending(string consumer)
    {
        var credentials = new Credentials { AcquiredTenant = "77777777-7777-7777-7777-777777777777" };
        var legacy = new RecordingTokenProvider();
        using var handler = new TokenHandler();
        using var sharing = new IslandSharingAdapter(legacy, PowerPlatformCloudMap.ForCloud(CloudInstance.Public), () => handler, credentials);
        using var powerApps = new PowerAppsConnectionAdapter(legacy, CoreServicesClusterCategory.Prod, () => handler, credentials);
        using var accessor = new AgentHostDataverseHttpClientAccessor(new AgentHostSyncAuthProvider(legacy, credentials), () => handler);
        var environment = Environment();
        using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));
        using var http = accessor.CreateClient(environment);

        var error = await Assert.ThrowsAsync<TokenClaimException>(() => consumer switch
        {
            "dataverse" => http.GetAsync(environment.DataverseOrganization),
            "sharing" => (Task)sharing.ReadAsync(environment, "agent", default),
            "island" => new AgentHostSyncAuthProvider(legacy, credentials)
                .AcquireTokenAsync(new Uri("api://96ff4394-9197-43aa-b393-6a41652e21f8")),
            _ => powerApps.DeleteConnectionAsync(environment, "connector", "connection", default),
        });

        Assert.Equal(TokenClaimFailure.WrongTenant, error.Failure);
        Assert.Single(credentials.Requests);
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PowerAppsCachedTokenCannotCrossCapturedProfileRevision()
    {
        var credentials = new Credentials();
        var legacy = new RecordingTokenProvider();
        using var handler = new TokenHandler();
        using var adapter = new PowerAppsConnectionAdapter(legacy, CoreServicesClusterCategory.Prod, () => handler, credentials);
        var environment = Environment();

        await adapter.DeleteConnectionAsync(environment, "connector", "connection", default);
        await adapter.DeleteConnectionAsync(environment, "connector", "connection", default);
        var replacement = environment with
        {
            Context = environment.Context! with { Profile = environment.Context!.Profile with { Revision = 18 } },
        };
        await adapter.DeleteConnectionAsync(replacement, "connector", "connection", default);

        Assert.Equal(2, credentials.Requests.Count);
        Assert.Equal([17L, 18L], credentials.Requests.Select(r => r.Profile.Revision));
        Assert.Empty(legacy.RequestedResources);
    }

    [Fact]
    public async Task SyncCatalogUsesCapturedAudienceClusterAndNoPowerAppsTokenForReferences()
    {
        var credentials = new Credentials();
        var legacy = new RecordingTokenProvider();
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => new TokenHandler());
        var sdk = new RecordingConnectionReferenceClient();
        var catalog = new CatalogClient();
        var adapter = new SyncConnectionAdapter(new RecordingConnectionManagementService(),
            new RecordingWorkflowActivationService(), new RecordingWorkspaceSynchronizer(), sdk, catalog, legacy,
            accessor, new WorkspaceFileAccessorFactory(), new DataverseWebApiClient(sdk, accessor),
            CoreServicesClusterCategory.Prod, credentials);
        var environment = Environment(cloud: CloudInstance.UsGov);
        var workspace = Path.Combine(System.Environment.CurrentDirectory, "nonexistent-profile-sync-workspace");

        await adapter.ListConnectorsAsync(environment, default);
        Assert.Equal(["https://gov.service.powerapps.us//.default"], Assert.Single(credentials.Requests).Scopes);
        Assert.Equal(SyncClusterCategory.For(CoreServicesClusterCategory.Gov), catalog.LastContext!.ClusterCategory);
        credentials.Requests.Clear();

        await adapter.CreateConnectionReferenceAsync(environment, workspace, "shared_teams", default);
        await adapter.ReadDeclaredConnectorsAsync(environment, workspace, default);
        await adapter.RemoveConnectionReferenceAsync(environment, workspace, "cr_teams", confirmed: false, default);

        Assert.Empty(credentials.Requests);
        Assert.Empty(legacy.RequestedResources);
    }

    [Theory]
    [InlineData(AuthCredentialMethod.Browser, "https://a.crm.dynamics.com")]
    [InlineData(AuthCredentialMethod.ClientSecret, "https://a.crm.dynamics.com")]
    [InlineData(AuthCredentialMethod.DefaultAzureCredential, "https://a.crm.dynamics.com")]
    [InlineData(AuthCredentialMethod.Browser, "api://96ff4394-9197-43aa-b393-6a41652e21f8")]
    [InlineData(AuthCredentialMethod.ClientSecret, "api://96ff4394-9197-43aa-b393-6a41652e21f8")]
    [InlineData(AuthCredentialMethod.DefaultAzureCredential, "api://96ff4394-9197-43aa-b393-6a41652e21f8")]
    public async Task RealCredentialProviderBindsGenericResourceWithoutPpapiValidation(AuthCredentialMethod method, string resource)
    {
        using var h = new ProfileCredentialTests.Harness();
        if (method != AuthCredentialMethod.Browser)
            h.Acquisition.Next = new()
            {
                AccessToken = ProfileCredentialTests.Jwt(new
                {
                    aud = resource, tid = Tenant, appid = ProfileCredentialTests.App, oid = ProfileCredentialTests.ObjectId,
                    idtyp = method == AuthCredentialMethod.ClientSecret ? "app" : "user",
                    scp = method == AuthCredentialMethod.ClientSecret ? null : "user_impersonation",
                }),
                TenantId = Tenant, ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
                AzureSource = method == AuthCredentialMethod.DefaultAzureCredential ? ProfileAzureSourceId.AzureCli : null,
            };
        var profile = await h.Provider.AuthenticateAsync(new()
        {
            Tenant = Tenant, ApplicationId = method == AuthCredentialMethod.ClientSecret ? ProfileCredentialTests.App : null,
            ClientSecret = method == AuthCredentialMethod.ClientSecret ? "synthetic-owned-secret" : null,
            ManagedIdentity = method == AuthCredentialMethod.DefaultAzureCredential,
            Scopes = [resource + "/.default"],
        }, default);
        h.Profiles.Items.Add(profile);
        await h.Provider.CompleteCreationAsync(profile, default);
        h.Profiles.Items.Clear();
        h.Profiles.Items.Add(Environment("later-active").Context!.Profile with { IsActive = true });
        var environment = Environment() with { Context = new(profile, PowerPlatformApiTargetConfig.Public) };
        var legacy = new RecordingTokenProvider();
        using var handler = new TokenHandler();
        var auth = new AgentHostSyncAuthProvider(legacy, h.Provider);
        using var accessor = new AgentHostDataverseHttpClientAccessor(auth, () => handler);
        using var client = accessor.CreateClient(environment);
        var island = resource.StartsWith("api://", StringComparison.Ordinal);

        try
        {
            if (island)
            {
                using var scope = DataverseOperationScope.Push(new(environment.DataverseOrganization, Tenant, environment.Context));
                Assert.NotEmpty(await auth.AcquireTokenAsync(new Uri(resource)));
            }
            else
            {
                using var response = await client.GetAsync(environment.DataverseOrganization);
            }
            Assert.Equal(2, h.Acquisition.Requests.Count);
            var renewal = h.Acquisition.Requests.Last();
            Assert.Equal(profile.ClientId, renewal.ClientId.ToString());
            Assert.Equal(profile.HomeAccountId, renewal.HomeAccountId);
            Assert.Equal(Tenant, renewal.Authority.TenantId!.Value.ToString());
            Assert.Equal([resource + (method == AuthCredentialMethod.Browser ? "//.default" : "/.default")], renewal.Scopes);
            Assert.False(renewal.IsCreation);
            Assert.Empty(legacy.RequestedResources);

            if (method == AuthCredentialMethod.DefaultAzureCredential)
            {
                Assert.Equal(AuthPrincipalType.User, profile.PrincipalType);
                var backendCalls = h.Backend.Calls;
                using var sharing = new IslandSharingAdapter(legacy, PowerPlatformCloudMap.ForCloud(CloudInstance.Public), () => handler, h.Provider);
                await Assert.ThrowsAsync<UsageException>(() => sharing.ReadAsync(environment, "agent", default));
                Assert.Equal(2, h.Acquisition.Requests.Count);
                Assert.Equal(backendCalls, h.Backend.Calls);
                Assert.Equal(island ? 0 : 1, handler.Requests.Count);
            }
        }
        finally
        {
            h.Profiles.Items.Clear();
            await h.Provider.RemoveOwnedCredentialAsync(profile, default);
        }
    }

    internal sealed class Credentials : IProfileCredentialProvider
    {
        internal sealed record Request(AuthProfile Profile, IReadOnlyList<string> Scopes, string? Tenant);
        internal ConcurrentQueue<Request> Requests { get; } = new();
        internal bool Pause { get; init; }
        internal string? AcquiredTenant { get; init; }
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TokenAcquisitionResult> AcquireAsync(AuthProfile profile, IReadOnlyList<string> scopes,
            string? targetTenantId, CancellationToken ct)
        {
            Requests.Enqueue(new(profile, scopes, targetTenantId));
            if (Pause)
                await Release.Task.WaitAsync(ct);
            return new() { AccessToken = "profile-" + profile.ProfileId, TenantId = AcquiredTenant ?? targetTenantId, ExpiresOn = DateTimeOffset.UtcNow.AddHours(1) };
        }
        public Task<AuthProfile> AuthenticateAsync(AuthCreateRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteCreationAsync(AuthProfile profile, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveOwnedCredentialAsync(AuthProfile profile, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CatalogClient : IConnectionCatalogClient
    {
        internal PowerAppsContext? LastContext { get; private set; }
        public Task<IReadOnlyList<ConnectorInfo>> ListConnectorsAsync(PowerAppsContext context, CancellationToken cancellationToken)
        {
            LastContext = context;
            return Task.FromResult<IReadOnlyList<ConnectorInfo>>([]);
        }
        public Task<IReadOnlyList<ConnectionInstance>> ListConnectionsAsync(PowerAppsContext context, string connectorName, CancellationToken cancellationToken)
        {
            LastContext = context;
            return Task.FromResult<IReadOnlyList<ConnectionInstance>>([]);
        }
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        internal ConcurrentQueue<(string Host, string? Token)> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.RequestUri!.Host, request.Headers.Authorization?.Parameter));
            var body = request.RequestUri.AbsolutePath.Contains("makerevaluations", StringComparison.Ordinal) ? "[]"
                : """{"shareWithOrg":false,"storeDiscoverable":false,"enableActivitySharing":false,"targets":[]}""";
            return Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
    }
}
