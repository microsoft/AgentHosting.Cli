namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Stages the folders a clone writes into and removes what a failed clone wrote, so each folder is left as the clone found it.</summary>
public static class CloneStaging
{
    /// <summary>Stages a folder a clone is about to write into, naming the outermost folder the clone creates for it, or the folder itself when it already exists.</summary>
    /// <param name="directory">The folder the clone writes into.</param>
    /// <returns>The folder a failed clone discards, and whether it existed before the clone.</returns>
    public static (string Path, bool Existed) Stage(string directory) => Directory.Exists(directory) ? (directory, true) : (OutermostMissing(directory), false);

    /// <summary>Whether a folder entry is the CLI's own configuration folder, which a clone never writes and a discard keeps.</summary>
    /// <param name="entry">The file or folder path.</param>
    /// <returns><see langword="true"/> for the CLI's configuration folder.</returns>
    public static bool IsCliOwned(string entry) => string.Equals(Path.GetFileName(entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), AgentConfigLocator.ConfigDirectoryName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes what a failed clone wrote, deleting the folders it created and emptying the folders it found apart from the CLI's own configuration.</summary>
    /// <param name="staged">The folders the clone wrote into, and whether each existed before it.</param>
    public static void Discard(IEnumerable<(string Path, bool Existed)> staged)
    {
        ArgumentNullException.ThrowIfNull(staged);

        foreach (var (path, existed) in staged)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    continue;
                }

                if (existed)
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(path).Where(candidate => !IsCliOwned(candidate)))
                    {
                        if (Directory.Exists(entry))
                        {
                            Directory.Delete(entry, recursive: true);
                        }
                        else
                        {
                            File.Delete(entry);
                        }
                    }
                }
                else
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string OutermostMissing(string directory) => Path.GetDirectoryName(directory) is { } parent && !Directory.Exists(parent) ? OutermostMissing(parent) : directory;
}
