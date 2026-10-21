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
        LiveTestAgentType.Standard,
        new Uri("https://example.test/agent"));

    [Theory]
    [InlineData("directengine")]
    [InlineData("directline")]
    public async Task JsonIsRejectedBeforeTargetResolutionOrToolDiscovery(string target)
    {
        var doubles = new Doubles();
        var execution = CreateExecution(
            new PlaygroundRequest { Target = target, AgentType = "standard", Json = true },
            doubles);

        var exitCode = await RenderAsync(execution);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("not yet supported for playground", doubles.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, doubles.ResolveCalls);
        Assert.Equal(0, doubles.ClientConfigurationCalls);
        Assert.Equal(0, doubles.LocateCalls);
        Assert.Equal(0, doubles.PromptCalls);
        Assert.Equal(0, doubles.LaunchCalls);
    }

    [Theory]
    [InlineData("resolve", 11049)]
    [InlineData("client", 12003)]
    [InlineData("locate", 12003)]
    [InlineData("install", 12003)]
    [InlineData("launch", 12003)]
    public async Task TypedFailuresRetainTheirErrorCodeForTelemetry(string stage, int expectedErrorCode)
    {
        AgentHostingException failure = stage == "resolve"
            ? ManagedAgentException.ConnectorNotServed("connector", "environment", [], "list-command")
            : new ClientApplicationConfigurationException("client-configuration");
        var doubles = new Doubles();
        switch (stage)
        {
            case "resolve":
                doubles.Resolve = _ => throw failure;
                break;
            case "client":
                doubles.ClientConfiguration = () => throw failure;
                break;
            case "locate":
                doubles.LocateException = failure;
                break;
            case "install":
                doubles.Tool = null;
                doubles.Install = _ => Task.FromException<PlaygroundTool>(failure);
                break;
            case "launch":
                doubles.LaunchException = failure;
                break;
        }
        var execution = CreateExecution(Request() with { InstallPlayground = true }, doubles);

        Assert.Equal(stage == "resolve" ? ExitCodes.Usage : ExitCodes.Failure, await RenderAsync(execution));

        var result = Assert.IsAssignableFrom<IStreamingFailureResult>(execution);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
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
        doubles.Launch = async (tool, connection, started, _) =>
        {
            doubles.Events.Add("launch");
            doubles.LaunchCalls++;
            doubles.LaunchedClientId = Assert.IsType<PlaygroundConnection.DirectEngine>(connection).ClientId;
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

    [Theory]
    [InlineData("directengine")]
    [InlineData("DIRECTENGINE")]
    public async Task ExplicitDirectEnginePreservesResolutionAndClientConfiguration(string target)
    {
        var doubles = new Doubles();

        var exitCode = await RenderAsync(CreateExecution(Request() with { Target = target }, doubles));

        Assert.Equal(0, exitCode);
        Assert.Equal(1, doubles.ResolveCalls);
        Assert.Equal(1, doubles.ClientConfigurationCalls);
        var connection = Assert.IsType<PlaygroundConnection.DirectEngine>(doubles.LaunchedConnection);
        Assert.Equal(Target, connection.Target);
        Assert.Equal(ClientId, connection.ClientId);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task DirectLineLaunchesWithoutResolvingTargetOrClient(
        bool install,
        bool plain,
        bool uppercase)
    {
        var doubles = new Doubles
        {
            Resolve = _ => throw new InvalidOperationException("resolver must not run"),
            ClientConfiguration = () => throw new InvalidOperationException("client must not run"),
        };
        if (install)
        {
            doubles.Tool = null;
        }

        var exitCode = await RenderAsync(CreateExecution(new PlaygroundRequest
        {
            Target = uppercase ? "DIRECTLINE" : "directline",
            Plain = plain,
            InstallPlayground = install,
        }, doubles));

        Assert.Equal(0, exitCode);
        Assert.IsType<PlaygroundConnection.DirectLine>(doubles.LaunchedConnection);
        Assert.Equal(0, doubles.ClientConfigurationCalls);
        Assert.Equal(install ? 1 : 0, doubles.InstallCalls);
        Assert.Equal(install ? 1 : 0, doubles.PromptCalls);
        Assert.Contains("real effects", doubles.Output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("masked terminal prompt", doubles.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("device code", doubles.Output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(!plain, doubles.Output.ToString().Contains("process started", StringComparison.OrdinalIgnoreCase));
        var details = Assert.IsType<PlaygroundTelemetryDetails>(Assert.Single(doubles.Telemetry));
        Assert.Equal(PlaygroundErrorCategory.None, details.ErrorCategory);
    }

    public static TheoryData<PlaygroundRequest> InvalidRequests => new()
    {
        new PlaygroundRequest { Target = "unsupported-private-value" },
        new PlaygroundRequest(),
        new PlaygroundRequest { AgentType = " " },
        new PlaygroundRequest { Target = "directline", AgentType = "standard" },
        new PlaygroundRequest { Target = "directline", Project = "private-project" },
        new PlaygroundRequest { Target = "directline", AgentId = "private-id" },
        new PlaygroundRequest { Target = "directline", AgentName = "private-name" },
        new PlaygroundRequest { Target = "directline", SchemaName = "private-schema" },
        new PlaygroundRequest { Target = "directline", DeploymentId = "private-id" },
        new PlaygroundRequest { Target = "directline", DeploymentName = "private-name" },
        new PlaygroundRequest { Target = "directline", EnvironmentId = "private-environment" },
        new PlaygroundRequest { Target = "directline", Tenant = "private-tenant" },
        new PlaygroundRequest { Target = "directline", Project = "" },
        new PlaygroundRequest { Target = "directline", NonInteractive = true },
        new PlaygroundRequest { Target = "directline", NonInteractive = true, InstallPlayground = true },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task InvalidTransportOptionsFailBeforeAnySideEffects(PlaygroundRequest request)
    {
        var doubles = new Doubles { Tool = null };
        var execution = CreateExecution(request, doubles);

        var exitCode = await RenderAsync(execution);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Equal(0, doubles.ResolveCalls);
        Assert.Equal(0, doubles.ClientConfigurationCalls);
        Assert.Equal(0, doubles.LocateCalls);
        Assert.Equal(0, doubles.PromptCalls);
        Assert.Equal(0, doubles.InstallCalls);
        Assert.Equal(0, doubles.LaunchCalls);
        Assert.Empty(doubles.Output.ToString());
        Assert.DoesNotContain("private", doubles.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CommandTelemetryOutcome.Usage, CommandTreeBuilder.StreamingOutcome(execution, exitCode));
        var details = Assert.IsType<PlaygroundTelemetryDetails>(Assert.Single(doubles.Telemetry));
        Assert.Equal(PlaygroundErrorCategory.Unsupported, details.ErrorCategory);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DirectLineRejectsRedirectedConsoleBeforeInstallation(bool input, bool output)
    {
        var doubles = new Doubles
        {
            Tool = null,
            IsInputRedirected = input,
            IsOutputRedirected = output,
        };

        var exitCode = await RenderAsync(CreateExecution(new PlaygroundRequest
        {
            Target = "directline",
            InstallPlayground = true,
        }, doubles));

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Equal(0, doubles.ResolveCalls);
        Assert.Equal(0, doubles.ClientConfigurationCalls);
        Assert.Equal(0, doubles.LocateCalls);
        Assert.Equal(0, doubles.InstallCalls);
        Assert.Equal(0, doubles.PromptCalls);
        Assert.Equal(0, doubles.LaunchCalls);
        Assert.Contains("interactive terminal for both input and output", doubles.Error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(130)]
    public async Task DirectLinePreservesIndependentChildExitCode(int childExitCode)
    {
        var doubles = new Doubles { Launch = (_, _, _, _) => Task.FromResult(childExitCode) };
        var execution = CreateExecution(new PlaygroundRequest { Target = "directline" }, doubles);

        Assert.Equal(childExitCode, await RenderAsync(execution));
        Assert.Equal(PlaygroundErrorCategory.LaunchFailed,
            Assert.IsType<PlaygroundTelemetryDetails>(Assert.Single(doubles.Telemetry)).ErrorCategory);
    }

    [Fact]
    public async Task DirectLineFailureDoesNotExposeNativeErrorDetails()
    {
        var doubles = new Doubles
        {
            LaunchException = new InvalidOperationException("private-token-endpoint-and-secret"),
        };

        Assert.Equal(ExitCodes.Failure, await RenderAsync(CreateExecution(
            new PlaygroundRequest { Target = "directline" }, doubles)));
        Assert.DoesNotContain("private", doubles.Error.ToString(), StringComparison.Ordinal);
    }

    private static PlaygroundRequest Request() => new()
    {
        AgentType = "standard",
        SchemaName = "schema",
        EnvironmentId = "environment",
        Tenant = "11111111-1111-1111-1111-111111111111",
    };

    private static PlaygroundExecution CreateExecution(PlaygroundRequest request, Doubles doubles) =>
        new(
            request,
            doubles.Resolve,
            doubles.Locator,
            doubles.Installer,
            doubles.Launcher,
            doubles.Confirmation,
            () =>
            {
                doubles.ClientConfigurationCalls++;
                return doubles.ClientConfiguration();
            },
            () => doubles.IsInputRedirected,
            () => doubles.IsOutputRedirected,
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
            Launch = async (tool, connection, started, _) =>
            {
                LaunchCalls++;
                LaunchedConnection = connection;
                if (connection is PlaygroundConnection.DirectEngine engine)
                {
                    LaunchedClientId = engine.ClientId;
                }
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
        public Func<AgentHostClientAppConfiguration> ClientConfiguration { get; set; }
        public Func<CancellationToken, Task<PlaygroundToolDiscoveryResult>> Locate { get; set; }
        public Func<CancellationToken, Task<PlaygroundToolDiscoveryResult>> DetectNpm { get; set; }
        public Func<CancellationToken, Task<PlaygroundTool>> Install { get; set; }
        public Func<PlaygroundTool, PlaygroundConnection, Action, CancellationToken, Task<int>> Launch { get; set; }
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
        public bool IsOutputRedirected { get; set; }
        public ConfirmationOutcome PromptOutcome { get; set; } = ConfirmationOutcome.Confirmed;
        public Exception? LaunchException { get; set; }
        public PlaygroundInstallException? InstallException { get; set; }
        public int ResolveCalls { get; set; }
        public int ClientConfigurationCalls { get; set; }
        public int LocateCalls { get; set; }
        public int DetectNpmCalls { get; set; }
        public int InstallCalls { get; set; }
        public int PromptCalls { get; set; }
        public int LaunchCalls { get; set; }
        public Guid LaunchedClientId { get; set; }
        public PlaygroundConnection? LaunchedConnection { get; set; }
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
            Func<PlaygroundTool, PlaygroundConnection, Action, CancellationToken, Task<int>> callback,
            Doubles owner) : IPlaygroundLauncher
        {
            public Task<int> RunAsync(
                PlaygroundTool tool,
                PlaygroundConnection connection,
                Action started,
                CancellationToken cancellationToken) =>
                owner.LaunchException is { } failure
                    ? Task.FromException<int>(failure)
                    : callback(tool, connection, started, cancellationToken);
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
