using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Playground;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

/// <summary>
/// Starts the package-native playground in the foreground. The child owns its console output and
/// authentication flow; this host only supplies resolved, non-secret launch arguments.
/// </summary>
public sealed class PlaygroundLauncher : IPlaygroundLauncher
{
    public static readonly TimeSpan ShutdownGracePeriod = TimeSpan.FromSeconds(10);
    public const string ModernConfigPathEnvironmentVariable = "M365_AGENTS_PLAYGROUND_CONFIG_PATH";
    public const string LegacyConfigPathEnvironmentVariable = "TEAMS_APP_TEST_TOOL_CONFIG_PATH";

    private static readonly HashSet<string> ScrubbedEnvironmentNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "APP_ENDPOINT",
            "APPLICATION_ENDPOINT",
            "AUTH_CLIENT_ID",
            "AUTH_CLIENT_SECRET",
            "AUTH_TENANT_ID",
            "BOT_ENDPOINT",
            "BROWSER",
            "CLIENT_ID",
            "CLIENT_SECRET",
            "CONVERSATION_ID",
            "DEFAULT_CHANNEL_ID",
            "DELIVERY_MODE",
            "ENABLE_EVENTS_RECORDING",
            "ENVIRONMENT_ID",
            "HOST",
            "LOGFILE",
            "M365_AGENTS_PLAYGROUND_CONFIG",
            "M365_AGENTS_PLAYGROUND_CONFIG_PATH",
            "M365_AGENTS_PLAYGROUND_CONVERSATION_ID",
            "M365_AGENTS_PLAYGROUND_DELIVERY_MODE",
            "M365_AGENTS_PLAYGROUND_ENABLE_EVENTS_RECORDING",
            "M365_AGENTS_PLAYGROUND_HOST",
            "M365_AGENTS_PLAYGROUND_LOGFILE",
            "M365_AGENTS_PLAYGROUND_RECORDING_FILE",
            "M365_AGENTS_PLAYGROUND_SERVICE_URL",
            "M365_AGENTS_PLAYGROUND_TARGET_SDK",
            "MSAL_CLIENT_SECRET",
            "PORT",
            "RECORDING_FILE",
            "SERVICE_URL",
            "TARGET_SDK",
            "TEAMS_APP_TEST_TOOL_CONFIG_PATH",
            "TEAMS_APP_TEST_TOOL_PORT",
            "TEAMSAPPTESTER_BROWSER",
            "TEAMSAPPTESTER_LOGFILE",
            "TEAMSAPPTESTER_PORT",
            "TENANT_ID",
            "NODE_OPTIONS",
            "NODE_PATH",
        };

    // Windows can briefly hold a just-terminated child's working directory (handle rundown, AV scans).
    internal static readonly TimeSpan[] InvocationDirectoryDeleteRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
    ];

    private readonly IPlaygroundProcessFactory _processFactory;
    private readonly TimeSpan _shutdownGracePeriod;
    private readonly Action<string> _deleteDirectory;
    private readonly Func<TimeSpan, Task> _delay;

    public PlaygroundLauncher(
        IPlaygroundProcessFactory? processFactory = null,
        TimeSpan? shutdownGracePeriod = null)
        : this(processFactory, shutdownGracePeriod, deleteDirectory: null, delay: null)
    {
    }

    internal PlaygroundLauncher(
        IPlaygroundProcessFactory? processFactory,
        TimeSpan? shutdownGracePeriod,
        Action<string>? deleteDirectory,
        Func<TimeSpan, Task>? delay)
    {
        _processFactory = processFactory ?? new ProcessPlaygroundProcessFactory();
        _shutdownGracePeriod = shutdownGracePeriod ?? ShutdownGracePeriod;
        _deleteDirectory = deleteDirectory ?? DeleteDirectoryIfPresent;
        _delay = delay ?? (static timeout => Task.Delay(timeout));
        if (_shutdownGracePeriod < TimeSpan.Zero ||
            _shutdownGracePeriod == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownGracePeriod));
        }
    }

    public static PlaygroundLaunchPlan BuildLaunchPlan(
        PlaygroundTool tool,
        LiveTestDestination target,
        Guid clientId,
        IReadOnlyDictionary<string, string>? inheritedEnvironment = null,
        string? workingDirectory = null,
        string? configPath = null)
    {
        ValidateTool(tool);
        ValidateTarget(target);
        ValidateLaunchPath(workingDirectory);
        ValidateLaunchPath(configPath);
        if (clientId == Guid.Empty)
        {
            throw Failure(
                PlaygroundLaunchFailureKind.InvalidClientId,
                "The playground client id is not configured.",
                "Configure a non-zero AgentHost public-client application id.");
        }

        var environment = new Dictionary<string, string>(
            StringComparer.Ordinal);
        if (inheritedEnvironment is null)
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string name && entry.Value is string value)
                {
                    environment[name] = value;
                }
            }
        }
        else
        {
            foreach (var pair in inheritedEnvironment)
            {
                environment[pair.Key] = pair.Value;
            }
        }

        foreach (var name in environment.Keys.ToArray())
        {
            if (IsScrubbedEnvironmentVariable(name))
            {
                environment.Remove(name);
            }
        }

        if (configPath is not null)
        {
            environment[ModernConfigPathEnvironmentVariable] = configPath;
            environment[LegacyConfigPathEnvironmentVariable] = configPath;
        }

        var endpoint = target.Endpoint!.AbsoluteUri;
        var arguments = new List<string>(tool.ArgumentPrefix.Count + 15);
        arguments.AddRange(tool.ArgumentPrefix);
        arguments.AddRange(
        [
            "start",
            "--target",
            "directengine",
            "--app-endpoint",
            endpoint,
            "--client-id",
            clientId.ToString("D"),
            "--tenant-id",
            target.TenantId.Trim(),
            "--environment-id",
            target.EnvironmentId.Trim(),
            "--host",
            "127.0.0.1",
            "--disable-telemetry",
        ]);

        return new PlaygroundLaunchPlan(
            tool.ExecutablePath,
            arguments,
            environment)
        {
            WorkingDirectory = workingDirectory,
        };
    }

    public static ProcessStartInfo BuildStartInfo(PlaygroundLaunchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var startInfo = new ProcessStartInfo
        {
            FileName = plan.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false,
        };

        if (plan.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = plan.WorkingDirectory;
        }

        foreach (var argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var pair in plan.Environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return startInfo;
    }

    public static ProcessStartInfo BuildStartInfo(
        PlaygroundTool tool,
        LiveTestDestination target,
        Guid clientId,
        IReadOnlyDictionary<string, string>? inheritedEnvironment = null,
        string? workingDirectory = null,
        string? configPath = null) =>
        BuildStartInfo(
            BuildLaunchPlan(
                tool,
                target,
                clientId,
                inheritedEnvironment,
                workingDirectory,
                configPath));

    public static bool IsScrubbedEnvironmentVariable(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.StartsWith("NPM_CONFIG_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ScrubbedEnvironmentNames.Contains(name))
        {
            return true;
        }

        var normalized = name.Replace("-", "_", StringComparison.Ordinal).ToUpperInvariant();
        var isDirectLineName = normalized.Contains("DIRECTLINE", StringComparison.Ordinal) ||
                               normalized.Contains("DIRECT_LINE", StringComparison.Ordinal);
        if (isDirectLineName &&
            (normalized.Contains("SECRET", StringComparison.Ordinal) ||
             normalized.Contains("TOKEN", StringComparison.Ordinal)))
        {
            return true;
        }

        return normalized.Contains("CLIENT_SECRET", StringComparison.Ordinal);
    }

    public async Task<int> RunAsync(
        PlaygroundTool tool,
        LiveTestDestination target,
        Guid clientId,
        Action started,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(started);
        cancellationToken.ThrowIfCancellationRequested();

        string invocationDirectory;
        try
        {
            invocationDirectory = CreateInvocationDirectory();
        }
        catch (Exception)
        {
            throw Failure(
                PlaygroundLaunchFailureKind.StartFailed,
                "The playground invocation directory could not be created.",
                "Verify the local profile is writable and try again.");
        }

        IPlaygroundProcess? process = null;
        int? exitCode = null;
        Exception? operationFailure = null;
        try
        {
            var configPath = Path.Combine(invocationDirectory, "absent-playground-config");
            var startInfo = BuildStartInfo(
                BuildLaunchPlan(
                    tool,
                    target,
                    clientId,
                    workingDirectory: invocationDirectory,
                    configPath: configPath));
            try
            {
                process = _processFactory.Start(startInfo)
                    ?? throw new InvalidOperationException("The process factory returned no process.");
            }
            catch (PlaygroundLaunchException)
            {
                throw;
            }
            catch (Exception)
            {
                throw Failure(
                    PlaygroundLaunchFailureKind.StartFailed,
                    "The playground process could not be started.",
                    "Verify the selected playground installation and try again.");
            }

            // MVP readiness is successful Process.Start only; no browser/local-ready signal is claimed.
            started();
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                exitCode = process.ExitCode;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                exitCode = 130;
            }
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        var processCleanupFailure = await CleanupProcessAsync(process).ConfigureAwait(false);
        var directoryCleanupFailure = await TryCleanupInvocationDirectoryAsync(invocationDirectory)
            .ConfigureAwait(false);
        if (processCleanupFailure is not null)
        {
            throw processCleanupFailure;
        }

        if (directoryCleanupFailure is not null)
        {
            throw directoryCleanupFailure;
        }

        if (operationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        return exitCode ?? 0;
    }

    private static void ValidateTool(PlaygroundTool tool)
    {
        if (tool is null ||
            string.IsNullOrWhiteSpace(tool.ExecutablePath) ||
            !Path.IsPathFullyQualified(tool.ExecutablePath) ||
            tool.ExecutablePath.Any(char.IsControl) ||
            tool.ArgumentPrefix.Any(argument => argument is null || argument.Any(char.IsControl)))
        {
            throw Failure(
                PlaygroundLaunchFailureKind.InvalidTool,
                "The selected playground executable is not a safe absolute path.",
                "Re-run playground tool discovery and select a supported installation.");
        }
    }

    private static void ValidateLaunchPath(string? path)
    {
        if (path is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            path.Any(char.IsControl))
        {
            throw Failure(
                PlaygroundLaunchFailureKind.InvalidLaunchDirectory,
                "The playground invocation path is not a safe absolute path.",
                "Retry the command so msagent can create a disposable local invocation directory.");
        }
    }

    private static void ValidateTarget(LiveTestDestination target)
    {
        if (target is null ||
            !string.Equals(target.TargetSource, "configured", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(target.TargetSource, "explicit", StringComparison.OrdinalIgnoreCase) ||
            target.Endpoint is null ||
            !target.Endpoint.IsAbsoluteUri ||
            !string.Equals(target.Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(target.Endpoint.Host) ||
            target.Endpoint.UserInfo.Length != 0 ||
            target.Endpoint.Fragment.Length != 0 ||
            (target.Endpoint.Port is not (-1 or 443)) ||
            string.IsNullOrWhiteSpace(target.EnvironmentId) ||
            target.EnvironmentId.Trim() != target.EnvironmentId ||
            target.EnvironmentId.Any(char.IsControl) ||
            target.EnvironmentId[0] == '-')
        {
            throw Failure(
                PlaygroundLaunchFailureKind.InvalidTarget,
                "The resolved playground target is not a supported HTTPS DirectEngine destination.",
                "Resolve the target again with a configured or explicit public-cloud live-test destination.");
        }

        if (!Guid.TryParse(target.TenantId, out var tenantId) || tenantId == Guid.Empty)
        {
            throw Failure(
                PlaygroundLaunchFailureKind.InvalidTenantId,
                "The resolved playground tenant id is not configured.",
                "Resolve the target again with a non-zero tenant GUID.");
        }
    }

    private async Task StopAsync(IPlaygroundProcess process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.SendInterrupt();
        }
        catch
        {
            // Continue to the bounded forceful cleanup path without exposing process details.
        }

        if (await WaitForExitWithinAsync(process, _shutdownGracePeriod).ConfigureAwait(false))
        {
            return;
        }

        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.KillTree();
        }
        catch
        {
            throw Failure(
                PlaygroundLaunchFailureKind.CleanupFailed,
                "The playground process could not be stopped safely.",
                "Close the playground process and retry the command.");
        }

        if (!await WaitForExitWithinAsync(process, _shutdownGracePeriod).ConfigureAwait(false))
        {
            throw Failure(
                PlaygroundLaunchFailureKind.CleanupFailed,
                "The playground process did not exit after bounded cleanup.",
                "Close the playground process and retry the command.");
        }
    }

    private static async Task<bool> WaitForExitWithinAsync(
        IPlaygroundProcess process,
        TimeSpan timeout)
    {
        var wait = process.WaitForExitAsync(CancellationToken.None);
        var completed = await Task.WhenAny(wait, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != wait)
        {
            return process.HasExited;
        }

        try
        {
            await wait.ConfigureAwait(false);
        }
        catch
        {
            return process.HasExited;
        }

        return true;
    }

    private static string CreateInvocationDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localApplicationData)
            ? AppContext.BaseDirectory
            : localApplicationData;
        var sessionsRoot = Path.Combine(
            root,
            "Microsoft",
            "Agents",
            "Playground",
            "sessions");
        Directory.CreateDirectory(sessionsRoot);

        var invocationDirectory = Path.Combine(
            sessionsRoot,
            $"launch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(invocationDirectory);
        return invocationDirectory;
    }

    private async Task<PlaygroundLaunchException?> CleanupProcessAsync(
        IPlaygroundProcess? process)
    {
        if (process is null)
        {
            return null;
        }

        PlaygroundLaunchException? cleanupFailure = null;
        try
        {
            if (!process.HasExited)
            {
                await StopAsync(process).ConfigureAwait(false);
            }
        }
        catch (PlaygroundLaunchException exception)
        {
            cleanupFailure = exception;
        }
        catch (Exception)
        {
            cleanupFailure = Failure(
                PlaygroundLaunchFailureKind.CleanupFailed,
                "The playground process could not be stopped safely.",
                "Close the playground process and retry the command.");
        }

        try
        {
            process.Dispose();
        }
        catch (Exception)
        {
            cleanupFailure ??= Failure(
                PlaygroundLaunchFailureKind.CleanupFailed,
                "The playground process could not be released safely.",
                "Close the playground process and retry the command.");
        }

        return cleanupFailure;
    }

    private async Task<PlaygroundLaunchException?> TryCleanupInvocationDirectoryAsync(
        string invocationDirectory)
    {
        Exception? lastFailure = null;
        // One initial attempt plus one retry per configured delay.
        for (var attempt = 0; attempt <= InvocationDirectoryDeleteRetryDelays.Length; attempt++)
        {
            if (attempt > 0)
            {
                await _delay(InvocationDirectoryDeleteRetryDelays[attempt - 1]).ConfigureAwait(false);
            }

            try
            {
                _deleteDirectory(invocationDirectory);
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                lastFailure = exception;
            }
            catch (Exception exception)
            {
                return InvocationDirectoryCleanupFailure(exception);
            }
        }

        return InvocationDirectoryCleanupFailure(lastFailure);
    }

    private static PlaygroundLaunchException InvocationDirectoryCleanupFailure(
        Exception? innerException) =>
        Failure(
            PlaygroundLaunchFailureKind.CleanupFailed,
            "The playground invocation directory could not be removed.",
            "Close the playground process and remove the owned invocation directory before retrying.",
            innerException);

    private static void DeleteDirectoryIfPresent(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PlaygroundLaunchException Failure(
        PlaygroundLaunchFailureKind kind,
        string message,
        string remediation,
        Exception? innerException = null) =>
        new(kind, message, remediation, innerException);
}

/// <summary>Starts a child with the exact native process arguments and inherited console handles.</summary>
public sealed class ProcessPlaygroundProcessFactory : IPlaygroundProcessFactory
{
    public IPlaygroundProcess Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The playground process did not start.");
        }

        return new ProcessPlaygroundProcess(process);
    }
}

internal sealed class ProcessPlaygroundProcess(Process process) : IPlaygroundProcess
{
    public int Id => process.Id;

    public bool HasExited => process.HasExited;

    public int ExitCode => process.ExitCode;

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        process.WaitForExitAsync(cancellationToken);

    public void SendInterrupt()
    {
        if (HasExited)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            _ = process.CloseMainWindow();
            return;
        }

        _ = UnixKill(process.Id, 2);
    }

    public void KillTree()
    {
        if (!HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    public void Dispose() => process.Dispose();

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int UnixKill(int processId, int signal);
}
