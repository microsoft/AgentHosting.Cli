namespace Microsoft.Agents.Hosting.Cli.Core.Playground;

/// <summary>Discovers global playground and npm commands without invoking a shell.</summary>
public sealed class PlaygroundToolLocator : IPlaygroundToolLocator
{
    public static readonly PlaygroundVersion MinimumNodeVersion = PlaygroundVersion.Parse("22.0.0");
    public static readonly PlaygroundVersion MinimumPlaygroundVersion = PlaygroundVersion.Parse("0.2.29");
    public static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(10);

    private const string PlaygroundPackageEntryPoint =
        "node_modules\\@microsoft\\m365agentsplayground\\cli.js";
    private const string WindowsNpmPackageEntryPoint = "node_modules\\npm\\bin\\npm-cli.js";
    private const string MinimumPlaygroundInstallRemediation =
        "Install a supported version with `npm install -g @microsoft/m365agentsplayground@latest`.";

    private readonly IPlaygroundProcessRunner _processRunner;
    private readonly IPlaygroundToolEnvironment _environment;

    public PlaygroundToolLocator(
        IPlaygroundProcessRunner processRunner,
        IPlaygroundToolEnvironment environment)
    {
        _processRunner = processRunner;
        _environment = environment;
    }

    public PlaygroundToolLocator(IPlaygroundProcessRunner processRunner)
        : this(processRunner, new SystemPlaygroundToolEnvironment())
    {
    }

