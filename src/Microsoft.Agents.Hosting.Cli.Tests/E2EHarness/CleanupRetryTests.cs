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

    [Fact]
    public async Task AnAmsAgentIsDeletedOnceItsDeployDrainsAndIsConfirmedGoneThroughTheEndpointsItWasCreatedWith()
    {
        var ledger = AmsLedger(resolved: true);
        var fake = ScriptedMsagent((SignedIn, 0), (DeployInProgress, 1), (DeployInProgress, 1), (AgentDeleted, 0), (AgentNotFound, 1));

        var orphans = await AmsCleanupDriving(fake, TimeSpan.FromMinutes(1)).RunAsync(ledger);

        Assert.Empty(orphans);
        Assert.Equal(ResourceState.Deleted, Assert.Single(ledger.Entries).State);
        Assert.Equal(["auth status --json --non-interactive", AmsDelete, AmsDelete, AmsDelete, $"agent show --agent-id {AmsAgentId} --environment-id {AmsHome} --json --non-interactive"], fake.Invocations);
        Assert.All(fake.Environments, environment => Assert.Equal($"{MsagentFeatures.Ams}|{AmsControlPlane}|{AmsDataPlane}", environment));
    }

    [Fact]
    public async Task AnAmsAgentIntentThatNeverBecameAnAgentIsDoneWithoutADelete()
    {
        var ledger = AmsLedger(resolved: false);
        var fake = ScriptedMsagent((SignedIn, 0), (AgentNotFound, 1));

        var orphans = await AmsCleanupDriving(fake, TimeSpan.FromMinutes(1)).RunAsync(ledger);

        Assert.Empty(orphans);
        Assert.Equal(ResourceState.Deleted, Assert.Single(ledger.Entries).State);
        Assert.Equal(["auth status --json --non-interactive", $"agent show --agent-name {AmsAgentName} --environment-id {AmsHome} --json --non-interactive"], fake.Invocations);
    }

    [Fact]
    public async Task AnAmsAgentIntentFoundByItsNameIsResolvedThenDeleted()
    {
        var ledger = AmsLedger(resolved: false);
        var fake = ScriptedMsagent((SignedIn, 0), (AgentShown, 0), (AgentDeleted, 0), (AgentNotFound, 1));

        var orphans = await AmsCleanupDriving(fake, TimeSpan.FromMinutes(1)).RunAsync(ledger);

        Assert.Empty(orphans);
        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(AmsAgentId, entry.Id);
        Assert.True(entry.Resolved);
        Assert.Equal(ResourceState.Deleted, entry.State);
        Assert.Equal(AmsDelete, fake.Invocations[2]);
    }

    [Fact]
    public async Task AnAmsAgentDeleteAnsweringNotFoundCountsAsDone()
    {
        var ledger = AmsLedger(resolved: true);
        var fake = ScriptedMsagent((SignedIn, 0), (AgentNotFound, 1), (AgentNotFound, 1));

        var orphans = await AmsCleanupDriving(fake, TimeSpan.FromMinutes(1)).RunAsync(ledger);

        Assert.Empty(orphans);
        Assert.Equal(ResourceState.Deleted, Assert.Single(ledger.Entries).State);
    }

    [Theory]
    [InlineData("""{"success":true,"signedIn":true,"cloud":"test","tenantId":"0f0e0d0c-0b0a-0908-0706-050403020100"}""", "tenant 0f0e0d0c-0b0a-0908-0706-050403020100")]
    [InlineData("""{"success":true,"signedIn":true,"cloud":"prod","tenantId":"5cae182f-63ff-49f9-9f35-3acc2724e738"}""", "the prod cloud")]
    [InlineData("""{"success":true,"signedIn":false}""", "no sign-in")]
    public async Task AnAmsAgentUnderAnotherSignInIsLeftForTheSignInItWasCreatedUnder(string status, string reason)
    {
        var ledger = AmsLedger(resolved: true);
        var fake = ScriptedMsagent((status, 0));

        var orphan = Assert.Single(await AmsCleanupDriving(fake, TimeSpan.FromMinutes(1)).RunAsync(ledger));

        Assert.Equal(ResourceState.Orphaned, orphan.State);
        Assert.Contains(reason, orphan.Failure, StringComparison.Ordinal);
        Assert.Equal(["auth status --json --non-interactive"], fake.Invocations);
    }

    [Fact]
    public async Task AnAmsAgentDeleteThatFailsIsReportedWithItsCause()
    {
        var ledger = AmsLedger(resolved: true);
        var fake = ScriptedMsagent((SignedIn, 0), ("""{"success":false,"exitCode":1,"errorKind":"request-failed","errorMessage":"the service answered 500"}""", 1));

        var orphan = Assert.Single(await AmsCleanupDriving(fake, TimeSpan.FromMinutes(1)).RunAsync(ledger));

        Assert.Contains("agent delete exited 1", orphan.Failure, StringComparison.Ordinal);
        Assert.Contains("the service answered 500", orphan.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAmsAgentStillShownAfterItsDeleteIsReported()
    {
        var ledger = AmsLedger(resolved: true);
        var fake = ScriptedMsagent((SignedIn, 0), (AgentDeleted, 0), (AgentShown, 0));

        var orphan = Assert.Single(await AmsCleanupDriving(fake, TimeSpan.FromMinutes(1)).RunAsync(ledger));

        Assert.Contains("still finds the agent", orphan.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAmsAgentWhoseDeployNeverDrainsIsReportedOnceTheWaitRunsOut()
    {
        var ledger = AmsLedger(resolved: true);
        var fake = ScriptedMsagent((SignedIn, 0), (DeployInProgress, 1));

        var orphan = Assert.Single(await AmsCleanupDriving(fake, TimeSpan.Zero).RunAsync(ledger));

        Assert.Contains("agent-deploy-in-progress", orphan.Failure, StringComparison.Ordinal);
        Assert.Equal(2, fake.Invocations.Count);
    }

    [Theory]
    [InlineData(true, $"--agent-id {AmsAgentId}")]
    [InlineData(false, $"--agent-name {AmsAgentName}")]
    public void TheManualCommandForAnAmsAgentNamesTheEndpointsTheSignInAndTheAgent(bool resolved, string selector)
    {
        var command = Cleanup.ManualCommand(Assert.Single(AmsLedger(resolved).Entries));

        Assert.Contains($"AH_CLI_AMS_ENDPOINT={AmsControlPlane}", command, StringComparison.Ordinal);
        Assert.Contains($"AH_CLI_AMSDP_ENDPOINT={AmsDataPlane}", command, StringComparison.Ordinal);
        Assert.Contains($"tenant {AmsTenant}", command, StringComparison.Ordinal);
        Assert.Contains($"msagent agent delete {selector} --environment-id {AmsHome}", command, StringComparison.Ordinal);
    }

    private const string AmsAgentName = "ahe2e-beef-ac01";
    private const string AmsAgentId = "e27dcdf7-ecfa-4732-b5aa-b38bb5a3c66c";
    private const string AmsHome = "245ed99c-a1cc-eae7-9ab5-367e3d921f1d";
    private const string AmsTenant = "5cae182f-63ff-49f9-9f35-3acc2724e738";
    private const string AmsControlPlane = "https://ams.example.test";
    private const string AmsDataPlane = "https://amsdp.example.test";
    private const string AmsDelete = $"agent delete --agent-id {AmsAgentId} --environment-id {AmsHome} --json --non-interactive";
    private const string SignedIn = $$"""{"success":true,"signedIn":true,"cloud":"test","tenantId":"{{AmsTenant}}"}""";
    private const string DeployInProgress = """{"success":false,"exitCode":1,"errorKind":"agent-deploy-in-progress","errorMessage":"a deploy may still run"}""";
    private const string AgentDeleted = $$"""{"success":true,"status":"agent-deleted","agentId":"{{AmsAgentId}}","deployments":[],"keptBots":[]}""";
    private const string AgentNotFound = """{"success":false,"exitCode":1,"errorKind":"agent-not-found","errorMessage":"no such agent"}""";
    private const string AgentShown = $$$"""{"success":true,"agent":{"agentId":"{{{AmsAgentId}}}","displayName":"{{{AmsAgentName}}}"}}""";

    private Ledger AmsLedger(bool resolved)
    {
        var ledger = NewLedger();
        ledger.RecordIntendedAmsAgent(AmsAgentName, new AmsCleanupContext { ControlPlaneEndpoint = AmsControlPlane, DataPlaneEndpoint = AmsDataPlane, Cloud = "test", TenantId = AmsTenant, HomeEnvironmentId = AmsHome }, "AC-01");

        if (resolved)
        {
            ledger.ResolveAmsAgent(AmsAgentName, AmsAgentId);
        }

        return ledger;
    }

    private FakeMsagent ScriptedMsagent(params (string StandardOutput, int ExitCode)[] responses) => new(Path.Combine(_runDirectory, "msagent"), responses);

    private Cleanup AmsCleanupDriving(FakeMsagent fake, TimeSpan drainLimit)
    {
        var transcript = new Transcript(_runDirectory);
        var msagent = Msagent(fake, transcript);
        var azure = new AzureCli(transcript);

        return new Cleanup(msagent, azure, new CloudProbe(msagent, azure), transcript, amsTiming: new AmsCleanupTiming(TimeSpan.Zero, drainLimit));
    }
}
