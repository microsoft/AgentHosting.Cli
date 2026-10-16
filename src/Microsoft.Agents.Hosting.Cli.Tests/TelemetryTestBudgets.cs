namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Generous telemetry budgets for tests that assert delivery. The production defaults (250 ms policy,
/// 1 s flush) intentionally drop telemetry when exceeded, which makes delivery assertions flaky under
/// CI thread-pool contention. Tests that exercise deadlines pass their own explicit small budgets.
/// </summary>
internal static class TelemetryTestBudgets
{
    internal static readonly TimeSpan Policy = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan Flush = TimeSpan.FromSeconds(30);
}
