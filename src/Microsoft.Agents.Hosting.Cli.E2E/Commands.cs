using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

namespace Microsoft.Agents.Hosting.Cli.E2E;

/// <summary>What each harness command does, once its arguments are parsed.</summary>
public static class Commands
{
    private const int ExitConfiguration = 2;

    /// <summary>Resolves capabilities, primes consent, and describes the run without executing any of it.</summary>
    public static async Task<int> PlanAsync(CommandOptions options, CancellationToken cancellationToken)
    {
        var environment = await HarnessEnvironment.PrepareAsync(options, plan: true, cancellationToken).ConfigureAwait(false);

        if (environment is null)
        {
            return ExitConfiguration;
        }

        Describe(environment, options);
        return 0;
    }

    /// <summary>Runs the scheduled scenarios, then cleans up from the ledger.</summary>
    public static async Task<int> RunAsync(CommandOptions options, CancellationToken cancellationToken)
    {
        var environment = await HarnessEnvironment.PrepareAsync(options, plan: false, cancellationToken).ConfigureAwait(false);

        if (environment is null)
        {
            return ExitConfiguration;
        }

        Describe(environment, options);

        if (!Confirm(options, environment))
        {
            return ExitConfiguration;
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<ScenarioResult> results = [];
        IReadOnlyList<LedgerEntry> orphans;

        try
        {
            results = await new Runner(environment.Run).RunAsync(environment.Scheduled, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stopwatch.Stop();
            orphans = await ReleaseResourcesAsync(environment, cancellationToken).ConfigureAwait(false);
        }

        RemovePassingWorkspaces(results, environment.Run);

        var report = RunReport.From(environment.Run.RunId, startedUtc, stopwatch.Elapsed, environment.Run.Binary, results, environment.Run.Ledger.Entries);
        environment.Run.Reporter.Summary(report, environment.Run.Ledger.Entries);
        Reporter.Persist(environment.Run.RunDirectory, report, environment.Run.Ledger.Entries);

        if (environment.Run.Standalone is { } standalone)
        {
            await standalone.FinishAsync(environment.Run.RunSuffix, cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine($"  {environment.Run.RunDirectory}");

        return report.ExitCode(orphans.Count > 0);
    }

    /// <summary>Releases everything the run created, reporting whatever could not be removed.</summary>
    /// <param name="environment">The prepared run.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    private static async Task<IReadOnlyList<LedgerEntry>> ReleaseResourcesAsync(HarnessEnvironment environment, CancellationToken cancellationToken)
    {
        var cleanup = new Cleanup(environment.Run.Msagent, environment.Run.Azure, environment.Run.Cloud, environment.Run.Transcript, environment.Run.Standalone);

        return await cleanup.RunAsync(environment.Run.Ledger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes whatever a previous run left behind.</summary>
    public static async Task<int> CleanupAsync(CommandOptions options, CancellationToken cancellationToken)
    {
        if (options.RunId is not { Length: > 0 } runId)
        {
            Console.Error.WriteLine("cleanup needs --run-id.");
            return ExitConfiguration;
        }

        var runDirectory = Path.Combine(HarnessEnvironment.RunsRoot, runId);
        var transcript = new Transcript(runDirectory);
        var ledger = Ledger.Open(runDirectory, runId);
        var config = E2EConfig.Load(HarnessEnvironment.ProjectDirectory);
        var binary = options.MsagentPath ?? HarnessEnvironment.InstalledBinary(runId);

        if (binary is null)
        {
            Console.Error.WriteLine($"No binary for run {runId}; pass --msagent-path so agent deletion can run.");
            return ExitConfiguration;
        }

        var msagent = new MsagentCli(binary, transcript, CrashDumps.DirectoryFor(runDirectory));
        var azure = new AzureCli(transcript);
        var standalone = await StandaloneForCleanupAsync(ledger, config, runDirectory, transcript, cancellationToken).ConfigureAwait(false);
        var cleanup = new Cleanup(msagent, azure, new CloudProbe(msagent, azure), transcript, standalone);
        var orphans = await cleanup.RunAsync(ledger, cancellationToken).ConfigureAwait(false);

        new Reporter(!options.NoColour).Summary(
            RunReport.From(runId, DateTimeOffset.UtcNow, TimeSpan.Zero, new MsagentBinary(binary, "cleanup", "unknown"), [], ledger.Entries),
            ledger.Entries);

        _ = config;
        return orphans.Count > 0 ? 3 : 0;
    }

    private static async Task<StandaloneServices?> StandaloneForCleanupAsync(Ledger ledger, E2EConfig config, string runDirectory, Transcript transcript, CancellationToken cancellationToken)
    {
        if (!ledger.Outstanding.Any(entry => entry.Kind is ResourceKind.StandaloneWorkflow or ResourceKind.StandaloneConnectionReference))
        {
            return null;
        }

        var standalone = new StandaloneServices(config, runDirectory, transcript, Path.Combine(HarnessEnvironment.ProjectDirectory, "fixtures", Capabilities.StandaloneFixturesFolder));
        var probed = await standalone.ProbeAsync(
            Capabilities.Resolve(config, Path.Combine(HarnessEnvironment.ProjectDirectory, "fixtures"), signedIn: true, "signed in", AzureCli.IsPresent()),
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"Standalone-workflow oracle: {probed.Reason(Capability.StandaloneOracle)}");
        return standalone;
    }

    /// <summary>Re-prints a previous run from its persisted report.</summary>
    public static int Report(CommandOptions options)
    {
        if (options.RunId is not { Length: > 0 } runId)
        {
            Console.Error.WriteLine("report needs --run-id.");
            return ExitConfiguration;
        }

        var path = Path.Combine(HarnessEnvironment.RunsRoot, runId, "report.json");

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No report at {path}.");
            return ExitConfiguration;
        }

        var report = JsonSerializer.Deserialize<RunReport>(File.ReadAllText(path), ConfigJson.Options)
            ?? throw new E2EException($"The report at {path} is empty.");

        var reporter = new Reporter(!options.NoColour);

        foreach (var scenario in report.Scenarios)
        {
            reporter.Line(scenario);
        }

        reporter.Summary(report, report.Ledger);
        return report.ExitCode(report.Ledger.Any(entry => entry.State == ResourceState.Orphaned));
    }

    private static void Describe(HarnessEnvironment environment, CommandOptions options)
    {
        Console.WriteLine();
        Console.WriteLine("Capabilities");

        var standaloneScheduled = environment.Run.Standalone is not null;

        foreach (var state in environment.Run.Capabilities.All.Where(state => standaloneScheduled || !IsStandalone(state.Capability)))
        {
            var mark = state.Available ? "yes" : "NO ";
            Console.WriteLine($"  {Name(state.Capability),-18} {mark}   {state.Detail}");
        }

        var runnable = environment.Scheduled
            .Where(scenario => environment.Run.Capabilities.FirstMissing(scenario.Requires) is null)
            .ToArray();

        var skipped = environment.Scheduled.Length - runnable.Length;

        Console.WriteLine();
        Console.WriteLine($"Will run {runnable.Length} scenarios  (readonly {Count(runnable, SafetyTier.ReadOnly)}, reversible {Count(runnable, SafetyTier.Reversible)}, creating {Count(runnable, SafetyTier.Creating)})");
        Console.WriteLine($"Will skip {skipped} scenarios");

        foreach (var scenario in environment.Scheduled.Except(runnable).Take(8))
        {
            var missing = environment.Run.Capabilities.FirstMissing(scenario.Requires)!.Value;
            Console.WriteLine($"  {scenario.Id,-6} {Truncate(scenario.Title, 38),-38} needs {Name(missing)}");
        }

        if (skipped > 8)
        {
            Console.WriteLine($"  … and {skipped - 8} more");
        }

        Console.WriteLine();
        Console.WriteLine($"Binary   {environment.Run.Binary.Origin} — {environment.Run.Binary.Path}");
        Console.WriteLine($"Run      {environment.Run.RunDirectory}");

        if (options.Command == "plan")
        {
            Console.WriteLine();
            Console.WriteLine("Nothing has been executed. Run `run` to execute.");
        }
    }

    private static bool Confirm(CommandOptions options, HarnessEnvironment environment)
    {
        var creating = environment.Scheduled.Count(scenario => scenario.Tier == SafetyTier.Creating);

        if (options.Yes || creating == 0)
        {
            return true;
        }

        Console.WriteLine();
        Console.Write($"{creating} scenarios create real resources. Continue? [y/N] ");

        return Console.ReadLine()?.Trim() is "y" or "Y" or "yes";
    }

    private static void RemovePassingWorkspaces(IReadOnlyList<ScenarioResult> results, RunContext run)
    {
        foreach (var result in results.Where(candidate => candidate.Outcome == Outcome.Pass))
        {
            var directory = Path.Combine(run.WorkspaceRoot, result.Scenario.Id);

            foreach (var candidate in EnumerateScenarioDirectories(run.WorkspaceRoot, result.Scenario.Id, directory))
            {
                TryDelete(candidate);
            }
        }
    }

    private static IEnumerable<string> EnumerateScenarioDirectories(string workspaceRoot, string scenarioId, string exact) =>
        Directory.Exists(workspaceRoot)
            ? Directory.EnumerateDirectories(workspaceRoot, $"{scenarioId}*", SearchOption.TopDirectoryOnly)
            : [exact];

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static int Count(IReadOnlyList<Scenario> scenarios, SafetyTier tier) => scenarios.Count(scenario => scenario.Tier == tier);

    private static string Name(Capability capability) => capability switch
    {
        Capability.McsDev => "mcs.dev",
        Capability.McsTest => "mcs.test",
        Capability.McsShareUser => "mcs.shareUser",
        Capability.WorkspaceCli => "workspace.cli",
        Capability.WorkspaceClassic => "workspace.classic",
        Capability.WorkspaceWorkflow => "workspace.workflow",
        Capability.AzureRead => "azure.read",
        Capability.AzureCreate => "azure.create",
        Capability.AzureExistingBot => "azure.existingBot",
        Capability.StandaloneFixtures => "sw.fixtures",
        Capability.StandaloneOracle => "sw.oracle",
        Capability.StandaloneFlowRuns => "sw.flowRuns",
        _ => capability.ToString().ToLowerInvariant(),
    };

    private static bool IsStandalone(Capability capability) =>
        capability is Capability.StandaloneFixtures or Capability.StandaloneOracle or Capability.StandaloneFlowRuns;

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";
}
