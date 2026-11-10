namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public static class LocalDirectories
{
    private static readonly EnumerationOptions EveryEntry = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.None };

    public static IEnumerable<string> Entries(string directory) => Directory.EnumerateFileSystemEntries(directory, "*", EveryEntry);

    public static void Delete(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", EveryEntry))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
    }
}
