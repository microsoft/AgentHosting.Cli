using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class WorkflowReviewFixTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new(withProjectFile: false);
    private readonly FakeWorkflowStorePort _store = new();
    private readonly FakeWorkflowScaffoldPort _scaffold = new();

    public void Dispose() => _workspace.Dispose();

    private WorkflowCreateService CreateService(JsonAgentConfigStore store) =>
        new(ManagedAgentFixtures.Resolver(), store, new HomeDeploymentRegistrar(store, store), _store, _scaffold, new AgentConfigLocator(), ManagedAgentFixtures.Principals(), new FakeWorkflowWorkspacePort());

    [Theory]
    [InlineData("settings.mcs.yml")]
    [InlineData("settings.mcs.yaml")]
    [InlineData("agent.mcs.yml")]
    [InlineData("agent.mcs.yaml")]
    [InlineData("collection.mcs.yml")]
    [InlineData("collection.mcs.yaml")]
    public async Task CreateRefusesADestinationThatAlreadyHoldsAnAgentProject(string agentFileName)
    {
        var destination = Path.Combine(_workspace.Root, new WorkflowWorkspaceAdapter().ProjectFolderName("WF Hook 2"));

        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, agentFileName), "kind: GptComponentMetadata\n");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => CreateService(ManagedAgentFixtures.Store()).CreateAsync(
            new ManagedAgentCreateRequest { Name = "WF Hook 2", AgentType = AgentTypes.McsWorkflow, Project = _workspace.Root },
            CancellationToken.None));

        Assert.Equal("workspace-not-empty", failure.ErrorKind);
        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Empty(_scaffold.Calls);
        Assert.False(Directory.Exists(Path.Combine(destination, WorkflowWorkspace.WorkflowsFolderName)));
    }

    [Fact]
    public async Task CreateStillAcceptsAnEmptyDestination()
    {
        var result = await CreateService(ManagedAgentFixtures.Store()).CreateAsync(
            new ManagedAgentCreateRequest { Name = "WF Hook 2", AgentType = AgentTypes.McsWorkflow, Project = _workspace.Root },
            CancellationToken.None);

        Assert.Equal(AgentTypes.McsWorkflow, result.AgentType);
        Assert.Single(_scaffold.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AgentTypes.McsAgent)]
    public async Task InitRefusesAWorkflowSourceForANonWorkflowType(string? agentType)
    {
        var service = new ManagedAgentInitService(
            ManagedAgentFixtures.Store(),
            new AgentConfigLocator(),
            CopilotTestFixtures.Locator(new FakeCopilotWorkspacePort { WorkspaceExists = false }, new SystemCopilotFileSystem()),
            ManagedAgentFixtures.Resolver(),
            ManagedAgentFixtures.Identities());

        var failure = await Assert.ThrowsAsync<UsageException>(() => service.InitAsync(
            new ManagedAgentInitRequest { Name = "Contoso", AgentType = agentType, McsWorkflowSource = _workspace.Root },
            CancellationToken.None));

        Assert.Contains("--mcs-workflow-source", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, WorkflowStateProjection.Draft)]
    [InlineData(1, WorkflowStateProjection.Activated)]
    [InlineData(2, WorkflowStateProjection.Suspended)]
    [InlineData(null, WorkflowStateProjection.Unknown)]
    public void TheStateCodeSurvivesTheSharedContract(int? stateCode, string expected) =>
        Assert.Equal(expected, WorkflowStateProjection.FromStateCode(stateCode));

    [Theory]
    [InlineData(WorkflowStateProjection.Activated, WorkflowStateProjection.Published)]
    [InlineData(WorkflowStateProjection.Draft, WorkflowStateProjection.Draft)]
    [InlineData(WorkflowStateProjection.Suspended, WorkflowStateProjection.Draft)]
    [InlineData(WorkflowStateProjection.Unknown, WorkflowStateProjection.Unknown)]
    public void AnUnknownStateIsNeverReportedAsADraft(string state, string expected) =>
        Assert.Equal(expected, WorkflowStateProjection.ToPortalLabel(state));

    [Fact]
    public void TheWorkspaceAdapterReportsAnAgentProjectThroughEitherExtension()
    {
        var adapter = new WorkflowWorkspaceAdapter();
        var directory = Path.Combine(_workspace.Root, "agentfolder");

        Directory.CreateDirectory(directory);

        Assert.False(adapter.HasAgentProject(directory));

        File.WriteAllText(Path.Combine(directory, "settings.mcs.yaml"), "kind: GptComponentMetadata\n");

        Assert.True(adapter.HasAgentProject(directory));
    }

    [Fact]
    public void TheWorkspaceAdapterReportsNoAgentProjectForAMissingDirectory() =>
        Assert.False(new WorkflowWorkspaceAdapter().HasAgentProject(Path.Combine(_workspace.Root, "absent")));
}
