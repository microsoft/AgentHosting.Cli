using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class LiveTestCommandHostTests
{
    [Fact]
    public async Task FeatureGateHidesCommandAndEnabledHostTransfersSoleOutputOwnership()
    {
        var handler = new TestHandler();
        var disabled = await Invoke(new TestFeatureFlags(), handler, "test", "ask", "--query", "hello", "--json");
        Assert.Equal(2, disabled.Exit);
        Assert.Equal(0, handler.Calls);

        var enabled = await Invoke(new TestFeatureFlags(FeatureName.LiveTestFeatures), handler,
            "test", "ask", "--query", "literal  text", "--json");
        Assert.Equal(0, enabled.Exit);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("literal  text", handler.Request!.Query);
        Assert.Equal("""{"streaming":true}""", enabled.Output);
        Assert.Empty(enabled.Error);
    }

    [Fact]
    public async Task RemovedAgentHarnessOptionIsRejectedBeforeDispatch()
    {
        var handler = new TestHandler();
        var result = await Invoke(new TestFeatureFlags(FeatureName.LiveTestFeatures), handler,
            "test", "ask", "--query", "hello", "--agent-harness", "standard", "--json");

        Assert.Equal(2, result.Exit);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task StreamingFailureDoesNotAppendGenericFailureEnvelope()
    {
        var handler = new TestHandler { Exit = 1 };
        var result = await Invoke(new TestFeatureFlags(FeatureName.LiveTestFeatures), handler,
            "test", "ask", "--query", "hello", "--json");
        Assert.Equal(1, result.Exit);
        Assert.Equal("""{"streaming":true}""", result.Output);
    }

    [Fact]
    public async Task RepeatedScalarOptionIsRejectedBeforeDispatch()
    {
        var handler = new TestHandler();
        var result = await Invoke(new TestFeatureFlags(FeatureName.LiveTestFeatures), handler,
            "test", "ask", "--query", "first", "--query", "second", "--json");
        Assert.Equal(2, result.Exit);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RealStreamingSinkOwnsOpeningFailureWithoutHostEnvelope()
    {
        var handler = new TestHandler
        {
            ResultFactory = request => new LiveTestExecution(
                request,
                _ => throw new InvalidOperationException("target must not be resolved"),
                (_, _, _) => throw new InvalidOperationException("auth must not run"),
                (_, _, _) => throw new InvalidOperationException("session must not be created")),
        };
        var output = new FailFirstWriteWriter();
        var error = new StringWriter();
        var cli = new AgentHostCli(new FakeOperations(), new HandlerProvider(handler),
            new TestFeatureFlags(FeatureName.LiveTestFeatures));

        var exit = await cli.InvokeAsync(
            ["test", "ask", "--query", "hello", "--json"],
            output,
            error,
            color: false);

        Assert.Equal(1, exit);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(output.ToString());
        Assert.Contains("output", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnexpectedStreamingFailureReportsSafeErrorWithoutSecondJson()
    {
        var handler = new TestHandler { ResultFactory = _ => new ThrowingResult() };
        var result = await Invoke(new TestFeatureFlags(FeatureName.LiveTestFeatures), handler,
            "test", "ask", "--query", "hello", "--json");

        Assert.Equal(1, result.Exit);
        Assert.Equal("""{"partial":""", result.Output);
        Assert.Contains("live-test", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret response body", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamingCommandNeverShowsTheProgressSpinner()
    {
        var time = new ManualTimeProvider();
        var handler = new TestHandler { OnHandle = () => time.Advance(TimeSpan.FromSeconds(1)) };
        var output = new StringWriter();
        var error = new StringWriter();
        var cli = new AgentHostCli(new FakeOperations(), telemetryFactory: null, services: new HandlerProvider(handler), featureFlags: new TestFeatureFlags(FeatureName.LiveTestFeatures), progressSpinner: new ProgressSpinner(error, enabled: true, ansi: false, time, () => 80));

        var exit = await cli.InvokeAsync(["test", "ask", "--query", "hello"], output, error, color: false);

        Assert.Equal(0, exit);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("""{"streaming":true}""", output.ToString());
        Assert.Empty(error.ToString());
    }

    private static async Task<(int Exit, string Output, string Error)> Invoke(
        IFeatureFlags flags, TestHandler handler, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var cli = new AgentHostCli(new FakeOperations(), new HandlerProvider(handler), flags);
        var exit = await cli.InvokeAsync(args, output, error, color: false);
        return (exit, output.ToString(), error.ToString());
    }

    private sealed class HandlerProvider(TestHandler handler) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ICommandHandler<LiveTestRequest, ILiveTestStreamingResult>)
                ? handler
                : null;
    }

    private sealed class TestHandler : ICommandHandler<LiveTestRequest, ILiveTestStreamingResult>
    {
        public int Calls { get; private set; }
        public int Exit { get; init; }
        public LiveTestRequest? Request { get; private set; }
        public Func<LiveTestRequest, ILiveTestStreamingResult>? ResultFactory { get; init; }
        public Action? OnHandle { get; init; }

        public Task<ILiveTestStreamingResult> HandleAsync(LiveTestRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            OnHandle?.Invoke();
            return Task.FromResult(ResultFactory?.Invoke(request) ?? (ILiveTestStreamingResult)new TestResult(Exit));
        }
    }

    private sealed class FailFirstWriteWriter : StringWriter
    {
        private bool _failed;

        public override Task WriteAsync(string? value)
        {
            if (!_failed)
            {
                _failed = true;
                throw new IOException("synthetic write failure");
            }

            return base.WriteAsync(value);
        }
    }

    private sealed class TestResult(int exit) : ILiveTestStreamingResult
    {
        public async Task<int> RenderAsync(TextWriter stdout, TextWriter stderr, bool color, CancellationToken cancellationToken)
        {
            await stdout.WriteAsync("""{"streaming":true}""");
            return exit;
        }
    }

    private sealed class ThrowingResult : ILiveTestStreamingResult
    {
        public async Task<int> RenderAsync(TextWriter stdout, TextWriter stderr, bool color, CancellationToken cancellationToken)
        {
            await stdout.WriteAsync("""{"partial":""");
            throw new InvalidOperationException("secret response body");
        }
    }
}
