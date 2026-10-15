using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class WorkflowCreateServiceTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new(withProjectFile: false);
    private readonly FakeWorkflowStorePort _store = new();
    private readonly FakeWorkflowScaffoldPort _scaffold = new();

    public void Dispose() => _workspace.Dispose();

    private WorkflowCreateService Service(JsonAgentConfigStore store) =>
        new(ManagedAgentFixtures.Resolver(), store, store, _store, _scaffold, new AgentConfigLocator(), ManagedAgentFixtures.Principals(), new FakeWorkflowWorkspacePort());

    [Fact]
    public async Task ScaffoldRegistersTheRecordAndWritesTheFiles()
    {
        var store = ManagedAgentFixtures.Store();

        var result = await Service(store).CreateAsync(
            new ManagedAgentCreateRequest { Name = "WF Hook 2", AgentType = AgentTypes.McsWorkflow, Project = _workspace.Root },
            CancellationToken.None);

        Assert.Equal(AgentTypes.McsWorkflow, result.AgentType);
        Assert.Equal("scaffold", result.CreatedFrom);
        Assert.Equal(WorkflowStateProjection.NotDeployed, result.WorkflowState);
        Assert.True(Guid.TryParse(result.McsWorkflowId, out _));
        Assert.Equal(4, result.CreatedFiles.Count);
        Assert.True(Directory.Exists(result.WorkflowFolder));

        var stored = Assert.Single(store.List(_workspace.Root));
        Assert.Equal(AgentTypes.McsWorkflow, stored.AgentType);
        Assert.Equal(result.McsWorkflowId, stored.McsWorkflowId);
    }

    [Fact]
    public async Task ScaffoldWritesTheConnectionReferenceDeclaration()
    {
        var result = await Service(ManagedAgentFixtures.Store()).CreateAsync(
            new ManagedAgentCreateRequest { Name = "WF Hook 2", AgentType = AgentTypes.McsWorkflow, Project = _workspace.Root },
            CancellationToken.None);

        var write = Assert.Single(_store.DeclarationWrites);

        Assert.StartsWith(Path.GetDirectoryName(Path.GetDirectoryName(result.WorkflowFolder)!)!, write, StringComparison.Ordinal);
        Assert.EndsWith(result.McsWorkflowId!, write, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryScaffoldedFileResolvesUnderTheReportedProjectDirectory()
    {
        var result = await Service(ManagedAgentFixtures.Store()).CreateAsync(
            new ManagedAgentCreateRequest { Name = "WF Hook 2", AgentType = AgentTypes.McsWorkflow, Project = _workspace.Root },
            CancellationToken.None);

        Assert.NotEmpty(result.CreatedFiles);
        Assert.All(result.CreatedFiles, file => Assert.True(File.Exists(Path.Combine(result.ProjectDirectory, file)), file));
    }

    [Fact]
    public async Task ScaffoldedFilesAreReportedBeneathTheWorkspaceFolder()
    {
        var result = await Service(ManagedAgentFixtures.Store()).CreateAsync(
            new ManagedAgentCreateRequest { Name = "WF Hook 2", AgentType = AgentTypes.McsWorkflow, Project = _workspace.Root },
            CancellationToken.None);

        var folderName = AgentConfigLocator.WorkspaceFolderName("WF Hook 2");

        Assert.All(result.CreatedFiles, file => Assert.StartsWith($"{folderName}/", file, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScaffoldGeneratesADistinctIdentifierEachTime()    {
        var store = ManagedAgentFixtures.Store();
        var first = await Service(store).CreateAsync(new ManagedAgentCreateRequest { Name = "One", AgentType = AgentTypes.McsWorkflow, Project = _workspace.Root }, CancellationToken.None);
        var second = await Service(store).CreateAsync(new ManagedAgentCreateRequest { Name = "Two", AgentType = AgentTypes.McsWorkflow, Project = _workspace.Root }, CancellationToken.None);

        Assert.NotEqual(first.McsWorkflowId, second.McsWorkflowId);
    }

    [Fact]
    public async Task ScaffoldRequiresAName()
    {
        var failure = await Assert.ThrowsAsync<UsageException>(() => Service(ManagedAgentFixtures.Store()).CreateAsync(
            new ManagedAgentCreateRequest { AgentType = AgentTypes.McsWorkflow, Project = _workspace.Root },
            CancellationToken.None));

        Assert.Contains("--agent-name", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScaffoldRejectsACloneEnvironmentWithoutASource() =>
        await Assert.ThrowsAsync<UsageException>(() => Service(ManagedAgentFixtures.Store()).CreateAsync(
            new ManagedAgentCreateRequest { Name = "WF", AgentType = AgentTypes.McsWorkflow, CloneEnvironmentId = "env-2", Project = _workspace.Root },
            CancellationToken.None));

    [Fact]
    public async Task ScaffoldRefusesADuplicateName()
    {
        var store = ManagedAgentFixtures.Store();
        await Service(store).CreateAsync(new ManagedAgentCreateRequest { Name = "WF Hook 2", AgentType = AgentTypes.McsWorkflow, Project = _workspace.Root }, CancellationToken.None);

        await Assert.ThrowsAsync<UsageException>(() => Service(store).CreateAsync(
            new ManagedAgentCreateRequest { Name = "WF Hook 2", AgentType = AgentTypes.McsWorkflow, Project = _workspace.Root },
            CancellationToken.None));
    }

    [Fact]
    public async Task CloneDownloadsTheWorkflowAndRecordsAHomeDeployment()
    {
        var workflowId = Guid.NewGuid();
        _store.Cloud[workflowId] = new WorkflowDocument { WorkflowId = workflowId, Name = "Remote Flow", State = WorkflowStateProjection.Activated };

        var store = ManagedAgentFixtures.Store();
        var result = await Service(store).CreateAsync(
            new ManagedAgentCreateRequest { AgentType = AgentTypes.McsWorkflow, CloneWorkflowId = workflowId.ToString(), Project = _workspace.Root },
            CancellationToken.None);

        Assert.Equal("clone", result.CreatedFrom);
        Assert.Equal("Remote Flow", result.DisplayName);
        Assert.Equal(workflowId.ToString(), result.McsWorkflowId);
        Assert.Equal(DeploymentStages.Dev, result.DeploymentType);
        Assert.True(Directory.Exists(result.WorkflowFolder));

        var deployment = Assert.Single(ListDeployments(store));
        Assert.Equal(AgentTypes.McsWorkflow, deployment.AgentType);
    }

    private IReadOnlyList<AgentDeploymentRecord> ListDeployments(JsonAgentConfigStore store) => ((IAgentDeploymentRegistry)store).List(_workspace.Root);

    [Fact]
    public async Task CloneFailsWhenTheSourceDoesNotExist() =>
        await Assert.ThrowsAsync<ManagedAgentException>(() => Service(ManagedAgentFixtures.Store()).CreateAsync(
            new ManagedAgentCreateRequest { AgentType = AgentTypes.McsWorkflow, CloneWorkflowId = Guid.NewGuid().ToString(), Project = _workspace.Root },
            CancellationToken.None));

    [Fact]
    public async Task CloneRefusesAWorkflowThatIsAlreadyRegistered()
    {
        var workflowId = Guid.NewGuid();
        _store.Cloud[workflowId] = new WorkflowDocument { WorkflowId = workflowId, Name = "Remote Flow", State = WorkflowStateProjection.Activated };

        var store = ManagedAgentFixtures.Store();
        var request = new ManagedAgentCreateRequest { AgentType = AgentTypes.McsWorkflow, CloneWorkflowId = workflowId.ToString(), Project = _workspace.Root };

        await Service(store).CreateAsync(request, CancellationToken.None);

        await Assert.ThrowsAsync<ManagedAgentException>(() => Service(store).CreateAsync(
            request with { Name = "Second Copy" },
            CancellationToken.None));
    }

    [Fact]
    public async Task CloneRejectsAnIdentifierThatIsNotAGuid() =>
        await Assert.ThrowsAsync<UsageException>(() => Service(ManagedAgentFixtures.Store()).CreateAsync(
            new ManagedAgentCreateRequest { AgentType = AgentTypes.McsWorkflow, CloneWorkflowId = "not-a-guid", Project = _workspace.Root },
            CancellationToken.None));
}

public sealed class WorkflowInitServiceTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new(withProjectFile: false);
    private readonly FakeWorkflowStorePort _store = new();

    public void Dispose() => _workspace.Dispose();

    private WorkflowInitService Service(JsonAgentConfigStore store) =>
        new(store, new AgentConfigLocator(), ManagedAgentFixtures.Resolver(), _store, ManagedAgentFixtures.Principals(), new FakeWorkflowWorkspacePort());

    [Fact]
    public async Task InitRegistersTheWorkflowOnDisk()
    {
        var workflowId = WorkflowFixtures.Write(_workspace.Root, "WF Hook 2");
        var store = ManagedAgentFixtures.Store();

        var result = await Service(store).InitAsync(
            new ManagedAgentInitRequest { Name = "devwf", AgentType = AgentTypes.McsWorkflow, McsWorkflowSource = _workspace.Root },
            CancellationToken.None);

        Assert.Equal("devwf", result.DisplayName);
        Assert.Equal(workflowId.ToString(), result.McsWorkflowId);
        Assert.Equal(AgentTypes.McsWorkflow, result.AgentType);
        Assert.Equal(WorkflowStateProjection.NotDeployed, result.WorkflowState);
        Assert.Equal(AgentConfigLocator.CurrentDirectoryLocation, Assert.Single(store.List(_workspace.Root)).AgentLocation);
    }

    [Fact]
    public async Task TheRecordNameIsIndependentOfTheCloudName()
    {
        WorkflowFixtures.Write(_workspace.Root, "Cloud Name");

        var result = await Service(ManagedAgentFixtures.Store()).InitAsync(
            new ManagedAgentInitRequest { Name = "local-record-name", AgentType = AgentTypes.McsWorkflow, McsWorkflowSource = _workspace.Root },
            CancellationToken.None);

        Assert.Equal("local-record-name", result.DisplayName);
    }

    [Fact]
    public async Task InitWritesTheConnectionReferenceDeclaration()
    {
        WorkflowFixtures.Write(_workspace.Root, "Cloud Name");

        var result = await Service(ManagedAgentFixtures.Store()).InitAsync(
            new ManagedAgentInitRequest { Name = "wf", AgentType = AgentTypes.McsWorkflow, McsWorkflowSource = _workspace.Root },
            CancellationToken.None);

        Assert.Equal($"{_workspace.Root}|{result.McsWorkflowId}", Assert.Single(_store.DeclarationWrites));
    }

    [Fact]
    public async Task InitRequiresAName()
    {
        WorkflowFixtures.Write(_workspace.Root, "WF");

        await Assert.ThrowsAsync<UsageException>(() => Service(ManagedAgentFixtures.Store()).InitAsync(
            new ManagedAgentInitRequest { AgentType = AgentTypes.McsWorkflow, McsWorkflowSource = _workspace.Root },
            CancellationToken.None));
    }

    [Fact]
    public async Task InitFailsWhenTheDirectoryHoldsSeveralWorkflows()
    {
        WorkflowFixtures.Write(_workspace.Root, "Alpha");
        WorkflowFixtures.Write(_workspace.Root, "Beta");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Service(ManagedAgentFixtures.Store()).InitAsync(
            new ManagedAgentInitRequest { Name = "devwf", AgentType = AgentTypes.McsWorkflow, McsWorkflowSource = _workspace.Root },
            CancellationToken.None));

        Assert.Contains("more than one workflow", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitFailsWhenTheDirectoryHoldsNoWorkflow()
    {
        await Assert.ThrowsAsync<ManagedAgentException>(() => Service(ManagedAgentFixtures.Store()).InitAsync(
            new ManagedAgentInitRequest { Name = "devwf", AgentType = AgentTypes.McsWorkflow, McsWorkflowSource = _workspace.Root },
            CancellationToken.None));
    }

    [Fact]
    public async Task InitRefusesAWorkflowThatIsAlreadyRegistered()
    {
        WorkflowFixtures.Write(_workspace.Root, "WF Hook 2");
        var store = ManagedAgentFixtures.Store();

        await Service(store).InitAsync(new ManagedAgentInitRequest { Name = "first", AgentType = AgentTypes.McsWorkflow, McsWorkflowSource = _workspace.Root }, CancellationToken.None);

        await Assert.ThrowsAsync<ManagedAgentException>(() => Service(store).InitAsync(
            new ManagedAgentInitRequest { Name = "second", AgentType = AgentTypes.McsWorkflow, McsWorkflowSource = _workspace.Root },
            CancellationToken.None));
    }

    [Fact]
    public async Task InitWritesNoCloudCache()
    {
        WorkflowFixtures.Write(_workspace.Root, "WF Hook 2");

        await Service(ManagedAgentFixtures.Store()).InitAsync(
            new ManagedAgentInitRequest { Name = "devwf", AgentType = AgentTypes.McsWorkflow, McsWorkflowSource = _workspace.Root },
            CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, ".mcs")));
    }
}

[Collection("CurrentDirectory")]
public sealed class WorkflowInitSourceDefaultTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new(withProjectFile: false);
    private readonly FakeWorkflowStorePort _store = new();
    private readonly JsonAgentConfigStore _config = ManagedAgentFixtures.Store();

    public void Dispose() => _workspace.Dispose();

    private WorkflowInitService Service() =>
        new(_config, new AgentConfigLocator(), ManagedAgentFixtures.Resolver(), _store, ManagedAgentFixtures.Principals(), new FakeWorkflowWorkspacePort());

    private string PrepareNestedWorkflow(string folderName, out Guid workflowId)
    {
        var nested = Path.Combine(_workspace.Root, folderName);

        Directory.CreateDirectory(nested);
        workflowId = WorkflowFixtures.Write(nested, "Nested Flow");

        _config.Add(_workspace.Root, WorkflowFixtures.Record(agentId: "other", displayName: "Other", workflowId: Guid.NewGuid().ToString(), agentLocation: "Other"));

        return nested;
    }

    [Fact]
    public async Task AnOmittedSourceRegistersTheCurrentDirectoryRatherThanTheEnclosingProject()
    {
        var nested = PrepareNestedWorkflow("Nested", out var workflowId);

        using var scope = new CurrentDirectoryScope(nested);

        var result = await Service().InitAsync(
            new ManagedAgentInitRequest { Name = "devwf", AgentType = AgentTypes.McsWorkflow },
            CancellationToken.None);

        Assert.Equal(workflowId.ToString(), result.McsWorkflowId);
        Assert.Equal(Path.GetFullPath(_workspace.Root), Path.GetFullPath(result.ProjectDirectory));
        Assert.Equal("Nested", _config.List(_workspace.Root).Single(agent => agent.AgentId == result.AgentId).AgentLocation);
    }

    [Fact]
    public async Task AnExplicitSourceStillWinsOverTheCurrentDirectory()
    {
        var nested = PrepareNestedWorkflow("Nested", out var workflowId);

        using var scope = new CurrentDirectoryScope(_workspace.Root);

        var result = await Service().InitAsync(
            new ManagedAgentInitRequest { Name = "devwf", AgentType = AgentTypes.McsWorkflow, McsWorkflowSource = nested },
            CancellationToken.None);

        Assert.Equal(workflowId.ToString(), result.McsWorkflowId);
    }
}
