using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class AmsMcsRingGuardTests : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly FakeMsagent _msagent = new();

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public async Task AnUnreadableEnvironmentListStopsBeforeAnyChange()
    {
        _msagent.Answer("env list", string.Empty, exitCode: 1);

        var (blocked, ledger) = await RefusedAsync(AmsMcsTestKit.Config());

        Assert.Equal("The test-ring guard stopped before changing anything: msagent env list exited 1.", blocked.Message);
        Assert.Empty(ledger.Entries);
    }

    [Theory]
    [InlineData(AmsMcsTestKit.Dev, "amsMcs.devEnvironmentId")]
    [InlineData(AmsMcsTestKit.Test, "amsMcs.testEnvironmentId")]
    [InlineData(AmsMcsTestKit.Prod, "amsMcs.prodEnvironmentId")]
    public async Task AnIdTheListDoesNotShowStopsTheRun(string unlisted, string key)
    {
        _msagent.Answer("env list", AmsMcsTestKit.EnvironmentList(AmsMcsTestKit.Dev, [.. new[] { AmsMcsTestKit.Dev, AmsMcsTestKit.Test, AmsMcsTestKit.Prod }.Where(id => id != unlisted)]));

        var (blocked, _) = await RefusedAsync(AmsMcsTestKit.Config());

        Assert.Equal($"The test-ring guard stopped before changing anything: {key} {unlisted} isn't one msagent env list shows; sign in with msagent auth login --cloud test, with a test-ring account.", blocked.Message);
    }

    [Fact]
    public async Task NoActiveEnvironmentStopsTheRun()
    {
        _msagent.Answer("env list", AmsMcsTestKit.EnvironmentList(null, AmsMcsTestKit.Dev, AmsMcsTestKit.Test, AmsMcsTestKit.Prod));

        var (blocked, _) = await RefusedAsync(AmsMcsTestKit.Config());

        Assert.Equal("The test-ring guard stopped before changing anything: no environment is active in msagent.", blocked.Message);
    }

    [Fact]
    public async Task AnActiveEnvironmentOutsideTheThreeStopsTheRun()
    {
        _msagent.Answer("env list", AmsMcsTestKit.EnvironmentList(AmsMcsTestKit.Elsewhere, AmsMcsTestKit.Dev, AmsMcsTestKit.Test, AmsMcsTestKit.Prod, AmsMcsTestKit.Elsewhere));

        var (blocked, ledger) = await RefusedAsync(AmsMcsTestKit.Config());

        Assert.Equal($"The test-ring guard stopped before changing anything: the active environment {AmsMcsTestKit.Elsewhere} isn't one of amsMcs's three.", blocked.Message);
        Assert.Empty(ledger.Entries);
        Assert.Empty(_msagent.CallsTo("env select"));
    }

    [Fact]
    public async Task AForbiddenEnvironmentStopsTheRun()
    {
        _msagent.Answer("env list", AmsMcsTestKit.EnvironmentList(AmsMcsTestKit.Test, AmsMcsTestKit.Dev, AmsMcsTestKit.Test, AmsMcsTestKit.Prod));

        var (blocked, ledger) = await RefusedAsync(AmsMcsTestKit.Config() with { ForbiddenEnvironmentIds = [$" {AmsMcsTestKit.Prod.ToUpperInvariant()} "] });

        Assert.Equal($"The test-ring guard stopped before changing anything: amsMcs.prodEnvironmentId {AmsMcsTestKit.Prod} is in amsMcs.forbiddenEnvironmentIds.", blocked.Message);
        Assert.Empty(ledger.Entries);
        Assert.Empty(_msagent.CallsTo("env select"));
    }

    [Fact]
    public async Task DevAlreadyActiveIsKeptWithoutAnEdit()
    {
        _msagent.Answer("env list", AmsMcsTestKit.EnvironmentList(AmsMcsTestKit.Dev.ToUpperInvariant(), AmsMcsTestKit.Dev.ToUpperInvariant(), AmsMcsTestKit.Test, AmsMcsTestKit.Prod));
        var ledger = Ledger.Create(_scratch.Folder("ledger"), "run-1");

        Assert.Equal(AmsMcsTestKit.Dev, await Services(AmsMcsTestKit.Config()).EnsureRingAsync(ledger, "AM-02", CancellationToken.None));
        Assert.Empty(ledger.Entries);
        Assert.Empty(_msagent.CallsTo("env select"));
    }

    [Fact]
    public async Task AnotherOfTheThreeActiveIsRecordedThenDevIsSelected()
    {
        _msagent.Answer("env list", AmsMcsTestKit.EnvironmentList(AmsMcsTestKit.Test, AmsMcsTestKit.Dev, AmsMcsTestKit.Test, AmsMcsTestKit.Prod, AmsMcsTestKit.Elsewhere));
        var ledger = Ledger.Create(_scratch.Folder("ledger"), "run-1");

        await Services(AmsMcsTestKit.Config()).EnsureRingAsync(ledger, "AM-02", CancellationToken.None);

        var edit = Assert.Single(ledger.Entries);
        var select = Assert.Single(_msagent.CallsTo("env select"));
        Assert.Equal(ResourceKind.ConfigEdit, edit.Kind);
        Assert.Equal("env-select", edit.Id);
        Assert.Equal(AmsMcsTestKit.Test, edit.Cleanup.PriorValue);
        Assert.Equal("AM-02", edit.CreatedBy);
        Assert.Equal(["env", "select", AmsMcsTestKit.Dev, "--non-interactive"], select.Arguments);
        Assert.Equal("AmsMcsAgents", select.Environment["AH_CLI_FEATURES"]);
        Assert.Equal(["env", "list", "--json", "--non-interactive"], _msagent.Calls[0].Arguments);
    }

    [Fact]
    public async Task ASelectionThatFailsStopsTheRunWithThePriorSelectionRecorded()
    {
        _msagent.Answer("env list", AmsMcsTestKit.EnvironmentList(AmsMcsTestKit.Prod, AmsMcsTestKit.Dev, AmsMcsTestKit.Test, AmsMcsTestKit.Prod)).Answer("env select", string.Empty, exitCode: 1);

        var (blocked, ledger) = await RefusedAsync(AmsMcsTestKit.Config());

        Assert.Equal($"The test-ring guard stopped before changing anything: msagent env select {AmsMcsTestKit.Dev} exited 1.", blocked.Message);
        Assert.Equal(AmsMcsTestKit.Prod, Assert.Single(ledger.Entries).Cleanup.PriorValue);
    }

    [Fact]
    public async Task TheGuardReadsTheEnvironmentListOncePerRun()
    {
        _msagent.Answer("env list", AmsMcsTestKit.EnvironmentList(AmsMcsTestKit.Test, AmsMcsTestKit.Dev, AmsMcsTestKit.Test, AmsMcsTestKit.Prod));
        var ledger = Ledger.Create(_scratch.Folder("ledger"), "run-1");
        var services = Services(AmsMcsTestKit.Config());

        await services.EnsureRingAsync(ledger, "AM-02", CancellationToken.None);
        await services.EnsureRingAsync(ledger, "AM-03", CancellationToken.None);

        Assert.Single(_msagent.CallsTo("env list"));
        Assert.Single(_msagent.CallsTo("env select"));
        Assert.Single(ledger.Entries);
    }

    [Fact]
    public async Task AStoppedGuardStopsEveryLaterScenarioWithTheSameReasonWithoutListingAgain()
    {
        _msagent.Answer("env list", AmsMcsTestKit.EnvironmentList(null, AmsMcsTestKit.Dev, AmsMcsTestKit.Test, AmsMcsTestKit.Prod));
        var ledger = Ledger.Create(_scratch.Folder("ledger"), "run-1");
        var services = Services(AmsMcsTestKit.Config());

        var first = await Assert.ThrowsAsync<ScenarioBlockedException>(() => services.EnsureRingAsync(ledger, "AM-02", CancellationToken.None));
        var later = await Assert.ThrowsAsync<ScenarioBlockedException>(() => services.EnsureRingAsync(ledger, "AM-03", CancellationToken.None));

        Assert.Equal(first.Message, later.Message);
        Assert.Equal(first.Cause, later.Cause);
        Assert.Null(later.BlocksCapability);
        Assert.Single(_msagent.CallsTo("env list"));
    }

    [Fact]
    public async Task TheWaitReturnsTheOperationOnceItSucceeds()
    {
        _msagent.Answer("agent operation show", AmsMcsTestKit.Operation("Running")).Answer("agent operation show", AmsMcsTestKit.Operation("succeeded"));
        var project = _scratch.Folder("project");

        var operation = await Services(AmsMcsTestKit.Config()).WaitForOperationAsync(project, "agent-1", "op-1", TimeSpan.FromMinutes(1), TimeSpan.Zero, CancellationToken.None);

        Assert.Equal("succeeded", operation.Status);
        Assert.Equal(2, _msagent.Calls.Count);
        Assert.All(_msagent.Calls, call => Assert.Equal(["agent", "operation", "show", "--project", project, "--agent-id", "agent-1", "--operation-id", "op-1", "--json", "--non-interactive"], call.Arguments));
        Assert.All(_msagent.Calls, call => Assert.Equal(project, call.WorkingDirectory));
        Assert.All(_msagent.Calls, call => Assert.Equal("AmsMcsAgents", call.Environment["AH_CLI_FEATURES"]));
    }

    [Fact]
    public async Task TheWaitFailsWhenTheOperationFails()
    {
        _msagent.Answer("agent operation show", AmsMcsTestKit.Operation("Failed", "DeployFailed"));

        var failure = await Assert.ThrowsAsync<AssertionException>(() => Services(AmsMcsTestKit.Config()).WaitForOperationAsync(_scratch.Path, "agent-1", "op-1", TimeSpan.FromMinutes(1), TimeSpan.Zero, CancellationToken.None));

        Assert.Equal("operation op-1 finishes", failure.Check);
        Assert.Equal("Succeeded", failure.Expected);
        Assert.Equal("Failed", failure.Actual);
        Assert.EndsWith("DeployFailed: the deploy stopped", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWaitStopsAtItsTimeLimit()
    {
        _msagent.Answer("agent operation show", AmsMcsTestKit.Operation("Running"));

        var failure = await Assert.ThrowsAsync<AssertionException>(() => Services(AmsMcsTestKit.Config()).WaitForOperationAsync(_scratch.Path, "agent-1", "op-1", TimeSpan.Zero, TimeSpan.FromMinutes(1), CancellationToken.None));

        Assert.Equal("an end within 0s", failure.Expected);
        Assert.Equal("Running", failure.Actual);
        Assert.Single(_msagent.Calls);
    }

    [Fact]
    public async Task TheWaitFailsWhenTheOperationCannotBeShown()
    {
        _msagent.Answer("agent operation show", "password=hunter2", exitCode: 1);

        var failure = await Assert.ThrowsAsync<AssertionException>(() => Services(AmsMcsTestKit.Config()).WaitForOperationAsync(_scratch.Path, "agent-1", "op-1", TimeSpan.FromMinutes(1), TimeSpan.Zero, CancellationToken.None));

        Assert.Equal("agent operation show to exit 0", failure.Expected);
        Assert.Equal("exit 1", failure.Actual);
        Assert.DoesNotContain("hunter2", failure.Message, StringComparison.Ordinal);
        Assert.Equal($"msagent agent operation show --project {_scratch.Path} --agent-id agent-1 --operation-id op-1 --json --non-interactive", failure.CommandLine);
    }

    private async Task<(ScenarioBlockedException Blocked, Ledger Ledger)> RefusedAsync(AmsMcsConfig config)
    {
        var ledger = Ledger.Create(_scratch.Folder("ledger"), "run-1");
        var blocked = await Assert.ThrowsAsync<ScenarioBlockedException>(() => Services(config).EnsureRingAsync(ledger, "AM-02", CancellationToken.None));

        Assert.Equal("the test ring", blocked.Cause);
        Assert.Null(blocked.BlocksCapability);
        return (blocked, ledger);
    }

    private AmsMcsServices Services(AmsMcsConfig config)
    {
        var transcript = new Transcript(_scratch.Folder("run"));
        return new AmsMcsServices(config, "run-1", _msagent.Cli(transcript, _scratch.Path), transcript, new FakeTokens());
    }
}
