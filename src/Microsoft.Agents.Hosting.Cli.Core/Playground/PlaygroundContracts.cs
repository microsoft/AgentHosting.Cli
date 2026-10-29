namespace Microsoft.Agents.Hosting.Cli.Core.Playground;

/// <summary>A bounded Semantic Version used by playground and runtime probes.</summary>
public readonly struct PlaygroundVersion : IComparable<PlaygroundVersion>, IEquatable<PlaygroundVersion>
{
    private const int MaximumLength = 256;
    private const int MaximumIdentifierCount = 64;
    private const int MaximumIdentifierLength = 64;

    private readonly string? _preRelease;
    private readonly string? _buildMetadata;

    private PlaygroundVersion(int major, int minor, int patch, string? preRelease, string? buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _preRelease = preRelease;
        _buildMetadata = buildMetadata;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public string? PreRelease => _preRelease;

    public string? BuildMetadata => _buildMetadata;

    public static PlaygroundVersion Parse(string value) =>
        TryParse(value, out var version)
            ? version
            : throw new FormatException("The value is not a valid bounded semantic version.");

    public static bool TryParse(string? value, out PlaygroundVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(value) ||
            value.Length > MaximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        var plusIndex = value.IndexOf('+');
        if (plusIndex != value.LastIndexOf('+'))
        {
            return false;
        }

        var versionWithoutBuild = plusIndex < 0 ? value : value[..plusIndex];
        var buildMetadata = plusIndex < 0 ? null : value[(plusIndex + 1)..];
        if (buildMetadata is not null && !TryValidateIdentifiers(buildMetadata, numericLeadingZeroesAllowed: true))
        {
            return false;
        }

        var hyphenIndex = versionWithoutBuild.IndexOf('-');
        var core = hyphenIndex < 0 ? versionWithoutBuild : versionWithoutBuild[..hyphenIndex];
        var preRelease = hyphenIndex < 0 ? null : versionWithoutBuild[(hyphenIndex + 1)..];
        if (preRelease is not null && !TryValidateIdentifiers(preRelease, numericLeadingZeroesAllowed: false))
        {
            return false;
        }

        var coreParts = core.Split('.');
        if (coreParts.Length != 3 ||
            !TryParseNumericCorePart(coreParts[0], out var major) ||
            !TryParseNumericCorePart(coreParts[1], out var minor) ||
            !TryParseNumericCorePart(coreParts[2], out var patch))
        {
            return false;
        }

        version = new PlaygroundVersion(major, minor, patch, preRelease, buildMetadata);
        return true;
    }

    public int CompareTo(PlaygroundVersion other)
    {
        var comparison = Major.CompareTo(other.Major);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0)
        {
            return comparison;
        }

        if (_preRelease is null && other._preRelease is null)
        {
            return 0;
        }

        if (_preRelease is null)
        {
            return 1;
        }

        if (other._preRelease is null)
        {
            return -1;
        }

        var leftIdentifiers = _preRelease.Split('.');
        var rightIdentifiers = other._preRelease.Split('.');
        for (var index = 0; index < Math.Min(leftIdentifiers.Length, rightIdentifiers.Length); index++)
        {
            comparison = ComparePreReleaseIdentifier(leftIdentifiers[index], rightIdentifiers[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
    }

    public bool Equals(PlaygroundVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is PlaygroundVersion other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Major, Minor, Patch, _preRelease);

    public override string ToString()
    {
        var value = $"{Major}.{Minor}.{Patch}";
        if (_preRelease is not null)
        {
            value += $"-{_preRelease}";
        }

        if (_buildMetadata is not null)
        {
            value += $"+{_buildMetadata}";
        }

        return value;
    }

    public static bool operator <(PlaygroundVersion left, PlaygroundVersion right) => left.CompareTo(right) < 0;

    public static bool operator <=(PlaygroundVersion left, PlaygroundVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >(PlaygroundVersion left, PlaygroundVersion right) => left.CompareTo(right) > 0;

    public static bool operator >=(PlaygroundVersion left, PlaygroundVersion right) => left.CompareTo(right) >= 0;

    public static bool operator ==(PlaygroundVersion left, PlaygroundVersion right) => left.Equals(right);

    public static bool operator !=(PlaygroundVersion left, PlaygroundVersion right) => !left.Equals(right);

    private static int ComparePreReleaseIdentifier(string left, string right)
    {
        var leftNumeric = IsNumericIdentifier(left);
        var rightNumeric = IsNumericIdentifier(right);
        if (leftNumeric && rightNumeric)
        {
            var lengthComparison = left.Length.CompareTo(right.Length);
            return lengthComparison != 0
                ? lengthComparison
                : string.CompareOrdinal(left, right);
        }

        if (leftNumeric)
        {
            return -1;
        }

        if (rightNumeric)
        {
            return 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static bool TryParseNumericCorePart(string value, out int result)
    {
        result = 0;
        if (value.Length == 0 ||
            value.Length > 10 ||
            (value.Length > 1 && value[0] == '0') ||
            !IsNumericIdentifier(value))
        {
            return false;
        }

        long parsed = 0;
        foreach (var character in value)
        {
            parsed = (parsed * 10) + (character - '0');
            if (parsed > int.MaxValue)
            {
                return false;
            }
        }

        result = (int)parsed;
        return true;
    }

    private static bool TryValidateIdentifiers(string value, bool numericLeadingZeroesAllowed)
    {
        var identifiers = value.Split('.');
        if (identifiers.Length > MaximumIdentifierCount)
        {
            return false;
        }

        foreach (var identifier in identifiers)
        {
            if (identifier.Length == 0 ||
                identifier.Length > MaximumIdentifierLength ||
                !identifier.All(IsIdentifierCharacter) ||
                (!numericLeadingZeroesAllowed &&
                 IsNumericIdentifier(identifier) &&
                 identifier.Length > 1 &&
                 identifier[0] == '0'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNumericIdentifier(string value) =>
        value.Length > 0 && value.All(character => character is >= '0' and <= '9');

    private static bool IsIdentifierCharacter(char character) =>
        character is >= '0' and <= '9' or
            >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or '-';
}

/// <summary>The command and argument prefix needed to launch a discovered tool.</summary>
public sealed record PlaygroundTool(
    string ExecutablePath,
    IReadOnlyList<string> ArgumentPrefix,
    string SelectedPath,
    PlaygroundVersion Version);

/// <summary>One sanitized reason a PATH candidate was not selected.</summary>
public sealed record PlaygroundToolRejection(string SelectedPath, PlaygroundFailure Failure);

/// <summary>The result of locating a tool without exposing child-process output.</summary>
public sealed record PlaygroundToolDiscoveryResult(
    PlaygroundTool? Tool,
    IReadOnlyList<PlaygroundToolRejection> Rejections);

/// <summary>Categories of bounded tool discovery and version-probe failures.</summary>
public enum PlaygroundFailureKind
{
    None,
    ProbeFailed,
    ProbeTimedOut,
    ProbeCancelled,
    InvalidVersion,
    BelowMinimum,
    NodeNotFound,
    NodeBelowMinimum,
    NodeProbeTimedOut,
    NodeInvalidVersion,
    NodeProbeFailed,
    NpmNotFound,
    NpmInvalidVersion,
    NpmProbeTimedOut,
    NpmProbeFailed,
    EntryPointNotFound,
    InvalidPathEntry,
}

/// <summary>A sanitized, actionable failure from a tool or version probe.</summary>
public sealed record PlaygroundFailure
{
    public required PlaygroundFailureKind Kind { get; init; }

    public required string Message { get; init; }

    public string? Remediation { get; init; }

    public int? ExitCode { get; init; }

    public PlaygroundVersion? ObservedVersion { get; init; }
}

/// <summary>The process request used by the future captured process runner.</summary>
public sealed record PlaygroundProcessRequest
{
    public required string ExecutablePath { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public TimeSpan Timeout { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>A null value removes an inherited child environment variable.</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentOverrides { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);
}

/// <summary>Captured output from a completed child process.</summary>
public sealed record PlaygroundProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs a process without shell interpolation and captures bounded output for the caller.</summary>
public interface IPlaygroundProcessRunner
{
    Task<PlaygroundProcessResult> RunAsync(
        PlaygroundProcessRequest request,
        CancellationToken cancellationToken);
}

/// <summary>The process-visible PATH and filesystem seam used by the locator.</summary>
public interface IPlaygroundToolEnvironment
{
    bool IsWindows { get; }

    string? PathValue { get; }

    bool FileExists(string path);

    string GetFullPath(string path);

    /// <summary>Returns the fully resolved symlink target, or the full path when it is not a link.</summary>
    string ResolveRealPath(string path) => GetFullPath(path);
}

/// <summary>The default process environment used by playground discovery.</summary>
public sealed class SystemPlaygroundToolEnvironment : IPlaygroundToolEnvironment
{
    public bool IsWindows => OperatingSystem.IsWindows();

    public string? PathValue => Environment.GetEnvironmentVariable("PATH");

    public bool FileExists(string path) => File.Exists(path);

    public string GetFullPath(string path) => Path.GetFullPath(path);

    public string ResolveRealPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        try
        {
            var target = File.ResolveLinkTarget(fullPath, returnFinalTarget: true);
            return target is null ? fullPath : Path.GetFullPath(target.FullName);
        }
        catch (IOException)
        {
            return fullPath;
        }
        catch (UnauthorizedAccessException)
        {
            return fullPath;
        }
    }
}

/// <summary>Locates a usable global playground and detects npm only when installation is needed.</summary>
public interface IPlaygroundToolLocator
{
    Task<PlaygroundToolDiscoveryResult> LocateAsync(CancellationToken cancellationToken);

    /// <summary>Checks the selected playground's Node.js runtime, or discovers a runtime for installation when playground is null.</summary>
    Task<PlaygroundToolDiscoveryResult> DetectNodeAsync(
        PlaygroundTool? playground,
        CancellationToken cancellationToken);

    Task<PlaygroundToolDiscoveryResult> DetectNpmAsync(CancellationToken cancellationToken);
}
