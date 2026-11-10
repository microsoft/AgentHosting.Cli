using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;
using Microsoft.CopilotStudio.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncConnectionAdapterActivationTests
{
    private const string SubmitExpense = "11111111-1111-1111-1111-111111111111";
    private const string NotifyOnReject = "22222222-2222-2222-2222-222222222222";

    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "ah-connection-activation");

    private static CopilotEnvironmentContext Environment() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "11111111-2222-3333-4444-555555555555",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static AgentConnectionView Teams(params ConnectionInstance[] candidates) => new()
    {
        ConnectionReferenceLogicalName = "cr_teams",
        ConnectorId = "shared_teams",
        IsDeclared = true,
        Candidates = [.. candidates],
    };

    [Fact]
    public async Task ActivateWorkflows_ReportsWhatThePassStartedObservedAndLeftUnbound()
    {
        var connections = new RecordingConnectionManagementService();
        connections.Views.Add(Teams());
        var workflows = new RecordingWorkflowActivationService
        {
            ActivationReturns = new WorkflowActivationResult
            {
                Succeeded = true,
                Message = "Workflow 'Escalate' was not activated: its trigger is invalid.",
                Workflows =
                [
                    new WorkflowStatusView { WorkflowId = SubmitExpense, DisplayName = "SubmitExpense", State = WorkflowState.Activated },
                    new WorkflowStatusView { WorkflowId = string.Empty, DisplayName = "Orphan", State = WorkflowState.Activated },
                ],
            },
        };
        workflows.Workflows.Add(new WorkflowStatusView { WorkflowId = SubmitExpense, DisplayName = "SubmitExpense", State = WorkflowState.Draft, CanEnable = true });
        workflows.Workflows.Add(new WorkflowStatusView { WorkflowId = NotifyOnReject, DisplayName = "NotifyOnReject", State = WorkflowState.Draft, CanEnable = false });
        workflows.Workflows.Add(new WorkflowStatusView { WorkflowId = string.Empty, DisplayName = "Orphan", State = WorkflowState.Draft, CanEnable = true });

        var report = await CopilotTestFixtures.ConnectionAdapter(connections, workflows).ActivateWorkflowsAsync(Environment(), Workspace, "op@contoso.com", CancellationToken.None);

        Assert.Equal(["NotifyOnReject", "Orphan", "SubmitExpense"], report.Workflows);
        Assert.Equal(["Orphan", "SubmitExpense"], report.Activated);
        Assert.Equal(["Workflow 'Escalate' was not activated: its trigger is invalid."], report.Refused);
        Assert.Equal(new Dictionary<string, bool> { [SubmitExpense] = true }, report.ObservedById);
        Assert.Equal(1, report.NotRunningCount);
        Assert.Equal(1, report.UnboundConnectionCount);
    }

    [Fact]
    public async Task ActivateWorkflows_WithNothingToActivate_ReportsNoWorkflowsAndNoRefusal()
    {
        var report = await CopilotTestFixtures.ConnectionAdapter(new RecordingConnectionManagementService(), new RecordingWorkflowActivationService()).ActivateWorkflowsAsync(Environment(), Workspace, "op@contoso.com", CancellationToken.None);

        Assert.Empty(report.Workflows);
        Assert.Empty(report.Refused);
        Assert.Empty(report.ObservedById);
        Assert.Equal(0, report.UnboundConnectionCount);
    }

    [Theory]
    [InlineData("op@contoso.com", "cr_teams=conn-1", 0)]
    [InlineData("someone@contoso.com", "", 1)]
    [InlineData(null, "", 1)]
    public async Task ActivateWorkflows_BindsOnlyAConnectionThePrincipalOwns(string? principal, string expectedBinding, int expectedUnbound)
    {
        var connections = new RecordingConnectionManagementService();
        connections.Views.Add(Teams(new ConnectionInstance { Name = "conn-1", DisplayName = "op@contoso.com", Status = "Connected", Owner = "op@contoso.com" }));

        var report = await CopilotTestFixtures.ConnectionAdapter(connections, new RecordingWorkflowActivationService()).ActivateWorkflowsAsync(Environment(), Workspace, principal, CancellationToken.None);

        Assert.Equal(expectedBinding, string.Join(",", connections.AppliedBindings.Select(binding => $"{binding.ConnectionReferenceLogicalName}={binding.ConnectionId}")));
        Assert.Equal(expectedUnbound, report.UnboundConnectionCount);
    }

    [Fact]
    public async Task ActivateWorkflows_WhenThePassFails_ThrowsItsFailure()
    {
        var connections = new RecordingConnectionManagementService { WriteConnectionsCacheThrows = new IOException("cache locked") };

        Assert.Equal("cache locked", (await Assert.ThrowsAsync<IOException>(() => CopilotTestFixtures.ConnectionAdapter(connections, new RecordingWorkflowActivationService()).ActivateWorkflowsAsync(Environment(), Workspace, "op@contoso.com", CancellationToken.None))).Message);
    }

    [Fact]
    public async Task ActivateWorkflows_RunsThePassTheAdapterWasGiven()
    {
        var connections = new RecordingConnectionManagementService();
        connections.Views.Add(Teams());

        var report = await Adapter(new WorkflowActivationPass(connections, new RecordingWorkflowActivationService(), new WorkspaceFileAccessorFactory())).ActivateWorkflowsAsync(Environment(), Workspace, "op@contoso.com", CancellationToken.None);

        Assert.Equal(1, report.UnboundConnectionCount);
    }

    [Fact]
    public void Constructor_WithoutAnActivationPass_Throws() => Assert.Equal("activationPass", Assert.Throws<ArgumentNullException>(() => Adapter(null!)).ParamName);

    private static SyncConnectionAdapter Adapter(WorkflowActivationPass activationPass)
    {
        var dataverse = new RecordingConnectionReferenceClient();
        var httpClientAccessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider());

        return new SyncConnectionAdapter(new RecordingConnectionManagementService(), new RecordingWorkflowActivationService(), activationPass, new RecordingWorkspaceSynchronizer(), dataverse, new StubConnectionCatalogClient(), new StubTokenProvider(), httpClientAccessor, new WorkspaceFileAccessorFactory(), new DataverseWebApiClient(dataverse, httpClientAccessor), PowerPlatformApiTargetConfig.Public);
    }

    [Fact]
    public void ReadWorkflows_JudgesEachWorkflowByTheLibrarysRule_AgainstTheReferencesGivenAsBound()
    {
        using var workspace = new TemporaryDirectory();
        WorkflowFixtures.Write(workspace.Path, "SqlOnly", activated: false, definition: Uses("cr_sql"));
        WorkflowFixtures.Write(workspace.Path, "NeedsBoth", activated: false, definition: Uses("cr_sql", "cr_teams"));
        WorkflowFixtures.Write(workspace.Path, "Independent", activated: false);
        using var provider = CopilotCompositionTests.BuildProvider();

        Assert.Equal([("Independent", true), ("NeedsBoth", false), ("SqlOnly", true)], provider.GetRequiredService<ICopilotConnectionPort>().ReadWorkflows(workspace.Path, ["CR_SQL"]).Select(workflow => (workflow.DisplayName, workflow.CanEnable)));
    }

    private static string Uses(params string[] logicalNames) => JsonSerializer.Serialize(new { properties = new { connectionReferences = logicalNames.ToDictionary(logicalName => logicalName, logicalName => new { connection = new { connectionReferenceLogicalName = logicalName } }) } });
}