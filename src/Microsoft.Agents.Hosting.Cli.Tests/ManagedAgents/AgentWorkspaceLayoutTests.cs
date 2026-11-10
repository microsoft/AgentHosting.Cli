using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class AgentWorkspaceLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ah-layout-" + Guid.NewGuid().ToString("N"));

    private readonly AgentConfigLocator _locator = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Nested(string name, string fileName)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), "schemaName: con_agent\n");

        return directory;
    }

    [Fact]
    public void AgentWorkspace_AtTheRootLocation_IsTheProjectItself()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, WorkspaceFileNames.Settings), "schemaName: con_agent\n");

        Assert.Equal(_root, _locator.AgentWorkspaceDirectory(_root, AgentConfigLocator.CurrentDirectoryLocation));
    }

    [Fact]
    public void AgentWorkspace_AtANestedLocation_IsThatFolder()
    {
        Assert.Equal(Nested("Contoso Helpdesk", WorkspaceFileNames.Settings), _locator.AgentWorkspaceDirectory(_root, "Contoso Helpdesk"));
    }

    [Fact]
    public void AgentWorkspace_ForAClassicProject_IsFound()
    {
        Assert.Equal(Nested("Contoso Helpdesk", WorkspaceFileNames.Agent), _locator.AgentWorkspaceDirectory(_root, "Contoso Helpdesk"));
    }

    [Fact]
    public void AgentWorkspace_AtALocationHoldingNoProject_IsNone()
    {
        Nested("Shared Tools", WorkspaceFileNames.Collection);

        Assert.Null(_locator.AgentWorkspaceDirectory(_root, "Shared Tools"));
    }

    [Fact]
    public void AgentWorkspace_AtAnAbsentLocation_IsNone()
    {
        Directory.CreateDirectory(_root);

        Assert.Null(_locator.AgentWorkspaceDirectory(_root, "Absent"));
    }

    [Fact]
    public void WorkspaceLocations_ForAFlatProject_AreTheRoot()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, WorkspaceFileNames.Settings), "schemaName: con_agent\n");

        Assert.Equal([AgentConfigLocator.CurrentDirectoryLocation], _locator.WorkspaceLocations(_root));
    }

    [Fact]
    public void WorkspaceLocations_AreEveryNestedWorkspace()
    {
        Nested("Second", WorkspaceFileNames.Settings);
        Nested("First", WorkspaceFileNames.Settings);
        Nested("Shared Tools", WorkspaceFileNames.Collection);

        Assert.Equal(["First", "Second"], _locator.WorkspaceLocations(_root));
    }

    [Fact]
    public void WorkspaceLocations_IgnoreTheConfigDirectory()
    {
        var config = Path.Combine(_root, AgentConfigLocator.ConfigDirectoryName);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, WorkspaceFileNames.Settings), "schemaName: con_agent\n");
        Nested("Contoso Helpdesk", WorkspaceFileNames.Settings);

        Assert.Equal(["Contoso Helpdesk"], _locator.WorkspaceLocations(_root));
    }

    [Fact]
    public void WorkspaceLocations_InAMissingDirectory_AreNone()
    {
        Assert.Empty(_locator.WorkspaceLocations(Path.Combine(_root, "absent")));
    }

    [Theory]
    [InlineData("Contoso Agent", "Contoso Agent")]
    [InlineData("Sales/Support", "Sales-Support")]
    [InlineData("a:b*c?d", "a-b-c-d")]
    [InlineData("  padded  ", "padded")]
    [InlineData("///", "agent")]
    [InlineData("", "agent")]
    [InlineData("CON", "CON_")]
    [InlineData("trailing.", "trailing")]
    public void WorkspaceFolderName_IsSafeOnDisk(string displayName, string expected)
    {
        Assert.Equal(expected, new WorkflowWorkspaceAdapter().ProjectFolderName(displayName));
    }

    [Fact]
    public void ProjectIdentity_IsReadFromTheNestedAgentWorkspace()
    {
        var agent = Path.Combine(_root, "Contoso Helpdesk");
        Directory.CreateDirectory(agent);
        File.WriteAllText(Path.Combine(agent, WorkspaceFileNames.Settings), "displayName: Contoso Helpdesk\nschemaName: con_helpdesk\n");

        var identity = new CopilotProjectIdentityReader(_locator, new SyncAgentWorkspaceIdentityReader()).Find(agent);

        Assert.Equal("con_helpdesk", identity?.SchemaName);
        Assert.Equal("Contoso Helpdesk", identity?.DisplayName);
    }
}

[Collection("CurrentDirectory")]
public sealed class EnclosingProjectDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ah-enclosing-" + Guid.NewGuid().ToString("N"));
    private readonly AgentConfigLocator _locator = new();

    private string Project()
    {
        var config = Path.Combine(_root, AgentConfigLocator.ConfigDirectoryName);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, AgentConfigLocator.ConfigFileName), "{}");

        return _root;
    }

    private string Nested(params string[] segments)
    {
        var path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(path);

        return path;
    }

    [Fact]
    public void TheProjectDirectoryItselfIsFound()
    {
        Assert.Equal(Project(), _locator.EnclosingProjectDirectory(_root));
    }

    [Fact]
    public void AWorkspaceOneLevelDownFindsItsProject()
    {
        Project();

        Assert.Equal(_root, _locator.EnclosingProjectDirectory(Nested("tmpl 3")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void AnyDepthUpToTheLimitFindsTheProject(int depth)
    {
        Project();
        var segments = Enumerable.Range(1, depth).Select(level => $"level{level}").ToArray();

        Assert.Equal(_root, _locator.EnclosingProjectDirectory(Nested(segments)));
    }

    [Fact]
    public void BeyondTheLimitTheStartingDirectoryIsUsed()
    {
        Project();
        var deep = Nested("a", "b", "c", "d", "e", "f");

        Assert.Equal(deep, _locator.EnclosingProjectDirectory(deep));
    }

    [Fact]
    public void WithNoProjectAboveTheStartingDirectoryIsUsed()
    {
        var alone = Nested("alone");

        Assert.Equal(alone, _locator.EnclosingProjectDirectory(alone));
    }

    [Fact]
    public void TheNearestProjectWins()
    {
        Project();
        var inner = Nested("outer", "inner");
        var innerConfig = Path.Combine(inner, AgentConfigLocator.ConfigDirectoryName);
        Directory.CreateDirectory(innerConfig);
        File.WriteAllText(Path.Combine(innerConfig, AgentConfigLocator.ConfigFileName), "{}");

        Assert.Equal(inner, _locator.EnclosingProjectDirectory(Nested("outer", "inner", "deeper")));
    }

    [Fact]
    public void AnExplicitProjectDirectoryIsNeverSearchedUpwards()
    {
        Project();
        var workspace = Nested("tmpl 3");

        Assert.Equal(workspace, _locator.ResolveProjectDirectory(workspace));
    }

    [Fact]
    public void WithoutAnExplicitDirectoryTheEnclosingProjectIsUsed()
    {
        Project();
        using var scope = new CurrentDirectoryScope(Nested("tmpl 3"));

        Assert.Equal(_root, _locator.ResolveProjectDirectory(null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
