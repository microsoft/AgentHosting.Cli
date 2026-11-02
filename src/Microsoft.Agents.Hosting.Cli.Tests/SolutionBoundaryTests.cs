using System.Xml.Linq;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>Proves all source projects and their references belong to the CLI solution.</summary>
public sealed class SolutionBoundaryTests
{
    private const string CliSolutionFileName = "Microsoft.Agents.Hosting.Cli.slnx";

    [Fact]
    public void TheCliSourceRootIsFound()
    {
        Assert.True(File.Exists(Path.Combine(SourceRoot(), CliSolutionFileName)));
    }

    [Fact]
    public void NoProjectReferenceLeavesTheCliSolution()
    {
        var sourceRoot = SourceRoot();
        var members = SolutionMembers(sourceRoot).ToHashSet(StringComparer.Ordinal);
        var crossings = ProjectFiles(sourceRoot)
            .SelectMany(projectFile => ReferencedProjectPaths(projectFile)
                .Where(referenced => !members.Contains(referenced))
                .Select(referenced => $"{Path.GetRelativePath(sourceRoot, projectFile)} -> {referenced}"))
            .ToArray();

        Assert.Empty(crossings);
    }

    [Fact]
    public void TheCliSolutionListsOnlyCliProjects()
    {
        var members = SolutionMembers(SourceRoot()).ToArray();
        Assert.NotEmpty(members);
        Assert.All(members, member => Assert.True(
            Path.GetFileNameWithoutExtension(member).StartsWith("Microsoft.Agents.Hosting.Cli", StringComparison.Ordinal),
            $"{member} is not a CLI project."));
    }

    [Fact]
    public void EverySourceProjectAppearsExactlyOnceInTheCliSolution()
    {
        var sourceRoot = SourceRoot();
        var projects = ProjectFiles(sourceRoot).Order(StringComparer.Ordinal).ToArray();
        var members = SolutionMembers(sourceRoot).Order(StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(projects);
        Assert.Equal(projects, members);
    }

    private static IEnumerable<string> ProjectFiles(string sourceRoot) => Directory
        .EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<string> ReferencedProjectPaths(string projectFilePath) => XDocument
        .Load(projectFilePath)
        .Descendants("ProjectReference")
        .Select(reference => ResolveProjectPath(Path.GetDirectoryName(projectFilePath)!, reference.Attribute("Include")!.Value));

    private static IEnumerable<string> SolutionMembers(string sourceRoot) => XDocument
        .Load(Path.Combine(sourceRoot, CliSolutionFileName))
        .Descendants("Project")
        .Select(project => ResolveProjectPath(sourceRoot, project.Attribute("Path")!.Value));

    private static string ResolveProjectPath(string baseDirectory, string projectPath) => Path.GetFullPath(
        projectPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar),
        baseDirectory);

    private static string SourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src");

            if (File.Exists(Path.Combine(candidate, CliSolutionFileName)))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"Could not find src/{CliSolutionFileName} from {AppContext.BaseDirectory}.");
    }
}
