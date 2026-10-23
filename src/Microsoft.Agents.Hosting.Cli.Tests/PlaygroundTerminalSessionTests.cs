using System.Diagnostics;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Playground;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Xunit.Sdk;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class PlaygroundTerminalSessionTests
{
    [Fact]
    public async Task RunAsync_PreservesCallersScrubbedEnvironmentForNativeProcess()
    {
        const string callerPath = @"C:\caller\node";
        const string callerCertificate = @"C:\certs\caller-ca.pem";
        var callerEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = callerPath,
            ["NODE_EXTRA_CA_CERTS"] = callerCertificate,
            ["CLIENT_SECRET"] = "must-not-be-forwarded",
            [PlaygroundLauncher.ModernConfigPathEnvironmentVariable] = @"C:\caller\playground.yml",
            [PlaygroundLauncher.LegacyConfigPathEnvironmentVariable] = @"C:\caller\legacy.yml",
        };
        var processFactory = new RecordingEnvironmentProcessFactory();
        Task<int>? childTask = null;
        var launcher = new PlaygroundTerminalSession(
            pipe => childTask = PlaygroundTerminalSession.RunChildAsync(
                pipe,
                environment => new PlaygroundLauncher(
                    processFactory, TimeSpan.FromMilliseconds(1), environment),
                CancellationToken.None),
            TimeSpan.FromSeconds(10),
            callerEnvironment);

        var result = await launcher.RunAsync(
            Tool(), new PlaygroundConnection.DirectLine(), () => { }, CancellationToken.None);
        Assert.NotNull(childTask);
        Assert.Equal(0, await childTask.WaitAsync(TimeSpan.FromSeconds(10)));

        var actualEnvironment = Assert.IsType<ProcessStartInfo>(processFactory.StartInfo).Environment;
        Assert.Equal(callerPath, actualEnvironment["PATH"]);
        Assert.Equal(callerCertificate, actualEnvironment["NODE_EXTRA_CA_CERTS"]);
        Assert.False(actualEnvironment.ContainsKey("CLIENT_SECRET"));
        Assert.NotEqual(callerEnvironment[PlaygroundLauncher.ModernConfigPathEnvironmentVariable],
            actualEnvironment[PlaygroundLauncher.ModernConfigPathEnvironmentVariable]);
        Assert.Equal(
            actualEnvironment[PlaygroundLauncher.ModernConfigPathEnvironmentVariable],
            actualEnvironment[PlaygroundLauncher.LegacyConfigPathEnvironmentVariable]);
        Assert.StartsWith(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Agents", "Playground", "sessions"),
            actualEnvironment[PlaygroundLauncher.ModernConfigPathEnvironmentVariable],
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ReturnsAfterStartupWhileChildRetainsSessionOwnership(bool directLine)
    {
        using var childLifetime = new CancellationTokenSource();
        using var parentLifetime = new CancellationTokenSource();
        var childLauncher = new HoldingLauncher();
        Task<int>? childTask = null;
        var launcher = new PlaygroundTerminalSession(
            pipe => childTask = PlaygroundTerminalSession.RunChildAsync(
                pipe, childLauncher, childLifetime.Token),
            TimeSpan.FromSeconds(10));
        PlaygroundConnection connection = directLine
            ? new PlaygroundConnection.DirectLine()
            : new PlaygroundConnection.DirectEngine(
                new LiveTestDestination(
                    "22222222-2222-2222-2222-222222222222",
                    "11111111-1111-1111-1111-111111111111",
                    "test_agent", "explicit",
                    Endpoint: new Uri("https://example.test/agent")),
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        var started = false;

        try
        {
            var result = await launcher.RunAsync(
                Tool(), connection, () => started = true, parentLifetime.Token);

            Assert.Equal(0, result);
            Assert.True(started);
            Assert.NotNull(childTask);
            Assert.False(childTask.IsCompleted);
            Assert.Equal(Tool().ExecutablePath, childLauncher.Tool!.ExecutablePath);
            Assert.Equal(Tool().ArgumentPrefix, childLauncher.Tool.ArgumentPrefix);
            Assert.Equal(Tool().SelectedPath, childLauncher.Tool.SelectedPath);
            Assert.Equal(Tool().Version, childLauncher.Tool.Version);
            Assert.Equal(connection, childLauncher.Connection);
            parentLifetime.Cancel();
            Assert.False(childLifetime.IsCancellationRequested);
            Assert.False(childTask.IsCompleted);
        }
        finally
        {
            childLifetime.Cancel();
            if (childTask is not null)
            {
                await childTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task RunAsync_ReportsChildStartupFailureWithoutReportingStarted()
    {
        Task<int>? childTask = null;
        var launcher = new PlaygroundTerminalSession(
            pipe => childTask = PlaygroundTerminalSession.RunChildAsync(
                pipe, new HoldingLauncher { Fail = true }, CancellationToken.None),
            TimeSpan.FromSeconds(10));
        var started = false;

        var failure = await Assert.ThrowsAsync<PlaygroundLaunchException>(() =>
            launcher.RunAsync(
                Tool(), new PlaygroundConnection.DirectLine(),
                () => started = true, CancellationToken.None));

        Assert.Equal(PlaygroundLaunchFailureKind.StartFailed, failure.Kind);
        Assert.False(started);
        Assert.NotNull(childTask);
        Assert.Equal(1, await childTask.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task RunAsync_BoundsTerminalStartupAndSanitizesErrors()
    {
        var launcher = new PlaygroundTerminalSession(_ => { }, TimeSpan.FromMilliseconds(100));

        var failure = await Assert.ThrowsAsync<PlaygroundLaunchException>(() =>
            launcher.RunAsync(
                Tool(), new PlaygroundConnection.DirectLine(),
                () => Assert.Fail("Startup was not confirmed."), CancellationToken.None));

        Assert.Equal(PlaygroundLaunchFailureKind.StartFailed, failure.Kind);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task RunAsync_PropagatesCancellationBeforeOpeningATerminal()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var launcher = new PlaygroundTerminalSession(
            _ => Assert.Fail("A cancelled launch must not open a terminal."),
            TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            launcher.RunAsync(
                Tool(), new PlaygroundConnection.DirectLine(), () => { }, cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_CancelledHandoffDoesNotLeaveChildRunning()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childLauncher = new HoldingLauncher { Entered = entered, BeforeStart = release.Task };
        Task<int>? childTask = null;
        var launcher = new PlaygroundTerminalSession(
            pipe => childTask = PlaygroundTerminalSession.RunChildAsync(
                pipe, childLauncher, CancellationToken.None),
            TimeSpan.FromSeconds(10));
        var parentTask = launcher.RunAsync(
            Tool(), new PlaygroundConnection.DirectLine(),
            () => Assert.Fail("A cancelled handoff must not report startup."), cancellation.Token);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parentTask);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            if (childTask is not null)
            {
                Assert.Equal(1, await childTask.WaitAsync(TimeSpan.FromSeconds(10)));
            }
        }

        Assert.True(childLauncher.Stopped);
    }

    [Fact]
    public async Task RunAsync_SanitizesTerminalStartExceptions()
    {
        var launcher = new PlaygroundTerminalSession(
            _ => throw new InvalidOperationException("private process details"),
            TimeSpan.FromSeconds(10));

        var failure = await Assert.ThrowsAsync<PlaygroundLaunchException>(() =>
            launcher.RunAsync(
                Tool(), new PlaygroundConnection.DirectLine(), () => { }, CancellationToken.None));

        Assert.DoesNotContain("private process details", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsLaunch_UsesANewShellConsoleWithoutCommandInterpolation()
    {
        var arguments = new[] { "path with spaces & quotes'.dll", PlaygroundTerminalSession.ChildOption, "pipe" };

        var info = PlaygroundTerminalSession.BuildTerminalStartInfo(
            Tool().ExecutablePath, arguments, "windows", _ => null);

        Assert.True(info.UseShellExecute);
        Assert.Equal(Tool().ExecutablePath, info.FileName);
        Assert.Equal(arguments, info.ArgumentList);
        Assert.False(info.RedirectStandardInput);
        Assert.False(info.RedirectStandardOutput);
    }

    [Theory]
    [InlineData("gnome-terminal", "--wait", "--")]
    [InlineData("konsole", "--separate", "--nofork", "-e")]
    [InlineData("xfce4-terminal", "--disable-server", "--execute")]
    [InlineData("xterm", "-e")]
    [InlineData("x-terminal-emulator", "-e")]
    public void LinuxLaunch_ExecutesChildDirectlyWithLiteralArguments(string terminal, params string[] prefix)
    {
        var executable = Tool().ExecutablePath;
        var terminalPath = Path.Combine(Path.GetTempPath(), terminal);
        var arguments = new[] { "file with spaces & '.dll", PlaygroundTerminalSession.ChildOption, "pipe" };

        var info = PlaygroundTerminalSession.BuildTerminalStartInfo(
            executable, arguments, "linux",
            name => name == terminal ? terminalPath : null);

        Assert.False(info.UseShellExecute);
        Assert.Equal(terminalPath, info.FileName);
        Assert.Equal(prefix.Concat([executable]).Concat(arguments), info.ArgumentList);
        Assert.DoesNotContain("-c", info.ArgumentList);
    }

    [Fact]
    public void LinuxLaunch_FailsExplicitlyWhenNoTerminalIsAvailable()
    {
        var failure = Assert.Throws<PlaygroundLaunchException>(() =>
            PlaygroundTerminalSession.BuildTerminalStartInfo(
                Tool().ExecutablePath, ["pipe"], "linux", _ => null));

        Assert.Equal(PlaygroundLaunchFailureKind.StartFailed, failure.Kind);
        Assert.Contains("Linux", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxTerminalOutputIsDrainedWhileSessionProcessRemainsRunning()
    {
        var node = FindNode();
        if (node is null)
        {
            throw SkipException.ForSkip("Node.js was not found on PATH.");
        }

        var directory = Directory.CreateTempSubdirectory("playground-terminal-output-");
        var markerPath = Path.Combine(directory.FullName, "pid");
        var stopPath = Path.Combine(directory.FullName, "stop");
        var startInfo = new ProcessStartInfo
        {
            FileName = node,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(
            "const fs=require('fs');const pid=process.argv[1];const stop=process.argv[2];fs.writeFileSync(pid,String(process.pid));console.log('terminal output');const timer=setInterval(()=>{if(fs.existsSync(stop)){clearInterval(timer);process.exit(0)}},10);");
        startInfo.ArgumentList.Add(markerPath);
        startInfo.ArgumentList.Add(stopPath);

        try
        {
            PlaygroundTerminalSession.StartTerminalProcess(startInfo, redirectOutput: true);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
            var pid = await WaitForPidAsync(markerPath);
            using var process = Process.GetProcessById(pid);
            Assert.False(process.HasExited);
            await File.WriteAllTextAsync(stopPath, "stop");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (File.Exists(stopPath))
            {
                File.Delete(stopPath);
            }
            else
            {
                await File.WriteAllTextAsync(stopPath, "stop");
            }

            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void MacLaunch_EscapesBothShellAndAppleScriptMetacharacters()
    {
        var info = PlaygroundTerminalSession.BuildTerminalStartInfo(
            "/path with ' and \"/dotnet", ["dollar$`backtick`\\file", "pipe"], "macos", _ => null);

        Assert.Equal("/usr/bin/osascript", info.FileName);
        string[] expected =
        [
            "-e",
            "tell application \"Terminal\" to do script \"'/path with '\\\\'' and \\\"/dotnet' 'dollar$`backtick`\\\\file' 'pipe'\"",
            "-e",
            "tell application \"Terminal\" to activate",
        ];
        Assert.Equal(expected, info.ArgumentList);
    }

    private static PlaygroundTool Tool() => new(
        Path.Combine(Path.GetTempPath(), "node & runtime", "node"),
        [Path.Combine(Path.GetTempPath(), "playground & test", "cli.js")],
        Path.Combine(Path.GetTempPath(), "agentsplayground"),
        PlaygroundVersion.Parse("0.2.29"));

    private static string? FindNode()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(
                directory, OperatingSystem.IsWindows() ? "node.exe" : "node"))
            .FirstOrDefault(File.Exists);
    }

    private static async Task<int> WaitForPidAsync(string path)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < timeoutAt)
        {
            if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path), out var pid))
            {
                return pid;
            }

            await Task.Delay(25);
        }

        throw new XunitException("The terminal fixture did not publish its process id.");
    }

    private sealed class RecordingEnvironmentProcessFactory : IPlaygroundProcessFactory
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public IPlaygroundProcess Start(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            return new ExitedProcess();
        }
    }

    private sealed class ExitedProcess : IPlaygroundProcess
    {
        public int Id => 1;
        public bool HasExited => true;
        public int ExitCode => 0;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void SendInterrupt() { }
        public void KillTree() { }
        public void Dispose() { }
    }

    private sealed class HoldingLauncher : IPlaygroundLauncher
    {
        public PlaygroundTool? Tool { get; private set; }
        public PlaygroundConnection? Connection { get; private set; }
        public bool Fail { get; init; }
        public TaskCompletionSource? Entered { get; init; }
        public Task? BeforeStart { get; init; }
        public bool Stopped { get; private set; }

        public async Task<int> RunAsync(
            PlaygroundTool tool,
            PlaygroundConnection connection,
            Action started,
            CancellationToken cancellationToken)
        {
            Tool = tool;
            Connection = connection;
            if (Fail)
            {
                throw new InvalidOperationException("private startup details");
            }

            try
            {
                Entered?.TrySetResult();
                if (BeforeStart is not null)
                {
                    await BeforeStart.WaitAsync(cancellationToken);
                }

                started();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            finally
            {
                Stopped = true;
            }
        }
    }
}
