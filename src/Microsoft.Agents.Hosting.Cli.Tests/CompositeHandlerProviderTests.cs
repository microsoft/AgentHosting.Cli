using System.Reflection;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class CompositeHandlerProviderTests
{
    private sealed record ProbeRequest(string Value);

    public class EvaluationlessOperations : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException();

        public static IAgentHostingOperations Create() => Create<IAgentHostingOperations, EvaluationlessOperations>();
    }

    private sealed record ProbeResult(string Value);

    private sealed class ProbeCommand : CommandBase<ProbeRequest, ProbeResult>
    {
        public override IReadOnlyList<CommandSymbol> Symbols => [];

        protected override ProbeRequest Bind(ICommandInput input) => new("bound");
    }

    private sealed class ProbeHandler : ICommandHandler<ProbeRequest, ProbeResult>
    {
        public Task<ProbeResult> HandleAsync(ProbeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ProbeResult(request.Value));
    }

    private sealed class EmptyCommandInput : ICommandInput
    {
        public string? GetString(string name) => null;

        public bool GetSwitch(string name) => false;

        public bool? GetOptionalSwitch(string name) => null;

        public int? GetInt(string name) => null;

        public string[] GetStrings(string name) => [];

        public string? GetArgument(string name) => null;

        public bool WasProvided(string name) => false;

        public bool Json => false;

        public bool NonInteractive => false;
    }

    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly Type _serviceType;
        private readonly object _service;

        public SingleServiceProvider(Type serviceType, object service)
        {
            _serviceType = serviceType;
            _service = service;
        }

        public object? GetService(Type serviceType) => serviceType == _serviceType ? _service : null;
    }

    private static ServiceProvider ContainerWithProbeHandler()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<ProbeRequest, ProbeResult>, ProbeHandler>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheFirstProviderThatSuppliesTheServiceWins()
    {
        var first = new SingleServiceProvider(typeof(string), "first");
        var second = new SingleServiceProvider(typeof(string), "second");

        Assert.Equal("first", new CompositeHandlerProvider(first, second).GetService(typeof(string)));
    }

    [Fact]
    public void ResolutionFallsThroughToALaterProvider()
    {
        var empty = new SingleServiceProvider(typeof(int), 0);
        var supplying = new SingleServiceProvider(typeof(string), "fallback");

        Assert.Equal("fallback", new CompositeHandlerProvider(empty, supplying).GetService(typeof(string)));
    }

    [Fact]
    public void NoProviderSupplyingTheServiceResolvesToNull()
    {
        Assert.Null(new CompositeHandlerProvider(new SingleServiceProvider(typeof(int), 0)).GetService(typeof(string)));
    }

    [Fact]
    public void ComposingNoProvidersResolvesToNull()
    {
        Assert.Null(new CompositeHandlerProvider().GetService(typeof(string)));
    }

    [Fact]
    public void ANullProviderIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new CompositeHandlerProvider(new SingleServiceProvider(typeof(int), 0), null!));
    }

    [Fact]
    public async Task AHandlerRegisteredOnlyInTheContainerDispatches()
    {
        await using var container = ContainerWithProbeHandler();
        var provider = new CompositeHandlerProvider(new FacadeHandlerProvider(new FakeOperations()), container);

        var result = await new ProbeCommand().ExecuteAsync(new EmptyCommandInput(), provider, CancellationToken.None);

        Assert.Equal("bound", Assert.IsType<ProbeResult>(result).Value);
    }

    [Fact]
    public async Task TheFacadeAloneCannotDispatchAContainerOnlyHandler()
    {
        var facadeOnly = new FacadeHandlerProvider(new FakeOperations());

        await Assert.ThrowsAsync<CommandConfigurationException>(
            () => new ProbeCommand().ExecuteAsync(new EmptyCommandInput(), facadeOnly, CancellationToken.None));
    }

    [Fact]
    public void TheFacadeAnswersForEvaluationWhenTheHostOffersIt()
    {
        var provider = new FacadeHandlerProvider(new FakeOperations());

        Assert.NotNull(provider.GetService(typeof(ICommandHandler<EvaluationRunRequest, EvaluationRunResult>)));
        Assert.NotNull(provider.GetService(typeof(ICommandHandler<EvaluationTestSetDeleteRequest, EvaluationTestSetDeleteResult>)));
    }

    [Fact]
    public void AHostWithoutTheEvaluationInterfaceStillBuildsTheRestOfTheSurface()
    {
        var provider = new FacadeHandlerProvider(EvaluationlessOperations.Create());

        Assert.NotNull(provider.GetService(typeof(ICommandHandler<EnvironmentListRequest, EnvironmentListResult>)));
        Assert.Null(provider.GetService(typeof(ICommandHandler<EvaluationRunRequest, EvaluationRunResult>)));
    }

    [Fact]
    public async Task TheFacadeStillAnswersForTheHandlersItOwns()
    {
        await using var container = ContainerWithProbeHandler();
        var provider = new CompositeHandlerProvider(new FacadeHandlerProvider(new FakeOperations()), container);

        Assert.NotNull(provider.GetService(typeof(ICommandHandler<EnvironmentListRequest, EnvironmentListResult>)));
    }

    [Fact]
    public async Task AHostComposedAsTheProgramComposesItStillDispatchesFacadeCommands()
    {
        var operations = new FakeOperations
        {
            OnEnvironmentList = () => new EnvironmentListResult { Status = "listed", Environments = [] },
        };

        await using var container = ContainerWithProbeHandler();
        var cli = new AgentHostCli(operations, new CompositeHandlerProvider(new FacadeHandlerProvider(operations), container));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await cli.InvokeAsync(["env", "list"], stdout, stderr, color: false);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());
    }
}