    public async Task<PlaygroundToolDiscoveryResult> LocateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rejections = new List<PlaygroundToolRejection>();
        foreach (var selectedPath in EnumerateCandidates("agentsplayground", rejections))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveCommand(
                    selectedPath,
                    "agentsplayground",
                    out var command,
                    out var resolutionFailure,
                    rejections))
            {
                if (resolutionFailure is not null)
                {
                    rejections.Add(new PlaygroundToolRejection(selectedPath, resolutionFailure));
                }

                continue;
            }

            var probe = await ProbeAsync(command!, MinimumPlaygroundVersion, PlaygroundProbeKind.Playground, cancellationToken)
                .ConfigureAwait(false);
            if (probe.Version is not null)
            {
                return new PlaygroundToolDiscoveryResult(
                    command! with { Version = probe.Version.Value },
                    rejections);
            }

            rejections.Add(new PlaygroundToolRejection(selectedPath, probe.Failure!));
        }

        return new PlaygroundToolDiscoveryResult(null, rejections);
    }

    public async Task<PlaygroundToolDiscoveryResult> DetectNpmAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rejections = new List<PlaygroundToolRejection>();
        var nodeCandidates = EnumerateCandidates("node", rejections);
        var node = await FindUsableNodeAsync(nodeCandidates, rejections, cancellationToken).ConfigureAwait(false);
        if (node is null)
        {
            if (nodeCandidates.Count == 0)
            {
                rejections.Add(new PlaygroundToolRejection(
                    "node",
                    Failure(
                        PlaygroundFailureKind.NodeNotFound,
                        "Node.js 22 or newer was not found on PATH.",
                        "Install Node.js 22 or newer and ensure it is available on PATH.")));
            }

            return new PlaygroundToolDiscoveryResult(null, rejections);
        }

        var npmCandidates = EnumerateCandidates("npm", rejections);
        foreach (var selectedPath in npmCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveCommand(
                    selectedPath,
                    "npm",
                    out var npmCommand,
                    out var resolutionFailure,
                    rejections))
            {
                if (resolutionFailure is not null)
                {
                    rejections.Add(new PlaygroundToolRejection(selectedPath, resolutionFailure));
                }

                continue;
            }

            if (npmCommand!.ArgumentPrefix.Count > 0)
            {
                npmCommand = npmCommand with { ExecutablePath = node.ExecutablePath };
            }

            var probe = await ProbeAsync(npmCommand!, minimum: null, PlaygroundProbeKind.Npm, cancellationToken)
                .ConfigureAwait(false);
            if (probe.Version is not null)
            {
                return new PlaygroundToolDiscoveryResult(
                    npmCommand! with { Version = probe.Version.Value },
                    rejections);
            }

            rejections.Add(new PlaygroundToolRejection(selectedPath, probe.Failure!));
        }

        if (npmCandidates.Count == 0)
        {
            rejections.Add(new PlaygroundToolRejection(
                "npm",
                Failure(
                    PlaygroundFailureKind.NpmNotFound,
                    "npm was not found on PATH.",
                    "Install npm with Node.js 22 or newer and ensure it is available on PATH.")));
        }

        return new PlaygroundToolDiscoveryResult(null, rejections);
    }

    private async Task<PlaygroundTool?> FindUsableNodeAsync(
        IReadOnlyList<string> candidates,
        List<PlaygroundToolRejection> rejections,
        CancellationToken cancellationToken)
    {
        foreach (var selectedPath in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveCommand(
                    selectedPath,
                    "node",
                    out var command,
                    out var resolutionFailure,
                    rejections))
            {
                if (resolutionFailure is not null)
                {
                    rejections.Add(new PlaygroundToolRejection(selectedPath, resolutionFailure));
                }

                continue;
            }

            var probe = await ProbeAsync(command!, MinimumNodeVersion, PlaygroundProbeKind.Node, cancellationToken)
                .ConfigureAwait(false);
            if (probe.Version is not null)
            {
                return command! with { Version = probe.Version.Value };
            }

            rejections.Add(new PlaygroundToolRejection(selectedPath, probe.Failure!));
        }

        return null;
    }

    private async Task<ProbeResult> ProbeAsync(
        PlaygroundTool command,
        PlaygroundVersion? minimum,
        PlaygroundProbeKind kind,
        CancellationToken cancellationToken)
    {
        var request = new PlaygroundProcessRequest
        {
            ExecutablePath = command.ExecutablePath,
            Arguments = [.. command.ArgumentPrefix, "--version"],
            Timeout = VersionProbeTimeout,
        };

        PlaygroundProcessResult processResult;
        try
        {
            processResult = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new ProbeResult(null, Failure(
                TimeoutKind(kind),
                $"{DisplayName(kind)} did not return a version within {VersionProbeTimeout.TotalSeconds:0} seconds.",
                "Check the installation and try the command again."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ProbeResult(null, Failure(
                PlaygroundFailureKind.ProbeCancelled,
                $"{DisplayName(kind)} version probing was cancelled.",
                "Try the command again."));
        }
        catch (Exception)
        {
            return new ProbeResult(null, Failure(
                ProbeFailureKind(kind),
                $"{DisplayName(kind)} could not be started for a version probe.",
                "Check that the executable is available and runnable."));
        }

        if (processResult.ExitCode != 0)
        {
            return new ProbeResult(null, Failure(
                ProbeFailureKind(kind),
                $"{DisplayName(kind)} version probing exited with code {processResult.ExitCode}.",
                "Check that the executable is available and runnable.",
                processResult.ExitCode));
        }

        var versionText = processResult.StandardOutput.Trim();
        if (kind == PlaygroundProbeKind.Node &&
            versionText.StartsWith('v'))
        {
            versionText = versionText[1..];
        }

        if (!PlaygroundVersion.TryParse(versionText, out var version))
        {
            return new ProbeResult(null, Failure(
                InvalidVersionKind(kind),
                $"{DisplayName(kind)} returned an invalid semantic version.",
                "Install a supported version and try the command again."));
        }

        if (minimum is not null && version < minimum.Value)
        {
            return new ProbeResult(null, Failure(
                BelowMinimumKind(kind),
                $"{DisplayName(kind)} version {version} is below the required minimum {minimum.Value}.",
                kind == PlaygroundProbeKind.Playground
                    ? MinimumPlaygroundInstallRemediation
                    : "Install Node.js 22 or newer and ensure it is available on PATH.",
                observedVersion: version));
        }

        return new ProbeResult(version, null);
    }

    private bool TryResolvePosixNpmCommand(
        string selectedPath,
        out PlaygroundTool? command,
        out PlaygroundFailure? failure)
    {
        command = null;
        failure = null;
        var directory = Path.GetDirectoryName(selectedPath);
        if (directory is null)
        {
            failure = Failure(
                PlaygroundFailureKind.EntryPointNotFound,
                "The npm script has no usable directory.",
                "Reinstall npm with Node.js 22 or newer and try the command again.");
            return false;
        }

        var prefix = Path.GetDirectoryName(directory);
        var resolved = _environment.GetFullPath(_environment.ResolveRealPath(selectedPath));
        var layoutEntryPoint = prefix is null
            ? null
            : _environment.GetFullPath(Path.Combine(
                prefix,
                "lib",
                "node_modules",
                "npm",
                "bin",
                "npm-cli.js"));
        var entryPoint =
            string.Equals(Path.GetFileName(resolved), "npm-cli.js", StringComparison.Ordinal) &&
            _environment.FileExists(resolved)
                ? resolved
                : layoutEntryPoint;
        if (entryPoint is null || !_environment.FileExists(entryPoint))
        {
            failure = Failure(
                PlaygroundFailureKind.EntryPointNotFound,
                "The npm script has no resolvable npm CLI entry point.",
                "Reinstall npm with Node.js 22 or newer and try the command again.");
            return false;
        }

        command = new PlaygroundTool(
            selectedPath,
            [entryPoint],
            selectedPath,
            default);
        return true;
    }

    private bool TryResolveCommand(
        string selectedPath,
        string commandName,
        out PlaygroundTool? command,
        out PlaygroundFailure? failure,
        List<PlaygroundToolRejection>? rejections = null)
    {
        command = null;
        failure = null;
        selectedPath = _environment.GetFullPath(selectedPath);

        if (!_environment.IsWindows &&
            commandName == "npm")
        {
            return TryResolvePosixNpmCommand(selectedPath, out command, out failure);
        }

        if (!_environment.IsWindows ||
            !selectedPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            command = new PlaygroundTool(
                selectedPath,
                [],
                selectedPath,
                default);
            return true;
        }

        var entryPointRelativePath = commandName switch
        {
            "agentsplayground" => PlaygroundPackageEntryPoint,
            "npm" => WindowsNpmPackageEntryPoint,
            _ => null,
        };
        if (entryPointRelativePath is null)
        {
            failure = Failure(
                PlaygroundFailureKind.EntryPointNotFound,
                "The command shim is not a recognized npm entry point.",
                "Use the supported global command installed by npm.");
            return false;
        }

        var directory = Path.GetDirectoryName(selectedPath);
        if (directory is null)
        {
            failure = Failure(
                PlaygroundFailureKind.EntryPointNotFound,
                "The recognized npm shim has no usable directory.",
                "Reinstall the global package and try the command again.");
            return false;
        }

        var entryPoint = _environment.GetFullPath(Path.Combine(directory, entryPointRelativePath));
        if (!_environment.FileExists(entryPoint))
        {
            failure = Failure(
                PlaygroundFailureKind.EntryPointNotFound,
                "The recognized npm shim has no adjacent package entry point.",
                "Reinstall the global package and try the command again.");
            return false;
        }

        var nodePath = _environment.GetFullPath(Path.Combine(directory, "node.exe"));
        if (!_environment.FileExists(nodePath))
        {
            nodePath = EnumerateCandidates("node", rejections)
                .Select(_environment.GetFullPath)
                .FirstOrDefault(_environment.FileExists);
        }

        if (nodePath is null)
        {
            failure = Failure(
                PlaygroundFailureKind.NodeNotFound,
                "Node.js was not found for the recognized npm shim.",
                "Install Node.js 22 or newer and ensure it is available on PATH.");
            return false;
        }

        command = new PlaygroundTool(nodePath, [entryPoint], selectedPath, default);
        return true;
    }

    private List<string> EnumerateCandidates(
        string commandName,
        List<PlaygroundToolRejection>? rejections = null)
    {
        var names = GetCandidateNames(commandName);
        var candidates = new List<string>();
        var seen = new HashSet<string>(
            _environment.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var directory in SplitPath(rejections))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = _environment.GetFullPath(Path.Combine(directory, name));
                    if (_environment.FileExists(candidate) && seen.Add(candidate))
                    {
                        candidates.Add(candidate);
                    }
                }
                catch (Exception)
                {
                    AddPathRejection(rejections, directory);
                    break;
                }
            }
        }

        return candidates;
    }

    private string[] GetCandidateNames(string commandName)
    {
        if (!_environment.IsWindows)
        {
            return [commandName];
        }

        return commandName switch
        {
            "agentsplayground" => ["agentsplayground.cmd", "agentsplayground.exe", "agentsplayground"],
            "node" => ["node.exe", "node"],
            "npm" => ["npm.cmd", "npm.exe", "npm"],
            _ => [commandName],
        };
    }

    private IEnumerable<string> SplitPath(List<PlaygroundToolRejection>? rejections)
    {
        if (string.IsNullOrWhiteSpace(_environment.PathValue))
        {
            yield break;
        }

        var pathSeparator = _environment.IsWindows ? ';' : Path.PathSeparator;
        foreach (var rawDirectory in _environment.PathValue.Split(
                     pathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim();
            if (directory.Length == 0)
            {
                continue;
            }

            if (_environment.IsWindows && directory.Contains('"'))
            {
                if (directory.Length < 2 ||
                    directory[0] != '"' ||
                    directory[^1] != '"' ||
                    directory[1..^1].Contains('"'))
                {
                    AddPathRejection(rejections, rawDirectory);
                    continue;
                }

                directory = directory[1..^1];
                if (directory.Length == 0)
                {
                    AddPathRejection(rejections, rawDirectory);
                    continue;
                }
            }

            string? fullDirectory;
            try
            {
                fullDirectory = _environment.GetFullPath(directory);
            }
            catch (Exception)
            {
                AddPathRejection(rejections, rawDirectory);
                continue;
            }

            if (string.IsNullOrWhiteSpace(fullDirectory))
            {
                AddPathRejection(rejections, rawDirectory);
                continue;
            }

            yield return fullDirectory;
        }
    }

    private static void AddPathRejection(
        List<PlaygroundToolRejection>? rejections,
        string selectedPath)
    {
        if (rejections is null ||
            rejections.Any(rejection =>
                rejection.SelectedPath.Equals(selectedPath, StringComparison.Ordinal)))
        {
            return;
        }

        rejections.Add(new PlaygroundToolRejection(
            selectedPath,
            Failure(
                PlaygroundFailureKind.InvalidPathEntry,
                "A PATH entry was malformed and was skipped.",
                "Fix the PATH entry and try the command again.")));
    }

    private static PlaygroundFailure Failure(
        PlaygroundFailureKind kind,
        string message,
        string? remediation = null,
        int? exitCode = null,
        PlaygroundVersion? observedVersion = null) =>
        new()
        {
            Kind = kind,
            Message = message,
            Remediation = remediation,
            ExitCode = exitCode,
            ObservedVersion = observedVersion,
        };

    private static string DisplayName(PlaygroundProbeKind kind) =>
        kind switch
        {
            PlaygroundProbeKind.Playground => "The playground tool",
            PlaygroundProbeKind.Node => "Node.js",
            PlaygroundProbeKind.Npm => "npm",
            _ => "The command",
        };

    private static PlaygroundFailureKind TimeoutKind(PlaygroundProbeKind kind) =>
        kind == PlaygroundProbeKind.Playground
            ? PlaygroundFailureKind.ProbeTimedOut
            : kind == PlaygroundProbeKind.Node
                ? PlaygroundFailureKind.NodeProbeTimedOut
                : PlaygroundFailureKind.NpmProbeTimedOut;

    private static PlaygroundFailureKind ProbeFailureKind(PlaygroundProbeKind kind) =>
        kind == PlaygroundProbeKind.Playground
            ? PlaygroundFailureKind.ProbeFailed
            : kind == PlaygroundProbeKind.Node
                ? PlaygroundFailureKind.NodeProbeFailed
                : PlaygroundFailureKind.NpmProbeFailed;

    private static PlaygroundFailureKind InvalidVersionKind(PlaygroundProbeKind kind) =>
        kind == PlaygroundProbeKind.Playground
            ? PlaygroundFailureKind.InvalidVersion
            : kind == PlaygroundProbeKind.Node
                ? PlaygroundFailureKind.NodeInvalidVersion
                : PlaygroundFailureKind.NpmInvalidVersion;

    private static PlaygroundFailureKind BelowMinimumKind(PlaygroundProbeKind kind) =>
        kind == PlaygroundProbeKind.Playground
            ? PlaygroundFailureKind.BelowMinimum
            : PlaygroundFailureKind.NodeBelowMinimum;

    private enum PlaygroundProbeKind
    {
        Playground,
        Node,
        Npm,
    }

    private sealed record ProbeResult(PlaygroundVersion? Version, PlaygroundFailure? Failure);
}
