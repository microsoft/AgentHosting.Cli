using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>The binary under test, and where it came from.</summary>
public sealed record MsagentBinary(string Path, string Origin, string Version);

/// <summary>Packs the CLI and installs it into a short tool folder, so every run drives freshly built code.</summary>
public sealed class MsagentCliBuilder
{
    private const string PackageId = "Microsoft.Agents.Hosting.Cli";

    /// <summary>The longest path the Windows loader will accept when resolving a native dependency.</summary>
    public const int MaximumNativeAssetPathLength = 259;

    /// <summary>The file the tool installs as today.</summary>
    public static string ExecutableName => Executable("msagent");

    /// <summary>The file the tool installed as before the rename, still found by cleanup.</summary>
    public static string LegacyExecutableName => Executable("ah");

    /// <summary>Every name an installed tool may carry, current one first.</summary>
    public static IReadOnlyList<string> ExecutableNames => [ExecutableName, LegacyExecutableName];

    private static string Executable(string commandName) => OperatingSystem.IsWindows() ? commandName + ".exe" : commandName;

    /// <summary>Finds the installed executable, which a runtime-specific tool keeps in its store rather than beside its command shim, or null when there is none.</summary>
    /// <param name="toolDirectory">The directory the tool was installed into.</param>
    public static string? FindInstalledBinary(string toolDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolDirectory);

