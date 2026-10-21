using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Playground;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class PlaygroundTerminalSessionTests
{
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
