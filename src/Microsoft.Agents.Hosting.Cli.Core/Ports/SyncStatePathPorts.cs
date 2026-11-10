namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>Recognizes the paths of the sync library's local state, the .mcs cache that only the cloud fills.</summary>
public interface ISyncStatePathRule
{
    /// <summary>Whether a '/'-separated path holds the sync-state folder as a segment at any depth.</summary>
    /// <param name="path">The path, as git lists it.</param>
    bool IsSyncStatePath(string path);
}

/// <summary>The git pathspecs of the sync library's local state, which match every file inside a .mcs folder at any depth and in any case.</summary>
public static class SyncStatePathspecs
{
    private static readonly string[] Patterns = [".mcs/*", "*/.mcs/*"];

    /// <summary>The pathspecs that select those files.</summary>
    public static IReadOnlyList<string> Included { get; } = [.. Patterns.Select(pattern => $":(icase){pattern}")];

    /// <summary>The pathspecs that leave those files out.</summary>
    public static IReadOnlyList<string> Excluded { get; } = [.. Patterns.Select(pattern => $":(exclude,icase){pattern}")];
}
