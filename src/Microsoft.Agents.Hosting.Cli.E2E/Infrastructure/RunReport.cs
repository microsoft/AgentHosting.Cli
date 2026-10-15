namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>The counts shown in the summary and diffed between runs.</summary>
public sealed record RunTotals(int Passed, int Failed, int Blocked, int Skipped, int Errors);

/// <summary>The run's identity, so a report can never be mistaken for one from another build.</summary>
public sealed record BinaryDescription(string Path, string Origin, string Version);

/// <summary>The complete result of one run, written to <c>report.json</c>.</summary>
public sealed record RunReport
{
    public required string RunId { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public required TimeSpan Duration { get; init; }

    public required BinaryDescription Binary { get; init; }

    public required RunTotals Totals { get; init; }

    public required IReadOnlyList<ScenarioResult> Scenarios { get; init; }

    public IReadOnlyList<LedgerEntry> Ledger { get; init; } = [];

    /// <summary>Builds the report from the finished results.</summary>
    public static RunReport From(string runId, DateTimeOffset startedUtc, TimeSpan duration, MsagentBinary binary, IReadOnlyList<ScenarioResult> results, IReadOnlyList<LedgerEntry> ledger) =>
        new()
        {
            RunId = runId,
            StartedUtc = startedUtc,
            Duration = duration,
            Binary = new BinaryDescription(binary.Path, binary.Origin, binary.Version),
            Totals = new RunTotals(
                Count(results, Outcome.Pass),
                Count(results, Outcome.Fail),
                Count(results, Outcome.Blocked),
                Count(results, Outcome.Skip),
                Count(results, Outcome.Error)),
            Scenarios = results,
            Ledger = ledger,
        };

    /// <summary>The process exit code, highest applicable wins.</summary>
    public int ExitCode(bool hasOrphans) =>
        Totals.Errors > 0 || Totals.Blocked > 0 ? 4
        : hasOrphans ? 3
        : Totals.Failed > 0 ? 1
        : 0;

    private static int Count(IReadOnlyList<ScenarioResult> results, Outcome outcome) => results.Count(result => result.Outcome == outcome);
}
