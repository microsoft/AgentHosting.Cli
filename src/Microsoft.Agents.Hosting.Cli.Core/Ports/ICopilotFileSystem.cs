namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>
/// The filesystem facts the Copilot Studio commands need. Kept behind a port so directory checks and
/// path resolution are deterministic under test.
/// </summary>
public interface ICopilotFileSystem
{
    /// <summary>The directory the command was invoked from.</summary>
    string CurrentDirectory { get; }

    /// <summary>Resolves <paramref name="path"/> to an absolute path.</summary>
    /// <param name="path">A relative or absolute path.</param>
    /// <returns>The absolute path.</returns>
    string ResolveFullPath(string path);

    /// <summary>Reports whether a directory is absent or contains no entries.</summary>
    /// <param name="path">The directory to inspect.</param>
    /// <returns><see langword="true"/> when the directory does not exist or is empty.</returns>
    bool IsEmptyDirectory(string path);

    /// <summary>Reports whether a file exists.</summary>
    /// <param name="path">The file to test.</param>
    /// <returns><see langword="true"/> when the file exists.</returns>
    bool FileExists(string path);

    /// <summary>Reads a file's text content.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The file's content.</returns>
    string ReadAllText(string path);

    /// <summary>Writes text to a file, creating the containing directory when needed.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="content">The content to write.</param>
    void WriteAllText(string path, string content);

    /// <summary>Returns the directory portion of a path.</summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns>The containing directory.</returns>
    string GetDirectoryName(string path);

    /// <summary>Combines two path segments.</summary>
    /// <param name="left">The leading segment.</param>
    /// <param name="right">The trailing segment.</param>
    /// <returns>The combined path.</returns>
    string Combine(string left, string right);
}
