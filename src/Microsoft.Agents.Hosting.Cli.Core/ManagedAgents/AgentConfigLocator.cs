using System.Text.RegularExpressions;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Resolves where <c>agent.config.json</c> lives for a command.
/// </summary>
public sealed partial class AgentConfigLocator
{
    /// <summary>The directory holding the configuration file, relative to a project.</summary>
    public const string ConfigDirectoryName = ".config";

    /// <summary>The configuration file's name.</summary>
    public const string ConfigFileName = "agent.config.json";

    /// <summary>The project file whose presence marks a directory as an MCS project.</summary>
    public const string ProjectSettingsFileName = "settings.mcs.yml";

    /// <summary>The legacy project file some classic workspaces carry instead.</summary>
    public const string ClassicProjectFileName = "agent.mcs.yml";

    /// <summary>The root file marking a directory as a component-collection workspace.</summary>
    public const string CollectionProjectFileName = "collection.mcs.yml";

    /// <summary>The file in which a workspace declares the collections it references.</summary>
    public const string ReferencesFileName = "references.mcs.yml";

    /// <summary>The location of a workspace that is the project root itself.</summary>
    public const string CurrentDirectoryLocation = ".";

    /// <summary>How many parent directories a command searches for the project it is standing inside.</summary>
    public const int MaximumProjectSearchDepth = 5;

    /// <summary>Resolves the project directory a command acts on.</summary>
    /// <param name="projectDirectory">An explicit directory, or <see langword="null"/> to discover the enclosing project.</param>
    /// <returns>The absolute project directory.</returns>
    public string ResolveProjectDirectory(string? projectDirectory) => string.IsNullOrWhiteSpace(projectDirectory) ? EnclosingProjectDirectory(Directory.GetCurrentDirectory()) : Path.GetFullPath(projectDirectory);

    /// <summary>Finds the nearest ancestor holding a configuration, or the starting directory when none does.</summary>
    /// <param name="startingDirectory">The directory to search from.</param>
    /// <returns>The project directory.</returns>
    // Deliberately an instance member: registered as a singleton and injected, so project discovery stays
    // a replaceable seam for tests that relocate the project root.
#pragma warning disable CA1822
    public string EnclosingProjectDirectory(string startingDirectory)
#pragma warning restore CA1822
    {
        var candidate = new DirectoryInfo(Path.GetFullPath(startingDirectory));

        for (var depth = 0; depth <= MaximumProjectSearchDepth && candidate is not null; depth++)
        {
            if (File.Exists(Path.Combine(candidate.FullName, ConfigDirectoryName, ConfigFileName)))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        return Path.GetFullPath(startingDirectory);
    }

    /// <summary>The configuration file path for a project directory.</summary>
    /// <param name="projectDirectory">The project directory.</param>
    /// <returns>The absolute configuration file path.</returns>
    public string ConfigFilePath(string projectDirectory) => Path.Combine(ResolveProjectDirectory(projectDirectory), ConfigDirectoryName, ConfigFileName);

    /// <summary>Reports whether a project directory already holds a configuration.</summary>
    /// <param name="projectDirectory">The project directory.</param>
    /// <returns><see langword="true"/> when a configuration file exists.</returns>
    public bool ConfigExists(string projectDirectory) => File.Exists(ConfigFilePath(projectDirectory));

    /// <summary>Resolves the workspace an agent's recorded location names.</summary>
    /// <param name="projectDirectory">The project directory.</param>
    /// <param name="agentLocation">The agent's location, relative to the project root.</param>
    /// <returns>The workspace directory, or <see langword="null"/> when it holds no project.</returns>
    public string? AgentWorkspaceDirectory(string? projectDirectory, string agentLocation)
    {
        var root = ResolveProjectDirectory(projectDirectory);
        var located = ContainedLocation(root, agentLocation);

        return located is not null && HoldsProjectFile(located) ? located : null;
    }

    /// <summary>Resolves a location beneath a project root, or <see langword="null"/> when it escapes it.</summary>
    /// <param name="projectDirectory">The project root the location is relative to.</param>
    /// <param name="location">The relative location to resolve.</param>
    /// <returns>The contained absolute path, or <see langword="null"/>.</returns>
    public string? ContainedLocation(string projectDirectory, string location) => ContainedLocationUnder(PhysicalPath(ResolveProjectDirectory(projectDirectory)), location);

    /// <summary>Resolves a location beneath an already-resolved project root.</summary>
    /// <param name="physicalProjectRoot">The project root, already resolved through any link.</param>
    /// <param name="location">The relative location to resolve.</param>
    /// <returns>The contained absolute path, or <see langword="null"/>.</returns>
    public static string? ContainedLocationUnder(string physicalProjectRoot, string location)
    {
        if (string.IsNullOrWhiteSpace(location) ||
            Path.IsPathRooted(location) ||
            location.StartsWith('\\') ||
            (location.Length >= 2 && char.IsAsciiLetter(location[0]) && location[1] == ':'))
        {
            return null;
        }

        var located = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(physicalProjectRoot, location)));

