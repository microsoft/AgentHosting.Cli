using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Playground;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

internal sealed class PlaygroundTerminalSession : IPlaygroundLauncher
{
    internal const string ChildOption = "--internal-playground-session";
    internal const string PipeNamePrefix = "msp-";
    private const byte HandoffCancelled = 2;
    private const byte HandoffCancellationComplete = 3;
    private static readonly TimeSpan HandoffCancelSignalTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HandoffCancellationCleanupTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private readonly Action<string> _openTerminal;
    private readonly TimeSpan _startupTimeout;
    private readonly IReadOnlyDictionary<string, string>? _inheritedEnvironment;
    private readonly Action? _stopTerminal;
    private Process? _terminalProcess;
    private bool _terminalProcessIsDrained;

    public PlaygroundTerminalSession()
    {
        _openTerminal = OpenTerminal;
        _startupTimeout = StartupTimeout;
    }

    internal PlaygroundTerminalSession(
        Action<string> openTerminal,
        TimeSpan startupTimeout,
        IReadOnlyDictionary<string, string>? inheritedEnvironment = null,
        Action? stopTerminal = null)
    {
        _openTerminal = openTerminal;
        _startupTimeout = startupTimeout;
        _inheritedEnvironment = inheritedEnvironment;
        _stopTerminal = stopTerminal;
    }

