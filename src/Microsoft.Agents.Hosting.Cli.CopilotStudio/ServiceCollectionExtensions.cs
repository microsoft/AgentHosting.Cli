using Microsoft.Agents.Hosting.Cli.CopilotStudio.Stubs;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using System.Reflection;
using Microsoft.Agents.ObjectModel.Abstractions;
using Microsoft.Agents.ObjectModel.Expressions;
using Microsoft.Agents.ObjectModel.PowerFx;
using Microsoft.Agents.ObjectModel.Telemetry;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.Agents.Platform.Content.Internal.Modules;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CliClusterCategory = Microsoft.Agents.Hosting.Cli.Core.Environments.CoreServicesClusterCategory;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Registers the Copilot Studio authoring and synchronization stack.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the sync library, its authoring-stack dependencies, and the adapters that bind them
    /// to the CLI's own authentication and output.
    /// </summary>
    /// <param name="services">The container to register into.</param>
    /// <param name="standardError">The stream sync progress is written to.</param>
    /// <param name="apiTarget">
    /// The Power Platform endpoints this host talks to. The adapters registered here reach Power
    /// Platform directly, so the cloud and ring are taken from the caller rather than assumed.
    /// </param>
    /// <param name="verboseProgress">Whether the sync library's progress and diagnostics are shown.</param>
    /// <returns>The same container, for chaining.</returns>
    public static IServiceCollection AddAgentHostCopilotStudio(this IServiceCollection services, TextWriter standardError, PowerPlatformApiTargetConfig apiTarget, bool verboseProgress = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentNullException.ThrowIfNull(apiTarget);

        services.TryAddSingleton(PowerPlatformCloudMap.ForTarget(apiTarget));
        services.TryAdd(ServiceDescriptor.Singleton(typeof(CliClusterCategory), (object)CoreServicesCluster.For(apiTarget)));

        services.AddLogging();
        ServiceRegistrations.AddServices(services);

        services.AddSingleton<IOperationLogger, NullOperationLogger>();
        services.AddSingleton<IFeatureConfigurationProvider, BaselineFeatureConfigurationProvider>();
        services.AddSingleton<IFeatureConfiguration, BaselineFeatureConfiguration>();
        services.AddSingleton<IAuthoringStatisticLogger, NoOperationAuthoringStatisticLogger>();
        services.AddSingleton<IConnectorDefinitionMetadataService, MinimalConnectorDefinitionMetadataService>();
        services.AddSingleton<IPluginEnrichmentService, PassThroughPluginEnrichmentService>();
        services.AddSingleton<IAIModelEnrichmentService, PassThroughModelEnrichmentService>();
        services.AddSingleton<ICloudFlowDefinitionEnrichementService, EmptyCloudFlowDefinitionEnrichementService>();
        services.AddSingleton<ISkillResourceContentLoader, NoSkillResourceContentLoader>();
        services.AddSingleton<IExpressionSyntaxAnalyzer>(_ => new PowerFxSyntaxAnalyzer(new BaselineFeatureConfiguration()));
        services.AddSingleton<IExpressionSyntaxAnalyzerProvider, PowerFxExpressionSyntaxAnalyzerProvider>();
        services.AddSingleton<IDataverseUserIdProvider, UnsupportedDataverseUserIdProvider>();

        services.AddSingleton<ISyncAuthProvider, AgentHostSyncAuthProvider>();
        services.AddSingleton<ISyncProgress>(_ => new StandardErrorSyncProgress(standardError, verboseProgress));
        services.AddSingleton<AgentHostDataverseHttpClientAccessor>();
        services.AddSingleton<IDataverseHttpClientAccessor>(provider => provider.GetRequiredService<AgentHostDataverseHttpClientAccessor>());

        services.AddSyncServices(userAgent: DataverseUserAgent(), isIslandPreauthorized: false);

        services.AddSingleton<CopilotChangeMapper>();
        services.AddSingleton<ICopilotFileSystem, SystemCopilotFileSystem>();
        services.AddSingleton<ICopilotScaffoldPort, EmbeddedTemplateScaffolder>();
        services.AddSingleton<IWorkflowScaffoldPort, EmbeddedWorkflowScaffolder>();
        services.AddSingleton<IWorkflowWorkspacePort, WorkflowWorkspaceAdapter>();
        services.AddSingleton<IWorkflowStorePort, WorkflowDataverseClient>();
        services.AddSingleton<IStandaloneWorkflowDataverseClient>(provider => (IStandaloneWorkflowDataverseClient)provider.GetRequiredService<ISyncDataverseClient>());
        services.AddSingleton<IStandaloneWorkflowSynchronizer>(provider => new StandaloneWorkflowSynchronizer(provider.GetRequiredService<IStandaloneWorkflowDataverseClient>()));
        services.AddSingleton<ICopilotWorkspaceAttachPort, WorkspaceReattachAdapter>();
        services.AddSingleton<ICopilotWorkspacePort, SyncWorkspaceAdapter>();
        services.AddSingleton<ICopilotCatalogPort, DataverseCatalogAdapter>();
        services.AddSingleton<DataverseWebApiClient>();
        services.AddSingleton<IAgentSharingPort>(provider => new IslandSharingAdapter(provider.GetRequiredService<ITokenProvider>(), provider.GetRequiredService<PowerPlatformCloudMap>()));
        services.AddSingleton<IAgentEvaluationPort>(provider => new MakerEvaluationAdapter(provider.GetRequiredService<ITokenProvider>(), provider.GetRequiredService<PowerPlatformCloudMap>()));
        services.AddSingleton<IEvaluationComponentStore, DataverseEvaluationComponentStore>();
        services.AddSingleton<IDirectoryPrincipalPort, DataversePrincipalAdapter>();
        services.AddSingleton<IConnectionManagementService>(provider => (IConnectionManagementService)provider.GetRequiredService<IWorkspaceSynchronizer>());
        services.AddSingleton<IWorkflowActivationService>(provider => (IWorkflowActivationService)provider.GetRequiredService<IWorkspaceSynchronizer>());
        services.AddSingleton<IConnectionCatalogClient>(_ => new PowerAppsClient(new HttpClient(TransportDefaults.CreateRedirectRejectingHandler()), DataverseUserAgent()));
        services.AddSingleton<SyncConnectionAdapter>();
        services.AddSingleton<ICopilotConnectionPort>(provider => provider.GetRequiredService<SyncConnectionAdapter>());
        services.AddSingleton<IConnectorCatalogPort>(provider => provider.GetRequiredService<SyncConnectionAdapter>());
        services.AddSingleton<IConnectionLifecyclePort>(provider => new PowerAppsConnectionAdapter(provider.GetRequiredService<ITokenProvider>(), provider.GetRequiredService<CliClusterCategory>()));

        return services;
    }

    private static string DataverseUserAgent()
    {
        var version = typeof(ServiceCollectionExtensions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return string.IsNullOrWhiteSpace(version) ? "AgentHost.Cli-unknown" : $"AgentHost.Cli-{version.Split('+')[0]}";
    }
}
