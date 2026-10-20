using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Playground;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Rendering;
using Microsoft.Agents.Hosting.Cli.Telemetry;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class PlaygroundExecutionTests
{
    private static readonly Guid ClientId = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly LiveTestDestination Target = new(
        "environment",
        "11111111-1111-1111-1111-111111111111",
        "schema",
        "explicit",
        new Uri("https://example.test/agent"));

    [Fact]
    public async Task JsonIsRejectedBeforeTargetResolutionOrToolDiscovery()
    {
        var doubles = new Doubles();
        var execution = CreateExecution(
            new PlaygroundRequest { Json = true },
            doubles);

        var exitCode = await RenderAsync(execution);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("not yet supported for playground", doubles.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, doubles.ResolveCalls);
        Assert.Equal(0, doubles.LocateCalls);
        Assert.Equal(0, doubles.PromptCalls);
        Assert.Equal(0, doubles.LaunchCalls);
        Assert.Equal(0, doubles.DiscoverCalls);
    }

    [Fact]
    public async Task TargetIsResolvedBeforeToolDiscovery()
    {
        var doubles = new Doubles();
        doubles.Resolve = _ =>
        {
            doubles.Events.Add("resolve");
            return Target;
        };
        doubles.Locate = _ =>
        {
            doubles.Events.Add("locate");
            return Task.FromResult(Discovery(doubles.Tool));
        };
        doubles.Launch = async (tool, target, clientId, started, _) =>
        {
            doubles.Events.Add("launch");
            doubles.LaunchCalls++;
            doubles.LaunchedClientId = clientId;
            started();
            doubles.StartedObservedBeforeLaunchCompleted = true;
            return 0;
        };

        var exitCode = await RenderAsync(CreateExecution(Request(), doubles));

        Assert.Equal(0, exitCode);
        Assert.Equal(["resolve", "locate", "launch"], doubles.Events);
    }

    [Fact]
    public async Task WorkingToolLaunchesWithoutInstallerOrPrerequisiteProbe()
    {
        var doubles = new Doubles { Tool = Tool("0.2.30") };

        var exitCode = await RenderAsync(CreateExecution(Request(), doubles));

        Assert.Equal(0, exitCode);
        Assert.Equal(1, doubles.LocateCalls);
        Assert.Equal(0, doubles.DetectNpmCalls);
        Assert.Equal(0, doubles.InstallCalls);
        Assert.Equal(1, doubles.LaunchCalls);
        Assert.Equal(ClientId, doubles.LaunchedClientId);
        Assert.Equal(1, doubles.DiscoverCalls);
    }

    [Fact]
    public async Task DiscoveredEndpointIsPassedToLauncherWithNonInteractivePreference()
    {
        var doubles = new Doubles();
        var endpoint = new Uri("https://example.test/discovered");
        doubles.Discover = (target, nonInteractive, _) =>
        {
            Assert.True(nonInteractive);
            return Task.FromResult(target with { Endpoint = endpoint });
        };
        doubles.Launch = (_, target, _, _, _) =>
        {
            Assert.Equal(endpoint, target.Endpoint);
            return Task.FromResult(0);
        };

        Assert.Equal(0, await RenderAsync(CreateExecution(Request() with { NonInteractive = true }, doubles)));
    }

    [Fact]
    public async Task EndpointDiscoveryFailureStopsBeforeLaunchingAndDoesNotExposeResponse()
    {
        var doubles = new Doubles
        {
            Discover = (_, _, _) => throw new HttpRequestException("CANARY-secret-discovery-response"),
        };

        Assert.Equal(ExitCodes.Failure, await RenderAsync(CreateExecution(Request(), doubles)));
        Assert.Equal(0, doubles.LaunchCalls);
        Assert.Contains("endpoint could not be discovered", doubles.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("CANARY", doubles.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndpointDiscoveryCancellationStopsBeforeLaunching()
    {
        using var cancellation = new CancellationTokenSource();
        var doubles = new Doubles
        {
            Discover = (_, _, _) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<LiveTestDestination>(cancellation.Token);
            },
        };
        var execution = CreateExecution(Request(), doubles);

        Assert.Equal(130, await RenderAsync(execution, cancellation.Token));
        Assert.Equal(PlaygroundSemanticOutcome.Cancellation, execution.SemanticOutcome);
        Assert.Equal(0, doubles.LaunchCalls);
    }

    [Fact]
    public async Task InteractiveMissingToolPromptsEvenWhenInstallSwitchIsSet()
    {
        var doubles = new Doubles
        {
            IsInputRedirected = false,
            Tool = null,
            InstallTool = Tool("0.2.30"),
            PromptOutcome = ConfirmationOutcome.Confirmed,
        };

        var exitCode = await RenderAsync(CreateExecution(
            Request() with { InstallPlayground = true },
            doubles));

        Assert.Equal(0, exitCode);
        Assert.Equal(1, doubles.PromptCalls);
        Assert.Equal("install", doubles.ExpectedResponse);
        Assert.Contains("@microsoft/m365agentsplayground@latest", doubles.PromptDetail, StringComparison.Ordinal);
        Assert.Equal(1, doubles.InstallCalls);
        Assert.Equal(1, doubles.LaunchCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulInstallClearsRecoveredDiscoveryError(bool belowMinimum)
    {
        var doubles = new Doubles
        {
            Tool = null,
            DiscoveryResult = belowMinimum
                ? BelowMinimumDiscovery()
                : Discovery(null),
            InstallTool = Tool("0.2.30"),
            IsInputRedirected = true,
        };

        var execution = CreateExecution(
            Request() with { InstallPlayground = true, NonInteractive = true },
            doubles);
        var exitCode = await RenderAsync(execution);

        Assert.Equal(0, exitCode);
        var details = Assert.IsType<PlaygroundTelemetryDetails>(Assert.Single(doubles.Telemetry));
        Assert.Equal(PlaygroundErrorCategory.None, details.ErrorCategory);
        Assert.Equal(PlaygroundInstallResult.Installed, details.InstallResult);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NonInteractiveOrRedirectedInvocationDeclinesWithoutExplicitInstall(
        bool nonInteractive,
        bool redirected)
    {
        var doubles = new Doubles
        {
            Tool = null,
            IsInputRedirected = redirected,
        };

        var exitCode = await RenderAsync(CreateExecution(
            Request() with { NonInteractive = nonInteractive },
            doubles));

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal(0, doubles.PromptCalls);
        Assert.Equal(0, doubles.InstallCalls);
        Assert.Contains("--install-playground", doubles.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitInstallAllowsUnattendedInstallationWithoutPrompt()
    {
        var doubles = new Doubles
        {
            Tool = null,
            IsInputRedirected = true,
            InstallTool = Tool("0.2.30"),
        };

        var execution = CreateExecution(
            Request() with { InstallPlayground = true, NonInteractive = true },
            doubles);
        var exitCode = await RenderAsync(execution);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, doubles.PromptCalls);
        Assert.Equal(1, doubles.InstallCalls);
        Assert.Equal(1, doubles.LaunchCalls);
    }

    [Fact]
    public async Task InteractiveDeclineReturnsGuidanceWithoutInstalling()
    {
        var doubles = new Doubles
        {
            Tool = null,
            IsInputRedirected = false,
            PromptOutcome = ConfirmationOutcome.Declined,
        };

        var exitCode = await RenderAsync(CreateExecution(Request(), doubles));

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal(1, doubles.PromptCalls);
        Assert.Equal(0, doubles.InstallCalls);
        Assert.Contains("not authorized", doubles.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        var details = Assert.IsType<PlaygroundTelemetryDetails>(Assert.Single(doubles.Telemetry));
        Assert.Equal(PlaygroundInstallResult.Declined, details.InstallResult);
    }

    [Fact]
    public async Task InstallerTimeoutUsesTimeoutTelemetryCategory()
    {
        var doubles = new Doubles
        {
            Tool = null,
            IsInputRedirected = true,
            InstallException = new PlaygroundInstallException(
                PlaygroundInstallFailureKind.Timeout,
                "safe timeout",
                "safe remediation"),
        };

        var execution = CreateExecution(
            Request() with { InstallPlayground = true, NonInteractive = true },
            doubles);
        var exitCode = await RenderAsync(execution);

        Assert.Equal(ExitCodes.Failure, exitCode);
        var details = Assert.IsType<PlaygroundTelemetryDetails>(Assert.Single(doubles.Telemetry));
        Assert.Equal(PlaygroundErrorCategory.Timeout, details.ErrorCategory);
        Assert.Equal(
            CommandTelemetryOutcome.Timeout,
            CommandTreeBuilder.StreamingOutcome(execution, exitCode));
    }

    [Fact]
    public async Task LocateTimeoutUsesTimeoutHostOutcome()
    {
        var doubles = new Doubles
        {
            LocateException = new TimeoutException(),
        };
        var execution = CreateExecution(Request(), doubles);

        var exitCode = await RenderAsync(execution);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal(
            CommandTelemetryOutcome.Timeout,
            CommandTreeBuilder.StreamingOutcome(execution, exitCode));
    }

    [Fact]
    public async Task InvalidClientConfigurationIsResolvedBeforeInstallationConsent()
    {
        var doubles = new Doubles
        {
            Tool = null,
            IsInputRedirected = false,
            ClientConfiguration = () => throw new InvalidOperationException("invalid client"),
        };

        var exitCode = await RenderAsync(CreateExecution(Request(), doubles));

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal(0, doubles.PromptCalls);
        Assert.Equal(0, doubles.InstallCalls);
        Assert.Contains("client configuration", doubles.Error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolverCancellationIsNotReclassifiedAsUsage()
    {
        var doubles = new Doubles();
        using var cancellation = new CancellationTokenSource();
        doubles.Resolve = _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        var exitCode = await RenderAsync(CreateExecution(Request(), doubles), cancellation.Token);

        Assert.Equal(130, exitCode);
        Assert.Equal(0, doubles.LocateCalls);
    }

    [Fact]
    public async Task HumanOutputDisclosesLiveEffectsDeviceCodeAndSelectedTool()
    {
        var doubles = new Doubles { Tool = Tool("0.2.30") };

        var exitCode = await RenderAsync(CreateExecution(Request(), doubles));
        var output = doubles.Output.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("real effects", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("device code", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(doubles.Tool!.SelectedPath, output, StringComparison.Ordinal);
        Assert.Contains(doubles.Tool.Version.ToString(), output, StringComparison.Ordinal);
        Assert.Contains("process started", output, StringComparison.OrdinalIgnoreCase);
        Assert.True(doubles.StartedObservedBeforeLaunchCompleted);
    }

    [Fact]
    public async Task PlainOutputSuppressesOptionalProcessStartedProgress()
    {
        var doubles = new Doubles { Tool = Tool("0.2.30") };

        var exitCode = await RenderAsync(
            CreateExecution(Request() with { Plain = true }, doubles));
        var output = doubles.Output.ToString();

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("process started", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("real effects", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("device code", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(doubles.Tool!.SelectedPath, output, StringComparison.Ordinal);
        Assert.Contains(doubles.Tool.Version.ToString(), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LauncherFailureIsSafeAndDoesNotExposeExceptionDetails()
    {
        var doubles = new Doubles
        {
            Tool = Tool("0.2.30"),
            LaunchException = new InvalidOperationException("secret C:\\private\\path"),
        };

        var exitCode = await RenderAsync(CreateExecution(Request(), doubles));

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Contains("process", doubles.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", doubles.Error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationReturns130AndDoesNotExposeExceptionDetails()
    {
        var doubles = new Doubles
        {
            Tool = Tool("0.2.30"),
            LaunchException = new OperationCanceledException("secret cancellation"),
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exitCode = await RenderAsync(CreateExecution(Request(), doubles), cancellation.Token);

        Assert.Equal(130, exitCode);
        Assert.DoesNotContain("secret", doubles.Error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static PlaygroundRequest Request() => new()
    {
        SchemaName = "schema",
        EnvironmentId = "environment",
        Tenant = "11111111-1111-1111-1111-111111111111",
    };

    private static PlaygroundExecution CreateExecution(PlaygroundRequest request, Doubles doubles) =>
        new(
            request,
            doubles.Resolve,
            doubles.Discover,
            doubles.Locator,
            doubles.Installer,
            doubles.Launcher,
            doubles.Confirmation,
            doubles.ClientConfiguration,
            () => doubles.IsInputRedirected,
            details => doubles.Telemetry.Add(details));

    private static async Task<int> RenderAsync(
        PlaygroundExecution execution,
        CancellationToken cancellationToken = default)
    {
        var doubles = Doubles.Current;
        return await execution.RenderAsync(
            doubles.Output,
            doubles.Error,
            color: false,
            cancellationToken);
    }

    private static PlaygroundTool Tool(string version) =>
        new(
            @"C:\tools\agentsplayground.cmd",
            ["agentsplayground.js"],
            @"C:\tools\agentsplayground.cmd",
            PlaygroundVersion.Parse(version));

    private static PlaygroundToolDiscoveryResult Discovery(PlaygroundTool? tool) =>
        new(tool, []);

    private static PlaygroundToolDiscoveryResult BelowMinimumDiscovery() =>
        new(
            null,
            [
                new PlaygroundToolRejection(
                    @"C:\tools\agentsplayground.cmd",
                    new PlaygroundFailure
                    {
                        Kind = PlaygroundFailureKind.BelowMinimum,
                        Message = "below minimum",
                        Remediation = "install a newer version",
                    }),
            ]);

    private sealed class Doubles
    {
        private static readonly AsyncLocal<Doubles?> CurrentValue = new();

        public Doubles()
        {
            CurrentValue.Value = this;
            Resolve = _ =>
            {
                ResolveCalls++;
                return Target;
            };
            Discover = (target, _, _) =>
            {
                DiscoverCalls++;
                return Task.FromResult(target);
            };
            Locate = _ =>
            {
                LocateCalls++;
                return LocateException is { } failure
                    ? Task.FromException<PlaygroundToolDiscoveryResult>(failure)
                    : Task.FromResult(DiscoveryResult ?? Discovery(Tool));
            };
            DetectNpm = _ =>
            {
                DetectNpmCalls++;
                return Task.FromResult(Discovery(Tool));
            };
            Install = _ =>
            {
                InstallCalls++;
                return InstallException is { } failure
                    ? Task.FromException<PlaygroundTool>(failure)
                    : Task.FromResult(InstallTool!);
            };
            Launch = async (tool, target, clientId, started, _) =>
            {
                LaunchCalls++;
                LaunchedClientId = clientId;
                started();
                StartedObservedBeforeLaunchCompleted = true;
                return 0;
            };
            Confirm = (detail, response, _) =>
            {
                PromptCalls++;
                PromptDetail = detail;
                ExpectedResponse = response;
                return Task.FromResult(PromptOutcome);
            };
            ClientConfiguration = () => AgentHostClientAppConfiguration.Create(
                ClientId.ToString("D"),
                new RuntimePlatformInfo());
        }

        public Func<LiveTestRequest, LiveTestDestination> Resolve { get; set; }
        public Func<LiveTestDestination, bool, CancellationToken, Task<LiveTestDestination>> Discover { get; set; }
        public Func<AgentHostClientAppConfiguration> ClientConfiguration { get; set; }
        public Func<CancellationToken, Task<PlaygroundToolDiscoveryResult>> Locate { get; set; }
        public Func<CancellationToken, Task<PlaygroundToolDiscoveryResult>> DetectNpm { get; set; }
        public Func<CancellationToken, Task<PlaygroundTool>> Install { get; set; }
        public Func<PlaygroundTool, LiveTestDestination, Guid, Action, CancellationToken, Task<int>> Launch { get; set; }
        public Func<string, string, CancellationToken, Task<ConfirmationOutcome>> Confirm { get; set; }
        public IPlaygroundToolLocator Locator => new DelegateLocator(Locate, DetectNpm);
        public IPlaygroundGlobalInstaller Installer => new DelegateInstaller(Install);
        public IPlaygroundLauncher Launcher => new DelegateLauncher(Launch, this);
        public IConfirmationPrompt Confirmation => new DelegateConfirmation(Confirm);
        public PlaygroundTool? Tool { get; set; } = Tool("0.2.30");
        public PlaygroundTool? InstallTool { get; set; } = Tool("0.2.30");
        public PlaygroundToolDiscoveryResult? DiscoveryResult { get; set; }
        public Exception? LocateException { get; set; }
        public bool IsInputRedirected { get; set; }
        public ConfirmationOutcome PromptOutcome { get; set; } = ConfirmationOutcome.Confirmed;
        public Exception? LaunchException { get; set; }
        public PlaygroundInstallException? InstallException { get; set; }
        public int ResolveCalls { get; set; }
        public int DiscoverCalls { get; set; }
        public int LocateCalls { get; set; }
        public int DetectNpmCalls { get; set; }
        public int InstallCalls { get; set; }
        public int PromptCalls { get; set; }
        public int LaunchCalls { get; set; }
        public Guid LaunchedClientId { get; set; }
        public bool StartedObservedBeforeLaunchCompleted { get; set; }
        public string PromptDetail { get; set; } = string.Empty;
        public string ExpectedResponse { get; set; } = string.Empty;
        public List<string> Events { get; } = [];
        public StringWriter Output { get; } = new();
        public StringWriter Error { get; } = new();
        public List<object> Telemetry { get; } = [];

        public static Doubles Current => CurrentValue.Value
            ?? throw new InvalidOperationException("Test doubles are not installed.");

        private sealed class DelegateLauncher(
            Func<PlaygroundTool, LiveTestDestination, Guid, Action, CancellationToken, Task<int>> callback,
            Doubles owner) : IPlaygroundLauncher
        {
            public Task<int> RunAsync(
                PlaygroundTool tool,
                LiveTestDestination target,
                Guid clientId,
                Action started,
                CancellationToken cancellationToken) =>
                owner.LaunchException is { } failure
                    ? Task.FromException<int>(failure)
                    : callback(tool, target, clientId, started, cancellationToken);
        }

        private sealed class DelegateInstaller(
            Func<CancellationToken, Task<PlaygroundTool>> callback) : IPlaygroundGlobalInstaller
        {
            public Task<PlaygroundTool> InstallAsync(CancellationToken cancellationToken) =>
                callback(cancellationToken);
        }

        private sealed class DelegateConfirmation(
            Func<string, string, CancellationToken, Task<ConfirmationOutcome>> callback) : IConfirmationPrompt
        {
            public Task<ConfirmationOutcome> ConfirmAsync(
                string detail,
                string expectedResponse,
                CancellationToken cancellationToken) =>
                callback(detail, expectedResponse, cancellationToken);
        }
    }

    private sealed class DelegateLocator(
        Func<CancellationToken, Task<PlaygroundToolDiscoveryResult>> locate,
        Func<CancellationToken, Task<PlaygroundToolDiscoveryResult>> detectNpm) : IPlaygroundToolLocator
    {
        public Task<PlaygroundToolDiscoveryResult> LocateAsync(CancellationToken cancellationToken) =>
            locate(cancellationToken);

        public Task<PlaygroundToolDiscoveryResult> DetectNpmAsync(CancellationToken cancellationToken) =>
            detectNpm(cancellationToken);
    }
}
