namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Where a project keeps the state of the copies being prepared into it, under <c>.config\copy</c>.</summary>
public static class CopyProjectState
{
    /// <summary>The folder under the project's configuration directory holding copy state.</summary>
    public const string FolderName = "copy";

    /// <summary>The lock file the one copy into a project holds in the project's copy state.</summary>
    public const string LockFileName = "copy.lock";

    /// <summary>The file, in the project's copy state, that keeps the copy state out of source control.</summary>
    public const string IgnoreFileName = ".gitignore";

    /// <summary>The disposable cloud cache inside an agent workspace, which holds only state the environment confirmed.</summary>
    public const string CloudCacheFolderName = ".mcs";

    /// <summary>The project's copy state directory.</summary>
    /// <param name="projectDirectory">The project root.</param>
    /// <returns>The directory.</returns>
    public static string Root(string projectDirectory) => Path.Combine(projectDirectory, AgentConfigLocator.ConfigDirectoryName, FolderName);

    /// <summary>The state directory of one copy being prepared.</summary>
    /// <param name="projectDirectory">The project root.</param>
    /// <param name="agentId">The copy's managed-agent identifier.</param>
    /// <returns>The directory.</returns>
    public static string AgentDirectory(string projectDirectory, string agentId) => Path.Combine(Root(projectDirectory), agentId);

    /// <summary>Keeps the project's copy state out of source control.</summary>
    /// <param name="projectDirectory">The project root.</param>
    public static void EnsureIgnored(string projectDirectory)
    {
        var ignore = Path.Combine(Root(projectDirectory), IgnoreFileName);

        if (File.Exists(ignore))
        {
            return;
        }

        Directory.CreateDirectory(Root(projectDirectory));
        File.WriteAllText(ignore, "*\n");
    }

    /// <summary>Takes the project's copy lock, which keeps every other copy into the project out until it is released.</summary>
    /// <param name="projectDirectory">The project root.</param>
    /// <returns>The lock, or <see langword="null"/> when another copy into the project holds it.</returns>
    public static ExclusiveFileLock? TryLockProject(string projectDirectory)
    {
        EnsureIgnored(projectDirectory);

        return ExclusiveFileLock.TryAcquire(Path.Combine(Directory.CreateDirectory(Root(projectDirectory)).FullName, LockFileName));
    }

    /// <summary>Deletes every copy's state left in the project, which only the holder of the project's copy lock may do because no other copy is then being prepared.</summary>
    /// <param name="projectDirectory">The project root.</param>
    public static void RemoveAbandoned(string projectDirectory)
    {
        var root = new DirectoryInfo(Root(projectDirectory));

        if (!root.Exists)
        {
            return;
        }

        foreach (var abandoned in root.EnumerateDirectories())
        {
            DeleteQuietly(abandoned.FullName);
        }
    }

    internal static void DeleteQuietly(string? directory)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
