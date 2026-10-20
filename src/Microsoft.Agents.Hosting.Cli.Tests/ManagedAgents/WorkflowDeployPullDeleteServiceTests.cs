using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class WorkflowDeployServiceTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new(withProjectFile: false);
    private readonly FakeWorkflowStorePort _store = new();
    private readonly FakeConnectionPort _connections = new();
    private readonly FakeCopilotFileSystem _fileSystem = new();
    private readonly JsonAgentConfigStore _config = ManagedAgentFixtures.Store();

    public void Dispose() => _workspace.Dispose();

    private WorkflowStateCache States() => ManagedAgentFixtures.WorkflowStates(_fileSystem);

    private WorkflowDeployService Service() => new(
        new WorkflowProjectResolver(ManagedAgentFixtures.AgentResolver(_config), new AgentConfigLocator(), ManagedAgentFixtures.Resolver(EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization), EnvironmentFixtures.Environment("env-2", instanceUrl: CopilotTestFixtures.DataverseOrganization)), new FakeWorkflowWorkspacePort()),
        _config,
        _config,
        _store,
        ManagedAgentFixtures.ActivationPass(_connections, States()),
        States(),
        ManagedAgentFixtures.Principals(),
        new FixedClock(ManagedAgentFixtures.Now));

    private Guid Register(bool activated = true, bool withDeployment = true)
    {
        var workflowId = WorkflowFixtures.Write(_workspace.Root, "WF Hook 2", activated);

        _config.Add(_workspace.Root, WorkflowFixtures.Record(workflowId: workflowId.ToString()));
        if (withDeployment)
        {
            ((IAgentDeploymentRegistry)_config).Add(_workspace.Root, WorkflowFixtures.Deployment());
        }

        return workflowId;
    }

    private ManagedAgentDeployRequest Request(bool publish = false, bool overwrite = false, string? schemaName = null) => new()
    {
        Project = _workspace.Root,
        AgentId = "wf-1",
        DeploymentName = "devwf",
        Publish = publish,
        Overwrite = overwrite,
        SchemaName = schemaName,
    };

    [Fact]
    public async Task TheFirstDeployCreatesTheCloudWorkflow()
    {
        var workflowId = Register();

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.Equal("create", result.Action);
        Assert.True(result.TargetAgentCreated);
        Assert.Equal(workflowId.ToString(), result.McsWorkflowId);
        Assert.Equal(AgentTypes.McsWorkflow, result.AgentType);
        Assert.Contains($"upsert:{workflowId}", _store.Calls);
    }

    [Fact]
    public async Task ASecondDeployUpdatesRatherThanCreates()
    {
        Register();

        await Service().DeployAsync(Request(), CancellationToken.None);
        var second = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.Equal("update", second.Action);
        Assert.False(second.TargetAgentCreated);
    }

    [Fact]
    public async Task PromotingToAnotherEnvironmentKeepsTheSameIdentifier()
    {
        var workflowId = Register();
        ((IAgentDeploymentRegistry)_config).Add(_workspace.Root, WorkflowFixtures.Deployment(deploymentName: "testwf", deploymentId: "dep-2", environmentId: "env-2"));

        await Service().DeployAsync(Request(), CancellationToken.None);
        var promoted = await Service().DeployAsync(Request() with { DeploymentName = "testwf" }, CancellationToken.None);

        Assert.Equal(workflowId.ToString(), promoted.McsWorkflowId);
    }

    [Fact]
    public async Task AWorkflowWithEveryReferenceBoundIsActivated()
    {
        Register();
        _connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = Guid.NewGuid().ToString(), DisplayName = "WF Hook 2", CanEnable = true });
        _connections.ActivatedWorkflows.Add("WF Hook 2");

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Published, result.WorkflowState);
    }

    [Fact]
    public async Task AWorkflowWithAnUnboundReferenceStaysADraftAndWarns()
    {
        Register();
        _store.DraftOnPush = true;
        _connections.Views.Add(new CopilotConnectionView { LogicalName = "shared_office365", ConnectorId = "shared_office365", BoundConnectionId = null, IsDeclared = true });
        _connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = Guid.NewGuid().ToString(), DisplayName = "WF Hook 2", CanEnable = false, ConnectionReferences = ["shared_office365"] });

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Draft, result.WorkflowState);
        Assert.Equal(1, result.UnboundConnectionCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("deployment connection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APushThatHadToDraftTheWorkflowStillReportsTheUnboundReferences()
    {
        Register();
        _store.DraftOnPush = true;
        _connections.Views.Add(new CopilotConnectionView { LogicalName = "shared_office365", ConnectorId = "shared_office365", BoundConnectionId = null, IsDeclared = true });
        _connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = Guid.NewGuid().ToString(), DisplayName = "WF Hook 2", CanEnable = false, ConnectionReferences = ["shared_office365"] });

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Draft, result.WorkflowState);
        Assert.Equal(1, result.UnboundConnectionCount);
        Assert.NotEqual(0, _connections.ReadCalls);
    }

    [Fact]
    public async Task APushThatHadToDraftTheWorkflowStillPublishesOnceTheConnectionsBind()
    {
        Register();
        _store.DraftOnPush = true;
        _connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = Guid.NewGuid().ToString(), DisplayName = "WF Hook 2", CanEnable = true });
        _connections.ActivatedWorkflows.Add("WF Hook 2");

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Published, result.WorkflowState);
    }

    [Fact]
    public async Task ADeployReportsTheObservedStateWhenAStopWasRefused()
    {
        Register(activated: false);
        _store.ObservedStateOnPush = WorkflowStateProjection.Activated;

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Published, result.WorkflowState);
        Assert.True(result.Published);
    }

    [Fact]
    public async Task ADeployNeverReportsAnUnknownStateAsADraft()
    {
        Register(activated: false);
        _store.ObservedStateOnPush = WorkflowStateProjection.Unknown;

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Unknown, result.WorkflowState);
        Assert.False(result.Published);
        Assert.Null(result.PublishedOn);
    }

    [Fact]
    public async Task APublishedWorkflowReportsItselfPublished()
    {
        Register();
        _connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = Guid.NewGuid().ToString(), DisplayName = "WF Hook 2", CanEnable = true });
        _connections.ActivatedWorkflows.Add("WF Hook 2");

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.True(result.Published);
        Assert.Equal(ManagedAgentFixtures.Now, result.PublishedOn);
    }

    [Fact]
    public async Task ADraftDeployReportsNoPublication()
    {
        Register(activated: false);

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.False(result.Published);
        Assert.Null(result.PublishedOn);
    }

    [Fact]
    public async Task ADraftDeployDropsWhatTheEnvironmentLastReported()
    {
        var workflowId = Register();
        _connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = Guid.NewGuid().ToString(), DisplayName = "WF Hook 2", CanEnable = true });
        _connections.ActivatedWorkflows.Add("WF Hook 2");

        await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.NotEmpty(States().Read(_workspace.Root, "env-1"));

        var folder = WorkflowWorkspaceFixtures.Single(_workspace.Root);
        File.WriteAllText(folder.MetadataPath, File.ReadAllText(folder.MetadataPath).Replace("stateCode: 1", "stateCode: 0", StringComparison.Ordinal).Replace("statusCode: 2", "statusCode: 1", StringComparison.Ordinal));
        _store.Cloud[workflowId] = _store.Cloud[workflowId] with { State = WorkflowStateProjection.Draft };

        await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.Empty(States().Read(_workspace.Root, "env-1"));
    }

    [Fact]
    public async Task ADeployRefusesAFolderThatHoldsAnotherWorkflow()
    {
        Register();

        foreach (var folder in WorkflowWorkspace.FindAll(_workspace.Root))
        {
            Directory.Delete(folder.FolderPath, recursive: true);
        }

        WorkflowFixtures.Write(_workspace.Root, "WF Hook 2");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Service().DeployAsync(Request(), CancellationToken.None));

        Assert.Equal("workflow-folder-mismatch", failure.ErrorKind);
        Assert.DoesNotContain(_store.Calls, call => call.StartsWith("upsert:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADraftWorkflowIsNotActivatedWithoutPublish()
    {
        Register(activated: false);

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Draft, result.WorkflowState);
        Assert.Equal(0, _connections.ReadCalls);
    }

    [Fact]
    public async Task ReferencesDeclaredForTheTargetEnvironmentAreWarnedAbout()
    {
        Register();
        _store.DeclaredConnectionReferences.AddRange(["new_sharedteams_aa643948", "new_sharedmsnweather_2a0bcba7"]);

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        var warning = Assert.Single(result.Warnings, line => line.Contains("Declared 2 connection reference", StringComparison.Ordinal));

        Assert.Contains("new_sharedteams_aa643948", warning, StringComparison.Ordinal);
        Assert.Contains("new_sharedmsnweather_2a0bcba7", warning, StringComparison.Ordinal);
        Assert.Contains("deployment connection update", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeployThatDeclaredNothingWarnsAboutNoReferences()
    {
        Register();

        var result = await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.DoesNotContain(result.Warnings, line => line.Contains("Declared", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishActivatesADraftWorkflow()
    {
        Register(activated: false);
        _connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = Guid.NewGuid().ToString(), DisplayName = "WF Hook 2", CanEnable = true });
        _connections.ActivatedWorkflows.Add("WF Hook 2");

        var result = await Service().DeployAsync(Request(publish: true), CancellationToken.None);

        Assert.Equal(WorkflowStateProjection.Published, result.WorkflowState);
    }

    [Fact]
    public async Task OverwriteIsAcceptedBecauseAWorkflowDeployAlwaysReplacesTheCloudCopy()
    {
        var workflowId = Register();

        var created = await Service().DeployAsync(Request(overwrite: true), CancellationToken.None);
        var replaced = await Service().DeployAsync(Request(overwrite: true), CancellationToken.None);

        Assert.Equal("create", created.Action);
        Assert.Equal("update", replaced.Action);
        Assert.Equal(2, _store.Calls.Count(call => call == $"upsert:{workflowId}"));
        Assert.Empty(created.Warnings);
        Assert.Empty(replaced.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SchemaNameIsRejectedBeforeAnythingIsUploadedOrRecorded(bool overwrite)
    {
        Register();
        var configuration = File.ReadAllBytes(_workspace.ConfigPath);

        var failure = await Assert.ThrowsAsync<UsageException>(() => Service().DeployAsync(Request(overwrite: overwrite, schemaName: "con_flow"), CancellationToken.None));

        Assert.Equal("--schema-name does not apply to a workflow, which is addressed by its workflow id. Remove --schema-name.", failure.Message);
        Assert.Empty(_store.Calls);
        Assert.Equal(0, _connections.ReadCalls);
        Assert.Equal(configuration, File.ReadAllBytes(_workspace.ConfigPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankSchemaNameIsNotRejected(string schemaName)
    {
        var workflowId = Register();

        var result = await Service().DeployAsync(Request(schemaName: schemaName), CancellationToken.None);

        Assert.Single(_store.Calls, call => call == $"upsert:{workflowId}");
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnUnresolvedDeploymentStopsBeforeCloudOrCacheChanges(bool hasDeployment, bool withSelector)
    {
        Register(withDeployment: hasDeployment);
        States().Write(_workspace.Root, "env-1", new Dictionary<string, int> { ["cloud-only"] = WorkflowStateCache.Activated });
        _workspace.AddFile(Path.Combine(".mcs", "botdefinition.json"), "{\"source\":\"cloud\"}");
        var configuration = File.ReadAllBytes(_workspace.ConfigPath);
        var cachePath = Path.Combine(_workspace.Root, ".mcs", "botdefinition.json");
        var cache = File.ReadAllBytes(cachePath);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Service().DeployAsync(
            new ManagedAgentDeployRequest { Project = _workspace.Root, AgentId = "wf-1", DeploymentName = withSelector ? "no-such-slot" : null },
            CancellationToken.None));

        Assert.Equal(withSelector ? "deployment-not-found" : hasDeployment ? "deployment-selector-required" : "deployment-records-empty", failure.ErrorKind);
        Assert.Contains(hasDeployment ? withSelector ? "no-such-slot" : "Available deployments: devwf" : "no deployment records", failure.Message, StringComparison.Ordinal);
        Assert.Empty(_store.Calls);
        Assert.Equal(0, _connections.ReadCalls);
        Assert.Equal(WorkflowStateCache.Activated, Assert.Single(States().Read(_workspace.Root, "env-1")).Value);
        Assert.Equal(configuration, File.ReadAllBytes(_workspace.ConfigPath));
        Assert.Equal(cache, File.ReadAllBytes(cachePath));
    }

    [Fact]
    public async Task DeployRecordsTheDeployerAndTimestamp()
    {
        Register();

        await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.Equal(ManagedAgentFixtures.Now, Assert.Single(((IAgentDeploymentRegistry)_config).List(_workspace.Root)).LastDeployedTime);
    }

    [Fact]
    public async Task DeployWritesNoCloudCache()
    {
        Register();

        await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_workspace.Root, ".mcs", "botdefinition.json")));
    }

    [Fact]
    public async Task DeployIssuesOneUpsertPerRun()
    {
        var workflowId = Register();

        await Service().DeployAsync(Request(), CancellationToken.None);

        Assert.Single(_store.Calls, call => call == $"upsert:{workflowId}");
    }

    [Fact]
    public async Task ADeployStillRefusesAWorkspaceRootThatIsGone()
    {
        WorkflowFixtures.RegisterNested(_config, _workspace.Root, "WFHook2");

        Directory.Delete(Path.Combine(_workspace.Root, "WFHook2"), recursive: true);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Service().DeployAsync(Request(), CancellationToken.None));

        Assert.Equal("workspace-missing", failure.ErrorKind);
        Assert.DoesNotContain(_store.Calls, call => call.StartsWith("upsert:", StringComparison.Ordinal));
    }
}

public sealed class WorkflowPullServiceTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new(withProjectFile: false);
    private readonly FakeWorkflowStorePort _store = new();
    private readonly FakeCopilotFileSystem _fileSystem = new();
    private readonly JsonAgentConfigStore _config = ManagedAgentFixtures.Store();

    public void Dispose() => _workspace.Dispose();

    private WorkflowStateCache States() => ManagedAgentFixtures.WorkflowStates(_fileSystem);

    private WorkflowPullService Service() => new(
        new WorkflowProjectResolver(ManagedAgentFixtures.AgentResolver(_config), new AgentConfigLocator(), ManagedAgentFixtures.Resolver(), new FakeWorkflowWorkspacePort()),
        _store,
        States());

    private Guid Register(string name = "WF Hook 2")
    {
        var workflowId = WorkflowFixtures.Write(_workspace.Root, name);

        _config.Add(_workspace.Root, WorkflowFixtures.Record(workflowId: workflowId.ToString()));
        ((IAgentDeploymentRegistry)_config).Add(_workspace.Root, WorkflowFixtures.Deployment());
        _store.Cloud[workflowId] = new WorkflowDocument { WorkflowId = workflowId, Name = name, State = WorkflowStateProjection.Activated };

        return workflowId;
    }

    private ManagedAgentPullRequest Request() => new() { Project = _workspace.Root, AgentId = "wf-1", DeploymentName = "devwf" };

    private const string NestedFolder = "WFHook2";

    private Guid RegisterNested()
    {
        var workflowId = WorkflowFixtures.RegisterNested(_config, _workspace.Root, NestedFolder);

        _store.Cloud[workflowId] = new WorkflowDocument { WorkflowId = workflowId, Name = "WF Hook 2", State = WorkflowStateProjection.Activated };

        return workflowId;
    }

    [Fact]
    public async Task PullRefusesToReplaceALocalEditTheEnvironmentHasNotSeen()
    {
        var workflowId = Register();
        var folder = WorkflowWorkspaceFixtures.Single(_workspace.Root);

        _store.CloudDefinition[workflowId] = "{\"properties\":{\"from\":\"cloud\"}}";
        _store.LastSynced[workflowId] = File.ReadAllText(folder.DefinitionPath);
        File.WriteAllText(folder.DefinitionPath, "{\"properties\":{\"from\":\"local\"}}");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Service().PullAsync(Request(), CancellationToken.None));

        Assert.Equal("workflow-local-changes-would-be-lost", failure.ErrorKind);
        Assert.Contains("--overwrite", failure.Remediation!, StringComparison.Ordinal);
        Assert.Equal("{\"properties\":{\"from\":\"local\"}}", File.ReadAllText(folder.DefinitionPath));
    }

    [Fact]
    public async Task PullWithOverwriteReplacesALocalEdit()
    {
        var workflowId = Register();
        var folder = WorkflowWorkspaceFixtures.Single(_workspace.Root);

        _store.CloudDefinition[workflowId] = "{\"properties\":{\"from\":\"cloud\"}}";
        _store.LastSynced[workflowId] = File.ReadAllText(folder.DefinitionPath);
        File.WriteAllText(folder.DefinitionPath, "{\"properties\":{\"from\":\"local\"}}");

        var result = await Service().PullAsync(Request() with { Overwrite = true }, CancellationToken.None);

        Assert.Equal("workflow-pulled", result.Status);
        Assert.Equal("{\"properties\":{\"from\":\"cloud\"}}", File.ReadAllText(WorkflowWorkspaceFixtures.Single(_workspace.Root).DefinitionPath));
    }

    [Fact]
    public async Task PullReportsUpToDateWhenNothingChanged()
    {
        Register();

        await Service().PullAsync(Request(), CancellationToken.None);
        var second = await Service().PullAsync(Request(), CancellationToken.None);

        Assert.Equal("up-to-date", second.Status);
        Assert.Equal(0, second.ChangeCount);
    }

    [Fact]
    public async Task PullRenamesTheFolderWhenTheCloudNameChanged()
    {
        var workflowId = Register();
        _store.Cloud[workflowId] = new WorkflowDocument { WorkflowId = workflowId, Name = "Renamed In Portal", State = WorkflowStateProjection.Activated };

        await Service().PullAsync(Request(), CancellationToken.None);

        Assert.Equal($"RenamedInPortal-{workflowId}", WorkflowWorkspaceFixtures.Single(_workspace.Root).FolderName);
    }

    [Fact]
    public async Task PullRestoresAWorkflowWhoseFilesWereDeleted()
    {
        var workflowId = Register();

        foreach (var folder in WorkflowWorkspace.FindAll(_workspace.Root))
        {
            Directory.Delete(folder.FolderPath, recursive: true);
        }

        var result = await Service().PullAsync(Request(), CancellationToken.None);

        Assert.Equal("workflow-pulled", result.Status);
        Assert.Equal(workflowId, WorkflowWorkspaceFixtures.Single(_workspace.Root).WorkflowId);
    }

    [Fact]
    public async Task PullRestoresAWorkflowWhoseWorkspaceWasDeleted()
    {
        var workflowId = Register();

        Directory.Delete(WorkflowWorkspace.WorkflowsDirectory(_workspace.Root), recursive: true);

        await Service().PullAsync(Request(), CancellationToken.None);

        Assert.Equal(workflowId, WorkflowWorkspaceFixtures.Single(_workspace.Root).WorkflowId);
    }

    [Fact]
    public async Task PullRefusesAFolderThatHoldsAnotherWorkflow()
    {
        Register();

        foreach (var folder in WorkflowWorkspace.FindAll(_workspace.Root))
        {
            Directory.Delete(folder.FolderPath, recursive: true);
        }

        WorkflowFixtures.Write(_workspace.Root, "WF Hook 2");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Service().PullAsync(Request(), CancellationToken.None));

        Assert.Equal("workflow-folder-mismatch", failure.ErrorKind);
    }

    [Fact]
    public async Task PullWritesNoCloudCache()
    {
        Register();

        await Service().PullAsync(Request(), CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_workspace.Root, ".mcs", "botdefinition.json")));
    }

    [Fact]
    public async Task PullRestoresAWorkflowWhoseWorkspaceRootWasDeleted()
    {
        var workflowId = RegisterNested();

        Directory.Delete(Path.Combine(_workspace.Root, NestedFolder), recursive: true);

        var result = await Service().PullAsync(Request(), CancellationToken.None);

        Assert.Equal("workflow-pulled", result.Status);
        Assert.Equal(workflowId, WorkflowWorkspaceFixtures.Single(Path.Combine(_workspace.Root, NestedFolder)).WorkflowId);
    }

    [Fact]
    public async Task PullRecordsTheStateTheEnvironmentReported()
    {
        var workflowId = Register();

        await Service().PullAsync(Request(), CancellationToken.None);

        Assert.Equal(WorkflowStateCache.Activated, States().Read(_workspace.Root, "env-1")[workflowId.ToString()]);
    }

    [Fact]
    public async Task PullReplacesAStaleActivationStateWithWhatTheCloudNowReports()
    {
        var workflowId = Register();

        await Service().PullAsync(Request(), CancellationToken.None);

        _store.Cloud[workflowId] = _store.Cloud[workflowId] with { State = WorkflowStateProjection.Draft };

        await Service().PullAsync(Request(), CancellationToken.None);

        Assert.Equal(WorkflowStateCache.Draft, States().Read(_workspace.Root, "env-1")[workflowId.ToString()]);
    }

    [Fact]
    public async Task TheStatePullRecordedIsTheStateAReportReads()
    {
        var workflowId = Register();
        _store.Cloud[workflowId] = _store.Cloud[workflowId] with { State = WorkflowStateProjection.Draft };

        await Service().PullAsync(Request(), CancellationToken.None);

        var reported = WorkflowStateProjection.FromCache(
            [new CopilotWorkflowView { WorkflowId = workflowId.ToString(), DisplayName = "WF Hook 2", IsActivatedLocally = null }],
            States().Read(_workspace.Root, "env-1"));

        Assert.Equal(WorkflowStateProjection.Draft, Assert.Single(reported).State);
    }

    [Fact]
    public async Task PullForgetsAStateTheCacheCannotRecord()
    {
        var workflowId = Register();

        await Service().PullAsync(Request(), CancellationToken.None);

        _store.Cloud[workflowId] = _store.Cloud[workflowId] with { State = WorkflowStateProjection.Suspended };

        await Service().PullAsync(Request(), CancellationToken.None);

        Assert.Empty(States().Read(_workspace.Root, "env-1"));
    }

    [Fact]
    public async Task PullLeavesWhatAnotherEnvironmentReportedAlone()
    {
        Register();
        States().Write(_workspace.Root, "env-other", new Dictionary<string, int> { ["wf-other"] = WorkflowStateCache.Draft });

        await Service().PullAsync(Request(), CancellationToken.None);

        Assert.Equal(WorkflowStateCache.Draft, States().Read(_workspace.Root, "env-other")["wf-other"]);
    }
}

public sealed class WorkflowDeleteServiceTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new(withProjectFile: false);
    private readonly FakeWorkflowStorePort _store = new();
    private readonly JsonAgentConfigStore _config = ManagedAgentFixtures.Store();

    public void Dispose() => _workspace.Dispose();

    private WorkflowDeleteService Service(IConfirmationPrompt? prompt = null) => new(
        ManagedAgentFixtures.AgentResolver(_config),
        new WorkflowProjectResolver(ManagedAgentFixtures.AgentResolver(_config), new AgentConfigLocator(), Environments(), new FakeWorkflowWorkspacePort()),
        _config,
        Environments(),
        _store,
        prompt ?? new ScriptedConfirmationPrompt { Answer = true });

    private static Core.Copilot.CopilotEnvironmentResolver Environments() =>
        ManagedAgentFixtures.Resolver(EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization), EnvironmentFixtures.Environment("env-2", instanceUrl: CopilotTestFixtures.DataverseOrganization));

    private Guid Register(bool inCloud = true)
    {
        var workflowId = WorkflowFixtures.Write(_workspace.Root, "WF Hook 2");

        _config.Add(_workspace.Root, WorkflowFixtures.Record(workflowId: workflowId.ToString()));
        ((IAgentDeploymentRegistry)_config).Add(_workspace.Root, WorkflowFixtures.Deployment());

        if (inCloud)
        {
            _store.Cloud[workflowId] = new WorkflowDocument { WorkflowId = workflowId, Name = "WF Hook 2", State = WorkflowStateProjection.Activated };
        }

        return workflowId;
    }

    private ManagedAgentDeleteRequest Request() => new() { Project = _workspace.Root, AgentId = "wf-1", NonInteractive = false };

    [Fact]
    public async Task DeleteRemovesTheCloudWorkflowAndEveryRecord()
    {
        var workflowId = Register();

        var result = await Service().DeleteAsync(Request(), CancellationToken.None);

        Assert.Equal("agent-deleted", result.Status);
        Assert.Equal(workflowId.ToString(), result.McsWorkflowId);
        Assert.Equal(1, result.RemovedAgentDeployments);
        Assert.Empty(_config.List(_workspace.Root));
        Assert.Empty(((IAgentDeploymentRegistry)_config).List(_workspace.Root));
    }

    [Fact]
    public async Task DeleteLeavesLocalFilesAlone()
    {
        Register();
        var folder = WorkflowWorkspaceFixtures.Single(_workspace.Root);

        await Service().DeleteAsync(Request(), CancellationToken.None);

        Assert.True(File.Exists(folder.DefinitionPath));
        Assert.True(File.Exists(folder.MetadataPath));
    }

    [Fact]
    public async Task DeleteDeactivatesBeforeDeleting()
    {
        var workflowId = Register();

        await Service().DeleteAsync(Request(), CancellationToken.None);

        Assert.True(_store.Calls.IndexOf($"deactivate:{workflowId}") < _store.Calls.IndexOf($"delete:{workflowId}"));
    }

    [Fact]
    public async Task AnAbsentCloudWorkflowStillClearsTheRecords()
    {
        Register(inCloud: false);

        var result = await Service().DeleteAsync(Request(), CancellationToken.None);

        Assert.True(result.McsAgentAlreadyAbsent);
        Assert.Equal("agent-deleted", result.Status);
        Assert.Empty(_config.List(_workspace.Root));
    }

    [Fact]
    public async Task OtherDeploymentsAreReportedAsOrphaned()
    {
        Register();
        ((IAgentDeploymentRegistry)_config).Add(_workspace.Root, WorkflowFixtures.Deployment(deploymentName: "prodwf", deploymentId: "dep-2", environmentId: "env-2"));

        var result = await Service().DeleteAsync(Request(), CancellationToken.None);

        Assert.Equal("env-2", Assert.Single(result.OrphanedTargets).EnvironmentId);
    }

    [Fact]
    public async Task ARenamedCloudWorkflowIsStillDeletable()
    {
        var workflowId = Register();
        _store.Cloud[workflowId] = new WorkflowDocument { WorkflowId = workflowId, Name = "Renamed In Portal", State = WorkflowStateProjection.Activated };

        var result = await Service().DeleteAsync(Request(), CancellationToken.None);

        Assert.Equal("agent-deleted", result.Status);
    }

    [Fact]
    public async Task ADeclinedConfirmationChangesNothing()
    {
        Register();

        var result = await Service(new ScriptedConfirmationPrompt { Answer = false }).DeleteAsync(Request(), CancellationToken.None);

        Assert.Equal("cancelled", result.Status);
        Assert.Single(_config.List(_workspace.Root));
        Assert.Single(_store.Cloud);
    }

    [Fact]
    public async Task TheHomeEnvironmentIsReportedAsOrphanedWhenNoDeploymentNamesItAnyMore()
    {
        Register();
        RemoveHomeDeployment();

        var result = await Service().DeleteAsync(Request() with { DeploymentName = "prodwf" }, CancellationToken.None);

        var orphan = Assert.Single(result.OrphanedTargets);

        Assert.Equal("env-1", orphan.EnvironmentId);
        Assert.Equal(WorkflowDeleteService.HomeTargetName, orphan.DeploymentName);
    }

    [Fact]
    public async Task TheConfirmationNamesAnOrphanedHomeEnvironment()
    {
        Register();
        RemoveHomeDeployment();

        var prompt = new ScriptedConfirmationPrompt { Answer = true };

        await Service(prompt).DeleteAsync(Request() with { DeploymentName = "prodwf" }, CancellationToken.None);

        Assert.Contains("home environment", prompt.LastDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHomeEnvironmentIsNotReportedWhenItIsTheOneBeingDeleted()
    {
        Register();

        var result = await Service().DeleteAsync(Request(), CancellationToken.None);

        Assert.Empty(result.OrphanedTargets);
    }

    [Fact]
    public async Task AnEnvironmentTheHomeAndADeploymentShareIsReportedOnce()
    {
        Register();
        ((IAgentDeploymentRegistry)_config).Add(_workspace.Root, WorkflowFixtures.Deployment(deploymentName: "prodwf", deploymentId: "dep-2", environmentId: "env-2"));

        var result = await Service().DeleteAsync(Request() with { DeploymentName = "prodwf" }, CancellationToken.None);

        var orphan = Assert.Single(result.OrphanedTargets);

        Assert.Equal("env-1", orphan.EnvironmentId);
        Assert.Equal("devwf", orphan.DeploymentName);
    }

    private void RemoveHomeDeployment()
    {
        var deployments = (IAgentDeploymentRegistry)_config;

        deployments.Add(_workspace.Root, WorkflowFixtures.Deployment(deploymentName: "prodwf", deploymentId: "dep-2", environmentId: "env-2"));

        var home = deployments.List(_workspace.Root).Single(deployment => deployment.DeploymentId == "dep-1");

        deployments.Remove(_workspace.Root, home.DeploymentId, home.EntityTag);
    }
}
