using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class RunnerResilienceTests : IDisposable
{
    private readonly string _runDirectory = Path.Combine(Path.GetTempPath(), "msagent-resilience-" + Guid.NewGuid().ToString("N"));

    public RunnerResilienceTests() => Directory.CreateDirectory(_runDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_runDirectory))
        {
            Directory.Delete(_runDirectory, recursive: true);
        }
    }

    private RunContext Context(AmsMcsServices? amsMcs = null, E2EConfig? configuration = null)
    {
        var config = configuration ?? new E2EConfig();
        var transcript = new Transcript(_runDirectory);
        var msagent = new MsagentCli("msagent", transcript, Path.Combine(_runDirectory, "dumps"));
        var azure = new AzureCli(transcript);

        return new RunContext
        {
            RunId = "test-run",
            RunSuffix = "test",
            RunDirectory = _runDirectory,
            WorkspaceRoot = Path.Combine(_runDirectory, "ws"),
            Config = config,
            Capabilities = Capabilities.Resolve(config, _runDirectory, signedIn: false, "not signed in", azureCliPresent: false),
            Binary = new MsagentBinary("msagent", "test", "test"),
            Msagent = msagent,
            Azure = azure,
            Cloud = new CloudProbe(msagent, azure),
            Ledger = Ledger.Create(_runDirectory, "test-run"),
            Workspaces = new Workspaces(_runDirectory, Path.Combine(_runDirectory, "ws"), config, "test"),
            Session = new SessionResources(),
            Transcript = transcript,
            Reporter = new Reporter(useColour: false),
            AmsMcs = amsMcs,
        };
    }

    private static Scenario ScenarioThat(Func<ScenarioContext, Task> run) => new()
    {
        Id = "TR-01",
        Title = "a scenario under test",
        Feature = "mcs-agent",
        Requires = [],
        Tier = SafetyTier.Creating,
        Run = run,
    };

    [Fact]
    public async Task AScenarioThrowingIsReportedWithoutEndingTheRun()
    {
        var second = false;
        var scenarios = new[]
        {
            ScenarioThat(_ => throw new InvalidOperationException("boom")),
            ScenarioThat(_ => { second = true; return Task.CompletedTask; }),
        };

        var results = await new Runner(Context(), TimeSpan.FromMinutes(1)).RunAsync(scenarios, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(Outcome.Error, results[0].Outcome);
        Assert.True(second);
    }

    [Fact]
    public async Task ResourcesRecordedBeforeAFailureRemainInTheLedgerForCleanup()
    {
        var run = Context();
        var scenario = ScenarioThat(context =>
        {
            context.Ledger.RecordLocalWorkspace(Path.Combine(_runDirectory, "ws", "leaked"), context.ScenarioId);
            throw new InvalidOperationException("boom");
        });

        await new Runner(run, TimeSpan.FromMinutes(1)).RunAsync([scenario], CancellationToken.None);

        Assert.Contains(run.Ledger.Entries, entry => entry.Kind == ResourceKind.LocalWorkspace);
    }

    [Fact]
    public async Task AScenarioSeesTheRunsAmsServices()
    {
        var transcript = new Transcript(_runDirectory);
        var services = new AmsMcsServices(new AmsMcsConfig(), "test-run", new MsagentCli("msagent", transcript, Path.Combine(_runDirectory, "dumps")), transcript, new FakeTokens());
        AmsMcsServices? seen = null;

        await new Runner(Context(services), TimeSpan.FromMinutes(1)).RunAsync([ScenarioThat(context => { seen = context.AmsMcs; return Task.CompletedTask; })], CancellationToken.None);

        Assert.Same(services, seen);
    }

    [Fact]
    public async Task AStoppedRingGuardBlocksEveryLaterAmsScenarioWithItsOwnReasonAndListsOnce()
    {
        var config = new E2EConfig { AmsMcs = AmsMcsTestKit.Config() };
        var transcript = new Transcript(_runDirectory);
        var msagent = new FakeMsagent().Answer("env list", string.Empty, exitCode: 1);
        var services = new AmsMcsServices(config.AmsMcs, "test-run", msagent.Cli(transcript, _runDirectory), transcript, new FakeTokens());
        var scenario = ScenarioThat(context => context.AmsMcs!.EnsureRingAsync(context.Ledger, context.ScenarioId, context.CancellationToken)) with { Feature = AmsMcsServices.Feature, Requires = [Capability.AmsMcs, Capability.AmsMcsRing] };

        var results = await new Runner(Context(services, config), TimeSpan.FromMinutes(1)).RunAsync([scenario with { Id = "AM-02" }, scenario with { Id = "AM-03" }], CancellationToken.None);

        Assert.Equal([Outcome.Blocked, Outcome.Blocked], results.Select(result => result.Outcome));
        Assert.All(results, result => Assert.Equal("The test-ring guard stopped before changing anything: msagent env list exited 1.", result.Reason));
        Assert.All(results, result => Assert.Equal("the test ring", result.BlockedCause));
        Assert.Single(msagent.CallsTo("env list"));
    }

    [Fact]
    public async Task AHungCommandEndsAsAnErrorNamingTheCommandAndItsTimeLimit()
    {
        var script = AmsMcsTestKit.Script(Directory.CreateDirectory(Path.Combine(_runDirectory, "bin")).FullName, "msagent", "ping -n 6 127.0.0.1 >nul\r\n", "sleep 5\n");
        var hung = new MsagentCli(script, new Transcript(_runDirectory), Path.Combine(_runDirectory, "dumps"));

        var result = Assert.Single(await new Runner(Context(), TimeSpan.FromMinutes(1)).RunAsync([ScenarioThat(_ => hung.RunAsync(["agent", "create", "--agent-name", "hung"], timeout: TimeSpan.FromSeconds(1)))], CancellationToken.None));

        Assert.Equal(Outcome.Error, result.Outcome);
        Assert.Equal($"Timed out after 1s: {script} agent create --agent-name hung", result.Reason);
    }
}
