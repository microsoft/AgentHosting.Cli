using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class WorkflowDeploymentRoutingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AgentConfigLocator.PhysicalPath(Path.GetTempPath()),
        "ah-wf-routing-" + Guid.NewGuid().ToString("N"));
    private readonly JsonAgentConfigStore _store = ManagedAgentFixtures.Store();
    private readonly DeploymentAgentTypeResolver _resolver;

    public WorkflowDeploymentRoutingTests()
    {
        Directory.CreateDirectory(_root);
        _resolver = new DeploymentAgentTypeResolver(new AgentConfigLocator(), _store, _store);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void AddWorkflow() => _store.Add(_root, WorkflowFixtures.Record(agentLocation: "wf1"));

    [Theory]
    [InlineData(AgentTypes.McsAgent, true)]
    [InlineData(AgentTypes.McsWorkflow, true)]
    [InlineData(AgentTypes.CodeAgent, false)]
    public void IsMcsIncludesAgentsAndWorkflows(string agentType, bool expected) =>
        Assert.Equal(expected, new DeploymentAgentTypeResult { AgentType = agentType, Source = DeploymentAgentTypeSource.AgentRecord }.IsMcs);

    [Fact]
    public void AWorkflowAgentIdResolvesToTheWorkflowType()
    {
        AddWorkflow();

        var resolved = _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, AgentId = "wf-1" });

        Assert.Equal(AgentTypes.McsWorkflow, resolved.AgentType);
        Assert.Equal(DeploymentAgentTypeSource.AgentRecord, resolved.Source);
        Assert.True(resolved.IsMcs);
    }

    [Fact]
    public void ALoneWorkflowRecordResolvesWithoutASelector()
    {
        AddWorkflow();

        Assert.True(_resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root }).IsMcs);
    }

    [Fact]
    public void AWorkflowDeploymentResolvesToTheWorkflowType()
    {
        AddWorkflow();
        _store.Add(_root, WorkflowFixtures.Deployment());

        var resolved = _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, DeploymentName = "devwf" });

        Assert.Equal(AgentTypes.McsWorkflow, resolved.AgentType);
        Assert.Equal(DeploymentAgentTypeSource.DeploymentRecord, resolved.Source);
        Assert.True(resolved.IsMcs);
    }

    [Fact]
    public void AnAgentRecordStillResolvesToTheAgentType()
    {
        _store.Add(_root, ManagedAgentFixtures.Agent());

        var resolved = _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, AgentId = "agent-1" });

        Assert.Equal(AgentTypes.McsAgent, resolved.AgentType);
        Assert.True(resolved.IsMcs);
    }

    [Fact]
    public void ACodeAgentRecordIsNotMcs()
    {
        _store.Add(_root, ManagedAgentFixtures.Agent() with { AgentType = AgentTypes.CodeAgent });

        Assert.False(_resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, AgentId = "agent-1" }).IsMcs);
    }

    [Fact]
    public async Task CreatingADeploymentForAWorkflowNeedsNoCopilotProjectFile()
    {
        AddWorkflow();
        Directory.CreateDirectory(Path.Combine(_root, "wf1"));

        var result = await new AgentDeploymentCreateService(
            ManagedAgentFixtures.AgentResolver(_store),
            _store,
            ManagedAgentFixtures.Resolver(),
            ManagedAgentFixtures.Identities(),
            ManagedAgentFixtures.Principals()).CreateAsync(
                new AgentDeploymentCreateRequest { Project = _root, AgentId = "wf-1", DeploymentType = "dev", DeploymentName = "devwf" },
                CancellationToken.None);

        Assert.Null(result.SchemaName);
        Assert.Equal("wf-1", result.AgentId);
        Assert.Equal("devwf", result.DeploymentName);
    }

    [Fact]
    public async Task ADeploymentCreateResultOmitsTheSchemaLineForAWorkflow()
    {
        AddWorkflow();
        Directory.CreateDirectory(Path.Combine(_root, "wf1"));

        var result = await new AgentDeploymentCreateService(
            ManagedAgentFixtures.AgentResolver(_store),
            _store,
            ManagedAgentFixtures.Resolver(),
            ManagedAgentFixtures.Identities(),
            ManagedAgentFixtures.Principals()).CreateAsync(
                new AgentDeploymentCreateRequest { Project = _root, AgentId = "wf-1", DeploymentType = "dev", DeploymentName = "devwf" },
                CancellationToken.None);

        Assert.DoesNotContain(result.ToTextLines(), line => line.Contains("Schema", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AWorkflowWorkspaceResolvesWithoutACopilotProjectFile()
    {
        AddWorkflow();
        Directory.CreateDirectory(Path.Combine(_root, "wf1"));

        var resolved = ManagedAgentFixtures.AgentResolver(_store).ResolveWorkspaceDirectory(_root, _store.Read(_root).ManagedAgents[0]);

        Assert.Equal(Path.Combine(_root, "wf1"), resolved);
    }

    [Fact]
    public void AMissingWorkflowWorkspaceStillReportsTheMissingDirectory()
    {
        AddWorkflow();

        var exception = Assert.Throws<ManagedAgentException>(
            () => ManagedAgentFixtures.AgentResolver(_store).ResolveWorkspaceDirectory(_root, _store.Read(_root).ManagedAgents[0]));

        Assert.Contains("wf1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWorkflowConnectionTargetNeedsNoSchemaNameOrCloudAgent()
    {
        AddWorkflow();
        WorkflowFixtures.Write(Path.Combine(_root, "wf1"), "WF Hook 2", workflowId: Guid.Parse("11111111-2222-3333-4444-555555555555"));
        _store.Add(_root, WorkflowFixtures.Deployment());

        var catalog = new FakeCopilotCatalogPort { ResolveReturns = null };

        var target = await new AgentDeploymentConnectionResolver(
            ManagedAgentFixtures.AgentResolver(_store),
            ManagedAgentFixtures.Resolver(),
            ManagedAgentFixtures.Identities(),
            catalog,
            ManagedAgentFixtures.WorkflowWorkspace()).ResolveAsync(
                new AgentDeploymentConnectionShowRequest { Project = _root, DeploymentName = "devwf" },
                "msagent deployment connection list",
                CancellationToken.None);

        Assert.Equal(Path.Combine(_root, "wf1"), target.WorkspaceDirectory);
        Assert.Equal("wf-1", target.Agent.AgentId);
        Assert.Equal("11111111-2222-3333-4444-555555555555", target.TargetAgentId);
        Assert.Empty(catalog.ResolvedValues);
    }

    [Fact]
    public async Task AWorkflowConnectionTargetRefusesAFolderHoldingAnotherWorkflow()
    {
        AddWorkflow();
        WorkflowFixtures.Write(Path.Combine(_root, "wf1"), "Swapped", workflowId: Guid.Parse("99999999-2222-3333-4444-555555555555"));
        _store.Add(_root, WorkflowFixtures.Deployment());

        await Assert.ThrowsAsync<ManagedAgentException>(() => new AgentDeploymentConnectionResolver(
            ManagedAgentFixtures.AgentResolver(_store),
            ManagedAgentFixtures.Resolver(),
            ManagedAgentFixtures.Identities(),
            new FakeCopilotCatalogPort { ResolveReturns = null },
            ManagedAgentFixtures.WorkflowWorkspace()).ResolveAsync(
                new AgentDeploymentConnectionShowRequest { Project = _root, DeploymentName = "devwf" },
                "msagent deployment connection list",
                CancellationToken.None));
    }

    [Fact]
    public void AWorkflowDeleteResultLabelsTheCloudRowAsAFlow()
    {
        var lines = new ManagedAgentDeleteResult
        {
            DisplayName = "wf1",
            AgentId = "wf-1",
            EnvironmentId = "env-1",
            McsWorkflowId = "flow-1",
            RemovedAgentDeployments = 1,
            Status = "deleted",
        }.ToTextLines();

        Assert.Contains(lines, line => line.Contains("cloud flow", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("cloud agent", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAgentDeleteResultStillLabelsTheCloudRowAsAnAgent()
    {
        var lines = new ManagedAgentDeleteResult
        {
            DisplayName = "agent1",
            AgentId = "agent-1",
            EnvironmentId = "env-1",
            RemovedAgentDeployments = 1,
            Status = "deleted",
        }.ToTextLines();

        Assert.Contains(lines, line => line.Contains("cloud agent", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("cloud flow", StringComparison.Ordinal));
    }
}
