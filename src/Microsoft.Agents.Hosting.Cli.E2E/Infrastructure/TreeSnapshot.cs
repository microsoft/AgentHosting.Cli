using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>Walks, searches, and removes directory trees, skipping named folders at any depth.</summary>
public static class FileTree
{
    private static readonly string[] BinaryExtensions = [".png", ".jpg", ".jpeg", ".gif", ".ico", ".zip", ".dll", ".exe", ".pdf", ".docx", ".xlsx", ".pptx"];

    /// <summary>Every file beneath a directory, skipping folders with the given names.</summary>
    public static IEnumerable<string> EnumerateFiles(string directory, IReadOnlyCollection<string> excludedFolders) => Directory.EnumerateFiles(directory).Concat(Directory.EnumerateDirectories(directory).Where(child => !excludedFolders.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)).SelectMany(child => EnumerateFiles(child, excludedFolders)));

    /// <summary>Whether a file holds binary content that text searches and rewrites skip.</summary>
    public static bool IsBinary(string path) => BinaryExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The text files beneath a directory that mention any of the fragments, matched case-insensitively.</summary>
    public static IReadOnlyList<string> FilesMentioning(string directory, IReadOnlyCollection<string> fragments, IReadOnlyCollection<string> excludedFolders) => fragments.Count == 0 ? [] : [.. Matches(directory, new Regex(string.Join('|', fragments.Select(Regex.Escape)), RegexOptions.IgnoreCase), excludedFolders).Select(found => found.Path)];

    /// <summary>Each text file beneath a directory that matches a pattern, with the distinct values matched in it.</summary>
    public static IReadOnlyList<(string Path, IReadOnlyList<string> Values)> Matches(string directory, Regex pattern, IReadOnlyCollection<string> excludedFolders) => Directory.Exists(directory) ? [.. EnumerateFiles(directory, excludedFolders).Where(path => !IsBinary(path)).Select(path => (Path: path, Values: (IReadOnlyList<string>)[.. pattern.Matches(File.ReadAllText(path)).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase)])).Where(found => found.Values.Count > 0)] : [];

    /// <summary>The nearest directory, from a starting one upwards, that passes a test.</summary>
    public static string? FindAncestor(string start, Func<string, bool> test)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (test(directory.FullName))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    /// <summary>Copies a directory tree, skipping files and folders with the given names at any depth.</summary>
    public static void CopyDirectory(string source, string destination, IReadOnlyCollection<string> excludedNames) => CopyTree(source, destination, relative => excludedNames.Contains(Path.GetFileName(relative), StringComparer.OrdinalIgnoreCase) ? null : relative);

    /// <summary>Copies a directory tree, relocating or skipping each entry, with everything below it, as the placement decides.</summary>
    public static void CopyTree(string source, string destination, Func<string, string?> place)
    {
        Directory.CreateDirectory(destination);
        CopyEntries(source, destination, string.Empty, place);
    }

    private static void CopyEntries(string source, string destination, string relativeDirectory, Func<string, string?> place)
    {
        foreach (var entry in new DirectoryInfo(Path.Combine(source, relativeDirectory)).EnumerateFileSystemInfos())
        {
            var relative = Path.Combine(relativeDirectory, entry.Name);

            if (place(relative) is not { } placed)
            {
                continue;
            }

            if (entry is DirectoryInfo)
            {
                Directory.CreateDirectory(Path.Combine(destination, placed));
                CopyEntries(source, destination, relative, place);
            }
            else
            {
                File.Copy(entry.FullName, Path.Combine(destination, placed), overwrite: true);
            }
        }
    }

    /// <summary>Deletes a directory tree, leaving it in place when the file system refuses.</summary>
    public static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A content hash of every file beneath a directory, keyed by relative path.</summary>
public sealed record TreeSnapshot(IReadOnlyDictionary<string, string> Files)
{
    /// <summary>Hashes every file beneath a directory, skipping folders with the given names.</summary>
    public static TreeSnapshot Take(string directory, params string[] excludedFolders) => new(Directory.Exists(directory) ? FileTree.EnumerateFiles(directory, excludedFolders).ToDictionary(path => Path.GetRelativePath(directory, path), Hash, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The relative paths added, removed, or changed since an earlier snapshot.</summary>
    public IReadOnlyList<string> DifferencesFrom(TreeSnapshot earlier) => [.. earlier.Files.Keys.Union(Files.Keys, StringComparer.OrdinalIgnoreCase).Where(path => !string.Equals(earlier.Files.GetValueOrDefault(path), Files.GetValueOrDefault(path), StringComparison.Ordinal)).Order(StringComparer.OrdinalIgnoreCase).Select(path => $"{(earlier.Files.ContainsKey(path) ? (Files.ContainsKey(path) ? "changed" : "removed") : "added")} {path}")];

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
