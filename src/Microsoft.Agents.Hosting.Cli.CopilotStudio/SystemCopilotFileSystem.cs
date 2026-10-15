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
    public string ReadAllText(string path) => File.ReadAllText(path);

    /// <inheritdoc />
    public void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Replace(path, content);
    }

    /// <summary>
    /// Writes beside the destination and moves the finished file into place.
    ///
    /// A process that stops midway through a direct write leaves a truncated file behind, which the
    /// next reader has to treat as corrupt. Moving a complete file over the old one means a reader
    /// sees either the previous content or the new content, never half of either.
    /// </summary>
    /// <param name="path">The file to replace.</param>
    /// <param name="content">What it should contain.</param>
    private static void Replace(string path, string content)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";

        File.WriteAllText(temporary, content);

        try
        {
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception)
        {
            Discard(temporary);

            throw;
        }
    }

    /// <summary>Removes a temporary file, leaving the original failure to be reported.</summary>
    /// <param name="path">The temporary file to remove.</param>
    private static void Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <inheritdoc />
    public string GetDirectoryName(string path) => Path.GetDirectoryName(path) ?? string.Empty;

    /// <inheritdoc />
    public string Combine(string left, string right) => Path.Combine(left, right);
}
