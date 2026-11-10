namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

internal static class AmsFileTree
{
    internal static void Copy(string source, string target)
    {
        if (File.Exists(source))
        {
            File.Copy(source, target, overwrite: true);
            return;
        }

        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            Copy(entry, Path.Combine(target, Path.GetFileName(entry)));
        }
    }

    internal static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
