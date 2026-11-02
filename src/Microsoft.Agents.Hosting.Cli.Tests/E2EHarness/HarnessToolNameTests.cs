using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.E2E;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed partial class HarnessToolNameTests
{
    private static readonly string[] HarnessVariables = ["TENANT_ID", "MCS_DEV_ENV", "AZURE_BOT_PREFIX"];

    [Fact]
    public void TheHarnessLooksForTheToolCommandNameTheCliShips()
    {
        if (ToolCommandName() is not { } commandName)
        {
            return;
        }

        var expected = OperatingSystem.IsWindows() ? commandName + ".exe" : commandName;

        Assert.Equal(expected, MsagentCliBuilder.ExecutableName);
    }

    [Fact]
    public void TheCurrentNameIsPreferredOverTheLegacyOne()
    {
        Assert.Equal([MsagentCliBuilder.ExecutableName, MsagentCliBuilder.LegacyExecutableName], MsagentCliBuilder.ExecutableNames);
    }

    [Fact]
    public void AnInstalledBinaryIsFoundUnderTheCurrentName()
    {
        using var directory = new TemporaryDirectory();
        var expected = directory.Place(MsagentCliBuilder.ExecutableName);

        Assert.Equal(expected, MsagentCliBuilder.FindInstalledBinary(directory.Path));
    }

    [Fact]
    public void ARunInstalledBeforeTheRenameIsStillFound()
    {
        using var directory = new TemporaryDirectory();
        var expected = directory.Place(MsagentCliBuilder.LegacyExecutableName);

        Assert.Equal(expected, MsagentCliBuilder.FindInstalledBinary(directory.Path));
    }

    [Fact]
    public void TheCurrentNameWinsWhenBothArePresent()
    {
        using var directory = new TemporaryDirectory();
        var expected = directory.Place(MsagentCliBuilder.ExecutableName);
        directory.Place(MsagentCliBuilder.LegacyExecutableName);

        Assert.Equal(expected, MsagentCliBuilder.FindInstalledBinary(directory.Path));
    }

    [Fact]
    public void NoInstalledBinaryIsReportedWhenTheToolIsAbsent()
    {
        using var directory = new TemporaryDirectory();

        Assert.Null(MsagentCliBuilder.FindInstalledBinary(directory.Path));
    }

    [Fact]
    public void ARuntimeSpecificInstallIsFoundAtTheEntryPointItsToolSettingsName()
    {
        using var directory = new TemporaryDirectory();
        var tools = Path.Combine(".store", "microsoft.agents.hosting.cli", "1.0.0", "microsoft.agents.hosting.cli.win-x64", "1.0.0", "tools", "net10.0", "win-x64");
        directory.Place("msagent.cmd");
        directory.Place(Path.Combine(tools, "DotnetToolSettings.xml"), ToolSettings("Microsoft.Agents.Hosting.Cli.exe", "executable"));
        var expected = directory.Place(Path.Combine(tools, "Microsoft.Agents.Hosting.Cli.exe"));

        Assert.Equal(expected, MsagentCliBuilder.FindInstalledBinary(directory.Path));
    }

    [Fact]
    public void AnEntryPointRunThroughDotnetIsNotTakenForTheExecutable()
    {
        using var directory = new TemporaryDirectory();
        var tools = Path.Combine(".store", "microsoft.agents.hosting.cli", "1.0.0", "microsoft.agents.hosting.cli", "1.0.0", "tools", "net10.0", "any");
        directory.Place(Path.Combine(tools, "DotnetToolSettings.xml"), ToolSettings("Microsoft.Agents.Hosting.Cli.dll", "dotnet"));
        directory.Place(Path.Combine(tools, "Microsoft.Agents.Hosting.Cli.dll"));

        Assert.Null(MsagentCliBuilder.FindInstalledBinary(directory.Path));
    }

    [Fact]
    public void LookingForAToolCreatesAndRemovesNothing()
    {
        using var directory = new TemporaryDirectory();

        MsagentCliBuilder.FindInstalledBinary(directory.Path);

        Assert.True(Directory.Exists(directory.Path));
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public void TheSourceRootIsTheFolderHoldingTheCliProject()
    {
        using var directory = new TemporaryDirectory();
        directory.Place(Path.Combine("Microsoft.Agents.Hosting.Cli", "Microsoft.Agents.Hosting.Cli.csproj"));

        Assert.Equal(directory.Path, HarnessEnvironment.SourceRoot(Directory.CreateDirectory(Path.Combine(directory.Path, "Microsoft.Agents.Hosting.Cli.E2E")).FullName));
    }

    [Fact]
    public void AMissingCliProjectStopsTheHarnessAndNamesTheProject()
    {
        using var directory = new TemporaryDirectory();

        Assert.Contains("Microsoft.Agents.Hosting.Cli.csproj", Assert.Throws<E2EException>(() => HarnessEnvironment.SourceRoot(directory.Path)).Message);
    }

    [Fact]
    public void TheHarnessBuildOutputLeadsToTheHarnessProjectAndTheSourceRootInThisRepository()
    {
        var sourceRoot = HarnessEnvironment.SourceRoot(AppContext.BaseDirectory);
        var harnessDirectory = Path.Combine(sourceRoot, "Microsoft.Agents.Hosting.Cli.E2E");
        var projectDirectory = HarnessEnvironment.FindProjectDirectory(Path.Combine(harnessDirectory, "bin", "Debug", "net10.0"));

        Assert.Equal(harnessDirectory, projectDirectory);
        Assert.Equal(sourceRoot, HarnessEnvironment.SourceRoot(projectDirectory!));
    }

    [Fact]
    public void TheHarnessProjectIsTheNearestFolderAboveTheBuildOutputHoldingItsProjectFile()
    {
        using var directory = new TemporaryDirectory();
        var harnessDirectory = Path.GetDirectoryName(directory.Place(Path.Combine("src", "Microsoft.Agents.Hosting.Cli.E2E", "Microsoft.Agents.Hosting.Cli.E2E.Tests.csproj")))!;
        directory.Place(Path.Combine("src", "Microsoft.Agents.Hosting.Cli", "Microsoft.Agents.Hosting.Cli.csproj"));

        var projectDirectory = HarnessEnvironment.FindProjectDirectory(Directory.CreateDirectory(Path.Combine(harnessDirectory, "bin", "Release", "net10.0")).FullName);

        Assert.Equal(harnessDirectory, projectDirectory);
        Assert.Equal(Path.Combine(directory.Path, "src"), HarnessEnvironment.SourceRoot(projectDirectory!));
    }

    [Fact]
    public void NoHarnessProjectIsReportedWhenOnlyTheRetiredProjectFileNameIsPresent()
    {
        using var directory = new TemporaryDirectory();

        Assert.Null(HarnessEnvironment.FindProjectDirectory(Path.Combine(Path.GetDirectoryName(directory.Place(Path.Combine("Microsoft.Agents.Hosting.Cli.E2E", "Microsoft.Agents.Hosting.Cli.E2E.csproj")))!, "bin", "Debug", "net10.0")));
    }

    [Theory]
    [InlineData("--msagent-path")]
    [InlineData("--ah-path")]
    public void BothTheCurrentAndLegacyBinaryOptionsAreAccepted(string option)
    {
        Assert.Equal("C:\\tools\\msagent.exe", CommandLine.Parse(["run", option, "C:\\tools\\msagent.exe"]).MsagentPath);
    }

    [Fact]
    public void TheCurrentEnvironmentPrefixIsHonoured()
    {
        using var environment = new HarnessVariableScope();
        HarnessVariableScope.Set(E2EConfig.EnvironmentPrefix + "TENANT_ID", "from-current");

        Assert.Equal("from-current", Load().TenantId);
    }

    [Fact]
    public void TheLegacyEnvironmentPrefixIsStillHonoured()
    {
        using var environment = new HarnessVariableScope();
        HarnessVariableScope.Set(E2EConfig.LegacyEnvironmentPrefix + "TENANT_ID", "from-legacy");

        Assert.Equal("from-legacy", Load().TenantId);
    }

    [Fact]
    public void TheCurrentEnvironmentPrefixWinsOverTheLegacyOne()
    {
        using var environment = new HarnessVariableScope();
        HarnessVariableScope.Set(E2EConfig.EnvironmentPrefix + "TENANT_ID", "wins");
        HarnessVariableScope.Set(E2EConfig.LegacyEnvironmentPrefix + "TENANT_ID", "loses");

        Assert.Equal("wins", Load().TenantId);
    }

    [Fact]
    public void ANeighbouringCurrentValueDoesNotDecideTheLegacyFallback()
    {
        using var environment = new HarnessVariableScope();
        HarnessVariableScope.Set(E2EConfig.EnvironmentPrefix + "MCS_DEV_ENV", "unrelated");
        HarnessVariableScope.Set(E2EConfig.LegacyEnvironmentPrefix + "TENANT_ID", "from-legacy");

        Assert.Equal("from-legacy", Load().TenantId);
    }

    [Fact]
    public void ReadingConfigurationLeavesNoDirectoriesBehind()
    {
        var before = Directory.GetDirectories(Path.GetTempPath(), "msagent-e2e-cfg-*").Length;

        using (var environment = new HarnessVariableScope())
        {
            HarnessVariableScope.Set(E2EConfig.EnvironmentPrefix + "TENANT_ID", "leak-check");
            Load();
        }

        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "msagent-e2e-cfg-*").Length);
    }

    private static E2EConfig Load()
    {
        using var directory = new TemporaryDirectory("msagent-e2e-cfg-");

        return E2EConfig.Load(directory.Path);
    }

    private static string ToolSettings(string entryPoint, string runner) => $"""
        <DotNetCliTool Version="2">
          <Commands>
            <Command Name="msagent" EntryPoint="{entryPoint}" Runner="{runner}" />
          </Commands>
        </DotNetCliTool>
        """;

    private static string? ToolCommandName()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "Microsoft.Agents.Hosting.Cli",
                "Microsoft.Agents.Hosting.Cli.csproj");

            if (File.Exists(candidate))
            {
                return ToolCommandNameElement().Match(File.ReadAllText(candidate)) is { Success: true } match
                    ? match.Groups[1].Value
                    : null;
            }
        }

        return null;
    }

    [GeneratedRegex(@"<ToolCommandName>([^<]+)</ToolCommandName>")]
    private static partial Regex ToolCommandNameElement();

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string prefix = "msagent-tool-")
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Place(string fileName, string content = "")
        {
            var file = System.IO.Path.Combine(Path, fileName);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);

            return file;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class HarnessVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _prior = [];

        public HarnessVariableScope()
        {
            foreach (var prefix in new[] { E2EConfig.EnvironmentPrefix, E2EConfig.LegacyEnvironmentPrefix })
            {
                foreach (var suffix in HarnessVariables)
                {
                    var name = prefix + suffix;
                    _prior[name] = Environment.GetEnvironmentVariable(name);
                    Environment.SetEnvironmentVariable(name, null);
                }
            }
        }

        public static void Set(string name, string value) => Environment.SetEnvironmentVariable(name, value);

        public void Dispose()
        {
            foreach (var entry in _prior)
            {
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }
    }
}
