using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>A failure in the harness itself, distinct from a scenario whose assertion did not hold.</summary>
public sealed class E2EException : Exception
{
    public E2EException(string message) : base(message) { }

    public E2EException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>One completed <c>msagent</c> invocation, with its streams kept separate.</summary>
public sealed record MsagentResult(IReadOnlyList<string> Arguments, int ExitCode, string StdOut, string StdErr, TimeSpan Duration, bool Interrupted = false)
{
    /// <summary>The command line as typed, for a failure report the operator can paste back.</summary>
    public string CommandLine => "msagent " + string.Join(' ', Arguments.Select(CommandLineArgument.Quote));

    /// <summary>Deserializes stdout into one of the CLI's own result records.</summary>
    public T Json<T>()
    {
        if (string.IsNullOrWhiteSpace(StdOut))
        {
            throw new E2EException($"Expected {typeof(T).Name} on stdout, got nothing. Exit {ExitCode}. Stderr: {StdErr.Trim()}");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(StdOut, MsagentJson.Options)
                ?? throw new E2EException($"Expected {typeof(T).Name} on stdout, got a JSON null.");
        }
        catch (JsonException exception)
        {
            throw new E2EException($"Could not read {typeof(T).Name} from stdout: {exception.Message}\n{Truncate(StdOut)}", exception);
        }
    }

    /// <summary>Reads stdout as a free-form document, for shapes the CLI does not expose as a record.</summary>
    public JsonElement JsonElement()
    {
        try
        {
            return JsonDocument.Parse(StdOut).RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new E2EException($"Could not parse stdout as JSON: {exception.Message}\n{Truncate(StdOut)}", exception);
        }
    }

    /// <summary>Both streams together, for assertions about a message without caring where it was written.</summary>
    public string Output => StdOut + StdErr;

    private static string Truncate(string value) => value.Length <= 2000 ? value : value[..2000] + "…";
}

/// <summary>Serializer settings matching the CLI's JSON output.</summary>
public static class MsagentJson
{
    public static JsonSerializerOptions Options { get; } = new() { PropertyNameCaseInsensitive = true };
}

/// <summary>When to kill a running process on purpose, to simulate an interruption: on an output line, after a delay, once a polled condition holds, or any of them.</summary>
public sealed record ProcessInterruption(Func<string, bool>? WhenOutput = null, TimeSpan? After = null, Func<bool>? WhenState = null);

/// <summary>Starts the <c>msagent</c> process; no scenario starts one itself.</summary>
public sealed class MsagentCli
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly string _binaryPath;
    private readonly Transcript _transcript;
    private readonly string _crashDumpDirectory;
    private readonly IReadOnlyCollection<string> _forbiddenEnvironmentIds;

    public MsagentCli(string binaryPath, Transcript transcript, string crashDumpDirectory, IReadOnlyCollection<string>? forbiddenEnvironmentIds = null)
    {
        _binaryPath = binaryPath;
        _transcript = transcript;
        _crashDumpDirectory = crashDumpDirectory;
        _forbiddenEnvironmentIds = forbiddenEnvironmentIds ?? [];

        Directory.CreateDirectory(crashDumpDirectory);
    }

    /// <summary>The binary every scenario in this run drives.</summary>
    public string BinaryPath => _binaryPath;

