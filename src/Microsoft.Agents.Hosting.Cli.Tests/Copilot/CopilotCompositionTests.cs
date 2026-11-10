using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class CopilotCompositionTests
{
    internal static ServiceProvider BuildProvider()
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
    [InlineData(typeof(IAgentSharingPort))]
    [InlineData(typeof(ICopilotScaffoldPort))]
    [InlineData(typeof(ICopilotComponentCollectionPort))]
    [InlineData(typeof(ICopilotWorkspaceAttachPort))]
    [InlineData(typeof(ICopilotWorkspaceConnectPort))]
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
