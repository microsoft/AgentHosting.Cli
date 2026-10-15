using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class CopilotProjectIdentityReaderTests
{
    private static CopilotProjectIdentityReader Reader() => new(new AgentConfigLocator());

    [Fact]
    public void Find_ReadsAQuotedSchemaAndDisplayName()
    {
        using var workspace = new TemporaryWorkspace(schemaName: "con_helpdesk", displayName: "Contoso Helpdesk");

        var identity = Reader().Find(workspace.Root);

        Assert.Equal("con_helpdesk", identity!.SchemaName);
        Assert.Equal("Contoso Helpdesk", identity.DisplayName);
    }

    [Fact]
    public void Find_ReadsAnUnquotedScalar()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        workspace.AddFile(AgentConfigLocator.ProjectSettingsFileName, "displayName: Contoso Helpdesk\nschemaName: con_helpdesk\n");

        var identity = Reader().Find(workspace.Root);

        Assert.Equal("con_helpdesk", identity!.SchemaName);
        Assert.Equal("Contoso Helpdesk", identity.DisplayName);
    }

    [Fact]
    public void Find_ReadsTheClassicProjectFile()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        workspace.AddFile(AgentConfigLocator.ClassicProjectFileName, "displayName: \"Classic Agent\"\nschemaName: \"con_classic\"\n");

        Assert.Equal("con_classic", Reader().Find(workspace.Root)!.SchemaName);
    }

    [Fact]
    public void Find_UnescapesAQuotedDisplayName()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        workspace.AddFile(AgentConfigLocator.ProjectSettingsFileName, "displayName: \"He said \\\"hi\\\"\"\nschemaName: \"con_agent\"\n");

        Assert.Equal("He said \"hi\"", Reader().Find(workspace.Root)!.DisplayName);
    }

    [Fact]
    public void Find_FallsBackToTheSchemaNameWhenThereIsNoDisplayName()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        workspace.AddFile(AgentConfigLocator.ProjectSettingsFileName, "schemaName: \"con_agent\"\n");

        Assert.Equal("con_agent", Reader().Find(workspace.Root)!.DisplayName);
    }

    [Fact]
    public void Find_IgnoresANestedKeyOfTheSameName()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        workspace.AddFile(AgentConfigLocator.ProjectSettingsFileName, "schemaName: \"con_agent\"\nconfiguration:\n  gPTSettings:\n    defaultSchemaName: con_agent.gpt.default\n");

        Assert.Equal("con_agent", Reader().Find(workspace.Root)!.SchemaName);
    }

    [Fact]
    public void Find_WithNoProjectFile_ReturnsNull()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);

        Assert.Null(Reader().Find(workspace.Root));
    }

    [Fact]
    public void Find_WithNoSchemaName_ReturnsNull()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        workspace.AddFile(AgentConfigLocator.ProjectSettingsFileName, "displayName: \"Contoso Helpdesk\"\n");

        Assert.Null(Reader().Find(workspace.Root));
    }

    [Fact]
    public void Require_WithNoProject_NamesTheDirectoryItSearched()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);

        var failure = Assert.Throws<ManagedAgentException>(() => Reader().Require(workspace.Root, "agent-1"));

        Assert.Equal("schema-name-unavailable", failure.ErrorKind);
        Assert.Contains(workspace.Root, failure.Message, StringComparison.Ordinal);
    }
}
