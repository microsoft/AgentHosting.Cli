using System.Text;
using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>Writes the console view while a run proceeds, then the summary, markdown, and JSON.</summary>
public sealed class Reporter
{
    private readonly bool _useColour;
    private string? _currentFeature;

    public Reporter(bool useColour) => _useColour = useColour;

    /// <summary>Prints one finished scenario, grouped under its feature.</summary>
    public void Line(ScenarioResult result)
    {
        if (!string.Equals(_currentFeature, result.Scenario.Feature, StringComparison.Ordinal))
        {
            _currentFeature = result.Scenario.Feature;
            Console.WriteLine(_currentFeature);
        }

        var duration = result.Outcome is Outcome.Skip or Outcome.Blocked ? "  —" : $"{result.Duration.TotalSeconds,3:N0}s";
        var reason = result.Outcome is Outcome.Skip or Outcome.Blocked ? $"   {result.Reason}" : string.Empty;

        Console.Write($"  {result.Scenario.Id,-6} {Truncate(result.Scenario.Title, 48),-48} ");
        WriteOutcome(result.Outcome);
        Console.WriteLine($" {duration}{reason}");
    }

    /// <summary>Prints the totals, the failures in full, the collapsed blocked causes, and the ledger.</summary>
    public void Summary(RunReport report, IReadOnlyList<LedgerEntry> ledger)
    {
        Console.WriteLine();
        WriteTotals(report);
        WriteFailures(report);
        WriteBlocked(report);
        WriteLedger(ledger);
    }

    private static void WriteTotals(RunReport report)
    {
        var totals = report.Totals;
        Console.WriteLine($"  {totals.Passed} passed · {totals.Failed} failed · {totals.Blocked} blocked · {totals.Skipped} skipped · {totals.Errors} errors        {Format(report.Duration)}");
    }

    private void WriteFailures(RunReport report)
    {
        foreach (var failure in report.Scenarios.Where(scenario => scenario.Outcome is Outcome.Fail or Outcome.Error))
        {
            Console.WriteLine();
            WriteColoured($"{failure.Outcome.ToString().ToUpperInvariant()}  {failure.Scenario.Id}  {failure.Scenario.Title}", ConsoleColor.Red);

            if (failure.CommandLine is { Length: > 0 } command)
            {
                Console.WriteLine();
                Console.WriteLine($"  {Redaction.Apply(command)}");
            }

            Console.WriteLine();

            if (failure.Expected is { Length: > 0 } && failure.Actual is { Length: > 0 })
            {
                Console.WriteLine($"  expected  {failure.Expected}");
                Console.WriteLine($"  actual    {failure.Actual}");
            }
            else if (failure.Reason is { Length: > 0 } reason)
            {
                Console.WriteLine($"  {reason}");
            }

            if (failure.TranscriptPath is { Length: > 0 } transcript)
            {
                Console.WriteLine();
                Console.WriteLine($"  transcript  {transcript}");
            }

            if (failure.RetainedWorkspace is { Length: > 0 } workspace && Directory.Exists(workspace))
            {
                Console.WriteLine($"  workspace   {workspace}   (retained)");
            }
        }
    }

    private void WriteBlocked(RunReport report)
    {
        var groups = report.Scenarios
            .Where(scenario => scenario.Outcome == Outcome.Blocked)
            .GroupBy(scenario => scenario.BlockedCause ?? scenario.Reason ?? "unknown")
            .ToArray();

        foreach (var group in groups)
        {
            var ids = group.Select(scenario => scenario.Scenario.Id).ToArray();
            var heading = ids.Length == 1 ? ids[0] : $"{ids[0]}…{ids[^1]}  ({ids.Length} scenarios)";

            Console.WriteLine();
            WriteColoured($"BLOCKED  {heading}", ConsoleColor.Yellow);
            Console.WriteLine($"  {group.First().Reason}");
        }
    }

    private void WriteLedger(IReadOnlyList<LedgerEntry> ledger)
    {
        if (ledger.Count == 0)
        {
            return;
        }

        var orphans = ledger.Where(entry => entry.State == ResourceState.Orphaned).ToArray();
        var removed = ledger.Where(entry => entry.State is ResourceState.Deleted or ResourceState.Restored).ToArray();
        var retained = ledger.Where(entry => entry.State == ResourceState.Retained).ToArray();

        Console.WriteLine();

        if (removed.Length > 0)
        {
            Console.WriteLine("  Created and removed");

            foreach (var entry in removed)
            {
                Console.WriteLine($"    {entry.Kind,-14} {Truncate(entry.DisplayName ?? entry.Id, 38),-38} {entry.State.ToString().ToLowerInvariant()}");
            }
        }

        if (retained.Length > 0)
        {
            Console.WriteLine();
            WriteColoured($"  Left in the environment, no msagent delete exists  ({retained.Length})", ConsoleColor.Yellow);

            foreach (var entry in retained)
            {
                Console.WriteLine($"    {entry.DisplayName,-20} {entry.Id}   {entry.Cleanup.EnvironmentId}");
            }
        }

        if (orphans.Length == 0)
        {
            Console.WriteLine(retained.Length == 0 ? "  Nothing left behind." : "  No orphans.");
            return;
        }

        Console.WriteLine();
        WriteColoured($"  Created and NOT removed  ← {orphans.Length} orphan{(orphans.Length == 1 ? string.Empty : "s")}", ConsoleColor.Red);

        foreach (var entry in orphans)
        {
            Console.WriteLine($"    {entry.Kind,-14} {entry.DisplayName ?? entry.Id}   {entry.Failure}");
            Console.WriteLine($"    {Cleanup.ManualCommand(entry)}");
        }
    }

