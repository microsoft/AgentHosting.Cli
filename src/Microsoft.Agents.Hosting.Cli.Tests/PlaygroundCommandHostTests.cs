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
            ["test", "playground", "--agent-harness", "standard", "--json"],
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
            ["test", "playground", "--agent-harness", "standard"],
            stdout,
            stderr,
            color: false);

        Assert.Equal(2, exitCode);
        Assert.Equal(0, handler.Calls);
        Assert.Contains("test", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
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

        public Task<ILiveTestStreamingResult> HandleAsync(
            PlaygroundRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
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
