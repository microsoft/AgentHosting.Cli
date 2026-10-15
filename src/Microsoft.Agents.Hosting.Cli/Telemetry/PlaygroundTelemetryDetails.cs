namespace Microsoft.Agents.Hosting.Cli.Telemetry;

internal enum PlaygroundInstallResult
{
    NotNeeded,
    Installed,
    Declined,
    Failed,
}

internal enum PlaygroundErrorCategory
{
    None,
    Unsupported,
    ToolNotFound,
    VersionTooOld,
    InstallDeclined,
    InstallFailed,
    LaunchFailed,
    Timeout,
    Cancellation,
}

/// <summary>Privacy-safe metadata for the existing test playground outcome.</summary>
/// <remarks>
/// Only numeric three-component release versions are retained. SemVer prerelease and other
/// arbitrary version text is dropped from metrics rather than accepted.
/// </remarks>
internal sealed record PlaygroundTelemetryDetails
{
    private const int MaximumVersionLength = 32;

    internal PlaygroundTelemetryDetails(
        long durationMs,
        string? playgroundVersion,
        PlaygroundInstallResult installResult,
        PlaygroundErrorCategory errorCategory)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(durationMs);
        if (!Enum.IsDefined(installResult))
        {
            throw new ArgumentOutOfRangeException(nameof(installResult));
        }

        if (!Enum.IsDefined(errorCategory))
        {
            throw new ArgumentOutOfRangeException(nameof(errorCategory));
        }

        DurationMs = durationMs;
        PlaygroundVersion = NormalizeReleaseVersion(playgroundVersion);
        InstallResult = installResult;
        ErrorCategory = errorCategory;
    }

    internal long DurationMs { get; }

    internal string? PlaygroundVersion { get; }

    internal PlaygroundInstallResult InstallResult { get; }

    internal PlaygroundErrorCategory ErrorCategory { get; }

    private static string? NormalizeReleaseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)
            || version.Length > MaximumVersionLength)
        {
            return null;
        }

        string[] components = version.Split('.');
        if (components.Length != 3)
        {
            return null;
        }

        foreach (string component in components)
        {
            if (component.Length == 0
                || component.Length > 10
                || component.Length > 1 && component[0] == '0'
                || !component.All(character => character is >= '0' and <= '9')
                || !uint.TryParse(component, out _))
            {
                return null;
            }
        }

        return version;
    }
}
