using Microsoft.Agents.Hosting.Cli.Core;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Reads filesystem facts from the real filesystem.
/// </summary>
public sealed class SystemCopilotFileSystem : ICopilotFileSystem
{
    /// <inheritdoc />
    public string CurrentDirectory => Directory.GetCurrentDirectory();

    /// <inheritdoc />
    public string ResolveFullPath(string path) => Path.GetFullPath(path);

    /// <inheritdoc />
    public bool IsEmptyDirectory(string path) => !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any();

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public string ReadAllText(string path) => AtomicFile.ReadAllText(path);

    /// <inheritdoc />
    public void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllText(path, content);
    }

    /// <inheritdoc />
    public string GetDirectoryName(string path) => Path.GetDirectoryName(path) ?? string.Empty;

    /// <inheritdoc />
    public string Combine(string left, string right) => Path.Combine(left, right);
}