    private void WriteOutcome(Outcome outcome)
    {
        var (text, colour) = outcome switch
        {
            Outcome.Pass => ("PASS   ", ConsoleColor.Green),
            Outcome.Fail => ("FAIL   ", ConsoleColor.Red),
            Outcome.Skip => ("SKIP   ", ConsoleColor.DarkGray),
            Outcome.Blocked => ("BLOCKED", ConsoleColor.Yellow),
            _ => ("ERROR  ", ConsoleColor.Magenta),
        };

        if (!_useColour)
        {
            Console.Write(text);
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }

    private void WriteColoured(string text, ConsoleColor colour)
    {
        if (!_useColour)
        {
            Console.WriteLine(text);
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    /// <summary>
    /// Writes the machine-readable report and the markdown one.
    /// </summary>
    public static void Persist(string runDirectory, RunReport report, IReadOnlyList<LedgerEntry> ledger)
    {
        var redacted = report with { Scenarios = [.. report.Scenarios.Select(Redact)] };

        File.WriteAllText(Path.Combine(runDirectory, "report.json"), JsonSerializer.Serialize(redacted, ConfigJson.Options));
        File.WriteAllText(Path.Combine(runDirectory, "report.md"), Markdown(redacted, ledger.Select(Redact).ToArray()));
    }

    /// <summary>One scenario's result with every free-form field masked.</summary>
    private static ScenarioResult Redact(ScenarioResult result) => result with
    {
        Reason = Mask(result.Reason),
        Expected = Mask(result.Expected),
        Actual = Mask(result.Actual),
        CommandLine = Mask(result.CommandLine),
        BlockedCause = Mask(result.BlockedCause),
        PassedChecks = [.. result.PassedChecks.Select(Redaction.Apply)],
    };

    /// <summary>
    /// One ledger entry with every free-form field masked.
    /// </summary>
    private static LedgerEntry Redact(LedgerEntry entry) => entry with
    {
        Id = Redaction.Apply(entry.Id),
        DisplayName = Mask(entry.DisplayName),
        Failure = Mask(entry.Failure),
    };

    private static string? Mask(string? value) => value is null ? null : Redaction.Apply(value);

    private static string Markdown(RunReport report, IReadOnlyList<LedgerEntry> ledger)
    {
        var markdown = new StringBuilder()
            .AppendLine($"# CLI e2e — {report.RunId}")
            .AppendLine()
            .AppendLine($"`{report.Binary.Origin}` · version `{report.Binary.Version}` · {Format(report.Duration)}")
            .AppendLine()
            .AppendLine($"**{report.Totals.Passed} passed · {report.Totals.Failed} failed · {report.Totals.Blocked} blocked · {report.Totals.Skipped} skipped · {report.Totals.Errors} errors**")
            .AppendLine()
            .AppendLine("| Id | Scenario | Outcome | Time |")
            .AppendLine("|---|---|---|---|");

        foreach (var scenario in report.Scenarios)
        {
            var duration = scenario.Outcome is Outcome.Skip or Outcome.Blocked ? "—" : $"{scenario.Duration.TotalSeconds:N0}s";
            markdown.AppendLine($"| {scenario.Scenario.Id} | {scenario.Scenario.Title} | {scenario.Outcome.ToString().ToUpperInvariant()} | {duration} |");
        }

        AppendFailures(markdown, report);
        AppendLedger(markdown, ledger);

        return markdown.ToString();
    }

    private static void AppendFailures(StringBuilder markdown, RunReport report)
    {
        var failures = report.Scenarios.Where(scenario => scenario.Outcome is Outcome.Fail or Outcome.Error).ToArray();

        if (failures.Length == 0)
        {
            return;
        }

        markdown.AppendLine().AppendLine("## Failures").AppendLine();

        foreach (var failure in failures)
        {
            markdown.AppendLine($"### {failure.Scenario.Id} — {failure.Scenario.Title}").AppendLine();

            if (failure.CommandLine is { Length: > 0 } command)
            {
                markdown.AppendLine("```").AppendLine(Redaction.Apply(command)).AppendLine("```").AppendLine();
            }

            markdown.AppendLine($"- expected: {failure.Expected ?? "—"}");
            markdown.AppendLine($"- actual: {failure.Actual ?? "—"}");
            markdown.AppendLine($"- {failure.Reason}").AppendLine();
        }
    }

    private static void AppendLedger(StringBuilder markdown, IReadOnlyList<LedgerEntry> ledger)
    {
        if (ledger.Count == 0)
        {
            return;
        }

        markdown.AppendLine().AppendLine("## Resources").AppendLine().AppendLine("| Kind | Name | State |").AppendLine("|---|---|---|");

        foreach (var entry in ledger)
        {
            markdown.AppendLine($"| {entry.Kind} | {entry.DisplayName ?? entry.Id} | {entry.State.ToString().ToLowerInvariant()} |");
        }
    }

    private static string Format(TimeSpan duration) =>
        duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s" : $"{duration.Minutes}m {duration.Seconds:00}s";

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";
}
