using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class FixtureCopiesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "msagent-fixture-copies-" + Guid.NewGuid().ToString("N"));

    public FixtureCopiesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void CopyWorkflow_WritesTheWorkflowStraightIntoItsOwnFolderUnderItsNewIdentity()
    {
        var source = Fixture();
        var destination = Path.Combine(_root, "copy");
        var workflowId = Guid.NewGuid();
        var folderName = WorkflowWorkspace.FolderNameFor("Payroll: approvals", workflowId);

        FixtureCopies.CopyWorkflow(source, destination, "Payroll: approvals", workflowId, keepLocalState: false);

        var folder = Assert.Single(Directory.GetDirectories(Path.Combine(destination, WorkflowWorkspace.WorkflowsFolderName)));
        Assert.Equal(folderName, Path.GetFileName(folder));
        Assert.Equal(
            [$"workflowId: {workflowId}", "name: 'Payroll: approvals'", "stateCode: 1", $"jsonFileName: {WorkflowWorkspace.WorkflowsFolderName}/{folderName}/{WorkflowWorkspace.DefinitionFileName}"],
            File.ReadAllLines(Path.Combine(folder, WorkflowWorkspace.MetadataFileName)));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(folder, WorkflowWorkspace.DefinitionFileName)));
        Assert.True(File.Exists(Path.Combine(destination, "infrastructure", "connections", "cr_teams.sync.yaml")));
        Assert.False(Directory.Exists(Path.Combine(destination, ".config")));
        Assert.False(Directory.Exists(Path.Combine(destination, ".mcs")));
        Assert.True(Directory.Exists(Path.Combine(source, WorkflowWorkspace.WorkflowsFolderName, "Fixture-0001")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopyTree_CopiesEveryFolderAndKeepsLocalStateOnlyWhenAsked(bool keepLocalState)
    {
        var destination = Path.Combine(_root, "tree");

        FixtureCopies.CopyTree(Fixture(), destination, keepLocalState);

        Assert.True(Directory.Exists(Path.Combine(destination, "knowledge", "files")));
        Assert.True(File.Exists(Path.Combine(destination, WorkflowWorkspace.WorkflowsFolderName, "Fixture-0001", WorkflowWorkspace.DefinitionFileName)));
        Assert.Equal(keepLocalState, File.Exists(Path.Combine(destination, ".config", "agent.config.json")));
        Assert.Equal(keepLocalState, File.Exists(Path.Combine(destination, ".mcs", "cache.json")));
    }

    [Fact]
    public void CopyTree_RefusesAMissingFixture() =>
        Assert.Throws<E2EException>(() => FixtureCopies.CopyTree(Path.Combine(_root, "missing"), Path.Combine(_root, "copy"), keepLocalState: true));

    private string Fixture()
    {
        var source = Path.Combine(_root, "fixture");
        var folder = Path.Combine(source, WorkflowWorkspace.WorkflowsFolderName, "Fixture-0001");

        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(source, "knowledge", "files"));
        Directory.CreateDirectory(Path.Combine(source, "infrastructure", "connections"));
        Directory.CreateDirectory(Path.Combine(source, ".config"));
        Directory.CreateDirectory(Path.Combine(source, ".mcs"));
        File.WriteAllLines(Path.Combine(folder, WorkflowWorkspace.MetadataFileName), ["workflowId: 00000000-0000-0000-0000-000000000001", "name: Fixture", "stateCode: 1", $"jsonFileName: {WorkflowWorkspace.WorkflowsFolderName}/Fixture-0001/{WorkflowWorkspace.DefinitionFileName}"]);
        File.WriteAllText(Path.Combine(folder, WorkflowWorkspace.DefinitionFileName), "{}");
        File.WriteAllText(Path.Combine(source, "infrastructure", "connections", "cr_teams.sync.yaml"), "connectionReferences: []");
        File.WriteAllText(Path.Combine(source, ".config", "agent.config.json"), "{}");
        File.WriteAllText(Path.Combine(source, ".mcs", "cache.json"), "{}");

        return source;
    }
}