    public async Task<int> RunAsync(
        PlaygroundTool tool,
        PlaygroundConnection connection,
        Action started,
        CancellationToken cancellationToken)
    {
        var launchPlan = PlaygroundLauncher.BuildLaunchPlan(
            tool, connection, inheritedEnvironment: _inheritedEnvironment);
        cancellationToken.ThrowIfCancellationRequested();
        var pipeName = $"{PipeNamePrefix}{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(_startupTimeout);

        try
        {
            _openTerminal(pipeName);
            await pipe.WaitForConnectionAsync(startup.Token).ConfigureAwait(false);
            var engine = connection as PlaygroundConnection.DirectEngine;
            await JsonSerializer.SerializeAsync(
                pipe,
                new SessionRequest(
                    tool.ExecutablePath, tool.ArgumentPrefix.ToArray(), tool.SelectedPath,
                    tool.Version.ToString(), engine?.Target, engine?.ClientId,
                    new Dictionary<string, string>(launchPlan.Environment, StringComparer.Ordinal)),
                cancellationToken: startup.Token).ConfigureAwait(false);
            await pipe.WriteAsync(new byte[] { (byte)'\n' }, startup.Token).ConfigureAwait(false);
            await pipe.FlushAsync(startup.Token).ConfigureAwait(false);
            var response = new byte[1];
            await pipe.ReadExactlyAsync(response, startup.Token).ConfigureAwait(false);
            if (response[0] != 1)
            {
                throw StartFailure();
            }

            startup.Token.ThrowIfCancellationRequested();
            await pipe.WriteAsync(new byte[] { 1 }, startup.Token).ConfigureAwait(false);
            await pipe.FlushAsync(startup.Token).ConfigureAwait(false);
            started();
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!await SignalHandoffCancelledAsync(pipe).ConfigureAwait(false))
            {
                StopTerminal();
            }

            throw;
        }
        catch (Exception failure) when (
            failure is not PlaygroundLaunchException)
        {
            throw StartFailure();
        }
        finally
        {
            ReleaseTerminalProcess();
        }
    }

    private void StopTerminal()
    {
        if (_stopTerminal is not null)
        {
            _stopTerminal();
            return;
        }

        var process = Interlocked.Exchange(ref _terminalProcess, null);
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            if (!_terminalProcessIsDrained)
            {
                process.Dispose();
            }
        }
    }

    private void ReleaseTerminalProcess()
    {
        var process = Interlocked.Exchange(ref _terminalProcess, null);
        if (process is not null && !_terminalProcessIsDrained)
        {
            process.Dispose();
        }

        _terminalProcessIsDrained = false;
    }

    private static async Task<bool> SignalHandoffCancelledAsync(NamedPipeServerStream pipe)
    {
        if (!pipe.IsConnected)
        {
            return false;
        }

        using var signalTimeout = new CancellationTokenSource(HandoffCancelSignalTimeout);
        try
        {
            await pipe.WriteAsync(new[] { HandoffCancelled }, signalTimeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(signalTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (
            failure is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }

        using var cleanupTimeout = new CancellationTokenSource(HandoffCancellationCleanupTimeout);
        try
        {
            while (true)
            {
                var response = new byte[1];
                await pipe.ReadExactlyAsync(response, cleanupTimeout.Token).ConfigureAwait(false);
                if (response[0] == HandoffCancellationComplete)
                {
                    return true;
                }

                if (response[0] != 1)
                {
                    return false;
                }
            }
        }
        catch (Exception failure) when (
            failure is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }
    }

    internal static async Task<int> RunChildAsync(
        string pipeName,
        IPlaygroundLauncher launcher,
        CancellationToken cancellationToken) =>
        await RunChildAsync(pipeName, _ => launcher, cancellationToken).ConfigureAwait(false);

    internal static async Task<int> RunChildAsync(
        string pipeName,
        Func<IReadOnlyDictionary<string, string>, IPlaygroundLauncher> launcherFactory,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? acknowledgeFailure = null)
    {
        var exitCode = await RunChildCoreAsync(pipeName, launcherFactory, cancellationToken)
            .ConfigureAwait(false);
        if (exitCode != 0 && exitCode != 130 && !cancellationToken.IsCancellationRequested &&
            acknowledgeFailure is not null)
        {
            try
            {
                await acknowledgeFailure(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        return exitCode;
    }

    internal static async Task AcknowledgeFailureAsync(
        bool inputRedirected,
        bool outputRedirected,
        CancellationToken cancellationToken)
    {
        if (inputRedirected || outputRedirected || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await Console.Error.WriteLineAsync(
            "Review the error above. Press Enter to close this window, or Ctrl+C to exit.")
            .ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (failure is IOException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync(
                "The terminal input is unavailable; this window cannot remain open. Rerun msagent test playground in an interactive terminal.")
                .ConfigureAwait(false);
        }
    }

    private static async Task<int> RunChildCoreAsync(
        string pipeName,
        Func<IReadOnlyDictionary<string, string>, IPlaygroundLauncher> launcherFactory,
        CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var acknowledged = false;
        var requestReceived = false;
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(StartupTimeout);
            await pipe.ConnectAsync(startup.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            var json = await reader.ReadLineAsync(startup.Token).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<SessionRequest>(
                json ?? throw new EndOfStreamException()) ?? throw new JsonException();
            requestReceived = true;
            var handoffResponse = ReadHandoffResponseAsync(reader, startup.Token);
            var launcher = launcherFactory(request.Environment);
            var tool = new PlaygroundTool(
                request.ExecutablePath, request.ArgumentPrefix, request.SelectedPath,
                PlaygroundVersion.Parse(request.Version));
            PlaygroundConnection connection = request.Target is null
                ? new PlaygroundConnection.DirectLine()
                : new PlaygroundConnection.DirectEngine(
                    request.Target, request.ClientId ?? Guid.Empty);

            var exitCode = await launcher.RunAsync(tool, connection, () =>
            {
                try
                {
                    pipe.WriteByte(1);
                    pipe.Flush();
                }
                catch (IOException)
                {
                }

                var response = handoffResponse.GetAwaiter().GetResult();
                if (response == HandoffCancelled)
                {
                    throw new HandoffCancelledException();
                }

                if (response != 1)
                {
                    throw new IOException("The launch handoff was not completed.");
                }

                acknowledged = true;
            }, cancellationToken).ConfigureAwait(false);
            if (exitCode == 130)
            {
                await Console.Error.WriteLineAsync("The playground session was cancelled.")
                    .ConfigureAwait(false);
            }
            else if (exitCode != 0)
            {
                await Console.Error.WriteLineAsync(
                    $"The playground process exited with code {exitCode}. Review the playground output above. Verify Node.js {PlaygroundToolLocator.MinimumNodeVersion} or newer with node --version, and update the playground with npm install -g @microsoft/m365agentsplayground@latest before retrying.")
                    .ConfigureAwait(false);
            }

            return exitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (HandoffCancelledException)
        {
            acknowledged = true;
            await Console.Error.WriteLineAsync(
                "The playground startup was cancelled from the calling terminal.")
                .ConfigureAwait(false);
            await pipe.WriteAsync(
                    new[] { HandoffCancellationComplete }, CancellationToken.None)
                .ConfigureAwait(false);
            await pipe.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return 130;
        }
        catch (IOException) when (!requestReceived)
        {
            return 130;
        }
        catch (PlaygroundLaunchException failure)
        {
            await Console.Error.WriteLineAsync($"{failure.Message} {failure.Remediation}")
                .ConfigureAwait(false);
            return 1;
        }
        catch (Exception)
        {
            await Console.Error.WriteLineAsync(
                "The playground session could not start or stop safely. Verify the installation and try again.")
                .ConfigureAwait(false);
            return 1;
        }
        finally
        {
            if (pipe.IsConnected && !acknowledged)
            {
                try
                {
                    pipe.WriteByte(0);
                    pipe.Flush();
                }
                catch (IOException)
                {
                    await Console.Error.WriteLineAsync("The calling terminal disconnected during startup.")
                        .ConfigureAwait(false);
                }
            }
        }
    }

    internal static async Task<char> ReadHandoffResponseAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var response = new char[1];
        var read = await reader.ReadAsync(response.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (read != 1)
        {
            throw new EndOfStreamException();
        }

        return response[0];
    }

    private void OpenTerminal(string pipeName)
    {
        var executable = Environment.ProcessPath ?? throw StartFailure();
        var arguments = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add(typeof(PlaygroundTerminalSession).Assembly.Location);
        }

        arguments.Add(ChildOption);
        arguments.Add(pipeName);
        var info = BuildTerminalStartInfo(
            executable, arguments,
            OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
            FindExecutable);
        var redirectOutput = OperatingSystem.IsLinux();
        _terminalProcess = StartTerminalProcess(info, redirectOutput);
        _terminalProcessIsDrained = redirectOutput;
    }

    internal static Process StartTerminalProcess(ProcessStartInfo info, bool redirectOutput)
    {
        if (redirectOutput)
        {
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
        }

        var process = Process.Start(info) ?? throw StartFailure();
        if (redirectOutput)
        {
            _ = DrainTerminalOutputAsync(process);
        }

        return process;
    }

    private static async Task DrainTerminalOutputAsync(Process process)
    {
        try
        {
            var standardOutput = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var standardError = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            await Task.WhenAll(
                process.WaitForExitAsync(),
                standardOutput,
                standardError).ConfigureAwait(false);
        }
        catch (IOException)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
        }
    }

    internal static ProcessStartInfo BuildTerminalStartInfo(
        string executable,
        IReadOnlyList<string> arguments,
        string platform,
        Func<string, string?> findExecutable)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = platform == "windows",
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        if (platform == "windows")
        {
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            return info;
        }

        if (platform == "macos")
        {
            var command = string.Join(" ", new[] { executable }.Concat(arguments).Select(QuoteShellArgument));
            info.FileName = "/usr/bin/osascript";
            info.ArgumentList.Add("-e");
            info.ArgumentList.Add(
                $"tell application \"Terminal\" to do script \"{command.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"");
            info.ArgumentList.Add("-e");
            info.ArgumentList.Add("tell application \"Terminal\" to activate");
            return info;
        }

        foreach (var (name, prefix) in new (string, string[])[]
        {
            ("gnome-terminal", ["--wait", "--"]),
            ("konsole", ["--separate", "--nofork", "-e"]),
            ("xfce4-terminal", ["--disable-server", "--execute"]),
            ("xterm", ["-e"]),
            ("x-terminal-emulator", ["-e"]),
        })
        {
            var terminal = findExecutable(name);
            if (terminal is null)
            {
                continue;
            }

            info.FileName = terminal;
            foreach (var argument in prefix.Concat([executable]).Concat(arguments))
            {
                info.ArgumentList.Add(argument);
            }

            return info;
        }

        throw StartFailure();
    }

    private static string QuoteShellArgument(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string? FindExecutable(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);

    private static PlaygroundLaunchException StartFailure() => new(
        PlaygroundLaunchFailureKind.StartFailed,
        "The playground could not be started in a separate terminal.",
        "Use a local desktop session with a terminal application available. On Linux, install GNOME Terminal, Konsole, Xfce Terminal, or xterm; on macOS, allow Terminal automation.");

    private sealed class HandoffCancelledException : Exception;

    internal sealed record SessionRequest(
        string ExecutablePath,
        string[] ArgumentPrefix,
        string SelectedPath,
        string Version,
        LiveTestDestination? Target,
        Guid? ClientId,
        Dictionary<string, string> Environment);
}
