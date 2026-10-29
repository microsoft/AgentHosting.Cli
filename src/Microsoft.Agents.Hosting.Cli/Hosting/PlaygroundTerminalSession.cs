using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Playground;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

internal sealed class PlaygroundTerminalSession : IPlaygroundLauncher
{
    internal const string ChildOption = "--internal-playground-session";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private readonly Action<string> _openTerminal;
    private readonly TimeSpan _startupTimeout;
    private readonly IReadOnlyDictionary<string, string>? _inheritedEnvironment;

    public PlaygroundTerminalSession() : this(OpenTerminal, StartupTimeout)
    {
    }

    internal PlaygroundTerminalSession(
        Action<string> openTerminal,
        TimeSpan startupTimeout,
        IReadOnlyDictionary<string, string>? inheritedEnvironment = null)
    {
        _openTerminal = openTerminal;
        _startupTimeout = startupTimeout;
        _inheritedEnvironment = inheritedEnvironment;
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
        var pipeName = $"msagent-playground-{Guid.NewGuid():N}";
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
            throw;
        }
        catch (Exception failure) when (
            failure is not PlaygroundLaunchException)
        {
            throw StartFailure();
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
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(StartupTimeout);
            await pipe.ConnectAsync(startup.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            var json = await reader.ReadLineAsync(startup.Token).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<SessionRequest>(
                json ?? throw new EndOfStreamException()) ?? throw new JsonException();
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
                pipe.WriteByte(1);
                pipe.Flush();
                var response = new byte[1];
                pipe.ReadExactlyAsync(response, startup.Token).AsTask().GetAwaiter().GetResult();
                if (response[0] != 1)
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

    private static void OpenTerminal(string pipeName)
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
        StartTerminalProcess(info, redirectOutput: OperatingSystem.IsLinux());
    }

    internal static void StartTerminalProcess(ProcessStartInfo info, bool redirectOutput)
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
        else
        {
            process.Dispose();
        }
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

    internal sealed record SessionRequest(
        string ExecutablePath,
        string[] ArgumentPrefix,
        string SelectedPath,
        string Version,
        LiveTestDestination? Target,
        Guid? ClientId,
        Dictionary<string, string> Environment);
}
