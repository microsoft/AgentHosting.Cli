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

    private RunContext Context(string msagentPath = "msagent")
    {
        var config = new E2EConfig();
        var transcript = new Transcript(_runDirectory);
        var msagent = new MsagentCli(msagentPath, transcript, Path.Combine(_runDirectory, "dumps"));
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
    public async Task OnlyAScenarioThatNeedsARolloutFeatureRunsMsagentWithIt()
    {
        var fake = new FakeMsagent(Path.Combine(_runDirectory, "msagent"), "{}");
        var scenarios = new[]
        {
            ScenarioThat(context => context.Msagent.RunAsync(["abs", "preview"])) with { MsagentFeature = MsagentFeatures.AzureBotService },
            ScenarioThat(context => context.Msagent.RunAsync(["auth", "status"])),
        };

        var results = await new Runner(Context(fake.ExecutablePath), TimeSpan.FromMinutes(1)).RunAsync(scenarios, CancellationToken.None);

        var inheritedEndpoints = $"{Environment.GetEnvironmentVariable("AH_CLI_AMS_ENDPOINT")}|{Environment.GetEnvironmentVariable("AH_CLI_AMSDP_ENDPOINT")}";
        Assert.All(results, result => Assert.Equal(Outcome.Pass, result.Outcome));
        Assert.Equal(new[] { $"{MsagentFeatures.AzureBotService}|{inheritedEndpoints}", $"{Environment.GetEnvironmentVariable(MsagentFeatures.Variable)}|{inheritedEndpoints}" }, fake.Environments);
    }
}
