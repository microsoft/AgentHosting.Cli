using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios.StandaloneWorkflow;

public static class StandaloneWorkflowScenarios
{
    public const string Feature = "mcs-standaloneworkflow";

    public const string ChaosPack = "swf-chaos";

    public static IEnumerable<Scenario> All() =>
    [
        .. SwAuthoring.All(),
        .. SwDeployMatrix.All(),
        .. SwRoundTrip.All(),
        .. SwListing.All(),
        .. SwDelete.All(),
        .. SwUnsupported.All(),
    ];
}

public static class SwScenario
{
    private static readonly ScenarioBuilder Builder = new(StandaloneWorkflowScenarios.Feature);

    internal static readonly Capability[] Local = [Capability.Auth, Capability.McsDev, Capability.StandaloneFixtures];

    internal static readonly Capability[] Cloud = [Capability.Auth, Capability.McsDev, Capability.StandaloneFixtures, Capability.StandaloneOracle];

    internal static readonly Capability[] CrossEnvironment = [Capability.Auth, Capability.McsDev, Capability.McsTest, Capability.StandaloneFixtures, Capability.StandaloneOracle];

    internal static readonly Capability[] FlowRun = [Capability.Auth, Capability.McsDev, Capability.StandaloneFixtures, Capability.StandaloneOracle, Capability.StandaloneFlowRuns];

    public static Scenario Make(string id, string title, IReadOnlyList<Capability> requires, SafetyTier tier, Func<SwRun, Task> body, bool smoke = false, string? pack = null) =>
        Builder.Make(id, title, requires, tier, async context =>
        {
            var run = new SwRun(context);

            try
            {
                await body(run).ConfigureAwait(false);
            }
            catch (AssertionException hard) when (run.HasFailedExpectations)
            {
                throw run.Combined(hard);
            }

            run.Conclude();
        }, smoke, pack);
}

public sealed class SwRun
{
    private readonly List<StandaloneFinding> _failed = [];
    private MsagentResult? _lastFailedResult;
    private int _materialized;

    public SwRun(ScenarioContext context)
    {
        Context = context;
        Services = context.Standalone
            ?? throw new E2EException($"{context.ScenarioId} needs the standalone-workflow services, which only a run scheduling {StandaloneWorkflowScenarios.Feature} prepares.");
    }

    public ScenarioContext Context { get; }

    public StandaloneServices Services { get; }

    public string Id => Context.ScenarioId;

    public string Key => Id.StartsWith("SW-", StringComparison.Ordinal) ? Id[3..] : Id;

    public Assertions Assert => Context.Assert;

    public string Dev => Context.DevEnvironmentId;

    public string Test => Context.TestEnvironmentId;

    public CancellationToken Token => Context.CancellationToken;

    public bool Oracle => Services.OracleAvailable;

    public bool HasFailedExpectations => _failed.Count > 0;

    public int NextIndex() => Interlocked.Increment(ref _materialized);

    public void Expect(bool holds, string check, string expected, string actual, string severity = "P1", string? reference = null, MsagentResult? result = null)
    {
        if (holds)
        {
            Assert.True(true, check);
            return;
        }

        var finding = new StandaloneFinding
        {
            ScenarioId = Id,
            Severity = severity,
            Check = check,
            Expected = expected,
            Actual = Clip(actual, 400),
            Reference = reference,
            Failed = true,
        };

        _failed.Add(finding);
        _lastFailedResult = result ?? _lastFailedResult;
        Services.AddFinding(finding);
        Context.Transcript.Note($"EXPECTATION NOT MET [{severity}] {check}: expected {expected}, got {finding.Actual}{(result is null ? string.Empty : $" ({result.CommandLine})")}");
    }

    public void ExpectExit(MsagentResult result, int expected, string check, string severity = "P1", string? reference = null) =>
        Expect(result.ExitCode == expected, check, $"exit {expected}", $"exit {result.ExitCode}: {Tail(result)}", severity, reference, result);

    public void ExpectRefused(MsagentResult result, string check, string severity = "P1", string? reference = null) =>
        Expect(result.ExitCode != 0, check, "a non-zero exit", $"exit 0: {Tail(result)}", severity, reference, result);

    public void ExpectFailure(MsagentResult result, int exitCode, string errorKind, string check, string severity = "P1", string? reference = null)
    {
        var actualKind = ErrorKind(result);
        Expect(result.ExitCode == exitCode && string.Equals(actualKind, errorKind, StringComparison.Ordinal), check, $"exit {exitCode} with errorKind {errorKind}",
            $"exit {result.ExitCode} with errorKind {actualKind ?? "none"}: {Tail(result)}", severity, reference, result);
    }

    public void Observe(string check, string detail, string severity = "info", string? reference = null)
    {
        Services.AddFinding(new StandaloneFinding
        {
            ScenarioId = Id,
            Severity = severity,
            Check = check,
            Expected = "-",
            Actual = Clip(detail, 400),
            Reference = reference,
            Failed = false,
        });

        Context.Transcript.Note($"observation [{severity}] {check}: {detail}");
    }

    public void Note(string message) => Context.Transcript.Note(message);

    public void Conclude()
    {
        if (_failed.Count > 0)
        {
            throw Combined(null);
        }
    }

    public AssertionException Combined(AssertionException? hard)
    {
        var lines = _failed.Select(finding => $"[{finding.Severity}] {finding.Check}: expected {finding.Expected}, got {finding.Actual}").ToList();

        if (hard is not null)
        {
            lines.Add($"then stopped: {hard.Message}");
        }

        return new AssertionException(
            $"{_failed.Count} desired behaviour(s) not met",
            "every desired behaviour",
            Clip(string.Join(" || ", lines), 1500),
            null,
            _lastFailedResult);
    }

    public static string Tail(MsagentResult result)
    {
        var output = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr + " " + result.StdOut;
        return Clip(Redaction.Apply(output.Trim().ReplaceLineEndings(" ")), 300);
    }

    public static string? ErrorKind(MsagentResult result) =>
        EnvelopeProperty(result, "errorKind") is { ValueKind: JsonValueKind.String } errorKind ? errorKind.GetString() : null;

    private static JsonElement? EnvelopeProperty(MsagentResult result, string name)
    {
        try
        {
            return result.JsonElement() is { ValueKind: JsonValueKind.Object } envelope && envelope.TryGetProperty(name, out var value) ? value : null;
        }
        catch (E2EException)
        {
            return null;
        }
    }

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length] + "…";
}