    /// <summary>Runs a command until it settles or the attempts run out, waiting between attempts; returns the last result and whether it settled.</summary>
    public static async Task<(MsagentResult Result, bool Settled)> RetriedAsync(Func<Task<MsagentResult>> run, Func<MsagentResult, Task<bool>> settled, int attempts, TimeSpan delay, CancellationToken cancellationToken)
    {
        var result = await run().ConfigureAwait(false);

        for (var attempt = 1; attempt < attempts; attempt++)
        {
            if (await settled(result).ConfigureAwait(false))
            {
                return (result, true);
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            result = await run().ConfigureAwait(false);
        }

        return (result, await settled(result).ConfigureAwait(false));
    }

    /// <summary>Runs <c>msagent</c> and captures both streams separately, refusing any command that names a forbidden environment.</summary>
    public async Task<MsagentResult> RunAsync(IReadOnlyList<string> arguments, string? workingDirectory = null, string? stdin = null, TimeSpan? timeout = null, ProcessInterruption? interruption = null, CancellationToken cancellationToken = default)
    {
        RefuseForbiddenEnvironment(arguments);

        var startInfo = new ProcessStartInfo(_binaryPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };

        ArmCrashDumps(startInfo, _crashDumpDirectory);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return await ProcessRunner.RunAsync(startInfo, arguments, stdin, timeout ?? DefaultTimeout, _transcript, interruption, cancellationToken).ConfigureAwait(false);
    }

    private void RefuseForbiddenEnvironment(IReadOnlyList<string> arguments)
    {
        if (_forbiddenEnvironmentIds.FirstOrDefault(forbidden => arguments.Any(argument => argument.Contains(forbidden, StringComparison.OrdinalIgnoreCase))) is { } environmentId)
        {
            throw new E2EException($"Refused to run `msagent {string.Join(' ', arguments)}`: it names {environmentId}, which mcs.forbiddenEnvironmentIds protects.");
        }
    }

    private static void ArmCrashDumps(ProcessStartInfo startInfo, string directory)
    {
        startInfo.Environment["DOTNET_DbgEnableMiniDump"] = "1";
        startInfo.Environment["DOTNET_DbgMiniDumpType"] = "2";
        startInfo.Environment["DOTNET_DbgMiniDumpName"] = Path.Combine(directory, "msagent.%p.dmp");
    }
}

/// <summary>Where a faulting <c>msagent</c> process writes its crash dump.</summary>
public static class CrashDumps
{
    /// <summary>The dump directory belonging to one run.</summary>
    /// <param name="runDirectory">The run's own directory.</param>
    public static string DirectoryFor(string runDirectory) => Path.Combine(runDirectory, "dumps");
}

/// <summary>Runs a subprocess with separated streams, a timeout, and transcript capture.</summary>
public static class ProcessRunner
{
    private static readonly TimeSpan StatePollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Runs any executable under the same rules the <c>msagent</c> wrapper uses, killing it early when an interruption asks for it.</summary>
    public static async Task<MsagentResult> RunAsync(ProcessStartInfo startInfo, IReadOnlyList<string> arguments, string? stdin, TimeSpan timeout, Transcript? transcript, ProcessInterruption? interruption = null, CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = startInfo };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var interrupted = 0;

        void Interrupt()
        {
            if (Interlocked.Exchange(ref interrupted, 1) == 0)
            {
                TryKill(process);
            }
        }

        process.OutputDataReceived += (_, eventArgs) => Capture(standardOutput, eventArgs.Data, interruption, Interrupt);
        process.ErrorDataReceived += (_, eventArgs) => Capture(standardError, eventArgs.Data, interruption, Interrupt);

        var stopwatch = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WriteStandardInputAsync(process, stdin).ConfigureAwait(false);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var interruptionTimer = interruption?.After is { } after ? new CancellationTokenSource(after) : null;
        using var interruptionRegistration = interruptionTimer?.Token.Register(Interrupt) ?? default;
        using var watching = new CancellationTokenSource();
        var watcher = interruption?.WhenState is { } condition ? WatchAsync(condition, Interrupt, watching.Token) : Task.CompletedTask;

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new E2EException($"Timed out after {timeout.TotalSeconds:N0}s: {startInfo.FileName} {string.Join(' ', arguments)}");
        }
        finally
        {
            await watching.CancelAsync().ConfigureAwait(false);
            await watcher.ConfigureAwait(false);
        }

        stopwatch.Stop();

        var result = new MsagentResult(arguments, process.ExitCode, standardOutput.ToString(), standardError.ToString(), stopwatch.Elapsed, interrupted == 1);
        transcript?.Record(startInfo.FileName, result);
        return result;
    }

    private static async Task WatchAsync(Func<bool> condition, Action interrupt, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(StatePollInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (condition())
                {
                    interrupt();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Capture(StringBuilder buffer, string? line, ProcessInterruption? interruption, Action interrupt)
    {
        if (line is null)
        {
            return;
        }

        buffer.AppendLine(line);

        if (interruption?.WhenOutput?.Invoke(line) == true)
        {
            interrupt();
        }
    }

    private static async Task WriteStandardInputAsync(Process process, string? stdin)
    {
        if (stdin is not null)
        {
            await process.StandardInput.WriteLineAsync(stdin).ConfigureAwait(false);
        }

        process.StandardInput.Close();
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
