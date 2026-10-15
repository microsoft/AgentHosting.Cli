using System.Xml.Linq;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>Proves the CLI and runtime clusters stay separable, so each solution builds alone.</summary>
public sealed class SolutionBoundaryTests
{
    private static readonly string[] RuntimeProjects =
    [
        "AuthSidecar",
        "HostingGateway",
        "MockPlex",
        "SampleAgent",
        "SandboxShell",
        "Tests",
    ];

    private static bool IsCliProject(string projectName) => projectName.StartsWith("Microsoft.Agents.Hosting.Cli", StringComparison.Ordinal);

    [Fact]
    public void NoProjectReferenceCrossesTheBoundary()
    {
        if (SourceRoot() is not { } sourceRoot)
        {
            return;
        }

        var crossings = ProjectFiles(sourceRoot)
            .SelectMany(projectFile => ReferencedProjectNames(projectFile)
                .Where(referenced => IsCliProject(ProjectName(projectFile)) != IsCliProject(referenced))
                .Select(referenced => $"{ProjectName(projectFile)} -> {referenced}"))
            .ToArray();

        Assert.Empty(crossings);
    }

    [Fact]
    public void TheCliSolutionListsOnlyCliProjects()
    {
        if (SourceRoot() is not { } sourceRoot)
        {
            return;
        }

        Assert.All(SolutionMembers(sourceRoot, "Microsoft.Agents.Hosting.Cli.slnx"), member => Assert.True(IsCliProject(member), $"{member} is not a CLI project."));
    }

    [Fact]
    public void TheRuntimeSolutionListsOnlyRuntimeProjects()
    {
        if (SourceRoot() is not { } sourceRoot)
        {
            return;
        }

        Assert.All(SolutionMembers(sourceRoot, "AgentHosting.Runtime.slnx"), member => Assert.False(IsCliProject(member), $"{member} is not a runtime project."));
    }

    [Fact]
    public void EveryProjectBelongsToExactlyOneSolution()
    {
        if (SourceRoot() is not { } sourceRoot)
        {
            return;
        }

        var cli = SolutionMembers(sourceRoot, "Microsoft.Agents.Hosting.Cli.slnx").ToHashSet(StringComparer.Ordinal);
        var runtime = SolutionMembers(sourceRoot, "AgentHosting.Runtime.slnx").ToHashSet(StringComparer.Ordinal);

        var misplaced = ProjectFiles(sourceRoot)
            .Select(ProjectName)
            .Where(name => cli.Contains(name) == runtime.Contains(name))
            .ToArray();

        Assert.Empty(misplaced);
    }

    [Fact]
    public void TheRuntimeClusterIsTheKnownSet()
    {
        if (SourceRoot() is not { } sourceRoot)
        {
            return;
        }

        Assert.Equal(
            RuntimeProjects.Order(StringComparer.Ordinal),
            ProjectFiles(sourceRoot).Select(ProjectName).Where(name => !IsCliProject(name)).Order(StringComparer.Ordinal));
    }

    private static string ProjectName(string projectFilePath) => Path.GetFileNameWithoutExtension(projectFilePath);

    private static IEnumerable<string> ProjectFiles(string sourceRoot) => Directory
        .EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<string> ReferencedProjectNames(string projectFilePath) => XDocument
        .Load(projectFilePath)
        .Descendants("ProjectReference")
        .Select(reference => reference.Attribute("Include")?.Value)
        .Where(include => !string.IsNullOrWhiteSpace(include))
        .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', Path.DirectorySeparatorChar)));

    private static IEnumerable<string> SolutionMembers(string sourceRoot, string solutionFileName) => XDocument
        .Load(Path.Combine(sourceRoot, solutionFileName))
        .Descendants("Project")
        .Select(project => project.Attribute("Path")?.Value)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => Path.GetFileNameWithoutExtension(path!.Replace('\\', '/')));

    private static string? SourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src");

            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "AgentHosting.slnx")))
            {
                return candidate;
            }
        }

        return null;
    }
}
