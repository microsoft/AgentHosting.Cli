using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class RunnerTimeoutTests : IDisposable
{
    private readonly string _runDirectory = Path.Combine(Path.GetTempPath(), "msagent-runner-" + Guid.NewGuid().ToString("N"));

    public RunnerTimeoutTests() => Directory.CreateDirectory(_runDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_runDirectory))
        {
            Directory.Delete(_runDirectory, recursive: true);
        }
    }

    private RunContext Context()
    {
        var config = new E2EConfig();
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
        };
    }

    private static Scenario ScenarioThat(Func<ScenarioContext, Task> run) => new()
    {
        Id = "TT-01",
        Title = "a scenario under test",
        Feature = "mcs-agent",
        Requires = [],
        Tier = SafetyTier.ReadOnly,
        Run = run,
    };

    [Fact]
    public async Task AScenarioThatExceedsItsBoundIsReportedRatherThanHangingTheRun()
    {
        var scenario = ScenarioThat(_ => Task.Delay(TimeSpan.FromMinutes(5), CancellationToken.None));

        var results = await new Runner(Context(), TimeSpan.FromMilliseconds(150)).RunAsync([scenario], CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(Outcome.Error, result.Outcome);
        Assert.Contains("bound", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheScenarioReceivesATokenThatCancelsAtTheBound()
    {
        CancellationToken observed = default;
        var scenario = ScenarioThat(async context =>
        {
            observed = context.CancellationToken;
            await Task.Delay(TimeSpan.FromMinutes(5), context.CancellationToken);
        });

        await new Runner(Context(), TimeSpan.FromMilliseconds(150)).RunAsync([scenario], CancellationToken.None);

        Assert.True(observed.CanBeCanceled);
        Assert.True(observed.IsCancellationRequested);
    }

    [Fact]
    public async Task AScenarioInsideItsBoundStillPasses()
    {
        var scenario = ScenarioThat(_ => Task.CompletedTask);

        var results = await new Runner(Context(), TimeSpan.FromMinutes(1)).RunAsync([scenario], CancellationToken.None);

        Assert.Equal(Outcome.Pass, Assert.Single(results).Outcome);
    }

    /// <summary>
    /// Scenarios share one deployed agent, and a sharing scenario removes the grant it made as it
    /// unwinds. The run therefore waits for a timed-out scenario to finish rather than letting its
    /// cleanup land in the middle of the next one.
    /// </summary>
    [Fact]
    public async Task ATimedOutScenarioFinishesUnwindingBeforeTheRunAdvances()
    {
        var cleanedUp = false;
        var scenario = ScenarioThat(async context =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), context.CancellationToken);
            }
            finally
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None);
                cleanedUp = true;
            }
        });

        var results = await new Runner(Context(), TimeSpan.FromSeconds(2)).RunAsync([scenario], CancellationToken.None);

        Assert.Equal(Outcome.Error, Assert.Single(results).Outcome);
        Assert.True(cleanedUp);
    }
}
