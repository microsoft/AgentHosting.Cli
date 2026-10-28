using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Rendering;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Telemetry;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class PlaygroundCommandHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TenantAliasesAreDeclaredForRegularAndRecursiveOptions(bool recursive)
    {
        var option = recursive
            ? OptionCatalog.Recursive("--tenant", "Tenant id.")
            : OptionCatalog.Value("--tenant", "Tenant id.", required: true);

        Assert.Equal("--tenant", option.Name);
        Assert.Equal("-t", option.ShortAlias);
        Assert.Equal("--tenant-id", Assert.Single(option.ExtraAliases));
        Assert.Equal(recursive, option.Recursive);
        Assert.Equal(!recursive, option.IsRequired);
    }

    [Theory]
    [InlineData(130, 5)]
    [InlineData(2, 1)]
    [InlineData(1, 3)]
    public void PlaygroundStreamingExitUsesTheTypedOutcomeContract(
        int exitCode,
        int expected)
    {
        var result = new PlaygroundResult(
            exitCode,
            exitCode switch
            {
                130 => PlaygroundSemanticOutcome.Cancellation,
                2 => PlaygroundSemanticOutcome.Usage,
                _ => PlaygroundSemanticOutcome.None,
            });

        Assert.Equal((CommandTelemetryOutcome)expected, CommandTreeBuilder.StreamingOutcome(result, exitCode));
        Assert.Equal(
            CommandTelemetryOutcome.Failure,
            CommandTreeBuilder.StreamingOutcome(new EmptyResult(), exitCode));
        Assert.Equal(
            CommandTelemetryOutcome.Failure,
            CommandTreeBuilder.StreamingOutcome(
                new PlaygroundResult(130, PlaygroundSemanticOutcome.None),
                130));
        Assert.Equal(
            CommandTelemetryOutcome.Failure,
            CommandTreeBuilder.StreamingOutcome(
                new PlaygroundResult(2, PlaygroundSemanticOutcome.None),
                2));
    }

    [Fact]
    public async Task JsonPlaygroundInvocationUsesTheActualCommandHostParserAndDispatch()
    {
        var handler = new RecordingHandler();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new AgentHostCli(
            new FakeOperations(),
            new HandlerProvider(handler),
            new TestFeatureFlags(FeatureName.LiveTestFeatures));

        var exitCode = await cli.InvokeAsync(
            ["test", "playground", "--json"],
            stdout,
            stderr,
            color: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task FeatureGateHidesPlaygroundBeforeHandlerDispatch()
    {
        var handler = new RecordingHandler();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new AgentHostCli(
            new FakeOperations(),
            new HandlerProvider(handler),
            new TestFeatureFlags());

        var exitCode = await cli.InvokeAsync(
            ["test", "playground"],
            stdout,
            stderr,
            color: false);

        Assert.Equal(2, exitCode);
        Assert.Equal(0, handler.Calls);
        Assert.Contains("test", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--target")]
    [InlineData("-tgt")]
    public async Task DirectLineParsesWithoutAHarness(string targetOption)
    {
        var handler = new RecordingHandler();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new AgentHostCli(
            new FakeOperations(),
            new HandlerProvider(handler),
            TestFeatureFlags.Everything);

        var exitCode = await cli.InvokeAsync(
            ["test", "playground", targetOption, "directline"], stdout, stderr, color: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("directline", handler.Request!.Target);
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task ExistingInvocationDefaultsToDirectEngine()
    {
        var handler = new RecordingHandler();
        var cli = new AgentHostCli(
            new FakeOperations(),
            new HandlerProvider(handler),
            TestFeatureFlags.Everything);

        Assert.Equal(0, await cli.InvokeAsync(
            ["test", "playground"],
            new StringWriter(), new StringWriter(), color: false));
        Assert.Equal("directengine", handler.Request!.Target);
    }

    [Theory]
    [InlineData("--tenant")]
    [InlineData("--tenant-id")]
    [InlineData("-t")]
    public async Task TenantSpellingsBindToTheSameRequestProperty(string tenantOption)
    {
        const string tenantId = "11111111-1111-1111-1111-111111111111";
        var handler = new RecordingHandler();
        var cli = new AgentHostCli(
            new FakeOperations(),
            new HandlerProvider(handler),
            TestFeatureFlags.Everything);
        var stderr = new StringWriter();

        var exitCode = await cli.InvokeAsync(
            ["test", "playground", tenantOption, tenantId],
            new StringWriter(), stderr, color: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(tenantId, handler.Request!.Tenant);
        Assert.Empty(stderr.ToString());
    }

    private sealed class HandlerProvider(RecordingHandler handler) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ICommandHandler<PlaygroundRequest, ILiveTestStreamingResult>)
                ? handler
                : null;
    }

    private sealed class RecordingHandler : ICommandHandler<PlaygroundRequest, ILiveTestStreamingResult>
    {
        public int Calls { get; private set; }
        public PlaygroundRequest? Request { get; private set; }

        public Task<ILiveTestStreamingResult> HandleAsync(
            PlaygroundRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            return Task.FromResult<ILiveTestStreamingResult>(new EmptyResult());
        }
    }

    private sealed class EmptyResult : ILiveTestStreamingResult
    {
        public Task<int> RenderAsync(
            TextWriter stdout,
            TextWriter stderr,
            bool color,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class PlaygroundResult(
        int exitCode,
        PlaygroundSemanticOutcome semanticOutcome) : IPlaygroundStreamingResult
    {
        public PlaygroundSemanticOutcome SemanticOutcome => semanticOutcome;

        public Task<int> RenderAsync(
            TextWriter stdout,
            TextWriter stderr,
            bool color,
            CancellationToken cancellationToken) =>
            Task.FromResult(exitCode);
    }
}
