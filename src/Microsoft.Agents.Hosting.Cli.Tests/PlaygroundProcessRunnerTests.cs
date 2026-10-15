using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Agents.Hosting.Cli.Core.Playground;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Xunit.Sdk;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class PlaygroundProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_CapturesBothStreamsAndReturnsNonZeroExitCode()
    {
        var node = RequireNode();
        var result = await new PlaygroundProcessRunner().RunAsync(
            Request(node, "-e", "process.stdout.write('standard output'); process.stderr.write('standard error'); process.exit(7)"),
            CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("standard output", result.StandardOutput);
        Assert.Equal("standard error", result.StandardError);
    }

    [Fact]
    public async Task RunAsync_PassesArgumentsWithoutShellInterpretation()
    {
        var node = RequireNode();
        const string value = "value with spaces & echo injected; $(not-a-command)";

        var result = await new PlaygroundProcessRunner().RunAsync(
            Request(node, "-e", "process.stdout.write(process.argv[1])", value),
            CancellationToken.None);

        Assert.Equal(value, result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task RunAsync_AppliesEnvironmentOverridesAndWorkingDirectoryWithoutMutatingParent()
    {
        var node = RequireNode();
        var workingDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(AppContext.BaseDirectory));
        const string childVariable = "PLAYGROUND_PROCESS_RUNNER_TEST_OVERRIDE";

        var result = await new PlaygroundProcessRunner().RunAsync(
            new PlaygroundProcessRequest
            {
                ExecutablePath = node,
                Arguments =
                [
                    "-e",
                    $"process.stdout.write(JSON.stringify({{value: process.env.{childVariable}, path: process.env.PATH === undefined ? 'missing' : 'present', cwd: process.cwd()}}))",
                ],
                Timeout = TimeSpan.FromSeconds(10),
                WorkingDirectory = workingDirectory,
                EnvironmentOverrides = new Dictionary<string, string?>
                {
                    [childVariable] = "overridden",
                    ["PATH"] = null,
                },
            },
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"value\":\"overridden\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"missing\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"\"cwd\":{System.Text.Json.JsonSerializer.Serialize(workingDirectory)}", result.StandardOutput, StringComparison.Ordinal);
        Assert.NotNull(Environment.GetEnvironmentVariable("PATH"));
        Assert.Null(Environment.GetEnvironmentVariable(childVariable));
    }

    [Fact]
    public async Task RunAsync_DrainsOversizedConcurrentStreamsAndBoundsCapturedOutput()
    {
        var node = RequireNode();
        var result = await new PlaygroundProcessRunner().RunAsync(
            Request(node, "-e", "process.stdout.write('o'.repeat(128 * 1024)); process.stderr.write('e'.repeat(128 * 1024))"),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(64 * 1024, result.StandardOutput.Length);
        Assert.Equal(64 * 1024, result.StandardError.Length);
        Assert.All(result.StandardOutput, character => Assert.Equal('o', character));
        Assert.All(result.StandardError, character => Assert.Equal('e', character));
    }

    [Fact]
    public async Task RunAsync_TimeoutKillsOnlyOwnedProcessTree()
    {
        var node = RequireNode();
        using var temporaryDirectory = new TemporaryDirectory();
        var markerPath = Path.Combine(temporaryDirectory.Path, "timeout-pids.txt");
        var runTask = new PlaygroundProcessRunner().RunAsync(
            LongRunningTreeRequest(node, markerPath, TimeSpan.FromSeconds(5)),
            CancellationToken.None);

        var processIds = await ReadProcessIdsAsync(markerPath);
        await Assert.ThrowsAsync<TimeoutException>(() => runTask);

        await AssertProcessesExitedAsync(processIds);
    }

    [Fact]
    public async Task RunAsync_ExternalCancellationKillsOnlyOwnedProcessTree()
    {
        var node = RequireNode();
        using var temporaryDirectory = new TemporaryDirectory();
        var markerPath = Path.Combine(temporaryDirectory.Path, "cancellation-pids.txt");
        using var cancellation = new CancellationTokenSource();
        var runTask = new PlaygroundProcessRunner().RunAsync(
            LongRunningTreeRequest(node, markerPath, TimeSpan.FromSeconds(30)),
            cancellation.Token);

        var processIds = await ReadProcessIdsAsync(markerPath);
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);
        await AssertProcessesExitedAsync(processIds);
    }

    [Fact]
    public async Task RunAsync_TimeoutCoversDrainWhenExitedDescendantHoldsOutputPipe()
    {
        var node = RequireNode();
        using var temporaryDirectory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var markerPath = Path.Combine(temporaryDirectory.Path, "pipe-holder-pids.txt");
        var releasePath = Path.Combine(temporaryDirectory.Path, "release.txt");
        var stopwatch = Stopwatch.StartNew();
        var requestTimeout = TimeSpan.FromSeconds(20);
        var testDeadline = TimeSpan.FromSeconds(22);
        var runTask = new PlaygroundProcessRunner().RunAsync(
            PipeHoldingDescendantRequest(node, markerPath, releasePath, requestTimeout),
            cancellation.Token);

        Process? parentProcess = null;
        Process? descendantProcess = null;
        Exception? testFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            var processIds = await ReadProcessIdsAsync(markerPath, TimeSpan.FromSeconds(15));
            parentProcess = Process.GetProcessById(processIds[0]);
            descendantProcess = Process.GetProcessById(processIds[1]);
            Assert.False(parentProcess.HasExited);
            Assert.False(descendantProcess.HasExited);
            File.WriteAllText(releasePath, "release");
            await parentProcess.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(parentProcess.HasExited);
            Assert.False(descendantProcess.HasExited);
            Assert.False(runTask.IsCompleted);

            var remainingRunnerBudget = requestTimeout - stopwatch.Elapsed;
            Assert.True(remainingRunnerBudget > TimeSpan.Zero);
            var remainingDeadline = testDeadline - stopwatch.Elapsed;
            Assert.True(remainingDeadline > TimeSpan.Zero);
            var drainStopwatch = Stopwatch.StartNew();
            var completedTask = await Task.WhenAny(
                runTask,
                Task.Delay(remainingDeadline));

            Assert.Same(runTask, completedTask);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => runTask);
            Assert.Equal("The process could not be stopped safely.", failure.Message);
            var minimumDrain = remainingRunnerBudget - TimeSpan.FromSeconds(2);
            if (minimumDrain < TimeSpan.Zero)
            {
                minimumDrain = TimeSpan.Zero;
            }

            Assert.InRange(
                drainStopwatch.Elapsed,
                minimumDrain,
                remainingRunnerBudget + TimeSpan.FromSeconds(2));
        }
        catch (Exception failure)
        {
            testFailure = failure;
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (OperationCanceledException)
                {
                }
                catch (InvalidOperationException failure)
                    when (failure.Message == "The process could not be stopped safely.")
                {
                }
                catch (TimeoutException)
                    when (testFailure is not null && descendantProcess is not null)
                {
                }
                catch (Exception failure)
                {
                    cleanupFailure = failure;
                }
            }
            finally
            {
                if (descendantProcess is not null)
                {
                    try
                    {
                        await KillAndJoinOwnedProcessAsync(descendantProcess);
                    }
                    catch (Exception failure)
                    {
                        cleanupFailure ??= failure;
                    }
                    finally
                    {
                        descendantProcess.Dispose();
                    }
                }

                parentProcess?.Dispose();
            }
        }

        if (testFailure is not null)
        {
            ExceptionDispatchInfo.Capture(testFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    [Fact]
    public async Task RunAsync_SanitizesProcessStartFailures()
    {
        var secretPath = Path.Combine(Path.GetTempPath(), "playground-secret-process-path");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PlaygroundProcessRunner().RunAsync(
                new PlaygroundProcessRequest
                {
                    ExecutablePath = secretPath,
                    Timeout = TimeSpan.FromSeconds(1),
                },
                CancellationToken.None));

        Assert.Equal("The requested process could not be started.", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(secretPath, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanupActions_PreservesFirstFailureAndAttemptsTreeStop()
    {
        var firstFailure = new InvalidOperationException("first cleanup failure");
        var secondFailure = new InvalidOperationException("second cleanup failure");
        var stopAttempted = false;

        var failure = await PlaygroundProcessRunner.RunCleanupActionsAsync(
            () => firstFailure,
            () =>
            {
                stopAttempted = true;
                return Task.FromResult<Exception?>(secondFailure);
            });

        Assert.Same(firstFailure, failure);
        Assert.True(stopAttempted);
    }

    private static PlaygroundProcessRequest Request(string node, params string[] arguments) =>
        new()
        {
            ExecutablePath = node,
            Arguments = arguments,
            Timeout = TimeSpan.FromSeconds(10),
        };

    private static PlaygroundProcessRequest LongRunningTreeRequest(
        string node,
        string markerPath,
        TimeSpan timeout) =>
        new()
        {
            ExecutablePath = node,
            Arguments =
            [
                "-e",
                "const fs=require('fs'); const cp=require('child_process'); const child=cp.spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{stdio:'ignore'}); fs.writeFileSync(process.env.PLAYGROUND_MARKER, `${process.pid}\\n${child.pid}\\n`); setInterval(()=>{},1000);",
            ],
            Timeout = timeout,
            EnvironmentOverrides = new Dictionary<string, string?>
            {
                ["PLAYGROUND_MARKER"] = markerPath,
            },
        };

    private static PlaygroundProcessRequest PipeHoldingDescendantRequest(
        string node,
        string markerPath,
        string releasePath,
        TimeSpan timeout) =>
        new()
        {
            ExecutablePath = node,
            Arguments =
            [
                "-e",
                "const fs=require('fs'); const cp=require('child_process'); const child=cp.spawn(process.execPath,['-e','setTimeout(()=>process.exit(0),60000)'],{stdio:['ignore','inherit','inherit'],detached:true}); fs.writeFileSync(process.env.PLAYGROUND_MARKER, `${process.pid}\\n${child.pid}\\n`); const wait=setInterval(()=>{ if(fs.existsSync(process.env.PLAYGROUND_RELEASE)){ clearInterval(wait); process.exit(0); } },10);",
            ],
            Timeout = timeout,
            EnvironmentOverrides = new Dictionary<string, string?>
            {
                ["PLAYGROUND_MARKER"] = markerPath,
                ["PLAYGROUND_RELEASE"] = releasePath,
            },
        };

    private static string RequireNode()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw SkipException.ForSkip("Node.js was not found on PATH.");
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "node.exe" : "node");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw SkipException.ForSkip("Node.js was not found on PATH.");
    }

    private static async Task<int[]> ReadProcessIdsAsync(
        string markerPath,
        TimeSpan? waitTimeout = null)
    {
        var deadline = DateTime.UtcNow + (waitTimeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(markerPath))
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(markerPath);
                    if (lines.Length >= 2 &&
                        int.TryParse(lines[0], out var parentProcessId) &&
                        int.TryParse(lines[1], out var childProcessId))
                    {
                        return [parentProcessId, childProcessId];
                    }
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(25);
        }

        throw new XunitException($"The child process did not create marker file: {markerPath}");
    }

    private static async Task KillAndJoinOwnedProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task AssertProcessesExitedAsync(IReadOnlyList<int> processIds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (processIds.All(IsExited))
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(processIds.All(IsExited), $"Processes are still running: {string.Join(", ", processIds.Where(IsRunning))}");
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsExited(int processId) => !IsRunning(processId);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("playground-process-runner-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
