using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class CopilotProjectIdentityReaderTests
{
    private static CopilotProjectIdentityReader Reader() => ManagedAgentFixtures.Identities();

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

    [Fact]
    public void Find_OfACliWorkspace_ReportsTheCliAuthoringMode()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        workspace.AddFile("agent.sync.yaml", "layoutVersion: 1\n");
        workspace.AddFile(AgentConfigLocator.ProjectSettingsFileName, "displayName: MCS CLI Agent\nschemaName: ahe2e_MCSCliAgent\naccessControlPolicy: GroupMembership\nauthenticationMode: Integrated\nauthenticationTrigger: Always\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n\n  agentSettings:\n    model:\n      series: GPT56Reasoning\n\n    instructions:\n      segments:\n        - kind: StaticSegment\n\ntemplate: cliagent-1.0.0\nlanguage: 1033\n");

        var identity = Reader().Find(workspace.Root);

        Assert.Equal(CopilotAuthoringModes.CliCopilot, identity!.AuthoringMode);
        Assert.Equal("ahe2e_MCSCliAgent", identity.SchemaName);
        Assert.Equal("MCS CLI Agent", identity.DisplayName);
    }

    [Fact]
    public void Find_OfAClassicWorkspaceWithASettingsFile_ReportsTheClassicAuthoringModeFromItsSettings()
    {
        using var workspace = TemporaryWorkspace.Classic(schemaName: "ahe2e_MCSClassicAgent", displayName: "MCS Classic Agent");

        var identity = Reader().Find(workspace.Root);

        Assert.Equal(CopilotAuthoringModes.Classic, identity!.AuthoringMode);
        Assert.Equal("ahe2e_MCSClassicAgent", identity.SchemaName);
        Assert.Equal("MCS Classic Agent", identity.DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void FindSchemaName_WithoutARecordedSchemaName_ReadsTheWorkspace(string? recordedSchemaName)
    {
        using var workspace = new TemporaryWorkspace(schemaName: "con_declared");

        Assert.Equal("con_declared", Reader().FindSchemaName(ManagedAgentFixtures.Agent() with { McsSchemaName = recordedSchemaName }, workspace.Root));
    }

    [Fact]
    public void FindSchemaName_WithARecordedSchemaName_ReadsNoWorkspace()
    {
        using var workspace = new TemporaryWorkspace(schemaName: "con_declared");
        var identities = new CountingIdentityPort();

        var schemaName = new CopilotProjectIdentityReader(new AgentConfigLocator(), identities).FindSchemaName(ManagedAgentFixtures.Agent() with { McsSchemaName = "con_recorded" }, workspace.Root);

        Assert.Equal("con_recorded", schemaName);
        Assert.Equal(0, identities.Reads);
    }

    [Fact]
    public void FindSchemaName_WithoutARecordedSchemaNameOrAWorkspace_IsNull()
    {
        var identities = new CountingIdentityPort();

        Assert.Null(new CopilotProjectIdentityReader(new AgentConfigLocator(), identities).FindSchemaName(ManagedAgentFixtures.Agent(), workspaceDirectory: null));
        Assert.Equal(0, identities.Reads);
    }

    [Theory]
    [InlineData("con_recorded", " bot-1 ", "con_recorded")]
    [InlineData(null, " bot-1 ", "bot-1")]
    [InlineData(null, " ", null)]
    [InlineData(null, null, null)]
    public void FindSelector_PrefersTheSchemaNameThenTheTrimmedCloudAgentId(string? recordedSchemaName, string? mcsAgentId, string? expected)
    {
        Assert.Equal(expected, Reader().FindSelector(ManagedAgentFixtures.Agent() with { McsSchemaName = recordedSchemaName, McsAgentId = mcsAgentId }, workspaceDirectory: null));
    }

    private sealed class CountingIdentityPort : ICopilotProjectIdentityPort
    {
        public int Reads { get; private set; }

        public ProjectIdentity? Read(string projectDirectory)
        {
            Reads++;

            return new ProjectIdentity { SchemaName = "con_declared", DisplayName = "Contoso Agent", AuthoringMode = CopilotAuthoringModes.CliCopilot };
        }
    }
}
