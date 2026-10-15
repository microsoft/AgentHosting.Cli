using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class WorkflowRegistryUniquenessTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new(withProjectFile: false);
    private readonly JsonAgentConfigStore _config = ManagedAgentFixtures.Store();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void ASecondRecordForOneWorkflowIsRejected()
    {
        var workflowId = Guid.NewGuid().ToString();

        _config.Add(_workspace.Root, WorkflowFixtures.Record(workflowId: workflowId));

        var failure = Assert.Throws<ManagedAgentException>(() => _config.Add(
            _workspace.Root,
            WorkflowFixtures.Record(agentId: "wf-2", displayName: "Second Copy", workflowId: workflowId, agentLocation: "second")));

        Assert.Equal("config-invalid", failure.ErrorKind);
        Assert.Contains(workflowId, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASecondRecordForOneWorkflowIsRejectedWhateverTheCasing()
    {
        var workflowId = Guid.NewGuid().ToString();

        _config.Add(_workspace.Root, WorkflowFixtures.Record(workflowId: workflowId.ToUpperInvariant()));

        Assert.Throws<ManagedAgentException>(() => _config.Add(
            _workspace.Root,
            WorkflowFixtures.Record(agentId: "wf-2", displayName: "Second Copy", workflowId: workflowId.ToLowerInvariant(), agentLocation: "second")));
    }

    [Fact]
    public void TwoRecordsForDifferentWorkflowsAreAccepted()
    {
        _config.Add(_workspace.Root, WorkflowFixtures.Record(workflowId: Guid.NewGuid().ToString()));
        _config.Add(_workspace.Root, WorkflowFixtures.Record(agentId: "wf-2", displayName: "Second Flow", workflowId: Guid.NewGuid().ToString(), agentLocation: "second"));

        Assert.Equal(2, _config.List(_workspace.Root).Count);
    }
}

public sealed class CleanupWorkspaceRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ah-cleanup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string? Remove(LedgerEntry entry, Ledger ledger) =>
        (string?)typeof(Cleanup)
            .GetMethod("RemoveLocalWorkspace", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [entry, ledger]);

    private Ledger WithOrphanAt(string projectDirectory)
    {
        var ledger = Ledger.Create(Path.Combine(_root, "run"), "run-1");
        var orphan = ledger.RecordMcsWorkflow(Guid.NewGuid().ToString(), "env-1", "WF Hook 2", projectDirectory, "wf-1", "MW-01");

        ledger.MarkOrphaned(orphan.Id, "the environment refused the delete");
        ledger.RecordLocalWorkspace(_root, "MW-01");

        return ledger;
    }

    [Fact]
    public void AWorkspaceThatIsTheOrphansOwnProjectIsKept()
    {
        Directory.CreateDirectory(_root);
        var ledger = WithOrphanAt(_root);

        Assert.NotNull(Remove(ledger.Entries.Single(entry => entry.Kind == ResourceKind.LocalWorkspace), ledger));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void AWorkspaceHoldingTheOrphansProjectDeeperDownIsKept()
    {
        var project = Path.Combine(_root, "nested", "project");
        Directory.CreateDirectory(project);
        var ledger = WithOrphanAt(project);

        Assert.NotNull(Remove(ledger.Entries.Single(entry => entry.Kind == ResourceKind.LocalWorkspace), ledger));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void AWorkspaceNoOrphanIsAddressedThroughIsDeleted()
    {
        Directory.CreateDirectory(_root);
        var ledger = WithOrphanAt(Path.Combine(Path.GetTempPath(), "ah-cleanup-elsewhere"));

        Assert.Null(Remove(ledger.Entries.Single(entry => entry.Kind == ResourceKind.LocalWorkspace), ledger));
        Assert.False(Directory.Exists(_root));
    }
}

public sealed class LedgerResourceKindTests
{
    [Theory]
    [InlineData(ResourceKind.McsAgent, 0)]
    [InlineData(ResourceKind.AzureBot, 1)]
    [InlineData(ResourceKind.EntraApp, 2)]
    [InlineData(ResourceKind.LocalWorkspace, 3)]
    [InlineData(ResourceKind.ConfigEdit, 4)]
    [InlineData(ResourceKind.EvaluationTestSet, 5)]
    [InlineData(ResourceKind.EvaluationRun, 6)]
    public void TheKindsARecordedLedgerAlreadyHoldsKeepTheirNumbers(ResourceKind kind, int persisted) =>
        Assert.Equal(persisted, (int)kind);

    [Fact]
    public void TheWorkflowKindTookANumberNothingElseUsed() =>
        Assert.Equal(7, (int)ResourceKind.McsWorkflow);
}

public sealed class CloudProbeListingTests
{
    private static bool? Read(string json) =>
        (bool?)typeof(CloudProbe)
            .GetMethod("ReadListing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [json, "mcsWorkflowId", "11111111-2222-3333-4444-555555555555"]);

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"agents":null,"truncated":false}""")]
    [InlineData("""{"agents":{},"truncated":false}""")]
    [InlineData("""{"agents":[],"truncated":"true"}""")]
    [InlineData("""{"agents":[]}""")]
    [InlineData("[]")]
    [InlineData("not json at all")]
    [InlineData("""{"agents":[],"truncated":true}""")]
    public void AListingThatProvesNothingReportsUnknown(string json) => Assert.Null(Read(json));

    [Fact]
    public void ACompleteListingWithoutTheWorkflowProvesItIsGone() =>
        Assert.False(Read("""{"agents":[{"mcsWorkflowId":"99999999-9999-9999-9999-999999999999"}],"truncated":false}"""));

    [Fact]
    public void AListingThatNamesTheWorkflowProvesItIsThere() =>
        Assert.True(Read("""{"agents":[{"mcsWorkflowId":"11111111-2222-3333-4444-555555555555"}],"truncated":false}"""));

    [Fact]
    public void ATruncatedListingThatNamesTheWorkflowStillProvesItIsThere() =>
        Assert.True(Read("""{"agents":[{"mcsWorkflowId":"11111111-2222-3333-4444-555555555555"}],"truncated":true}"""));

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void TheProbeReadsTheSameShapeTheListingCommandWrites()
    {
        var listing = JsonSerializer.Serialize(new ManagedAgentListResult
        {
            Scope = "environment",
            AgentType = AgentTypes.McsWorkflow,
            Limit = 500,
            Agents = [new ManagedAgentListItem { McsWorkflowId = "11111111-2222-3333-4444-555555555555", AgentType = AgentTypes.McsWorkflow, DisplayName = "WF Hook 2", TenantId = "tenant-1", EnvironmentId = "env-1" }],
        }, CamelCase);

        Assert.True(Read(listing));
    }
}
