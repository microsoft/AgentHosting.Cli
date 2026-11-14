namespace Microsoft.Agents.Hosting.Cli.Core;

/// <summary>Reads and replaces whole files so that a reader sees either the old or the new content, never part of either.</summary>
public static class AtomicFile
{
    /// <summary>Reads a whole file while sharing it with processes that write or delete it meanwhile.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The file's text.</returns>
    public static string ReadAllText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    /// <summary>Writes a file beside the target and moves it over the target, retrying briefly while another process holds the target open.</summary>
    /// <param name="path">The file to write, in a directory that exists.</param>
    /// <param name="contents">The text the file holds afterwards.</param>
    public static void WriteAllText(string path, string contents)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllText(temporaryPath, contents);
            FileSystemRetry.WhileHeld(() => File.Move(temporaryPath, path, overwrite: true));
        }
        finally
        {
            DeleteQuietly(temporaryPath);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }
    }
}
