namespace Microsoft.Agents.Hosting.Cli.Core.Packaging;

/// <summary>
/// Local bounds applied while the CLI constructs a version-zero package.
///
/// <para>
/// Every entry is stored rather than compressed, so <see cref="MaxArchiveBytes"/> directly bounds
/// the bytes sent. <see cref="MaxEntryCount"/> bounds the source enumeration and writer metadata the
/// local process must hold while building.
/// </para>
///
/// <para>
/// AMS does not use this table. It independently bounds the request body and otherwise treats the
/// archive as opaque bytes.
/// </para>
///
/// <para>
/// A non-default instance exists so tests can exercise bound enforcement without materializing
/// hundreds of megabytes.
/// </para>
/// </summary>
public sealed record AgentPackageLimits
{
    /// <summary>The version-zero bound table.</summary>
    public static readonly AgentPackageLimits V0 = new()
    {
        MaxArchiveBytes = 128L * 1024 * 1024,
        MaxEntryCount = 10_000,
    };

    /// <summary>Maximum size of the archive itself — the publication request body.</summary>
    public required long MaxArchiveBytes { get; init; }

    /// <summary>Maximum number of archive entries, including the root envelope.</summary>
    public required int MaxEntryCount { get; init; }
}
