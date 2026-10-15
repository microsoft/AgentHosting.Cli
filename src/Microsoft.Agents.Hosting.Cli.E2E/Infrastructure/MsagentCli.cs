using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>A failure in the harness itself, distinct from a scenario whose assertion did not hold.</summary>
public sealed class E2EException : Exception
{
    public E2EException(string message) : base(message) { }

    public E2EException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>One completed <c>msagent</c> invocation, with its streams kept separate.</summary>
public sealed record MsagentResult(IReadOnlyList<string> Arguments, int ExitCode, string StdOut, string StdErr, TimeSpan Duration)
{
    /// <summary>The command line as typed, for a failure report the operator can paste back.</summary>
    public string CommandLine => "msagent " + string.Join(' ', Arguments.Select(Quote));

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

    private static string Quote(string argument) => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;

    private static string Truncate(string value) => value.Length <= 2000 ? value : value[..2000] + "…";
}

/// <summary>Serializer settings matching the CLI's JSON output.</summary>
public static class MsagentJson
{
    public static JsonSerializerOptions Options { get; } = new() { PropertyNameCaseInsensitive = true };
}

/// <summary>Starts the <c>msagent</c> process; no scenario starts one itself.</summary>
public sealed class MsagentCli
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly string _binaryPath;
    private readonly Transcript _transcript;
    private readonly string _crashDumpDirectory;

    public MsagentCli(string binaryPath, Transcript transcript, string crashDumpDirectory)
    {
        _binaryPath = binaryPath;
        _transcript = transcript;
        _crashDumpDirectory = crashDumpDirectory;

        Directory.CreateDirectory(crashDumpDirectory);
    }

    /// <summary>The binary every scenario in this run drives.</summary>
    public string BinaryPath => _binaryPath;

    /// <summary>Runs <c>msagent</c> and captures both streams separately.</summary>
    public async Task<MsagentResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        string? stdin = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
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

        return await ProcessRunner
            .RunAsync(startInfo, arguments, stdin, timeout ?? DefaultTimeout, _transcript, cancellationToken)
            .ConfigureAwait(false);
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
    /// <summary>Runs any executable under the same rules the <c>msagent</c> wrapper uses.</summary>
    public static async Task<MsagentResult> RunAsync(
        ProcessStartInfo startInfo,
        IReadOnlyList<string> arguments,
        string? stdin,
        TimeSpan timeout,
        Transcript? transcript,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) { standardOutput.AppendLine(eventArgs.Data); } };
        process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) { standardError.AppendLine(eventArgs.Data); } };

        var stopwatch = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WriteStandardInputAsync(process, stdin).ConfigureAwait(false);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

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

        stopwatch.Stop();

        var result = new MsagentResult(arguments, process.ExitCode, standardOutput.ToString(), standardError.ToString(), stopwatch.Elapsed);
        transcript?.Record(startInfo.FileName, result);
        return result;
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
