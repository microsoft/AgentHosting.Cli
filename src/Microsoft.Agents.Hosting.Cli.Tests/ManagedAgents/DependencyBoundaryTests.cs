using System.Reflection;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class DependencyBoundaryTests
{
    private const string ForbiddenPackage = "Yaml" + "DotNet";

    private const string PublishAction = "Pva" + "Publish";

    private static DirectoryInfo SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Microsoft.Agents.Hosting.Cli.Core")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException("The source root was not found from the test output directory.");
    }

    internal static IEnumerable<string> SourceFiles(string extension) =>
        Directory.EnumerateFiles(SourceRoot().FullName, extension, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [Fact]
    public void NoSourceFileImportsYamlDotNet()
    {
        var offenders = SourceFiles("*.cs")
            .Where(path => File.ReadAllText(path).Contains($"using {ForbiddenPackage}", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoSourceFileCallsThePublishActionOutsideTheSyncLibrary()
    {
        var offenders = SourceFiles("*.cs")
            .Where(path => File.ReadAllText(path).Contains(PublishAction, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFileName(path))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoProjectReferencesYamlDotNet()
    {
        var offenders = SourceFiles("*.csproj")
            .Concat(SourceFiles("Directory.Packages.props"))
            .Where(path => File.ReadAllText(path).Contains(ForbiddenPackage, StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheCoreProjectTakesNoPackageDependency()
    {
        var core = Path.Combine(SourceRoot().FullName, "Microsoft.Agents.Hosting.Cli.Core", "Microsoft.Agents.Hosting.Cli.Core.csproj");

        Assert.DoesNotContain("<PackageReference", File.ReadAllText(core), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCoreAssemblyNeverReferencesTheSyncLibrary()
    {
        var referenced = typeof(Core.Ports.IWorkflowStorePort).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name).ToArray();

        Assert.DoesNotContain("Microsoft.CopilotStudio.Sync", referenced);
        Assert.DoesNotContain(ForbiddenPackage, referenced);
    }
}
