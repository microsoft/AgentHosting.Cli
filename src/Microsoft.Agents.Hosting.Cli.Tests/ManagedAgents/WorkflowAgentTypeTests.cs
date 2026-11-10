using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class WorkflowAgentTypeTests
{
    [Theory]
    [InlineData("MCSWorkflow")]
    [InlineData("mcsworkflow")]
    [InlineData("  MCSWorkflow  ")]
    public void ParseResolvesWorkflowSpellings(string supplied) => Assert.Equal(AgentTypes.McsWorkflow, AgentTypes.Parse(supplied));

    [Theory]
    [InlineData("MCSAgent", AgentTypes.McsAgent)]
    [InlineData("mcs", AgentTypes.McsAgent)]
    [InlineData("CodeAgent", AgentTypes.CodeAgent)]
    [InlineData(null, AgentTypes.CodeAgent)]
    public void ParseStillResolvesTheExistingTypes(string? supplied, string expected) => Assert.Equal(expected, AgentTypes.Parse(supplied));

    [Fact]
    public void ParseRejectsAnUnknownType() => Assert.Null(AgentTypes.Parse("MCSFlow"));

    [Theory]
    [InlineData(AgentTypes.McsAgent, true, false, true)]
    [InlineData(AgentTypes.McsWorkflow, false, true, true)]
    [InlineData(AgentTypes.CodeAgent, false, false, false)]
    public void EachTypeAnswersTheThreePredicates(string agentType, bool isMcsAgent, bool isMcsWorkflow, bool isMcs)
    {
        Assert.Equal(isMcsAgent, AgentTypes.IsMcsAgent(agentType));
        Assert.Equal(isMcsWorkflow, AgentTypes.IsMcsWorkflow(agentType));
        Assert.Equal(isMcs, AgentTypes.IsMcs(agentType));
    }

    [Fact]
    public void EveryTypeIsListed() => Assert.Equal([AgentTypes.McsAgent, AgentTypes.McsWorkflow, AgentTypes.CodeAgent], AgentTypes.All);

    [Fact]
    public void WorkflowIdRoundTripsAtSchemaVersionThree()
    {
        var document = new AgentConfigDocument { ManagedAgents = [WorkflowFixtures.Record(workflowId: "6a1d7c54-0f2b-4c3a-9c21-9a1f0b7d5e42")] };
        var json = JsonSerializer.Serialize(document, AgentConfigJsonContext.Default.AgentConfigDocument);
        var restored = JsonSerializer.Deserialize(json, AgentConfigJsonContext.Default.AgentConfigDocument);

        Assert.Equal(3, restored!.SchemaVersion);
        Assert.Equal("6a1d7c54-0f2b-4c3a-9c21-9a1f0b7d5e42", restored.ManagedAgents[0].McsWorkflowId);
        Assert.Equal(AgentTypes.McsWorkflow, restored.ManagedAgents[0].AgentType);
    }

    [Fact]
    public void AnAbsentWorkflowIdIsNotWritten()
    {
        var json = JsonSerializer.Serialize(new AgentConfigDocument { ManagedAgents = [ManagedAgentFixtures.Agent()] }, AgentConfigJsonContext.Default.AgentConfigDocument);

        Assert.DoesNotContain("mcsWorkflowId", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentWithoutTheFieldStillReads()
    {
        var json = """{"schemaVersion":3,"managedAgents":[{"tenantId":"t","environmentId":"e","agentId":"a","displayName":"d","agentType":"MCSAgent","agentLocation":".","agentState":"Saved","entityTag":""}],"agentDeployments":[]}""";
        var restored = JsonSerializer.Deserialize(json, AgentConfigJsonContext.Default.AgentConfigDocument);

        Assert.Null(restored!.ManagedAgents[0].McsWorkflowId);
    }

    [Fact]
    public void RequireMcsWorkflowAcceptsOnlyWorkflows()
    {
        Assert.Equal("wf-1", ManagedAgentResolver.RequireMcsWorkflow(WorkflowFixtures.Record(), "verb").AgentId);
        Assert.Throws<ManagedAgentException>(() => ManagedAgentResolver.RequireMcsWorkflow(ManagedAgentFixtures.Agent(), "verb"));
    }

    [Fact]
    public void RequireMcsAgentStillRejectsWorkflows() =>
        Assert.Throws<ManagedAgentException>(() => ManagedAgentResolver.RequireMcsAgent(WorkflowFixtures.Record(), "verb"));

    [Fact]
    public void RequireMcsAgentOrWorkflowAcceptsBoth()
    {
        Assert.Equal("wf-1", ManagedAgentResolver.RequireMcsAgentOrWorkflow(WorkflowFixtures.Record(), "verb").AgentId);
        Assert.Equal("agent-1", ManagedAgentResolver.RequireMcsAgentOrWorkflow(ManagedAgentFixtures.Agent(), "verb").AgentId);
    }

    [Fact]
    public void RequireWorkflowIdRejectsAMissingOrEmptyId()
    {
        Assert.Throws<ManagedAgentException>(() => ManagedAgentResolver.RequireWorkflowId(WorkflowFixtures.Record(workflowId: null)));
        Assert.Throws<ManagedAgentException>(() => ManagedAgentResolver.RequireWorkflowId(WorkflowFixtures.Record(workflowId: Guid.Empty.ToString())));
    }
}

public sealed class WorkflowWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ah-wf-locator-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void FindSingleReturnsTheOnlyWorkflow()
    {
        var workflowId = WorkflowFixtures.Write(_root, "WF Hook 2");
        var folder = WorkflowWorkspaceFixtures.Single(_root);

        Assert.Equal(workflowId, folder.WorkflowId);
        Assert.Equal($"WFHook2-{workflowId}", folder.FolderName);
        Assert.True(File.Exists(folder.DefinitionPath));
        Assert.True(File.Exists(folder.MetadataPath));
    }

    [Fact]
    public void FindSingleFailsWhenThereIsNone()
    {
        Directory.CreateDirectory(_root);

        var failure = Assert.Throws<ManagedAgentException>(() => WorkflowWorkspaceFixtures.RequireSingle(_root));

        Assert.Contains("holds no workflow", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindSingleNamesEveryCandidateWhenThereAreSeveral()
    {
        var first = WorkflowFixtures.Write(_root, "Alpha");
        var second = WorkflowFixtures.Write(_root, "Beta");

        var failure = Assert.Throws<ManagedAgentException>(() => WorkflowWorkspaceFixtures.RequireSingle(_root));

        Assert.Contains($"Alpha-{first}", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"Beta-{second}", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderMissingEitherFileIsNotAWorkflow()
    {
        var incomplete = Path.Combine(WorkflowWorkspace.WorkflowsDirectory(_root), $"Partial-{Guid.NewGuid()}");
        Directory.CreateDirectory(incomplete);
        File.WriteAllText(Path.Combine(incomplete, WorkflowWorkspace.MetadataFileName), "name: Partial");

        Assert.Empty(WorkflowWorkspace.FindAll(_root));
    }

    [Fact]
    public void AFolderWithoutATrailingIdentifierIsNotAWorkflow()
    {
        var unnamed = Path.Combine(WorkflowWorkspace.WorkflowsDirectory(_root), "NoIdentifier");
        Directory.CreateDirectory(unnamed);
        File.WriteAllText(Path.Combine(unnamed, WorkflowWorkspace.MetadataFileName), "name: x");
        File.WriteAllText(Path.Combine(unnamed, WorkflowWorkspace.DefinitionFileName), "{}");

        Assert.Empty(WorkflowWorkspace.FindAll(_root));
    }

    [Theory]
    [InlineData("WF Hook 2", "WFHook2")]
    [InlineData("  spaced  name ", "spacedname")]
    [InlineData("trailing dots...", "trailingdots")]
    [InlineData("a/b\\c:d", "abcd")]
    public void FolderNamesMatchTheSyncLibraryRules(string displayName, string expectedPrefix)
    {
        var workflowId = Guid.NewGuid();

        Assert.Equal($"{expectedPrefix}-{workflowId}", WorkflowWorkspace.FolderNameFor(displayName, workflowId));
    }

    [Fact]
    public void TheTrailingIdentifierIsRead()
    {
        var workflowId = Guid.NewGuid();

        Assert.Equal(workflowId, WorkflowWorkspace.WorkflowIdOf($"Name-{workflowId}"));
        Assert.Null(WorkflowWorkspace.WorkflowIdOf("Name-not-a-guid"));
        Assert.Null(WorkflowWorkspace.WorkflowIdOf(string.Empty));
    }

    [Fact]
    public void AStandaloneWorkspaceHoldsWorkflowsAndNoAgentProject()
    {
        WorkflowFixtures.Write(_root, "Alpha");

        Assert.True(WorkflowWorkspace.IsStandaloneWorkspace(_root));
    }

    [Theory]
    [InlineData(WorkspaceFileNames.Settings)]
    [InlineData(WorkspaceFileNames.Agent)]
    [InlineData(WorkspaceFileNames.Collection)]
    public void AnAgentWorkspaceWithWorkflowsIsNotStandalone(string projectFileName)
    {
        WorkflowFixtures.Write(_root, "Alpha");
        File.WriteAllText(Path.Combine(_root, projectFileName), "schemaName: con_agent");

        Assert.False(WorkflowWorkspace.IsStandaloneWorkspace(_root));
    }

    [Fact]
    public void ADirectoryWithoutWorkflowsIsNotStandalone()
    {
        Directory.CreateDirectory(_root);

        Assert.False(WorkflowWorkspace.IsStandaloneWorkspace(_root));
    }
}