        return IsContainedIn(PhysicalPath(located), physicalProjectRoot) ? located : null;
    }

    private static bool IsContainedIn(string candidate, string root) =>
        string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves a path through any symbolic link or junction on the way to it.</summary>
    /// <param name="path">The path to resolve.</param>
    /// <returns>The physical path, with any link on the way to it resolved.</returns>
    public static string PhysicalPath(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var remainder = new Stack<string>();
        var candidate = normalized;

        while (!string.IsNullOrEmpty(candidate))
        {
            if (ResolvedLinkTarget(candidate) is { } resolved)
            {
                return remainder.Count == 0 ? resolved : Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine([resolved, .. remainder])));
            }

            if (Path.GetDirectoryName(candidate) is not { Length: > 0 } parent)
            {
                break;
            }

            remainder.Push(Path.GetFileName(candidate));
            candidate = parent;
        }

        return normalized;
    }

    private static string? ResolvedLinkTarget(string path)
    {
        try
        {
            return Directory.ResolveLinkTarget(path, returnFinalTarget: true) is { } resolved ? PhysicalPath(resolved.FullName) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The locations of every agent workspace under a project root, relative to it.</summary>
    /// <param name="projectDirectory">The project directory.</param>
    /// <returns>The workspace locations, in name order, with <c>.</c> for the root itself.</returns>
    public IReadOnlyList<string> WorkspaceLocations(string? projectDirectory)
    {
        var resolved = ResolveProjectDirectory(projectDirectory);

        if (HoldsProjectFile(resolved))
        {
            return [CurrentDirectoryLocation];
        }

        return Directory.Exists(resolved)
            ? [.. Directory.EnumerateDirectories(resolved)
                .Where(candidate => !string.Equals(Path.GetFileName(candidate), ConfigDirectoryName, StringComparison.OrdinalIgnoreCase))
                .Where(HoldsProjectFile)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)!]
            : [];
    }

    private static bool HoldsProjectFile(string directory) =>
        File.Exists(Path.Combine(directory, ProjectSettingsFileName)) || File.Exists(Path.Combine(directory, ClassicProjectFileName));

    /// <summary>The component-collection workspaces an agent's workspace declares, in declaration order.</summary>
    /// <param name="agentWorkspaceDirectory">The directory holding the agent's workspace.</param>
    /// <param name="projectDirectory">The project root the collections must stay beneath.</param>
    /// <returns>The collection workspace directories that exist, and the declarations that resolve to nothing.</returns>
    public (IReadOnlyList<string> Found, IReadOnlyList<string> Missing) ComponentCollectionDirectories(string? agentWorkspaceDirectory, string? projectDirectory)
    {
        var resolved = ResolveProjectDirectory(agentWorkspaceDirectory);
        var referencesPath = Path.Combine(resolved, ReferencesFileName);

        if (!File.Exists(referencesPath))
        {
            return ([], []);
        }

        var root = PhysicalPath(ResolveProjectDirectory(projectDirectory));
        var declared = ReferencedDirectories(File.ReadAllText(referencesPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var contained = declared.Select(directory => (Declared: directory, Path: ContainedCollection(resolved, directory, root))).ToArray();

        return (
            [.. contained.Where(candidate => candidate.Path is not null).Select(candidate => candidate.Path!).Distinct(StringComparer.OrdinalIgnoreCase)],
            [.. contained.Where(candidate => candidate.Path is null).Select(candidate => candidate.Declared)]);
    }

    private static string? ContainedCollection(string workspaceDirectory, string declaredDirectory, string projectRoot)
    {
        var located = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(workspaceDirectory, declaredDirectory)));

        return IsContainedIn(PhysicalPath(located), projectRoot) && File.Exists(Path.Combine(located, CollectionProjectFileName)) ? located : null;
    }

    private static IEnumerable<string> ReferencedDirectories(string references) =>
        ReferencedDirectoryPattern().Matches(references).Select(match => match.Groups["directory"].Value.Trim());

    [GeneratedRegex(@"^\s*directory:\s*(?<directory>\S.*?)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ReferencedDirectoryPattern();
}

