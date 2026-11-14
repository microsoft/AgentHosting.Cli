using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using HarnessCommands = Microsoft.Agents.Hosting.Cli.E2E.Commands;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class CleanupRetryTests : IDisposable
{
    private const string CopyRunId = "20261113-current-beef";
    private const string CopyEnvironmentId = "env-1";
    private const string CopyDisplayName = "ahe2e MC-35 copy beef";
    private const string ListedWorkflowId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string UnlistedWorkflowId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string ForeignWorkflowId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";
    private const string AnotherAgentRow = """{"mcsAgentId":"another-id","schemaName":"another_schema"}""";
    private const string ClonedWorkflowRecord = """{"agentId":"rec-1","displayName":"ahe2e flow","agentType":"MCSWorkflow","tenantId":"tenant-1","environmentId":"env-1","projectDirectory":"","configPath":"","createdFrom":"clone","deploymentName":"dev"}""";

    private readonly string _runDirectory = Path.Combine(Path.GetTempPath(), "msagent-cleanup-" + Guid.NewGuid().ToString("N"));

    public CleanupRetryTests() => Directory.CreateDirectory(_runDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_runDirectory))
        {
            Directory.Delete(_runDirectory, recursive: true);
        }
    }

    private Ledger NewLedger() => Ledger.Create(_runDirectory, "test-run");

    private LedgerEntry RecordWorkflow(Ledger ledger) => ledger.RecordStandaloneWorkflow("11111111-1111-1111-1111-111111111111", "env-1", "ahe2e flow", _runDirectory, null, "dev", "SW-B01");

    private Ledger NewCopyLedger()
    {
        var ledger = Ledger.Create(_runDirectory, CopyRunId);
        ledger.RecordCopy("cr1_copy", CopyDisplayName, CopyEnvironmentId, _runDirectory, _runDirectory, "copy-record", "copy-dev", "MC-35");
        ledger.RecordCopyWorkflows([ListedWorkflowId, UnlistedWorkflowId], CopyDisplayName, CopyEnvironmentId, "MC-35");
        return ledger;
    }

    private Ledger LedgerHoldingAnAgentWhoseProjectIsGone(ResourceKind kind)
    {
        var ledger = Ledger.Create(_runDirectory, CopyRunId);
        var project = Path.Combine(_runDirectory, "gone");

        if (kind == ResourceKind.McsCopy)
        {
            ledger.RecordCopy("cr1_copy", CopyDisplayName, CopyEnvironmentId, project, project, "copy-record", "copy-dev", "MC-35");
        }
        else
        {
            ledger.RecordMcsAgent("33333333-3333-3333-3333-333333333333", CopyEnvironmentId, "cr1_agent", project, "dev", "MA-01");
        }

        return ledger;
    }

    private MsagentCli Msagent(FakeMsagent fake, Transcript transcript) => new(fake.ExecutablePath, transcript, Path.Combine(_runDirectory, "dumps"));

    private Cleanup CleanupDriving(FakeMsagent fake)
    {
        var transcript = new Transcript(_runDirectory);
        var msagent = Msagent(fake, transcript);
        var azure = new AzureCli(transcript);

        return new Cleanup(msagent, azure, new CloudProbe(msagent, azure), transcript);
    }

    [Theory]
    [InlineData(ResourceKind.McsCopy)]
    [InlineData(ResourceKind.McsAgent)]
    public async Task AnAgentWhoseProjectIsGoneStaysOrphanedWhenTheAgentListingStopsShortOfIt(ResourceKind kind)
    {
        var ledger = LedgerHoldingAnAgentWhoseProjectIsGone(kind);
        var fake = new FakeMsagent(Path.Combine(_runDirectory, "msagent"), $$"""{"agents":[{{AnotherAgentRow}}],"truncated":true}""");

        var orphans = await CleanupDriving(fake).RunAsync(ledger);

        var orphan = Assert.Single(orphans);
        Assert.Equal(ResourceState.Orphaned, orphan.State);
        Assert.Contains("can be proven absent", orphan.Failure, StringComparison.Ordinal);
        Assert.NotEmpty(fake.Invocations);
        Assert.All(fake.Invocations, invocation => Assert.StartsWith("agent list ", invocation, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ResourceKind.McsCopy)]
    [InlineData(ResourceKind.McsAgent)]
    public async Task AnAgentWhoseProjectIsGoneIsDoneOnceACompleteAgentListingLacksIt(ResourceKind kind)
    {
        var ledger = LedgerHoldingAnAgentWhoseProjectIsGone(kind);
        var fake = new FakeMsagent(Path.Combine(_runDirectory, "msagent"), $$"""{"agents":[{{AnotherAgentRow}}],"truncated":false}""");

        var orphans = await CleanupDriving(fake).RunAsync(ledger);

        Assert.Empty(orphans);
        Assert.Equal(ResourceState.Deleted, Assert.Single(ledger.Entries).State);
    }

    [Fact]
    public async Task DeletingAWorkflowThroughAFreshCloneAddressesTheRecordTheCloneReported()
    {
        var fake = new FakeMsagent(Path.Combine(_runDirectory, "msagent"), ClonedWorkflowRecord);

        var failure = await Cleanup.DeleteWorkflowAsync(Msagent(fake, new Transcript(_runDirectory)), ListedWorkflowId, CopyEnvironmentId, Path.Combine(_runDirectory, "clone"), CancellationToken.None);

        Assert.Null(failure);
        Assert.Equal([$"agent create --agent-type MCSWorkflow --mcs-clone-workflow-id {ListedWorkflowId} --mcs-clone-environment-id {CopyEnvironmentId} --environment-id {CopyEnvironmentId} --json --non-interactive", "agent delete --agent-id rec-1 --deployment-name dev --json --non-interactive"], fake.Invocations);
    }

    [Fact]
    public async Task AWorkflowCloneThatReportsNoReadableRecordIsAFailureTheCallerCanStillVerifyAndDeletesNothing()
    {
        var fake = new FakeMsagent(Path.Combine(_runDirectory, "msagent"), "not json");

        var failure = await Cleanup.DeleteWorkflowAsync(Msagent(fake, new Transcript(_runDirectory)), ListedWorkflowId, CopyEnvironmentId, Path.Combine(_runDirectory, "clone"), CancellationToken.None);

        Assert.Contains("reported no readable record", failure, StringComparison.Ordinal);
        Assert.StartsWith("agent create ", Assert.Single(fake.Invocations), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCopyWorkflowSweepLeavesAWorkflowNamedAfterTheRunThatNoEntryRecordsInPlace()
    {
        var ledger = NewCopyLedger();

        var sweep = Cleanup.PlanCopyWorkflowSweep(ledger, CopyEnvironmentId, [(ListedWorkflowId, $"{CopyDisplayName} - Flow"), (ForeignWorkflowId, "ahe2e foreign-project beef - Workflow")]);

        Assert.Equal(ForeignWorkflowId, Assert.Single(sweep.Unproven).WorkflowId);
        Assert.DoesNotContain(sweep.Remaining.Concat(sweep.Gone), entry => entry.Id == ForeignWorkflowId);
    }

    [Fact]
    public void TheCopyWorkflowSweepRemovesTheRecordedWorkflowsStillListedAndSettlesTheOthersAsGone()
    {
        var ledger = NewCopyLedger();

        var sweep = Cleanup.PlanCopyWorkflowSweep(ledger, CopyEnvironmentId, [(ListedWorkflowId, $"{CopyDisplayName} - Flow")]);

        Assert.Equal(ListedWorkflowId, Assert.Single(sweep.Remaining).Id);
        Assert.Equal(UnlistedWorkflowId, Assert.Single(sweep.Gone).Id);
        Assert.Empty(sweep.Unproven);
    }

    [Fact]
    public void AnUnknownWorkflowListingRemovesEveryRecordedCopyWorkflowOfThatEnvironmentById()
    {
        var ledger = NewCopyLedger();
        ledger.RecordCopyWorkflows(["cccccccc-cccc-cccc-cccc-cccccccccccc"], "ahe2e MC-36 copy beef", "env-2", "MC-36");

        var sweep = Cleanup.PlanCopyWorkflowSweep(ledger, CopyEnvironmentId, null);

        Assert.Equal([ListedWorkflowId, UnlistedWorkflowId], sweep.Remaining.Select(entry => entry.Id));
        Assert.Empty(sweep.Gone);
        Assert.Empty(sweep.Unproven);
    }

    [Fact]
    public void TheCopyWorkflowSweepSkipsSettledCopyWorkflowsAndCallsNoWorkflowAnotherEntryRecordsUnproven()
    {
        var ledger = NewCopyLedger();
        ledger.MarkDeleted(ListedWorkflowId);
        ledger.RecordStandaloneWorkflow(ForeignWorkflowId, CopyEnvironmentId, "ahe2e SW-B01 beef - Flow", _runDirectory, null, "dev", "SW-B01");

        var sweep = Cleanup.PlanCopyWorkflowSweep(ledger, CopyEnvironmentId, [(ListedWorkflowId, $"{CopyDisplayName} - Flow"), (ForeignWorkflowId, "ahe2e SW-B01 beef - Flow")]);

        Assert.Empty(sweep.Remaining);
        Assert.Equal(UnlistedWorkflowId, Assert.Single(sweep.Gone).Id);
        Assert.Empty(sweep.Unproven);
    }

    [Fact]
    public void AnOrphanedCopyWorkflowNamesTheCommandsThatDeleteItByHand()
    {
        var ledger = NewCopyLedger();

        var command = Cleanup.ManualCommand(ledger.Entries.Single(entry => entry.Id == ListedWorkflowId));

        Assert.Contains($"--mcs-clone-workflow-id {ListedWorkflowId} --mcs-clone-environment-id {CopyEnvironmentId}", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"agents":[{"mcsWorkflowId":"a","displayName":"x"}],"truncated":true}""")]
    [InlineData("""{"agents":[{"mcsWorkflowId":"a","displayName":"x"}]}""")]
    [InlineData("""{"agents":{},"truncated":false}""")]
    [InlineData("""{"agents":[1],"truncated":false}""")]
    [InlineData("""[]""")]
    [InlineData("not json")]
    public void AWorkflowListingThatCannotProveWhichWorkflowsAreAbsentReadsAsUnknown(string json)
    {
        Assert.Null(CloudProbe.ReadCompleteWorkflowListing(json));
    }

    [Fact]
    public void ACompleteWorkflowListingReadsEveryRow()
    {
        var listed = CloudProbe.ReadCompleteWorkflowListing("""{"agents":[{"mcsWorkflowId":"a","displayName":"x"},{"mcsWorkflowId":"b","displayName":"y"}],"truncated":false}""");

        Assert.Equal(new[] { ("a", "x"), ("b", "y") }, listed);
    }

    [Fact]
    public void ARetryWhoseStandaloneEntriesAreAllOrphanedReopensThemAndNeedsTheVerifyingServices()
    {
        var ledger = NewLedger();
        var workflow = RecordWorkflow(ledger);
        var reference = ledger.RecordStandaloneConnectionReference("new_sharedteams_1", "/providers/Microsoft.PowerApps/apis/shared_teams", "env-1", "SW-B01");
        ledger.MarkOrphaned(workflow.Id, "the environment refused the delete");
        ledger.MarkOrphaned(reference.Id, "the row was still present");

        Assert.True(HarnessCommands.ReopenForCleanup(ledger));
        Assert.All(ledger.Entries, entry => Assert.Equal(ResourceState.Created, entry.State));
        Assert.All(ledger.Entries, entry => Assert.Null(entry.Failure));
    }

    [Fact]
    public void ARetryWithAStandaloneEntryStillOutstandingNeedsTheVerifyingServices()
    {
        var ledger = NewLedger();
        RecordWorkflow(ledger);

        Assert.True(HarnessCommands.ReopenForCleanup(ledger));
    }

    [Fact]
    public void ARetryWithNoStandaloneEntryLeftReopensTheRestWithoutTheStandaloneServices()
    {
        var ledger = NewLedger();
        var agent = ledger.RecordMcsAgent("22222222-2222-2222-2222-222222222222", "env-1", "ahe2e_agent", _runDirectory, "dev", "MA-01");
        var workflow = RecordWorkflow(ledger);
        ledger.MarkOrphaned(agent.Id, "the environment refused the delete");
        ledger.MarkDeleted(workflow.Id);

        Assert.False(HarnessCommands.ReopenForCleanup(ledger));
        Assert.Equal(ResourceState.Created, ledger.Entries.Single(entry => entry.Kind == ResourceKind.McsAgent).State);
        Assert.Equal(ResourceState.Deleted, ledger.Entries.Single(entry => entry.Kind == ResourceKind.StandaloneWorkflow).State);
    }
}
