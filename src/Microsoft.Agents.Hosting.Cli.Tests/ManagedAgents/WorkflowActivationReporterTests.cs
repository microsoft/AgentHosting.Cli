using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class WorkflowActivationReporterTests : IDisposable
{
    private const string Remedy = " Run `msagent deployment connection update` to see and fix them.";

    private readonly TemporaryWorkspace _workspace = new(withProjectFile: false);
    private readonly WorkflowStateCache _states = ManagedAgentFixtures.WorkflowStates();

    public void Dispose() => _workspace.Dispose();

    private Task<WorkflowActivationOutcome> RunAsync(FakeConnectionPort connections) =>
        ManagedAgentFixtures.ActivationReporter(connections, _states).RunAsync(ManagedAgentFixtures.EnvironmentContext(), _workspace.Root, CancellationToken.None);

    private void RememberAnEarlierObservation() =>
        _states.Write(_workspace.Root, "env-1", new Dictionary<string, int> { ["33333333-3333-3333-3333-333333333333"] = WorkflowStateCache.Activated });

    [Fact]
    public async Task Run_AsksThePassToBindOnlyConnectionsTheSignedInOperatorOwns()
    {
        var connections = new FakeConnectionPort();

        await RunAsync(connections);

        Assert.Equal([ManagedAgentFixtures.Principal], connections.PassPrincipals);
        Assert.Equal([_workspace.Root], connections.Directories);
    }

    [Fact]
    public async Task Run_RecordsOnlyTheStatesTheServiceObserved()
    {
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "11111111-1111-1111-1111-111111111111", DisplayName = "SubmitExpense", CanEnable = true });
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "22222222-2222-2222-2222-222222222222", DisplayName = "NotifyOnReject", CanEnable = false });
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "44444444-4444-4444-4444-444444444444", DisplayName = "Unasked", CanEnable = false });
        connections.ActivatedWorkflows.Add("SubmitExpense");
        connections.DraftWorkflows.Add("NotifyOnReject");
        RememberAnEarlierObservation();

        var outcome = await RunAsync(connections);

        Assert.Equal(
            new Dictionary<string, int> { ["11111111-1111-1111-1111-111111111111"] = WorkflowStateCache.Activated, ["22222222-2222-2222-2222-222222222222"] = WorkflowStateCache.Draft },
            _states.Read(_workspace.Root, "env-1"));
        Assert.Equal(["NotifyOnReject", "SubmitExpense", "Unasked"], outcome.Reported);
        Assert.Equal(["SubmitExpense"], outcome.Activated);
        Assert.Equal(["2 workflows are not running." + Remedy], outcome.Warnings);
    }

    [Fact]
    public async Task Run_AppendsTheRefusalsTheServiceExplainedAfterTheCounts()
    {
        var connections = new FakeConnectionPort { UnboundConnectionCount = 1 };
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "11111111-1111-1111-1111-111111111111", DisplayName = "SubmitExpense", CanEnable = true });
        connections.RefusedWorkflows.Add("Workflow 'SubmitExpense' was not activated: the flow has an invalid trigger.");

        var outcome = await RunAsync(connections);

        Assert.Equal(["1 workflow is not running and 1 connection reference is not bound." + Remedy, "Workflow 'SubmitExpense' was not activated: the flow has an invalid trigger."], outcome.Warnings);
        Assert.Equal(1, outcome.UnboundConnectionCount);
    }

    [Fact]
    public async Task Run_WithNoWorkflows_ForgetsEarlierObservationsAndCountsOnlyUnboundReferences()
    {
        RememberAnEarlierObservation();

        var outcome = await RunAsync(new FakeConnectionPort { UnboundConnectionCount = 2 });

        Assert.Empty(_states.Read(_workspace.Root, "env-1"));
        Assert.Equal(["2 connection references are not bound." + Remedy], outcome.Warnings);
        Assert.Empty(outcome.Reported);
        Assert.Equal(2, outcome.UnboundConnectionCount);
    }

    [Fact]
    public async Task Run_WithNothingLeftToFix_WarnsAboutNothing()
    {
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "11111111-1111-1111-1111-111111111111", DisplayName = "SubmitExpense", CanEnable = true });
        connections.ActivatedWorkflows.Add("SubmitExpense");

        Assert.Empty((await RunAsync(connections)).Warnings);
    }

    [Fact]
    public async Task Run_WhenThePassFails_ForgetsEarlierObservationsAndWarnsInsteadOfFailing()
    {
        RememberAnEarlierObservation();

        var outcome = await RunAsync(new FakeConnectionPort { PassThrows = new InvalidOperationException("The service is unavailable.") });

        Assert.Empty(_states.Read(_workspace.Root, "env-1"));
        Assert.Equal(["The workflows could not be activated after the push: The service is unavailable. Run `msagent deployment connection update` to retry."], outcome.Warnings);
    }

    [Fact]
    public async Task Run_WhenCancelled_ForgetsEarlierObservationsAndStops()
    {
        RememberAnEarlierObservation();

        await Assert.ThrowsAsync<OperationCanceledException>(() => RunAsync(new FakeConnectionPort { PassThrows = new OperationCanceledException() }));

        Assert.Empty(_states.Read(_workspace.Root, "env-1"));
    }
}