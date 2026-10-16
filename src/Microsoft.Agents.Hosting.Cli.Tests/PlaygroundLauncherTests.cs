using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Playground;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class PlaygroundLauncherTests
{
    private static readonly Guid ClientId = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly string[] InterruptThenKill = ["interrupt", "kill"];
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string EnvironmentId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void TestFixturePathsAreHostAbsoluteAndMetacharacterSafe()
    {
        var tool = Tool();

        Assert.True(Path.IsPathFullyQualified(tool.ExecutablePath));
        Assert.True(Path.IsPathFullyQualified(tool.ArgumentPrefix[0]));
        Assert.Contains(" & ", tool.ExecutablePath, StringComparison.Ordinal);
        Assert.Contains(" & ", tool.ArgumentPrefix[0], StringComparison.Ordinal);
    }

    [Fact]
    public void UnixInterruptImportTargetsTheLibcKillSymbol()
    {
        var method = typeof(ProcessPlaygroundProcess).GetMethod(
            "UnixKill",
            BindingFlags.NonPublic | BindingFlags.Static);
        var import = method?.GetCustomAttribute<DllImportAttribute>();

        Assert.NotNull(import);
        Assert.Equal("kill", import!.EntryPoint);
    }

    [Theory]
    [InlineData(LiveTestAgentType.Standard, "https://example.test/standard")]
    [InlineData(LiveTestAgentType.GitHubCopilot, "https://example.test/github")]
    public void BuildLaunchPlan_UsesTheNativeDirectEngineArguments(
        LiveTestAgentType agentType,
        string endpoint)
    {
        var tool = Tool();
        var target = Target(agentType, endpoint);

        var plan = PlaygroundLauncher.BuildLaunchPlan(tool, target, ClientId);

        Assert.Equal(tool.ExecutablePath, plan.ExecutablePath);
        Assert.Equal(
            new[]
            {
                tool.ArgumentPrefix[0],
                "start",
                "--target",
                "directengine",
                "--app-endpoint",
                endpoint,
                "--client-id",
                ClientId.ToString("D"),
                "--tenant-id",
                TenantId,
                "--environment-id",
                EnvironmentId,
                "--host",
                "127.0.0.1",
                "--disable-telemetry",
            },
            plan.Arguments);
    }

    [Fact]
    public void BuildStartInfo_UsesInheritedNativeConsoleStreamsWithoutShellExecution()
    {
        var info = PlaygroundLauncher.BuildStartInfo(
            PlaygroundLauncher.BuildLaunchPlan(
                Tool(),
                Target(LiveTestAgentType.Standard, "https://example.test/standard"),
                ClientId));

        Assert.False(info.UseShellExecute);
        Assert.False(info.RedirectStandardInput);
        Assert.False(info.RedirectStandardOutput);
        Assert.False(info.RedirectStandardError);
        Assert.False(info.CreateNoWindow);
        Assert.Equal(HostPath("nodejs & runtime", "node.exe"), info.FileName);
        Assert.Equal(
            new[]
            {
                HostPath("playground & test", "cli.js"),
                "start",
                "--target",
                "directengine",
                "--app-endpoint",
                "https://example.test/standard",
                "--client-id",
                ClientId.ToString("D"),
                "--tenant-id",
                TenantId,
                "--environment-id",
                EnvironmentId,
                "--host",
                "127.0.0.1",
                "--disable-telemetry",
            },
            info.ArgumentList);
    }

    [Fact]
    public void BuildLaunchPlan_ScrubsPlaygroundOverridesWithoutMutatingTheCallerEnvironment()
    {
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AUTH_CLIENT_ID"] = "stale-client",
            ["aUtH_cLiEnT_sEcReT"] = "secret",
            ["AUTH_TENANT_ID"] = "stale-tenant",
            ["BOT_ENDPOINT"] = "https://stale.example",
            ["DEFAULT_CHANNEL_ID"] = "directline",
            ["M365_AGENTS_PLAYGROUND_CONFIG_PATH"] = HostPath("stale", "config"),
            ["M365_AGENTS_PLAYGROUND_CONVERSATION_ID"] = "stale-conversation-prefixed",
            ["M365_AGENTS_PLAYGROUND_DELIVERY_MODE"] = "expectReplies",
            ["M365_AGENTS_PLAYGROUND_ENABLE_EVENTS_RECORDING"] = "true",
            ["M365_AGENTS_PLAYGROUND_HOST"] = "0.0.0.0",
            ["M365_AGENTS_PLAYGROUND_LOGFILE"] = "stale-prefixed.log",
            ["M365_AGENTS_PLAYGROUND_RECORDING_FILE"] = "stale-prefixed.json",
            ["M365_AGENTS_PLAYGROUND_SERVICE_URL"] = "https://stale.example/service",
            ["M365_AGENTS_PLAYGROUND_TARGET_SDK"] = "directline",
            ["CONVERSATION_ID"] = "stale-conversation",
            ["DELIVERY_MODE"] = "expectReplies",
            ["ENABLE_EVENTS_RECORDING"] = "true",
            ["HOST"] = "0.0.0.0",
            ["LOGFILE"] = "stale.log",
            ["RECORDING_FILE"] = "stale.json",
            ["SERVICE_URL"] = "https://stale.example/service",
            ["TARGET_SDK"] = "directline",
            ["TEAMS_APP_TEST_TOOL_CONFIG_PATH"] = HostPath("stale", "teams.json"),
            ["TEAMSAPPTESTER_LOGFILE"] = "stale-teams.log",
            ["PORT"] = "9999",
            ["BROWSER"] = "false",
            ["DiReCtLiNe_ToKeN"] = "secret",
            ["DIRECT_LINE_SECRET"] = "secret",
            ["NODE_OPTIONS"] = $"--require {HostPath("attacker.js")}",
            ["NODE_PATH"] = HostPath("attacker"),
            ["NPM_CONFIG_REGISTRY"] = "https://registry.example",
        };
        var original = new Dictionary<string, string>(inherited, StringComparer.OrdinalIgnoreCase);

        var plan = PlaygroundLauncher.BuildLaunchPlan(
            Tool(),
            Target(LiveTestAgentType.Standard, "https://example.test/standard"),
            ClientId,
            inherited);

        Assert.Equal(original, inherited);
        Assert.DoesNotContain(plan.Environment.Keys, name =>
            PlaygroundLauncher.IsScrubbedEnvironmentVariable(name));
        Assert.Equal("https://registry.example", plan.Environment["NPM_CONFIG_REGISTRY"]);
    }

    [Fact]
    public void BuildLaunchPlan_IsolatesInheritedAndLocalConfigFromAnAbsentOwnedConfig()
    {
        var sourceDirectory = Path.Combine(
            AppContext.BaseDirectory,
            $"playground-source-{Guid.NewGuid():N}");
        var ownedDirectory = Path.Combine(
            AppContext.BaseDirectory,
            $"playground-owned-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(ownedDirectory);

        var modernSourceConfig = Path.Combine(sourceDirectory, "m365agentsplayground.yml");
        var legacySourceConfig = Path.Combine(sourceDirectory, "teamsapptester.yml");
        File.WriteAllText(modernSourceConfig, "target: directline");
        File.WriteAllText(legacySourceConfig, "target: directline");

        var inherited = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["M365_AGENTS_PLAYGROUND_CONFIG_PATH"] = modernSourceConfig,
            ["TEAMS_APP_TEST_TOOL_CONFIG_PATH"] = legacySourceConfig,
        };
        var original = new Dictionary<string, string>(inherited, StringComparer.Ordinal);
        var absentConfig = Path.Combine(ownedDirectory, "absent-playground-config");

        try
        {
            var plan = PlaygroundLauncher.BuildLaunchPlan(
                Tool(),
                Target(LiveTestAgentType.Standard, "https://example.test/standard"),
                ClientId,
                inherited,
                ownedDirectory,
                absentConfig);

            Assert.Equal(original, inherited);
            Assert.Equal(ownedDirectory, plan.WorkingDirectory);
            Assert.Equal(
                absentConfig,
                plan.Environment["M365_AGENTS_PLAYGROUND_CONFIG_PATH"]);
            Assert.Equal(
                absentConfig,
                plan.Environment["TEAMS_APP_TEST_TOOL_CONFIG_PATH"]);
            Assert.DoesNotContain(
                plan.Environment.Values,
                value => string.Equals(value, modernSourceConfig, StringComparison.Ordinal) ||
                         string.Equals(value, legacySourceConfig, StringComparison.Ordinal));
            Assert.False(File.Exists(absentConfig));
        }
        finally
        {
            Directory.Delete(sourceDirectory, recursive: true);
            Directory.Delete(ownedDirectory, recursive: true);
        }
    }

    [Fact]
    public void BuildLaunchPlan_PreservesCaseDistinctEnvironmentNames()
    {
        var inherited = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "upper",
            ["path"] = "lower",
        };

        var plan = PlaygroundLauncher.BuildLaunchPlan(
            Tool(),
            Target(LiveTestAgentType.Standard, "https://example.test/standard"),
            ClientId,
            inherited);

        Assert.Equal("upper", plan.Environment["PATH"]);
        Assert.Equal("lower", plan.Environment["path"]);
    }

    [Fact]
    public async Task RunAsync_DeletesItsOwnedInvocationDirectoryAfterExit()
    {
        var process = new FakeProcess { Exited = true };
        var factory = new RecordingProcessFactory(process);
        var launcher = new PlaygroundLauncher(factory, TimeSpan.FromMilliseconds(1));

        var exitCode = await launcher.RunAsync(
            Tool(),
            Target(LiveTestAgentType.Standard, "https://example.test/standard"),
            ClientId,
            () => { },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.NotNull(factory.StartInfo);
        Assert.NotNull(factory.StartInfo!.WorkingDirectory);
        Assert.False(Directory.Exists(factory.StartInfo.WorkingDirectory));
        Assert.False(File.Exists(
            factory.StartInfo.Environment["M365_AGENTS_PLAYGROUND_CONFIG_PATH"]));
        Assert.False(File.Exists(
            factory.StartInfo.Environment["TEAMS_APP_TEST_TOOL_CONFIG_PATH"]));
    }

    [Theory]
    [InlineData("http://example.test/agent")]
    [InlineData("file:///agent")]
    public async Task RunAsync_RejectsUnsafeEndpointsBeforeStartingAProcess(string endpoint)
    {
        var factory = new RecordingProcessFactory();
        var launcher = new PlaygroundLauncher(factory, TimeSpan.FromMilliseconds(1));

        var error = await Assert.ThrowsAsync<PlaygroundLaunchException>(() =>
            launcher.RunAsync(
                Tool(),
                Target(LiveTestAgentType.Standard, endpoint),
                ClientId,
                () => { },
                CancellationToken.None));

        Assert.Equal(PlaygroundLaunchFailureKind.InvalidTarget, error.Kind);
        Assert.Equal(0, factory.StartCount);
    }

    [Fact]
    public async Task RunAsync_RejectsUndefinedHarnessBeforeStartingAProcess()
    {
        var factory = new RecordingProcessFactory();
        var launcher = new PlaygroundLauncher(factory, TimeSpan.FromMilliseconds(1));

        var error = await Assert.ThrowsAsync<PlaygroundLaunchException>(() =>
            launcher.RunAsync(
                Tool(),
                Target((LiveTestAgentType)99, "https://example.test/agent"),
                ClientId,
                () => { },
                CancellationToken.None));

        Assert.Equal(PlaygroundLaunchFailureKind.InvalidTarget, error.Kind);
        Assert.Equal(0, factory.StartCount);
    }

    [Fact]
    public void BuildLaunchPlan_RejectsAnEmptyClientId()
    {
        var error = Assert.Throws<PlaygroundLaunchException>(() =>
            PlaygroundLauncher.BuildLaunchPlan(
                Tool(),
                Target(LiveTestAgentType.Standard, "https://example.test/agent"),
                Guid.Empty));

        Assert.Equal(PlaygroundLaunchFailureKind.InvalidClientId, error.Kind);
        Assert.NotEmpty(error.Remediation);
    }

    [Fact]
    public void BuildLaunchPlan_RejectsAnEmptyTenantId()
    {
        var target = new LiveTestDestination(
            EnvironmentId,
            Guid.Empty.ToString("D"),
            "Bot01",
            "explicit",
            LiveTestAgentType.Standard,
            new Uri("https://example.test/agent"));

        var error = Assert.Throws<PlaygroundLaunchException>(() =>
            PlaygroundLauncher.BuildLaunchPlan(
                Tool(),
                target,
                ClientId));

        Assert.Equal(PlaygroundLaunchFailureKind.InvalidTenantId, error.Kind);
        Assert.NotEmpty(error.Remediation);
    }

    [Fact]
    public async Task RunAsync_ReportsProcessStartFailuresWithoutLeakingTheOriginalException()
    {
        var factory = new ThrowingProcessFactory(new InvalidOperationException("secret tool"));
        var launcher = new PlaygroundLauncher(factory, TimeSpan.FromMilliseconds(1));

        var error = await Assert.ThrowsAsync<PlaygroundLaunchException>(() =>
            launcher.RunAsync(
                Tool(),
                Target(LiveTestAgentType.Standard, "https://example.test/agent"),
                ClientId,
                () => { },
                CancellationToken.None));

        Assert.Equal(PlaygroundLaunchFailureKind.StartFailed, error.Kind);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(error.Remediation);
    }

    [Fact]
    public async Task RunAsync_CancellationInterruptsThenKillsOwnedProcessAndReturns130()
    {
        var process = new FakeProcess();
        var factory = new RecordingProcessFactory(process);
        var launcher = new PlaygroundLauncher(factory, TimeSpan.FromMilliseconds(20));
        using var cancellation = new CancellationTokenSource();

        var exitCode = await launcher.RunAsync(
            Tool(),
            Target(LiveTestAgentType.Standard, "https://example.test/agent"),
            ClientId,
            cancellation.Cancel,
            cancellation.Token);

        Assert.Equal(130, exitCode);
        Assert.Equal(InterruptThenKill, process.Events);
        Assert.Equal(1, factory.StartCount);
    }

    [Fact]
    public async Task RunAsync_CleansUpWhenTheStartedCallbackFails()
    {
        var process = new FakeProcess();
        var launcher = new PlaygroundLauncher(
            new RecordingProcessFactory(process),
            TimeSpan.FromMilliseconds(1));
        var callbackFailure = new InvalidOperationException("writer failed");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.RunAsync(
                Tool(),
                Target(LiveTestAgentType.Standard, "https://example.test/agent"),
                ClientId,
                () => throw callbackFailure,
                CancellationToken.None));

        Assert.Same(callbackFailure, error);
        Assert.Equal(InterruptThenKill, process.Events);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task RunAsync_CleansUpWhenWaitingForTheChildFails()
    {
        var process = new FakeProcess
        {
            WaitFailure = new InvalidOperationException("wait failed"),
        };
        var launcher = new PlaygroundLauncher(
            new RecordingProcessFactory(process),
            TimeSpan.FromMilliseconds(1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.RunAsync(
                Tool(),
                Target(LiveTestAgentType.Standard, "https://example.test/agent"),
                ClientId,
                () => { },
                CancellationToken.None));

        Assert.Equal("wait failed", error.Message);
        Assert.Equal(InterruptThenKill, process.Events);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task RunAsync_ReportsCleanupFailureAfterBothShutdownBudgets()
    {
        var process = new FakeProcess { IgnoreKill = true };
        var launcher = new PlaygroundLauncher(
            new RecordingProcessFactory(process),
            TimeSpan.FromMilliseconds(1));
        using var cancellation = new CancellationTokenSource();

        var error = await Assert.ThrowsAsync<PlaygroundLaunchException>(() =>
            launcher.RunAsync(
                Tool(),
                Target(LiveTestAgentType.Standard, "https://example.test/agent"),
                ClientId,
                cancellation.Cancel,
                cancellation.Token));

        Assert.Equal(PlaygroundLaunchFailureKind.CleanupFailed, error.Kind);
        Assert.Equal(InterruptThenKill, process.Events);
    }

    [Fact]
    public async Task RunAsync_PreservesIndependentChildExitCode()
    {
        var process = new FakeProcess { Exited = true, ExitCode = 37 };
        var launcher = new PlaygroundLauncher(new RecordingProcessFactory(process), TimeSpan.FromMilliseconds(1));

        var exitCode = await launcher.RunAsync(
            Tool(),
            Target(LiveTestAgentType.Standard, "https://example.test/agent"),
            ClientId,
            () => { },
            CancellationToken.None);

        Assert.Equal(37, exitCode);
        Assert.Empty(process.Events);
    }

    [Fact]
    public async Task RunAsync_RetriesTransientInvocationDirectoryLocks()
    {
        var attempts = new List<string>();
        var delays = new List<TimeSpan>();
        var launcher = new PlaygroundLauncher(
            new RecordingProcessFactory(new FakeProcess { Exited = true, ExitCode = 0 }),
            TimeSpan.FromMilliseconds(1),
            directory =>
            {
                attempts.Add(directory);
                if (attempts.Count < 3)
                {
                    throw new IOException("locked");
                }

                Directory.Delete(directory, recursive: true);
            },
            delay =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var exitCode = await launcher.RunAsync(
            Tool(),
            Target(LiveTestAgentType.Standard, "https://example.test/agent"),
            ClientId,
            () => { },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(3, attempts.Count);
        Assert.Single(attempts.Distinct(StringComparer.Ordinal));
        Assert.Equal(PlaygroundLauncher.InvocationDirectoryDeleteRetryDelays.Take(2), delays);
        Assert.False(Directory.Exists(attempts[0]));
    }

    [Fact]
    public async Task RunAsync_ReportsInvocationDirectoryCleanupFailureAfterBoundedRetries()
    {
        var attempts = new List<string>();
        var lastFailure = new UnauthorizedAccessException("denied");
        var launcher = new PlaygroundLauncher(
            new RecordingProcessFactory(new FakeProcess { Exited = true, ExitCode = 0 }),
            TimeSpan.FromMilliseconds(1),
            directory =>
            {
                attempts.Add(directory);
                throw attempts.Count % 2 == 0 ? lastFailure : new IOException("locked");
            },
            _ => Task.CompletedTask);

        try
        {
            var error = await Assert.ThrowsAsync<PlaygroundLaunchException>(() =>
                launcher.RunAsync(
                    Tool(),
                    Target(LiveTestAgentType.Standard, "https://example.test/agent"),
                    ClientId,
                    () => { },
                    CancellationToken.None));

            Assert.Equal(PlaygroundLaunchFailureKind.CleanupFailed, error.Kind);
            Assert.Equal(
                PlaygroundLauncher.InvocationDirectoryDeleteRetryDelays.Length + 1,
                attempts.Count);
            Assert.Equal(lastFailure, error.InnerException);
        }
        finally
        {
            if (attempts.Count > 0 && Directory.Exists(attempts[0]))
            {
                Directory.Delete(attempts[0], recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_DoesNotRetryNonTransientInvocationDirectoryFailures()
    {
        var attempts = new List<string>();
        var failure = new InvalidOperationException("unexpected");
        var launcher = new PlaygroundLauncher(
            new RecordingProcessFactory(new FakeProcess { Exited = true, ExitCode = 0 }),
            TimeSpan.FromMilliseconds(1),
            directory =>
            {
                attempts.Add(directory);
                throw failure;
            },
            _ => throw new Xunit.Sdk.XunitException("A non-transient failure must not be retried."));

        try
        {
            var error = await Assert.ThrowsAsync<PlaygroundLaunchException>(() =>
                launcher.RunAsync(
                    Tool(),
                    Target(LiveTestAgentType.Standard, "https://example.test/agent"),
                    ClientId,
                    () => { },
                    CancellationToken.None));

            Assert.Equal(PlaygroundLaunchFailureKind.CleanupFailed, error.Kind);
            Assert.Single(attempts);
            Assert.Same(failure, error.InnerException);
        }
        finally
        {
            if (attempts.Count > 0 && Directory.Exists(attempts[0]))
            {
                Directory.Delete(attempts[0], recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_UsesARealNodeFixtureAndTerminatesOnlyItsOwnedProcess()
    {
        var node = FindNode();
        if (node is null)
        {
            return;
        }

        var fixture = Path.Combine(
            AppContext.BaseDirectory,
            $"playground-launcher-fixture-{Guid.NewGuid():N}.js");
        var pidFile = fixture + ".pid";
        await File.WriteAllTextAsync(
            fixture,
            """
            const fs = require("fs");
            fs.writeFileSync(process.argv[2], String(process.pid));
            setInterval(() => {}, 1000);
            """);

        using var cancellation = new CancellationTokenSource();
        Task<int>? runTask = null;
        try
        {
            var launcher = new PlaygroundLauncher(
                new ProcessPlaygroundProcessFactory(),
                TimeSpan.FromSeconds(2));
            runTask = launcher.RunAsync(
                new PlaygroundTool(
                    node,
                    [fixture, pidFile],
                    node,
                    PlaygroundVersion.Parse("0.2.29")),
                Target(LiveTestAgentType.Standard, "https://example.test/agent"),
                ClientId,
                () => { },
                cancellation.Token);

            var pid = await WaitForPidMarkerAsync(pidFile);
            cancellation.Cancel();

            Assert.Equal(130, await runTask);
            await WaitForProcessExitAsync(pid);
        }
        finally
        {
            cancellation.Cancel();
            if (runTask is not null)
            {
                await runTask;
            }

            File.Delete(pidFile);
            File.Delete(fixture);
        }
    }

    [Fact]
    public async Task ReadinessFailure_CancelsAndAwaitsTheOwnedLauncherTask()
    {
        var markerPath = Path.Combine(
            AppContext.BaseDirectory,
            $"playground-launcher-missing-{Guid.NewGuid():N}.pid");
        var process = new FakeProcess();
        using var cancellation = new CancellationTokenSource();
        Task<int>? runTask = null;

        try
        {
            runTask = new PlaygroundLauncher(
                    new RecordingProcessFactory(process),
                    TimeSpan.FromMilliseconds(1))
                .RunAsync(
                    Tool(),
                    Target(LiveTestAgentType.Standard, "https://example.test/agent"),
                    ClientId,
                    () => { },
                    cancellation.Token);

            await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() =>
                WaitForPidMarkerAsync(
                    markerPath,
                    timeout: TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            cancellation.Cancel();
            if (runTask is not null)
            {
                Assert.Equal(130, await runTask);
            }

            File.Delete(markerPath);
        }

        Assert.Equal(InterruptThenKill, process.Events);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task WaitForPidMarkerAsync_WaitsForACompleteMarker()
    {
        var markerPath = Path.Combine(
            AppContext.BaseDirectory,
            $"playground-launcher-marker-{Guid.NewGuid():N}.pid");
        await File.WriteAllTextAsync(markerPath, string.Empty);

        try
        {
            var invalidMarkerObserved = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var writer = Task.Run(async () =>
            {
                await invalidMarkerObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await File.WriteAllTextAsync(markerPath, "12345");
            });

            Assert.Equal(
                12345,
                await WaitForPidMarkerAsync(
                    markerPath,
                    () => invalidMarkerObserved.TrySetResult(true)));
            await writer;
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    private static string HostPath(params string[] parts) =>
        Path.Combine([Path.GetTempPath(), "msagent playground & tests", .. parts]);

    private static PlaygroundTool Tool()
    {
        var executable = HostPath("nodejs & runtime", "node.exe");
        var argumentPrefix = HostPath("playground & test", "cli.js");
        return new PlaygroundTool(
            executable,
            [argumentPrefix],
            executable,
            PlaygroundVersion.Parse("0.2.29"));
    }

    private static LiveTestDestination Target(LiveTestAgentType agentType, string endpoint) =>
        new(EnvironmentId, TenantId, "Bot01", "explicit", agentType, new Uri(endpoint));

    private static string? FindNode()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "node.exe" : "node");
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<int> WaitForPidMarkerAsync(
        string path,
        Action? invalidMarkerObserved = null,
        TimeSpan? timeout = null)
    {
        var watchdog = timeout ?? TimeSpan.FromSeconds(10);
        var deadline = Stopwatch.GetTimestamp() +
                       (long)(watchdog.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    var marker = (await File.ReadAllTextAsync(path)).Trim();
                    if (int.TryParse(marker, out var processId) && processId > 0)
                    {
                        return processId;
                    }

                    invalidMarkerObserved?.Invoke();
                }
                catch (FileNotFoundException)
                {
                    invalidMarkerObserved?.Invoke();
                }
                catch (IOException exception) when (IsTransientMarkerSharing(exception))
                {
                    invalidMarkerObserved?.Invoke();
                }
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException(
            $"The Node fixture did not publish a valid PID marker within {watchdog.TotalSeconds:0.#} seconds.");
    }

    private static bool IsTransientMarkerSharing(IOException exception) =>
        OperatingSystem.IsWindows()
            ? exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
            : false;

    private static async Task WaitForProcessExitAsync(int processId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException($"Owned process {processId} did not exit.");
    }

    private sealed class RecordingProcessFactory(IPlaygroundProcess? process = null) : IPlaygroundProcessFactory
    {
        public int StartCount { get; private set; }

        public ProcessStartInfo? StartInfo { get; private set; }

        public IPlaygroundProcess Start(ProcessStartInfo startInfo)
        {
            StartCount++;
            StartInfo = startInfo;
            return process ?? new FakeProcess();
        }
    }

    private sealed class ThrowingProcessFactory(Exception exception) : IPlaygroundProcessFactory
    {
        public IPlaygroundProcess Start(ProcessStartInfo startInfo) => throw exception;
    }

    private sealed class FakeProcess : IPlaygroundProcess
    {
        public List<string> Events { get; } = [];

        public int Id => 12345;

        public bool Exited { get; set; }

        public bool HasExited => Exited;

        public bool IgnoreKill { get; init; }

        public int ExitCode { get; set; }

        public bool Disposed { get; private set; }

        public Exception? WaitFailure { get; init; }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            if (WaitFailure is not null)
            {
                return Task.FromException(WaitFailure);
            }

            if (Exited)
            {
                return Task.CompletedTask;
            }

            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public void SendInterrupt() => Events.Add("interrupt");

        public void KillTree()
        {
            Events.Add("kill");
            if (!IgnoreKill)
            {
                Exited = true;
            }
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
