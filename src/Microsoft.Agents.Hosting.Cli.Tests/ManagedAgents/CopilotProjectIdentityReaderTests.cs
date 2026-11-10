using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class CopilotProjectIdentityReaderTests
{
    private static CopilotProjectIdentityReader Reader() => new(new AgentConfigLocator(), new SyncAgentWorkspaceIdentityReader());

    public static TheoryData<string> UnreadableSettings => ["", "   \n", "displayName: \"Contoso Helpdesk\"\n", "schemaName: \"con_agent\nconfiguration: [unclosed\n", "<<<<<<< HEAD\nschemaName: con_agent\n=======\nschemaName: con_other\n>>>>>>> branch\n"];

    [Fact]
    public void Find_ReadsTheIdentityTheSettingsFileDeclares()
    {
        using var workspace = new TemporaryWorkspace(schemaName: "con_helpdesk", displayName: "Contoso Helpdesk");

        Assert.Equal(new ProjectIdentity { SchemaName = "con_helpdesk", DisplayName = "Contoso Helpdesk" }, Reader().Find(workspace.Root));
    }

    [Fact]
    public void Find_ReadsTheClassicAgentFileOfAWorkspaceWithoutASettingsFile()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        workspace.AddFile(WorkspaceFileNames.Agent, "displayName: \"Classic Agent\"\nschemaName: \"con_classic\"\n");

        Assert.Equal("con_classic", Reader().Find(workspace.Root)!.SchemaName);
    }

    [Fact]
    public void Find_WithNoProjectFile_ReturnsNull()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);

        Assert.Null(Reader().Find(workspace.Root));
    }

    [Fact]
    public void Find_InAFolderThatDoesNotExist_ReturnsNull()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);

        Assert.Null(Reader().Find(Path.Combine(workspace.Root, "missing")));
    }

    [Theory]
    [MemberData(nameof(UnreadableSettings))]
    public void Find_WithASettingsFileThatDeclaresNoReadableIdentity_ReturnsNull(string settings)
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        workspace.AddFile(WorkspaceFileNames.Settings, settings);

        Assert.Null(Reader().Find(workspace.Root));
    }

    [Fact]
    public void Require_ReturnsTheDeclaredIdentity()
    {
        using var workspace = new TemporaryWorkspace(schemaName: "con_helpdesk", displayName: "Contoso Helpdesk");

        Assert.Equal("con_helpdesk", Reader().Require(workspace.Root, "agent-1").SchemaName);
    }

    [Fact]
    public void Require_WithNoProject_NamesTheDirectoryItSearched()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);

        var failure = Assert.Throws<ManagedAgentException>(() => Reader().Require(workspace.Root, "agent-1"));

        Assert.Equal("schema-name-unavailable", failure.ErrorKind);
        Assert.Contains(workspace.Root, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnreadableSettings))]
    public void Require_WithASettingsFileThatDeclaresNoReadableIdentity_NamesTheFileToFix(string settings)
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        workspace.AddFile(WorkspaceFileNames.Settings, settings);

        var failure = Assert.Throws<CopilotStudioException>(() => Reader().Require(workspace.Root, "agent-1"));

        Assert.Equal((ErrorCodes.Workspace.CopilotWorkspaceFilesUnreadable, ExitCodes.Usage, CopilotErrorKinds.WorkspaceFilesUnreadable), (failure.ErrorCode, failure.ExitCode, failure.ErrorKind));
        Assert.Equal($"Agent 'agent-1' has no readable schema name: '{Path.Combine(workspace.Root, WorkspaceFileNames.Settings)}' is empty, is not valid YAML, or names no schemaName.", failure.Message);
        Assert.Equal("Fix settings.mcs.yml so it is valid YAML that names the agent's schemaName, then run the command again.", failure.Remediation);
    }

    [Fact]
    public void Require_WithAnUnreadableClassicAgentFile_NamesTheAgentFile()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        workspace.AddFile(WorkspaceFileNames.Agent, "kind: GptComponentMetadata\ninstructions: Help people.\n");

        var failure = Assert.Throws<CopilotStudioException>(() => Reader().Require(workspace.Root, "agent-1"));

        Assert.Contains($"'{Path.Combine(workspace.Root, WorkspaceFileNames.Agent)}'", failure.Message, StringComparison.Ordinal);
        Assert.StartsWith("Fix agent.mcs.yml ", failure.Remediation, StringComparison.Ordinal);
    }
}
