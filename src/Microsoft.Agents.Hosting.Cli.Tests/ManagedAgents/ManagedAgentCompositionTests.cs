using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

/// <summary>
/// Proves the managed-agent command services resolve from the same registrations the process entry
/// point uses. A missing registration only surfaces at runtime otherwise, and every one of these
/// verbs is reached by a single command-line invocation with no earlier chance to fail.
/// </summary>
public sealed class ManagedAgentCompositionTests
{
    private static ServiceProvider BuildProvider(PowerPlatformApiTargetConfig? apiTarget = null)
    {
        apiTarget ??= PowerPlatformApiTargetConfig.Public;
        var services = new ServiceCollection();

        services.AddSingleton<IClock>(new FixedClock(ManagedAgentFixtures.Now));
        services.AddSingleton<ITokenProvider>(new RecordingTokenProvider());
        services.AddAgentHostCopilotStudio(TextWriter.Null, apiTarget);

        services.AddSingleton<IEnvironmentClient>(new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization)));
        services.AddSingleton<IEnvironmentSelectionStore, InMemoryEnvironmentSelectionStore>();
        services.AddSingleton<IAuthProfileStore>(InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));
        services.AddSingleton<EnvironmentSelectionService>();

        services.AddSingleton<CopilotSchemaName>();
        services.AddSingleton<CopilotEnvironmentResolver>();
        services.AddSingleton<CopilotWorkspaceLocator>();
        services.AddSingleton<CopilotCatalogService>();
        services.AddSingleton<CopilotWorkspaceService>();
        services.AddSingleton<CopilotScaffoldService>();

        services.AddSingleton<AgentConfigLocator>();
        services.AddSingleton<CopilotProjectIdentityReader>();
        services.AddSingleton<JsonAgentConfigStore>();
        services.AddSingleton<IManagedAgentRegistry>(sp => sp.GetRequiredService<JsonAgentConfigStore>());
        services.AddSingleton<IAgentDeploymentRegistry>(sp => sp.GetRequiredService<JsonAgentConfigStore>());
        services.AddSingleton<IConfirmationPrompt>(new ScriptedConfirmationPrompt());
        services.AddSingleton<ManagedAgentPrincipalResolver>();
        services.AddSingleton<ManagedAgentResolver>();
        services.AddSingleton<ManagedAgentCloneMaterialiser>();
        services.AddSingleton<HomeDeploymentRegistrar>();
        services.AddSingleton<ManagedAgentCreateService>();
        services.AddSingleton<ManagedAgentInitService>();
        services.AddSingleton<ManagedAgentListService>();
        services.AddSingleton<ManagedAgentShowService>();
        services.AddSingleton<ManagedAgentDeleteService>();
        services.AddSingleton<IAgentSharingPort>(new FakeAgentSharingPort());
        services.AddSingleton<IDirectoryPrincipalPort>(new FakeDirectoryPrincipalPort());
        services.AddSingleton<ITenantUserDirectoryPort>(new FakeTenantUserDirectoryPort());
        services.AddSingleton<ManagedAgentShareService>();
        services.AddSingleton<ManagedAgentPullService>();
        services.AddSingleton<ManagedAgentDeployService>();
        services.AddSingleton<AgentDeploymentCreateService>();
        services.AddSingleton<AgentDeploymentListService>();
        services.AddSingleton<AgentDeploymentShowService>();
        services.AddSingleton<AgentDeploymentUpdateService>();
        services.AddSingleton<AgentDeploymentDeleteService>();
        services.AddSingleton<AgentDeploymentConnectionResolver>();
        services.AddSingleton<ConnectionTargetResolver>();
        services.AddSingleton<WorkflowStateCache>();
        services.AddSingleton<WorkflowActivationPass>();
        services.AddSingleton<IConnectionSelectionPrompt>(_ => new ScriptedSelectionPrompt());
        services.AddSingleton(_ => new ConnectionCreationFlow(ManagedAgentFixtures.ConnectorReadiness(), new ScriptedConsentFlow()));
        services.AddSingleton<ConnectionBindingCoordinator>();
        services.AddSingleton(sp => AgentDeploymentConnectionService.CreateDeferred(
            sp.GetRequiredService<AgentDeploymentConnectionResolver>(),
            sp.GetRequiredService<ICopilotConnectionPort>(),
            sp.GetRequiredService<ConnectionBindingCoordinator>(),
            sp.GetRequiredService<IConnectionSelectionPrompt>(),
            sp.GetRequiredService<WorkflowStateCache>(),
            sp.GetRequiredService<ManagedAgentPrincipalResolver>(),
            () => PowerAppsServiceTarget.ClusterFor(apiTarget)));
        services.AddSingleton(sp => AgentDeploymentConnectionReader.CreateDeferred(
            sp.GetRequiredService<AgentDeploymentConnectionResolver>(),
            sp.GetRequiredService<ICopilotConnectionPort>(),
            sp.GetRequiredService<WorkflowStateCache>(),
            () => PowerAppsServiceTarget.ClusterFor(apiTarget)));
        services.AddSingleton<AgentDeploymentConnectorListService>();
        services.AddSingleton<ConnectorResolver>();
        services.AddSingleton(sp => new AgentDeploymentConnectionCreateService(
            sp.GetRequiredService<ConnectionTargetResolver>(),
            sp.GetRequiredService<ConnectorResolver>(),
            sp.GetRequiredService<ConnectionCreationFlow>(),
            sp.GetRequiredService<ManagedAgentPrincipalResolver>()));
        services.AddSingleton(sp => new AgentDeploymentConnectionReferenceCreateService(
            sp.GetRequiredService<AgentDeploymentConnectionResolver>(),
            sp.GetRequiredService<ConnectorResolver>(),
            sp.GetRequiredService<ICopilotConnectionPort>(),
            sp.GetRequiredService<ConnectionBindingCoordinator>(),
            sp.GetRequiredService<ManagedAgentPrincipalResolver>()));
        services.AddSingleton<IConnectionLifecyclePort, FakeConnectionLifecyclePort>();
        services.AddSingleton<AgentDeploymentConnectionRemoveService>();
        services.AddSingleton<DeploymentAgentTypeResolver>();
        services.AddSingleton<IDeploymentStore, ProjectDeploymentStore>();
        services.AddSingleton<IWorkflowStorePort, FakeWorkflowStorePort>();
        services.AddSingleton<IWorkflowScaffoldPort, FakeWorkflowScaffoldPort>();
        services.AddSingleton<WorkflowProjectResolver>();
        services.AddSingleton<WorkflowCreateService>();
        services.AddSingleton<WorkflowInitService>();
        services.AddSingleton<WorkflowDeployService>();
        services.AddSingleton<WorkflowPullService>();
        services.AddSingleton<WorkflowDeleteService>();
        services.AddSingleton<ManagedAgentServices>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
    }

    [Theory]
    [InlineData(CloudInstance.UsNat)]
    [InlineData(CloudInstance.UsSec)]
    public void NationalTargetDoesNotResolveStudioClustersWhenActivatingTheFacadeDependencyGraph(CloudInstance cloud)
    {
        using var provider = BuildProvider(PowerPlatformApiTargetSelection.TargetFor(cloud, PowerPlatformApiRing.Production));
        Assert.NotNull(provider.GetRequiredService<ManagedAgentServices>());
    }

    [Theory]
    [InlineData(typeof(ManagedAgentServices))]
    [InlineData(typeof(ManagedAgentCreateService))]
    [InlineData(typeof(ManagedAgentInitService))]
    [InlineData(typeof(ManagedAgentListService))]
    [InlineData(typeof(ManagedAgentDeleteService))]
    [InlineData(typeof(ManagedAgentShareService))]
    [InlineData(typeof(ManagedAgentPullService))]
    [InlineData(typeof(ManagedAgentDeployService))]
    [InlineData(typeof(AgentDeploymentCreateService))]
    [InlineData(typeof(AgentDeploymentListService))]
    [InlineData(typeof(AgentDeploymentShowService))]
    [InlineData(typeof(AgentDeploymentUpdateService))]
    [InlineData(typeof(AgentDeploymentDeleteService))]
    [InlineData(typeof(AgentDeploymentConnectionService))]
    [InlineData(typeof(DeploymentAgentTypeResolver))]
    [InlineData(typeof(IManagedAgentRegistry))]
    [InlineData(typeof(IAgentDeploymentRegistry))]
    [InlineData(typeof(IDeploymentStore))]
    public void EveryManagedAgentServiceResolves(Type serviceType)
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService(serviceType));
    }

    [Fact]
    public void BothRegistryPortsShareOneStore()
    {
        using var provider = BuildProvider();

        Assert.Same(provider.GetRequiredService<IManagedAgentRegistry>(), provider.GetRequiredService<IAgentDeploymentRegistry>());
    }
}
