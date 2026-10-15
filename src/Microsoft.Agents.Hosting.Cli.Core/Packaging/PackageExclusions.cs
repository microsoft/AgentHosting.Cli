namespace Microsoft.Agents.Hosting.Cli.Core.Packaging;

/// <summary>
/// The fixed, closed version-zero exclusion list.
///
/// <para>
/// The builder never reads <c>.gitignore</c>, <c>.dockerignore</c>, or any other environment-supplied
/// ignore-set. Two contributors on different machines must produce the same digest from the same
/// tracked source, and a discovered ignore-set makes the digest a property of the working copy rather
/// than of the content. Any other exclusion requires a package version bump; an unexpected large or
/// transient directory is a validation failure the operator resolves, not a silent exclusion.
/// </para>
///
/// <para>
/// Matching is ordinal case-insensitive. A case-insensitive filesystem cannot hold both <c>bin</c> and
/// <c>Bin</c>, so case-insensitive matching is the rule that gives the same answer on Windows, macOS,
/// and Linux for the same tracked source.
/// </para>
/// </summary>
public static class PackageExclusions
{
    /// <summary>Directory names excluded wherever they appear in the tree.</summary>
    public static readonly IReadOnlyList<string> Directories =
    [
        ".git",
        ".svn",
        ".hg",
        "node_modules",
        "bin",
        "obj",
        ".vs",
        ".vscode",
        ".idea",
        "__pycache__",
        ".venv",
        "venv",
    ];

    /// <summary>File names excluded wherever they appear in the tree.</summary>
    public static readonly IReadOnlyList<string> FileNames =
    [
        ".git",
        ".DS_Store",
        "Thumbs.db",
    ];

    /// <summary>File extensions excluded wherever they appear in the tree.</summary>
    public static readonly IReadOnlyList<string> FileExtensions =
    [
        ".user",
        ".suo",
    ];

    /// <summary>Whether <paramref name="directoryName"/> is an excluded directory.</summary>
    public static bool IsExcludedDirectory(string directoryName) =>
        Directories.Contains(directoryName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="fileName"/> is an excluded file.</summary>
    public static bool IsExcludedFile(string fileName) =>
        FileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase) ||
        FileExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);
}
