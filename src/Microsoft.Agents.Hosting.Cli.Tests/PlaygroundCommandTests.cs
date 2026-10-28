using System.Reflection;
using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class PlaygroundCommandTests
{
    [Fact]
    public void TestGroupDiscoversPlaygroundAfterAsk()
    {
        var testGroup = CommandReflector
            .Discover(typeof(TestGroup).Assembly)
            .Single(group => group.DeclaringType == typeof(TestGroup));

        Assert.Equal(["ask", "playground"], testGroup.ChildNames);
    }

    [Fact]
    public void PlaygroundInstallFlagHasOneCatalogDeclaration()
    {
        Assert.True(OptionCatalog.DeclaredAliases.ContainsKey("--install-playground"));
        Assert.Equal("-ipg", OptionCatalog.Alias("--install-playground"));
        Assert.Equal("-tgt", OptionCatalog.Alias("--target"));
    }

    [Fact]
    public void PlaygroundHasTheLiveFeatureGateAndSortsAfterAsk()
    {
        var group = CommandReflector
            .Discover(typeof(TestGroup).Assembly, TestFeatureFlags.Everything)
            .Single(group => group.DeclaringType == typeof(TestGroup));
        var playground = group.Commands.Single(command => command.Name == "playground");

        Assert.Equal(20, playground.SortPriority);
        Assert.Equal([FeatureName.LiveTestFeatures], playground.RequiredFeatures);
        Assert.Contains("Live actions can have real effects.", playground.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaygroundHelpUsesDiscoveryAndOwnOptions()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await new AgentHostCli(new FakeOperations(), featureFlags: TestFeatureFlags.Everything)
            .InvokeAsync(["test", "playground", "--help"], stdout, stderr, color: false);

        Assert.Equal(0, exitCode);
        Assert.Contains(
            "msagent test playground [options]",
            stdout.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("--install-playground", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("may execute package lifecycle scripts", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "DirectEngine browser/device-code sign-in may still require user action even with --non-interactive; --json is unsupported in this MVP.",
            stdout.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("--plain", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("--target", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--query", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--conversation-id", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--agent-harness", stdout.ToString(), StringComparison.Ordinal);
        Assert.False(OptionCatalog.DeclaredAliases.ContainsKey("--agent-harness"));
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task PlaygroundIsRejectedWhenLiveTestFeatureIsDisabled()
    {
        var handler = new RecordingHandler();
        var disabledHelp = await InvokePlayground(
            new TestFeatureFlags(),
            handler,
            "test",
            "playground",
            "--help");
        var disabledInvocation = await InvokePlayground(
            new TestFeatureFlags(),
            handler,
            "test",
            "playground");

        Assert.Equal(ExitCodes.Usage, disabledHelp.ExitCode);
        Assert.Empty(disabledHelp.Stdout);
        Assert.Contains("test", disabledHelp.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ExitCodes.Usage, disabledInvocation.ExitCode);
        Assert.Empty(disabledInvocation.Stdout);
        Assert.Contains("test", disabledInvocation.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("--query", "hello")]
    [InlineData("--conversation-id", "conversation")]
    [InlineData("--agent-harness", "standard")]
    [InlineData("-ah", "github-copilot")]
    public async Task PlaygroundRejectsAskOnlyInputsBeforeDispatch(string option, string value)
    {
        var handler = new RecordingHandler();
        var result = await InvokePlayground(
            TestFeatureFlags.Everything,
            handler,
            "test",
            "playground",
            option,
            value);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("Unrecognized", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TestGroupUsageAdvertisesPlayground()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await new AgentHostCli(new FakeOperations(), featureFlags: TestFeatureFlags.Everything)
            .InvokeAsync(["test"], stdout, stderr, color: false);

        Assert.Equal(2, exitCode);
        Assert.Contains("Valid commands: ask, playground.", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(stdout.ToString());
    }

    [Fact]
    public void PlaygroundSymbolsAddTargetToAskSelectorsWithoutQueryOrConversation()
    {
        var group = CommandReflector
            .Discover(typeof(TestGroup).Assembly, TestFeatureFlags.Everything)
            .Single(group => group.DeclaringType == typeof(TestGroup));
        var ask = group.Commands.Single(command => command.Name == "ask");
        var playground = group.Commands.Single(command => command.Name == "playground");

        var expected = ask.Implementation.Symbols
            .Select(symbol => symbol.Name)
            .Where(name => name is not "--query" and not "--conversation-id")
            .Prepend("--target")
            .Append("--install-playground");

        Assert.Equal(expected, playground.Implementation.Symbols.Select(symbol => symbol.Name));
        Assert.DoesNotContain(playground.Implementation.Symbols, symbol => symbol.Name == "--agent-harness");
    }

    [Fact]
    public void PlaygroundBindingCapturesSelectorsAndFlags()
    {
        var command = FindPlayground().Implementation;
        var request = Assert.IsType<PlaygroundRequest>(InvokeBind(command, new FakeInput(
            values: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["--target"] = "directengine",
                ["--project"] = "project",
                ["--agent-id"] = "agent-id",
                ["--agent-name"] = "agent-name",
                ["--schema-name"] = "schema",
                ["--deployment-id"] = "deployment-id",
                ["--deployment-name"] = "deployment-name",
                ["--environment-id"] = "environment",
                ["--tenant"] = "tenant",
            },
            switches: new HashSet<string>(["--plain", "--install-playground"], StringComparer.Ordinal),
            json: true,
            nonInteractive: true)));

        Assert.Equal("directengine", request.Target);
        Assert.Equal("project", Property(request, "Project"));
        Assert.Equal("agent-id", Property(request, "AgentId"));
        Assert.Equal("agent-name", Property(request, "AgentName"));
        Assert.Equal("schema", Property(request, "SchemaName"));
        Assert.Equal("deployment-id", Property(request, "DeploymentId"));
        Assert.Equal("deployment-name", Property(request, "DeploymentName"));
        Assert.Equal("environment", Property(request, "EnvironmentId"));
        Assert.Equal("tenant", Property(request, "Tenant"));
        Assert.Equal(true, Property(request, "Json"));
        Assert.Equal(true, Property(request, "Plain"));
        Assert.Equal(true, Property(request, "NonInteractive"));
        Assert.Equal(true, Property(request, "InstallPlayground"));
    }

    [Fact]
    public void PlaygroundProjectsOnlySelectorsIntoLiveTestRequest()
    {
        var command = FindPlayground().Implementation;
        var request = Assert.IsType<PlaygroundRequest>(InvokeBind(command, new FakeInput(
            values: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["--project"] = "project",
                ["--schema-name"] = "schema",
                ["--environment-id"] = "environment",
                ["--tenant"] = "tenant",
            },
            switches: new HashSet<string>(["--plain", "--install-playground"], StringComparer.Ordinal),
            json: true,
            nonInteractive: true)));

        var liveTestRequest = request.ToLiveTestRequest();

        Assert.IsType<LiveTestRequest>(liveTestRequest);
        Assert.Equal("project", Property(liveTestRequest, "Project"));
        Assert.Equal("schema", Property(liveTestRequest, "SchemaName"));
        Assert.Equal("environment", Property(liveTestRequest, "EnvironmentId"));
        Assert.Equal("tenant", Property(liveTestRequest, "Tenant"));
        Assert.Null(Property(liveTestRequest, "Query"));
        Assert.Null(Property(liveTestRequest, "ConversationId"));
        Assert.Equal(false, Property(liveTestRequest, "Json"));
        Assert.Equal(false, Property(liveTestRequest, "Plain"));
        Assert.Equal(false, Property(liveTestRequest, "NonInteractive"));
    }

    private static CommandMetadata FindPlayground() =>
        CommandReflector
            .Discover(typeof(TestGroup).Assembly, TestFeatureFlags.Everything)
            .Single(group => group.DeclaringType == typeof(TestGroup))
            .Commands
            .Single(command => command.Name == "playground");

    private static object InvokeBind(ICommandImplementation command, ICommandInput input) =>
        command.GetType()
            .GetMethod("Bind", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(command, [input])!;

    private static object? Property(object instance, string name) =>
        instance.GetType().GetProperty(name)!.GetValue(instance);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokePlayground(
        IFeatureFlags featureFlags,
        RecordingHandler handler,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new AgentHostCli(
            new FakeOperations(),
            new HandlerProvider(handler),
            featureFlags);
        var exitCode = await cli.InvokeAsync(args, stdout, stderr, color: false);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed class FakeInput(
        IReadOnlyDictionary<string, string?> values,
        IReadOnlySet<string> switches,
        bool json,
        bool nonInteractive) : ICommandInput
    {
        public bool Json => json;

        public bool NonInteractive => nonInteractive;

        public string? GetString(string name) => values.TryGetValue(name, out var value) ? value : null;

        public bool GetSwitch(string name) => switches.Contains(name);

        public bool? GetOptionalSwitch(string name) => switches.Contains(name);

        public int? GetInt(string name) => null;

        public string[] GetStrings(string name) => [];

        public string? GetArgument(string name) => null;

        public bool WasProvided(string name) => values.ContainsKey(name) || switches.Contains(name);
    }

    private sealed class HandlerProvider(RecordingHandler handler) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ICommandHandler<LiveTestRequest, ILiveTestStreamingResult>)
                ? handler
                : null;
    }

    private sealed class RecordingHandler : ICommandHandler<LiveTestRequest, ILiveTestStreamingResult>
    {
        public int Calls { get; private set; }

        public Task<ILiveTestStreamingResult> HandleAsync(
            LiveTestRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<ILiveTestStreamingResult>(new RecordingResult());
        }
    }

    private sealed class RecordingResult : ILiveTestStreamingResult
    {
        public Task<int> RenderAsync(
            TextWriter stdout,
            TextWriter stderr,
            bool color,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}
