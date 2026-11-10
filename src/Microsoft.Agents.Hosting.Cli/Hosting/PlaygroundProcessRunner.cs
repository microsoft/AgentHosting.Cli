using System.Diagnostics;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Playground;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

/// <summary>Runs a playground process without shell interpolation and captures bounded output.</summary>
public sealed class PlaygroundProcessRunner : IPlaygroundProcessRunner
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumTaskDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    public async Task<PlaygroundProcessResult> RunAsync(
        PlaygroundProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process
        {
            StartInfo = CreateStartInfo(request),
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException();
            }
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The requested process could not be started.");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeoutCancellation = new CancellationTokenSource();
        var timeoutTask = CancelBudgetAfterAsync(
            budget,
            request.Timeout,
            timeoutCancellation.Token);
        var budgetCancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, budget.Token);
        var standardOutputTask = CaptureOutputAsync(process.StandardOutput.BaseStream, request.MaximumOutputBytes, budget.Token);
        var standardErrorTask = CaptureOutputAsync(process.StandardError.BaseStream, PlaygroundProcessRequest.DefaultMaximumOutputBytes, budget.Token);
        var standardInputTask = WriteInputAsync(process, request.StandardInput, budget.Token);
        var waitTask = process.WaitForExitAsync(budget.Token);
        var processTasks = Task.WhenAll(waitTask, standardOutputTask, standardErrorTask, standardInputTask);

        var needsCleanup = false;
        var timedOut = false;
        Exception? primaryFailure = null;
        Exception? cleanupFailure = null;

        try
        {
            var completedTask = await Task.WhenAny(processTasks, budgetCancellationTask)
                .ConfigureAwait(false);
            if (completedTask == processTasks)
            {
                try
                {
                    await processTasks.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    needsCleanup = true;
                    primaryFailure = new InvalidOperationException(
                        "The process did not complete safely.");
                    timedOut = !cancellationToken.IsCancellationRequested &&
                        budget.IsCancellationRequested;
                }
            }
            else
            {
                needsCleanup = true;
                timedOut = !cancellationToken.IsCancellationRequested;
            }

            if (!needsCleanup)
            {
                var standardOutput = await standardOutputTask.ConfigureAwait(false);
                return new PlaygroundProcessResult(process.ExitCode, request.DecodeStandardOutput ? Encoding.UTF8.GetString(standardOutput.Span) : string.Empty, Encoding.UTF8.GetString((await standardErrorTask.ConfigureAwait(false)).Span)) { StandardOutputBytes = standardOutput };
            }
        }
        finally
        {
            budget.Cancel();
            timeoutCancellation.Cancel();
            await ObserveTimerTaskAsync(timeoutTask).ConfigureAwait(false);
            await ObserveCancellationTaskAsync(budgetCancellationTask).ConfigureAwait(false);

            if (needsCleanup)
            {
                cleanupFailure = await CleanupAfterFailureAsync(
                        process,
                        processTasks)
                    .ConfigureAwait(false);
            }
        }

        if (cleanupFailure is not null)
        {
            _ = ObserveProcessTaskAsync(processTasks);
            throw cleanupFailure;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (timedOut)
        {
            throw new TimeoutException("The process exceeded the configured timeout.");
        }

        throw primaryFailure ?? new InvalidOperationException("The process did not complete safely.");
    }

    internal static ProcessStartInfo CreateStartInfo(PlaygroundProcessRequest request)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = request.ExecutablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = request.StandardInput is not null,
                StandardInputEncoding = request.StandardInput is null ? null : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                CreateNoWindow = true,
            };

            if (request.WorkingDirectory is not null)
            {
                startInfo.WorkingDirectory = request.WorkingDirectory;
            }

            foreach (var argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var (name, value) in request.EnvironmentOverrides)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }

            return startInfo;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The requested process could not be started.");
        }
    }

    private static void ValidateRequest(PlaygroundProcessRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ExecutablePath) ||
            !Path.IsPathFullyQualified(request.ExecutablePath))
        {
            throw new ArgumentException(
                "ExecutablePath must be an absolute path.",
                nameof(request));
        }

        if (request.Timeout <= TimeSpan.Zero || request.Timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Timeout must be positive and finite.");
        }

        if (request.WorkingDirectory is not null &&
            !Path.IsPathFullyQualified(request.WorkingDirectory))
        {
            throw new ArgumentException(
                "WorkingDirectory must be an absolute path when supplied.",
                nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.Arguments);
        ArgumentNullException.ThrowIfNull(request.EnvironmentOverrides);
    }

    private static async Task<ReadOnlyMemory<byte>> CaptureOutputAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        try
        {
            using var captured = new MemoryStream(PlaygroundProcessRequest.DefaultMaximumOutputBytes);
            var buffer = new byte[8192];

            while (true)
            {
                var bytesRead = await stream.ReadAsync(
                        buffer.AsMemory(),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                var bytesToCapture = Math.Min(bytesRead, maximumBytes - checked((int)captured.Length));
                if (bytesToCapture > 0)
                {
                    await captured.WriteAsync(buffer.AsMemory(0, bytesToCapture), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return captured.GetBuffer().AsMemory(0, checked((int)captured.Length));
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The process output could not be captured safely.");
        }
    }

    private static async Task WriteInputAsync(Process process, string? input, CancellationToken cancellationToken)
    {
        if (input is null)
        {
            return;
        }

        await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
    }

    private static async Task<Exception?> CleanupAfterFailureAsync(
        Process process,
        Task processTasks)
    {
        var stopwatch = Stopwatch.StartNew();
        var failure = await RunCleanupActionsAsync(
                () => DisposeCapturedStreams(process),
                () => StopProcessTreeAsync(
                    process,
                    RemainingCleanupTime(stopwatch)))
            .ConfigureAwait(false);

        try
        {
            await processTasks.WaitAsync(RemainingCleanupTime(stopwatch))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            failure ??= new InvalidOperationException("The process could not be stopped safely.");
        }
        catch (Exception)
        {
        }

        return failure;
    }

    internal static async Task<Exception?> RunCleanupActionsAsync(
        Func<Exception?> disposeStreams,
        Func<Task<Exception?>> stopProcessTree)
    {
        ArgumentNullException.ThrowIfNull(disposeStreams);
        ArgumentNullException.ThrowIfNull(stopProcessTree);

        Exception? firstFailure = null;
        try
        {
            firstFailure = disposeStreams();
        }
        catch (Exception)
        {
            firstFailure = new InvalidOperationException(
                "The process could not be stopped safely.");
        }

        try
        {
            var stopFailure = await stopProcessTree().ConfigureAwait(false);
            firstFailure ??= stopFailure;
        }
        catch (Exception)
        {
            firstFailure ??= new InvalidOperationException(
                "The process could not be stopped safely.");
        }

        return firstFailure;
    }

    private static async Task<Exception?> StopProcessTreeAsync(
        Process process,
        TimeSpan timeout)
    {
        Exception? failure = null;
        bool processExited;
        try
        {
            processExited = process.HasExited;
        }
        catch (Exception)
        {
            processExited = true;
            failure = new InvalidOperationException("The process could not be stopped safely.");
        }

        if (processExited)
        {
            failure ??= new InvalidOperationException("The process could not be stopped safely.");
        }
        else
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                failure ??= new InvalidOperationException("The process could not be stopped safely.");
            }
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(timeout)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            failure ??= new InvalidOperationException("The process could not be stopped safely.");
        }

        return failure;
    }

    private static Exception? DisposeCapturedStreams(Process process)
    {
        Exception? failure = null;
        try
        {
            process.StandardOutput.BaseStream.Dispose();
        }
        catch (Exception)
        {
            failure = new InvalidOperationException("The process could not be stopped safely.");
        }

        try
        {
            process.StandardError.BaseStream.Dispose();
        }
        catch (Exception)
        {
            failure ??= new InvalidOperationException("The process could not be stopped safely.");
        }

        return failure;
    }

    private static TimeSpan RemainingCleanupTime(Stopwatch stopwatch)
    {
        var remaining = CleanupTimeout - stopwatch.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static async Task CancelBudgetAfterAsync(
        CancellationTokenSource budget,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                var remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    budget.Cancel();
                    return;
                }

                await Task.Delay(
                        remaining > MaximumTaskDelay ? MaximumTaskDelay : remaining,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task ObserveTimerTaskAsync(Task timeoutTask)
    {
        try
        {
            await timeoutTask.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task ObserveCancellationTaskAsync(Task cancellationTask)
    {
        try
        {
            await cancellationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ObserveProcessTaskAsync(Task processTask)
    {
        try
        {
            await processTask.ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