        return ExecutableNames.Select(name => Path.Combine(toolDirectory, name)).FirstOrDefault(File.Exists) ?? FindStoredEntryPoint(Path.Combine(toolDirectory, ".store"));
    }

    private static string? FindStoredEntryPoint(string storeDirectory) => Directory.Exists(storeDirectory)
        ? Directory.EnumerateFiles(storeDirectory, "DotnetToolSettings.xml", SearchOption.AllDirectories)
            .SelectMany(settings => XDocument.Load(settings).Descendants("Command")
                .Where(command => (string?)command.Attribute("Runner") == "executable")
                .Select(command => (string?)command.Attribute("EntryPoint"))
                .OfType<string>()
                .Select(entryPoint => Path.Combine(Path.GetDirectoryName(settings) ?? storeDirectory, entryPoint)))
            .FirstOrDefault(File.Exists)
        : null;

    private readonly string _sourceRoot;
    private readonly Transcript _transcript;

    public MsagentCliBuilder(string sourceRoot, Transcript transcript)
    {
        _sourceRoot = sourceRoot;
        _transcript = transcript;
    }

    /// <summary>Uses the operator's binary when one is named, otherwise packs and installs the tool.</summary>
    /// <param name="runDirectory">The run folder, which receives the built package.</param>
    /// <param name="toolDirectory">Where the tool is installed, kept short for the native loader.</param>
    /// <param name="overridePath">An operator-supplied binary, used in place of building one.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    public async Task<MsagentBinary> AcquireAsync(string runDirectory, string toolDirectory, string? overridePath, CancellationToken cancellationToken)
    {
        if (overridePath is not { Length: > 0 })
        {
            return await PackAndInstallAsync(runDirectory, toolDirectory, cancellationToken).ConfigureAwait(false);
        }

        var supplied = new MsagentBinary(overridePath, "--msagent-path override", "unknown");

        return supplied with { Version = await ReadVersionAsync(supplied.Path, runDirectory, cancellationToken).ConfigureAwait(false) };
    }

    /// <summary>
    /// Fails when an installed native asset sits beyond the loader's reach.
    ///
    /// A native dependency the loader cannot open does not stop the process — it falls back to the OS
    /// search path and may bind a same-named library belonging to something else entirely. Catching the
    /// path here turns that silent substitution into a stated failure.
    /// </summary>
    /// <param name="toolDirectory">The directory the tool was installed into.</param>
    public static void VerifyNativeAssetsAreReachable(string toolDirectory)
    {
        var unreachable = Directory
            .EnumerateFiles(toolDirectory, "*", SearchOption.AllDirectories)
            .Where(path => path.Length > MaximumNativeAssetPathLength && IsNativeAsset(path))
            .ToArray();

        if (unreachable.Length == 0)
        {
            return;
        }

        throw new E2EException(
            $"The tool installed into {toolDirectory}, where {unreachable.Length} native asset(s) exceed the "
            + $"{MaximumNativeAssetPathLength}-character limit the Windows loader applies. The loader would "
            + $"silently fall back to PATH and bind a different library. Longest: {unreachable.MaxBy(path => path.Length)}");
    }

    private static bool IsNativeAsset(string path) => path.Contains($"{Path.DirectorySeparatorChar}native{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".so" or ".dylib" => true,
        ".dll" => !IsManagedAssembly(path),
        _ => false,
    };

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            using var reader = new PEReader(File.OpenRead(path));

            return reader.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private async Task<MsagentBinary> PackAndInstallAsync(string runDirectory, string toolDirectory, CancellationToken cancellationToken)
    {
        var packageDirectory = Path.Combine(runDirectory, "pkg");
        Directory.CreateDirectory(packageDirectory);
        ResetToolDirectory(toolDirectory);

        await RunToolchainAsync(
            "dotnet",
            ["pack", Path.Combine(_sourceRoot, "Microsoft.Agents.Hosting.Cli"), "-c", "Release", "-o", packageDirectory],
            "pack the CLI",
            cancellationToken).ConfigureAwait(false);

        var packed = PackedVersion(packageDirectory);

        await RunToolchainAsync(
            "dotnet",
            ["tool", "install", PackageId, "--configfile", WriteIsolatedFeed(packageDirectory), "--tool-path", toolDirectory, "--version", packed],
            $"install the packed tool {packed}",
            cancellationToken).ConfigureAwait(false);

        VerifyNativeAssetsAreReachable(toolDirectory);

        var path = LocateInstalledTool(toolDirectory);
        var reported = await ReadVersionAsync(path, runDirectory, cancellationToken).ConfigureAwait(false);
        VerifyInstalledVersionMatches(packed, reported, await ReadHeadCommitAsync(cancellationToken).ConfigureAwait(false));

        return new MsagentBinary(path, "packed from source", reported);
    }

    /// <summary>The commit the source tree sits on, or empty when git cannot say.</summary>
    private async Task<string> ReadHeadCommitAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _sourceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        string[] arguments = ["rev-parse", "HEAD"];

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            var result = await ProcessRunner
                .RunAsync(startInfo, arguments, stdin: null, TimeSpan.FromMinutes(1), _transcript, cancellationToken)
                .ConfigureAwait(false);

            return result.ExitCode == 0 ? result.StdOut.Trim() : string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The version of the tool package <c>dotnet pack</c> just produced, which the install is then pinned to; the runtime-specific packages packed beside it are skipped.
    /// </summary>
    /// <param name="packageDirectory">The directory the package was written to.</param>
    public static string PackedVersion(string packageDirectory)
    {
        var versions = Directory.GetFiles(packageDirectory, $"{PackageId}.*.nupkg")
            .Select(package => Path.GetFileNameWithoutExtension(package)[(PackageId.Length + 1)..])
            .Where(version => char.IsAsciiDigit(version.FirstOrDefault()))
            .ToArray();

        return versions.Length == 1
            ? versions[0]
            : throw new E2EException(
                $"Expected exactly one {PackageId} package in {packageDirectory}, found {versions.Length}. "
                + "The install is pinned to the packed version, so it cannot choose between them.");
    }

    /// <summary>
    /// Writes a NuGet configuration naming the packed output and nothing else.
    ///
    /// <para><c>--add-source</c> adds a feed rather than replacing the configured ones, and
    /// <c>dotnet tool install</c> then resolves the highest version across all of them. A feed
    /// publishing a newer build of this same package therefore wins, and the suite silently exercises
    /// the published tool instead of the working tree. The tool package and the runtime-specific
    /// packages it installs are all packed into this directory, so clearing every other source costs nothing.</para>
    /// </summary>
    /// <param name="packageDirectory">The directory holding the packed package.</param>
    public static string WriteIsolatedFeed(string packageDirectory)
    {
        var path = Path.Combine(packageDirectory, "nuget.config");

        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="e2e-packed" value="{System.Security.SecurityElement.Escape(packageDirectory)}" />
              </packageSources>
            </configuration>
            """);

        return path;
    }

    /// <summary>
    /// Fails when the installed tool is not the one that was just packed, from the commit under test.
    /// </summary>
    /// <param name="packed">The version <c>dotnet pack</c> produced.</param>
    /// <param name="reported">The version the installed tool reports.</param>
    /// <param name="headCommit">The commit the working tree sits on, empty when git cannot say.</param>
    public static void VerifyInstalledVersionMatches(string packed, string reported, string headCommit)
    {
        var (expectedRelease, expectedCommit) = SplitVersion(packed);
        var (actualRelease, actualCommit) = SplitVersion(reported);

        if (actualRelease.Length == 0 || !string.Equals(expectedRelease, actualRelease, StringComparison.Ordinal))
        {
            throw new E2EException(
                $"The harness packed {packed} but the installed tool reports {reported}, so the run would not exercise this working tree.");
        }

        if (actualCommit.Length == 0)
        {
            throw new E2EException(
                $"The installed tool reports {reported}, which names no commit, so it cannot be shown to come from the commit under test.");
        }

        if (expectedCommit.Length > 0 && !string.Equals(expectedCommit, actualCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new E2EException(
                $"The harness packed {packed} but the installed tool reports {reported}, built from a different commit.");
        }

        if (headCommit.Length > 0 && !headCommit.StartsWith(actualCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new E2EException(
                $"The installed tool reports {reported}, built from a commit other than the checked-out {headCommit}, so the run would not exercise this working tree.");
        }

        if (expectedCommit.Length == 0 && headCommit.Length == 0)
        {
            throw new E2EException(
                $"The harness packed {packed}, which names no commit, and git could not name the checked-out commit, so the installed {reported} cannot be tied to the source under test.");
        }
    }

    private static (string Release, string Commit) SplitVersion(string version)
    {
        var build = version.IndexOf('+', StringComparison.Ordinal);
        var release = build < 0 ? version : version[..build];
        var commit = build < 0 ? string.Empty : version[(build + 1)..];
        var suffix = release.IndexOf("-g", StringComparison.Ordinal);

        return suffix < 0 ? (release, commit) : (release[..suffix], release[(suffix + 2)..]);
    }

    private static void ResetToolDirectory(string toolDirectory)
    {
        if (Directory.Exists(toolDirectory))
        {
            Directory.Delete(toolDirectory, recursive: true);
        }

        Directory.CreateDirectory(toolDirectory);
    }

    private static string LocateInstalledTool(string toolDirectory) => FindInstalledBinary(toolDirectory) ?? throw new E2EException($"The tool installed but neither {ExecutableName} nor a stored entry point is in {toolDirectory}.");

    private async Task RunToolchainAsync(string fileName, IReadOnlyList<string> arguments, string what, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var result = await ProcessRunner
            .RunAsync(startInfo, arguments, stdin: null, TimeSpan.FromMinutes(10), _transcript, cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new E2EException($"Could not {what} (exit {result.ExitCode}).\n{result.StdOut}\n{result.StdErr}");
        }
    }

    private async Task<string> ReadVersionAsync(string binaryPath, string runDirectory, CancellationToken cancellationToken)
    {
        var cli = new MsagentCli(binaryPath, _transcript, CrashDumps.DirectoryFor(runDirectory));
        var result = await cli.RunAsync(["--version"], cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.ExitCode == 0 && result.StdOut.Trim() is { Length: > 0 } version ? version.Trim() : "unknown";
    }
}
