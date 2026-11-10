using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Rendering;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsModeTests
{
    private static readonly Type[] StepHandlers =
    [
        typeof(ICommandHandler<ManagedAgentInitRequest, ManagedAgentInitResult>),
        typeof(ICommandHandler<ManagedAgentDeployRequest, ManagedAgentDeployResult>),
        typeof(ICommandHandler<ManagedAgentShowRequest, ManagedAgentShowResult>),
        typeof(ICommandHandler<AgentDeploymentListRequest, AgentDeploymentListResult>),
        typeof(ICommandHandler<AgentDeploymentShowRequest, AgentDeploymentShowResult>),
    ];

    private static readonly Type[] AmsCommandHandlers =
    [
        typeof(ICommandHandler<McsAmsAgentDeployRequest, McsAmsAgentDeployResult>),
        typeof(ICommandHandler<McsAmsAgentListRequest, ManagedAgentListResult>),
        typeof(ICommandHandler<McsAmsAgentCaptureRequest, McsAmsAgentCaptureResult>),
        typeof(ICommandHandler<McsAmsOperationShowRequest, McsAmsOperationShowResult>),
    ];

    [Fact]
    public void AddAmsMode_WithTheFlagOff_RegistersNothing()
    {
        var services = new ServiceCollection();

        Program.AddAmsMode(services, amsMcsAgents: false, PowerPlatformApiTargetConfig.Public);

        Assert.Empty(services);
    }

    [Fact]
    public void AddAmsMode_WithTheFlagOn_RegistersTheHooksTheStepsAndTheAmsCommandHandlers()
    {
        var services = new ServiceCollection().AddAgentHostCopilotStudio(TextWriter.Null, PowerPlatformApiTargetConfig.Public);

        Program.AddAmsMode(services, amsMcsAgents: true, PowerPlatformApiTargetConfig.Public);

        Assert.All([typeof(IAgentRecordMirror), typeof(IAgentCreationHook), typeof(IWorkspaceDisconnectedRemediation), .. StepHandlers, .. AmsCommandHandlers], type => Assert.Contains(services, descriptor => descriptor.ServiceType == type));
    }

    [Fact]
    public void AmsStepHandlers_SupplyTheFiveStepsAndNothingElse()
    {
        var handlers = new AmsStepHandlers(new EchoingProvider());

        Assert.All(StepHandlers, type => Assert.Same(type, handlers.GetService(type)));
        Assert.All([.. AmsCommandHandlers, typeof(ICommandHandler<ManagedAgentCreateRequest, ManagedAgentCreateResult>)], type => Assert.Null(handlers.GetService(type)));
        Assert.Throws<ArgumentNullException>(() => new AmsStepHandlers(null!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handlers_PutTheAmsStepsAheadOfMainsHandlers_OnlyWithTheFlagOn(bool amsMcsAgents)
    {
        var operations = new FakeOperations { OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult { AgentType = AgentTypes.McsAgent, Source = DeploymentAgentTypeSource.DeploymentRecord } };
        var step = new RecordingHandler<ManagedAgentDeployRequest, ManagedAgentDeployResult>(await new FakeOperations().DeployManagedAgentAsync(new ManagedAgentDeployRequest(), default));
        await using var container = new ServiceCollection().AddSingleton<ICommandHandler<ManagedAgentDeployRequest, ManagedAgentDeployResult>>(step).BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await new AgentHostCli(operations, Program.Handlers(amsMcsAgents, container, operations), amsMcsAgents ? new TestFeatureFlags(FeatureName.AmsMcsAgents) : new TestFeatureFlags()).InvokeAsync(["agent", "deploy", "--agent-id", "agent-1", "--deployment-id", "deployment-1"], stdout, stderr, color: false);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(amsMcsAgents, step.Request is not null);
        Assert.Equal(!amsMcsAgents, operations.LastManagedAgentDeployRequest is not null);
    }

    [Theory]
    [InlineData(true, "To bring Copilot Studio changes into the repository, run `msagent agent capture`, then `git pull`. To connect this folder, deploy it to Dev: `msagent agent deploy` deploys the folder, and `msagent agent deploy --remote` connects it when the folder holds exactly the commit it deploys.")]
    [InlineData(false, "Run `msagent agent deploy` to create the agent and connect this directory, or pull into an empty directory instead.")]
    public async Task TheComposedContainer_ResolvesTheRemoteDeployAndGivesThePullAmsAdvice_OnlyWithTheFlagOn(bool amsMcsAgents, string remediation)
    {
        using var workspace = new TemporaryWorkspace();
        ManagedAgentFixtures.Store().Add(workspace.Root, ManagedAgentFixtures.Agent() with { SourceControlMetadata = new AgentSourceControlMetadata("repository-1", "main", "Contoso Agent") });
        await using var provider = Program.ComposeServices([], new Program.InvocationTarget(PowerPlatformApiTargetConfig.Public, RingKnown: true), amsMcsAgents ? new TestFeatureFlags(FeatureName.AmsMcsAgents) : new TestFeatureFlags(), new AgentHostAuthOptions { ConfigDirectory = Path.Combine(workspace.Root, "config") }, new ProgressSpinner(TextWriter.Null, enabled: false, ansi: false, TimeProvider.System, () => 80)).BuildServiceProvider();

        Assert.Equal(amsMcsAgents, provider.GetService<AmsRemoteDeployService>() is not null);
        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => provider.GetRequiredService<ManagedAgentPullService>().PullAsync(new ManagedAgentPullRequest { Project = workspace.Root }, CancellationToken.None));
        Assert.Equal((ErrorCodes.Workspace.WorkspaceDisconnected, remediation), (failure.ErrorCode, failure.Remediation));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheComposedContainer_KeepsCloudCacheCopiesAroundEverySyncPort_OnlyWithTheFlagOn(bool amsMcsAgents)
    {
        using var workspace = new TemporaryWorkspace();
        await using var provider = Program.ComposeServices([], new Program.InvocationTarget(PowerPlatformApiTargetConfig.Public, RingKnown: true), amsMcsAgents ? new TestFeatureFlags(FeatureName.AmsMcsAgents) : new TestFeatureFlags(), new AgentHostAuthOptions { ConfigDirectory = Path.Combine(workspace.Root, "config") }, new ProgressSpinner(TextWriter.Null, enabled: false, ansi: false, TimeProvider.System, () => 80)).BuildServiceProvider();
        Type[] expected = amsMcsAgents
            ? [typeof(AmsCloudCacheWorkspacePort), typeof(AmsCloudCacheAttachPort), typeof(AmsCloudCacheWorkflowStore), typeof(AmsCloudCacheConnectionPort)]
            : [typeof(SyncWorkspaceAdapter), typeof(WorkspaceReattachAdapter), typeof(WorkflowDataverseClient), typeof(SyncConnectionAdapter)];

        var attach = provider.GetRequiredService<ICopilotWorkspaceAttachPort>();

        Assert.Equal(expected, new object[] { provider.GetRequiredService<ICopilotWorkspacePort>(), attach, provider.GetRequiredService<IWorkflowStorePort>(), provider.GetRequiredService<ICopilotConnectionPort>() }.Select(service => service.GetType()));
        Assert.Same(attach, provider.GetRequiredService<ICopilotWorkspaceConnectPort>());
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("factory")]
    [InlineData("type")]
    public void Decorate_WrapsTheLastRegistration_HoweverItWasRegistered(string registration)
    {
        var services = new ServiceCollection().AddSingleton<IClock>(new FixedClock(DateTimeOffset.MinValue));
        _ = registration switch
        {
            "instance" => services.AddSingleton<IClock>(new FixedClock(DateTimeOffset.UnixEpoch)),
            "factory" => services.AddSingleton<IClock>(_ => new FixedClock(DateTimeOffset.UnixEpoch)),
            _ => services.AddSingleton<IClock, EpochClock>(),
        };

        var inner = Program.Decorate<IClock, NextDayClock>(services);
        services.AddSingleton(sp => new NextDayClock(inner(sp)));
        using var provider = services.BuildServiceProvider();

        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(1), provider.GetRequiredService<IClock>().UtcNow);
    }

    private sealed class EpochClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private sealed class NextDayClock(IClock inner) : IClock
    {
        public DateTimeOffset UtcNow => inner.UtcNow.AddDays(1);
    }

    private sealed class EchoingProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType;
    }
}
