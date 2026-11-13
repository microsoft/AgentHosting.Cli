using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Extensions.DependencyInjection;
using CoreServicesClusterCategory = Microsoft.Agents.Hosting.Cli.Core.Environments.CoreServicesClusterCategory;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class CopilotCompositionTests
{
    [Theory]
    [InlineData(CloudInstance.UsNat)]
    [InlineData(CloudInstance.UsSec)]
    public async Task UnsupportedStudioTargetRegistersAndActivatesWithoutAcquiringCredentials(CloudInstance cloud)
    {
        var services = new ServiceCollection();
        var legacy = new RecordingTokenProvider();
        var credentials = new ProfileSyncResourceTests.Credentials();
        services.AddSingleton<ITokenProvider>(legacy);
        services.AddSingleton<IProfileCredentialProvider>(credentials);
        services.AddSingleton<Func<HttpMessageHandler>>(() => throw new InvalidOperationException("Network construction is forbidden."));
        var target = PowerPlatformApiTargetSelection.TargetFor(cloud, PowerPlatformApiRing.Production);
        services.AddAgentHostCopilotStudio(TextWriter.Null, target);
        var clusterResolutions = 0;
        services.AddSingleton(typeof(CoreServicesClusterCategory), _ =>
        {
            clusterResolutions++;
            return PowerAppsServiceTarget.ClusterFor(target);
        });
        using var provider = services.BuildServiceProvider();
        foreach (var type in new[]
        {
            typeof(ISyncAuthProvider), typeof(IWorkspaceSynchronizer), typeof(ICopilotWorkspacePort),
            typeof(ICopilotCatalogPort), typeof(ICopilotConnectionPort), typeof(IConnectorCatalogPort),
            typeof(IConnectionLifecyclePort), typeof(IIslandControlPlaneService), typeof(IWorkflowStorePort),
            typeof(IAgentSharingPort), typeof(IAgentEvaluationPort),
        })
            Assert.NotNull(provider.GetRequiredService(type));
        Assert.Equal(0, clusterResolutions);
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(credentials.Requests);

        using (var gateway = provider.GetRequiredService<AgentHostDataverseHttpClientAccessor>()
            .CreateGatewayClient(PowerPlatformCloudMap.ForTarget(target), () => provider.GetRequiredService<CoreServicesClusterCategory>()))
        {
            Assert.Equal(0, clusterResolutions);
            await Assert.ThrowsAsync<EndpointTrustException>(() => gateway.GetAsync(target.GlobalEndpoint));
        }
        Assert.Equal(1, clusterResolutions);
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(credentials.Requests);

        await Assert.ThrowsAsync<EndpointTrustException>(() => provider.GetRequiredService<ISyncAuthProvider>()
            .AcquireTokenAsync(new Uri("https://unverified.invalid/"), default));
        Assert.Empty(legacy.RequestedResources);

        var environment = new CopilotEnvironmentContext
        {
            EnvironmentId = "national", TenantId = EnvironmentFixtures.ContosoTenant,
            DataverseOrganization = new Uri("https://unverified.invalid/"),
        };
        await Assert.ThrowsAsync<EndpointTrustException>(() => provider.GetRequiredService<IConnectorCatalogPort>()
            .ListConnectorsAsync(environment, default));
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(credentials.Requests);

        var profile = new AuthProfile { ProfileId = "national", AccountKey = "principal", Username = "",
            TenantId = EnvironmentFixtures.ContosoTenant, Cloud = cloud };
        environment = environment with { Context = new(profile, target) };
        using var sharing = new IslandSharingAdapter(legacy, PowerPlatformCloudMap.ForTarget(target),
            () => throw new InvalidOperationException("Network construction is forbidden."), credentials);
        await Assert.ThrowsAsync<EndpointTrustException>(() => sharing.ReadAsync(environment, "agent", default));
        await Assert.ThrowsAsync<UserPrincipalRequiredException>(() => sharing.ReadAsync(
            environment with { Context = new(profile with { PrincipalType = AuthPrincipalType.ServicePrincipal }, target) }, "agent", default));
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(credentials.Requests);

        using var scope = DataverseOperationScope.Push(new(new Uri("https://unverified.invalid/"), profile.TenantId, new(profile, target)));
        await Assert.ThrowsAsync<EndpointTrustException>(() => provider.GetRequiredService<ISyncAuthProvider>()
            .AcquireTokenAsync(AgentHostSyncAuthProvider.IslandAudienceFor(CoreServicesClusterCategory.Prod), default));
        Assert.Empty(legacy.RequestedResources);
        Assert.Empty(credentials.Requests);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITokenProvider>(new RecordingTokenProvider());
        services.AddAgentHostCopilotStudio(TextWriter.Null, PowerPlatformApiRingSelection.Default);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
    }

    [Theory]
    [InlineData(typeof(ISyncAuthProvider))]
    [InlineData(typeof(ISyncProgress))]
    [InlineData(typeof(IWorkspaceSynchronizer))]
    [InlineData(typeof(IOperationContextProvider))]
    [InlineData(typeof(ICopilotWorkspacePort))]
    [InlineData(typeof(ICopilotCatalogPort))]
    [InlineData(typeof(ICopilotScaffoldPort))]
    [InlineData(typeof(ICopilotWorkspaceAttachPort))]
    [InlineData(typeof(IWorkspaceRetargetService))]
    [InlineData(typeof(ICopilotFileSystem))]
    [InlineData(typeof(IBotContentReader))]
    [InlineData(typeof(IBotCreationService))]
    [InlineData(typeof(ISyncComponentCollectionDataverseClient))]
    [InlineData(typeof(ICopilotConnectionPort))]
    [InlineData(typeof(IWorkflowStorePort))]
    [InlineData(typeof(IStandaloneWorkflowSynchronizer))]
    public void EveryServiceTheCommandsDependOn_Resolves(Type serviceType)
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService(serviceType));
    }

    [Fact]
    public void TheSyncStackResolvesWithoutAHostSuppliedLoggingProvider()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<IWorkspaceSynchronizer>());
    }
}
