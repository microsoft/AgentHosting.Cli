using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class WorkflowSafetyFixTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new(withProjectFile: false);
    private readonly FakeWorkflowStorePort _store = new();
    private readonly FakeConnectionPort _connections = new();
    private readonly FakeCopilotFileSystem _fileSystem = new();
    private readonly JsonAgentConfigStore _config = ManagedAgentFixtures.Store();

    public void Dispose() => _workspace.Dispose();

    private WorkflowStateCache States() => ManagedAgentFixtures.WorkflowStates(_fileSystem);

    private WorkflowDeployService Service() => new(
        new WorkflowProjectResolver(ManagedAgentFixtures.AgentResolver(_config), new AgentConfigLocator(), ManagedAgentFixtures.Resolver(EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization)), new FakeWorkflowWorkspacePort()),
        _config,
        _config,
        _store,
        ManagedAgentFixtures.ActivationReporter(_connections, States()),
        States(),
        ManagedAgentFixtures.StateRules,
        ManagedAgentFixtures.Principals(),
        new FixedClock(ManagedAgentFixtures.Now));

    private void Register(bool activated = true)
    {
        var workflowId = WorkflowFixtures.Write(_workspace.Root, "WF Hook 2", activated);

        _config.Add(_workspace.Root, WorkflowFixtures.Record(workflowId: workflowId.ToString()));
        ((IAgentDeploymentRegistry)_config).Add(_workspace.Root, WorkflowFixtures.Deployment());
    }

    private ManagedAgentDeployRequest Request() => new() { Project = _workspace.Root, AgentId = "wf-1", DeploymentName = "devwf" };

    [Fact]
    public async Task DeployReportsTheReasonThePushLeftTheWorkflowDrafted()
    {
        Register();
        _store.DraftOnPush = true;
        _store.WarningOnPush = "Workflow 'WF Hook 2' was saved as a draft because one of its connection references has no connection you can use.";

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.Equal(CopilotStudioStates.Draft, result.WorkflowState);
        Assert.Contains(result.Warnings, warning => warning.Contains("saved as a draft", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeployDropsAStaleDraftWarningOnceTheWorkflowIsRunning()
    {
        Register();
        _store.DraftOnPush = true;
        _store.DraftedForConnectionsOnPush = true;
        _store.WarningOnPush = "Workflow 'WF Hook 2' was saved as a draft because one of its connection references has no connection you can use.";
        _connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = Guid.NewGuid().ToString(), DisplayName = "WF Hook 2", CanEnable = true });
        _connections.ActivatedWorkflows.Add("WF Hook 2");

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.Equal(CopilotStudioStates.Published, result.WorkflowState);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("saved as a draft", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeployKeepsAWarningTheActivationPassDidNotResolveEvenWhenTheWorkflowEndsUpRunning()
    {
        Register();
        _store.WarningOnPush = "Workflow 'WF Hook 2' is still running because the environment refused to stop it.";
        _connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = Guid.NewGuid().ToString(), DisplayName = "WF Hook 2", CanEnable = true });
        _connections.ActivatedWorkflows.Add("WF Hook 2");

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.Equal(CopilotStudioStates.Published, result.WorkflowState);
        Assert.Contains(result.Warnings, warning => warning.Contains("refused to stop it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeployKeepsADraftWarningTheActivationPassCouldNotResolve()
    {
        Register();
        _store.DraftOnPush = true;
        _store.DraftedForConnectionsOnPush = true;
        _store.WarningOnPush = "Workflow 'WF Hook 2' was saved as a draft because one of its connection references has no connection you can use.";

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.Equal(CopilotStudioStates.Draft, result.WorkflowState);
        Assert.Contains(result.Warnings, warning => warning.Contains("saved as a draft", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeployReportsNoPushWarningWhenThePushRaisedNone()
    {
        Register(activated: false);

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("saved as a draft", StringComparison.Ordinal));
    }

    [Fact]
    public void AProjectHoldingUnpushedChangesIsRestatedAsItsOwnFailure()
    {
        var failure = WorkflowFailureTranslation.Translate(ProjectHoldsUnpushedChanges());

        Assert.Equal("workflow-project-holds-unpushed-changes", failure.ErrorKind);
        Assert.Contains("MyFlow", failure.Message, StringComparison.Ordinal);
        Assert.NotEqual(ManagedAgentException.WorkflowPushFailed("MyFlow", "env-1", "x").ErrorKind, failure.ErrorKind);
    }

    [Fact]
    public async Task AProjectHoldingUnpushedChangesSurvivesTheTranslatingWrapper()
    {
        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => WorkflowFailureTranslation.TranslateAsync<int>(() => throw ProjectHoldsUnpushedChanges()));

        Assert.Equal("workflow-project-holds-unpushed-changes", failure.ErrorKind);
    }

    private static StandaloneWorkflowException ProjectHoldsUnpushedChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcs-agenthost-guard-" + Guid.NewGuid().ToString("N"));

        try
        {
            WorkflowScaffolder.ScaffoldProject(root, "My Flow", Guid.NewGuid());

            return Assert.Throws<StandaloneWorkflowException>(() => WorkflowWorkspace.ResolveProjectFolder(root, "My Flow", WorkflowWorkspace.FindAll(Path.Combine(root, "MyFlow")).Single().WorkflowId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private WorkflowPullService PullService() => new(
        new WorkflowProjectResolver(ManagedAgentFixtures.AgentResolver(_config), new AgentConfigLocator(), ManagedAgentFixtures.Resolver(EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization)), new FakeWorkflowWorkspacePort()),
        _store,
        States(),
        ManagedAgentFixtures.StateRules);

    private WorkflowDeleteService DeleteService(IConfirmationPrompt confirmations) => new(
        ManagedAgentFixtures.AgentResolver(_config),
        new WorkflowProjectResolver(ManagedAgentFixtures.AgentResolver(_config), new AgentConfigLocator(), ManagedAgentFixtures.Resolver(EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization)), new FakeWorkflowWorkspacePort()),
        _config,
        ManagedAgentFixtures.Resolver(EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization)),
        _store,
        confirmations);

    [Fact]
    public async Task PullRefusesToReplaceAnEditTheEnvironmentHasNotSeen()
    {
        Register();

        var folder = WorkflowWorkspaceFixtures.Single(_workspace.Root);

        _store.Cloud[folder.WorkflowId] = new WorkflowDocument { WorkflowId = folder.WorkflowId, Name = "WF Hook 2", State = CopilotStudioStates.Activated };
        _store.CloudDefinition[folder.WorkflowId] = "{\"properties\":{\"from\":\"cloud\"}}";
        _store.LastSynced[folder.WorkflowId] = File.ReadAllText(folder.DefinitionPath);
        File.WriteAllText(folder.DefinitionPath, "{\"properties\":{\"from\":\"local\"}}");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => PullService().PullAsync(new ManagedAgentPullRequest { Project = _workspace.Root, AgentId = "wf-1", DeploymentName = "devwf" }, CancellationToken.None));

        Assert.Equal("workflow-local-changes-would-be-lost", failure.ErrorKind);
        Assert.Equal("{\"properties\":{\"from\":\"local\"}}", File.ReadAllText(folder.DefinitionPath));
    }

    [Fact]
    public async Task AnAgentPullRejectsTheWorkflowOnlyOverwriteSwitch()
    {
        var workspaces = new FakeCopilotWorkspacePort();
        var locator = CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem());
        var service = new ManagedAgentPullService(
            ManagedAgentFixtures.AgentResolver(_config),
            locator,
            new CopilotWorkspaceService(workspaces, locator),
            new SyncComponentCollectionAdapter(),
            ManagedAgentFixtures.Resolver());

        var failure = await Assert.ThrowsAsync<UsageException>(
            () => service.PullAsync(new ManagedAgentPullRequest { Project = _workspace.Root, AgentId = "wf-1", Overwrite = true }, CancellationToken.None));

        Assert.Contains("--overwrite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingByDeploymentAloneResolvesItsOwnerInAMultiAgentProject()
    {
        Register();

        var otherWorkflowId = WorkflowFixtures.Write(Path.Combine(_workspace.Root, "Other"), "Other Flow");

        _config.Add(_workspace.Root, WorkflowFixtures.Record(agentId: "wf-2", displayName: "Other Flow", workflowId: otherWorkflowId.ToString(), agentLocation: "Other"));
        ((IAgentDeploymentRegistry)_config).Add(_workspace.Root, WorkflowFixtures.Deployment(agentId: "wf-2", deploymentName: "otherwf", deploymentId: "dep-2"));

        var result = await DeleteService(new ScriptedConfirmationPrompt { Answer = true }).DeleteAsync(
            new ManagedAgentDeleteRequest { Project = _workspace.Root, DeploymentId = "dep-1" },
            CancellationToken.None);

        Assert.Equal("agent-deleted", result.Status);
        Assert.Equal("wf-1", result.AgentId);
        Assert.Equal("dep-1", result.DeploymentId);
    }

    [Fact]
    public async Task DeletingByAgentAloneStillRefusesAnAmbiguousProject()
    {
        Register();

        var otherWorkflowId = WorkflowFixtures.Write(Path.Combine(_workspace.Root, "Other"), "Other Flow");

        _config.Add(_workspace.Root, WorkflowFixtures.Record(agentId: "wf-2", displayName: "Other Flow", workflowId: otherWorkflowId.ToString(), agentLocation: "Other"));

        await Assert.ThrowsAsync<ManagedAgentException>(() => DeleteService(new ScriptedConfirmationPrompt { Answer = true }).DeleteAsync(
            new ManagedAgentDeleteRequest { Project = _workspace.Root },
            CancellationToken.None));
    }
}
