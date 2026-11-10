using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class WorkflowListServiceTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new(withProjectFile: false);
    private readonly FakeWorkflowStorePort _store = new();
    private readonly JsonAgentConfigStore _config = ManagedAgentFixtures.Store();

    public void Dispose() => _workspace.Dispose();

    private ManagedAgentListService Service(params PowerPlatformEnvironment[] environments) => new(
        ManagedAgentFixtures.Resolver(environments),
        new FakeCopilotCatalogPort(),
        _store,
        ManagedAgentFixtures.StateRules,
        ManagedAgentFixtures.AgentResolver(_config),
        new AgentConfigLocator(),
        ManagedAgentFixtures.Identities(),
        _config,
        _config);

    private ManagedAgentListRequest Request(string? filter = null, int? limit = null, bool tenant = false) => new()
    {
        Project = _workspace.Root,
        AgentType = AgentTypes.McsWorkflow,
        Filter = filter,
        Limit = limit,
        Tenant = tenant,
    };

    private Guid AddCloud(string name, string state = CopilotStudioStates.Activated, DateTimeOffset? modifiedOn = null)
    {
        var workflowId = Guid.NewGuid();
        _store.Cloud[workflowId] = new WorkflowDocument { WorkflowId = workflowId, Name = name, State = state, ModifiedOn = modifiedOn };

        return workflowId;
    }

    [Fact]
    public async Task TheCloudWorkflowsAreListed()
    {
        var workflowId = AddCloud("WF Hook 2");

        var result = await Service().ListAsync(Request(), CancellationToken.None);

        var row = Assert.Single(result.Agents);
        Assert.Equal(workflowId.ToString(), row.McsWorkflowId);
        Assert.Equal("WF Hook 2", row.DisplayName);
        Assert.Equal(CopilotStudioStates.Published, row.WorkflowState);
        Assert.Equal(AgentTypes.McsWorkflow, row.AgentType);
        Assert.Equal(AgentTypes.McsWorkflow, result.AgentType);
        Assert.Null(row.McsAgentId);
        Assert.Null(row.SchemaName);
    }

    [Fact]
    public async Task ARegisteredWorkflowCarriesItsRecordAndDeploymentCount()
    {
        var workflowId = AddCloud("WF Hook 2");
        _config.Add(_workspace.Root, WorkflowFixtures.Record(workflowId: workflowId.ToString()));
        ((IAgentDeploymentRegistry)_config).Add(_workspace.Root, WorkflowFixtures.Deployment());

        var row = Assert.Single((await Service().ListAsync(Request(), CancellationToken.None)).Agents);

        Assert.Equal("wf-1", row.AgentId);
        Assert.Equal(1, row.DeploymentSlotCount);
    }

    [Fact]
    public async Task AnUnregisteredCloudWorkflowCarriesNoRecord()
    {
        AddCloud("Portal Flow");

        var row = Assert.Single((await Service().ListAsync(Request(), CancellationToken.None)).Agents);

        Assert.Null(row.AgentId);
        Assert.Null(row.DeploymentSlotCount);
    }

    [Fact]
    public async Task ALocalWorkflowThatIsNotInTheCloudIsNotListed()
    {
        _config.Add(_workspace.Root, WorkflowFixtures.Record(workflowId: Guid.NewGuid().ToString()));

        Assert.Empty((await Service().ListAsync(Request(), CancellationToken.None)).Agents);
    }

    [Fact]
    public async Task AnAgentRecordDoesNotAnnotateAWorkflowRow()
    {
        var workflowId = AddCloud("WF Hook 2");
        _config.Add(_workspace.Root, ManagedAgentFixtures.Agent() with { McsWorkflowId = workflowId.ToString() });

        Assert.Null(Assert.Single((await Service().ListAsync(Request(), CancellationToken.None)).Agents).AgentId);
    }

    [Fact]
    public async Task TheFilterIsPassedToTheStore()
    {
        AddCloud("Alpha Flow");
        AddCloud("Beta Flow");

        var result = await Service().ListAsync(Request(filter: "Alpha"), CancellationToken.None);

        Assert.Equal("Alpha Flow", Assert.Single(result.Agents).DisplayName);
        Assert.Contains(_store.Calls, call => call.Contains(":Alpha:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLimitTruncatesAndIsReported()
    {
        AddCloud("Alpha");
        AddCloud("Beta");
        AddCloud("Gamma");

        var result = await Service().ListAsync(Request(limit: 2), CancellationToken.None);

        Assert.Equal(2, result.Agents.Count);
        Assert.True(result.Truncated);
        Assert.Equal(2, result.Limit);
    }

    [Fact]
    public async Task AFullPageIsNotReportedAsTruncated()
    {
        AddCloud("Alpha");
        AddCloud("Beta");

        var result = await Service().ListAsync(Request(limit: 2), CancellationToken.None);

        Assert.Equal(2, result.Agents.Count);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task TheStoreIsAskedForOneRowBeyondTheLimit()
    {
        AddCloud("Alpha");

        await Service().ListAsync(Request(limit: 5), CancellationToken.None);

        Assert.Contains(_store.Calls, call => call.EndsWith(":6", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARegisteredWorkflowIsNotAnnotatedInAnEnvironmentItDoesNotBelongTo()
    {
        var workflowId = AddCloud("WF Hook 2");
        _config.Add(_workspace.Root, WorkflowFixtures.Record(workflowId: workflowId.ToString(), environmentId: "env-1"));

        var result = await Service(
            EnvironmentFixtures.Environment("env-2", instanceUrl: CopilotTestFixtures.DataverseOrganization))
            .ListAsync(Request(), CancellationToken.None);

        var row = Assert.Single(result.Agents);
        Assert.Null(row.AgentId);
        Assert.Null(row.DeploymentSlotCount);
    }

    [Fact]
    public async Task ARegisteredWorkflowIsAnnotatedInAnEnvironmentItDeploysTo()
    {
        var workflowId = AddCloud("WF Hook 2");
        _config.Add(_workspace.Root, WorkflowFixtures.Record(workflowId: workflowId.ToString(), environmentId: "env-1"));
        ((IAgentDeploymentRegistry)_config).Add(_workspace.Root, WorkflowFixtures.Deployment(environmentId: "env-2"));

        var result = await Service(
            EnvironmentFixtures.Environment("env-2", instanceUrl: CopilotTestFixtures.DataverseOrganization))
            .ListAsync(Request(), CancellationToken.None);

        var row = Assert.Single(result.Agents);
        Assert.Equal("wf-1", row.AgentId);
        Assert.Equal(1, row.DeploymentSlotCount);
    }

    [Fact]
    public async Task TheLargestLimitDoesNotAskTheStoreForANegativeCount()
    {
        AddCloud("Alpha");

        await Service().ListAsync(Request(limit: int.MaxValue), CancellationToken.None);

        Assert.Contains(_store.Calls, call => call.EndsWith($":{int.MaxValue}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANonPositiveLimitIsRejected() =>
        await Assert.ThrowsAsync<UsageException>(() => Service().ListAsync(Request(limit: 0), CancellationToken.None));

    [Fact]
    public async Task TheFilterIsRejectedOnTheAgentRoute()
    {
        var failure = await Assert.ThrowsAsync<UsageException>(() => Service().ListAsync(
            new ManagedAgentListRequest { Project = _workspace.Root, Filter = "Alpha" },
            CancellationToken.None));

        Assert.Contains("--filter applies to MCSWorkflow only", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAgentRouteStillListsAgents()
    {
        AddCloud("Should Not Appear");

        var result = await Service().ListAsync(new ManagedAgentListRequest { Project = _workspace.Root }, CancellationToken.None);

        Assert.Equal(AgentTypes.McsAgent, result.AgentType);
        Assert.Empty(result.Agents);
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public async Task TenantScopeReadsEveryReachableEnvironment()
    {
        AddCloud("Alpha");

        var result = await Service(
            EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization),
            EnvironmentFixtures.Environment("env-2", instanceUrl: CopilotTestFixtures.DataverseOrganization))
            .ListAsync(Request(tenant: true), CancellationToken.None);

        Assert.Equal("tenant", result.Scope);
        Assert.Null(result.EnvironmentId);
        Assert.Equal(2, _store.Calls.Count(call => call.StartsWith("list:", StringComparison.Ordinal)));
    }
}

public sealed class WorkflowListRenderingTests
{
    private static ManagedAgentListItem Row(string? agentId = "wf-1", string state = CopilotStudioStates.Activated, int? deployments = 1) => new()
    {
        AgentId = agentId,
        McsWorkflowId = "36af9d18-cdab-eb87-8bdb-e11ba6806e75",
        TenantId = "tenant-1",
        EnvironmentId = "env-1",
        DisplayName = "WF Hook 2",
        AgentType = AgentTypes.McsWorkflow,
        WorkflowState = state,
        DeploymentSlotCount = deployments,
    };

    private static ManagedAgentListResult Result(string scope = "environment", params ManagedAgentListItem[] rows) => new()
    {
        Scope = scope,
        AgentType = AgentTypes.McsWorkflow,
        EnvironmentId = scope == "environment" ? "env-1" : null,
        Limit = 20,
        Agents = rows,
    };

    [Fact]
    public void TheWorkflowGridCarriesTheWorkflowHeaders()
    {
        var lines = string.Join('\n', Result("environment", Row()).ToTextLines());

        Assert.Contains("WORKFLOW ID", lines, StringComparison.Ordinal);
        Assert.Contains("STATE", lines, StringComparison.Ordinal);
        Assert.Contains("DEPLOYMENTS", lines, StringComparison.Ordinal);
        Assert.DoesNotContain("SCHEMA NAME", lines, StringComparison.Ordinal);
    }

    [Fact]
    public void TenantScopeAddsTheEnvironmentColumn() =>
        Assert.Contains("ENVIRONMENT", string.Join('\n', Result("tenant", Row()).ToTextLines()), StringComparison.Ordinal);

    [Fact]
    public void TheCountIsNamedAsWorkflows() =>
        Assert.Contains("1 workflow(s)", string.Join('\n', Result("environment", Row()).ToTextLines()), StringComparison.Ordinal);

    [Fact]
    public void AnUnregisteredRowRendersDashes()
    {
        var lines = string.Join('\n', Result("environment", Row(agentId: null, deployments: null)).ToTextLines());

        Assert.Contains(TextGrid.Dash, lines, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyWorkflowListExplainsItself() =>
        Assert.Contains("No workflows found", string.Join('\n', Result("environment").ToTextLines()), StringComparison.Ordinal);

    [Fact]
    public void AnEmptyAgentListStillExplainsItself()
    {
        var result = new ManagedAgentListResult { Scope = "environment", AgentType = AgentTypes.McsAgent, Limit = 20 };

        Assert.Contains("No agents found", string.Join('\n', result.ToTextLines()), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAgentGridKeepsItsHeaders()
    {
        var result = new ManagedAgentListResult
        {
            Scope = "environment",
            AgentType = AgentTypes.McsAgent,
            Limit = 20,
            Agents =
            [
                new ManagedAgentListItem
                {
                    McsAgentId = "bot-1",
                    SchemaName = "con_agent",
                    TenantId = "tenant-1",
                    EnvironmentId = "env-1",
                    DisplayName = "Contoso",
                    AgentType = AgentTypes.McsAgent,
                },
            ],
        };

        var lines = string.Join('\n', result.ToTextLines());

        Assert.Contains("MCS AGENT ID", lines, StringComparison.Ordinal);
        Assert.Contains("SCHEMA NAME", lines, StringComparison.Ordinal);
        Assert.Contains("1 agent(s)", lines, StringComparison.Ordinal);
    }
}
