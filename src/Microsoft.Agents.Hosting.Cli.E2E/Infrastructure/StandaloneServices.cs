using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public sealed record StandaloneFinding
{
    public required string ScenarioId { get; init; }

    public required string Severity { get; init; }

    public required string Check { get; init; }

    public required string Expected { get; init; }

    public required string Actual { get; init; }

    public string? Reference { get; init; }

    public bool Failed { get; init; }
}

public sealed record StandaloneTiming(string ScenarioId, string Command, int ExitCode, double Seconds);

public sealed class StandaloneServices
{
    public const string FlowNamePrefix = "ahe2e-SW";

    public const string ReferencePrefix = "new_ahe2e_";

    private readonly List<StandaloneFinding> _findings = [];
    private readonly List<StandaloneTiming> _timings = [];
    private readonly Lock _gate = new();
    private int _environmentClaimed;

    public StandaloneServices(E2EConfig config, string runDirectory, Transcript transcript, string fixturesDirectory)
    {
        Config = config;
        RunDirectory = runDirectory;
        Transcript = transcript;
        FixturesDirectory = fixturesDirectory;

        var tokens = new AzTokens(config.TenantId);
        Dataverse = new DataverseProbe(tokens, transcript);
        Flows = new FlowRunner(tokens, transcript);
        Connections = new ConnectionProbe(tokens, transcript);
    }

    public E2EConfig Config { get; }

    public string RunDirectory { get; }

    public Transcript Transcript { get; }

    public string FixturesDirectory { get; }

    public string ScratchDirectory => Path.Combine(RunDirectory, "sw-scratch");

    public DataverseProbe Dataverse { get; }

    public FlowRunner Flows { get; }

    public ConnectionProbe Connections { get; }

    public bool OracleAvailable { get; private set; }

    public bool FlowRunsAvailable { get; private set; }

    public IReadOnlyList<StandaloneFinding> Findings
    {
        get { lock (_gate) { return [.. _findings]; } }
    }

    public IReadOnlyList<StandaloneTiming> Timings
    {
        get { lock (_gate) { return [.. _timings]; } }
    }

    public void AddFinding(StandaloneFinding finding)
    {
        lock (_gate)
        {
            _findings.Add(finding);
        }
    }

    public void AddTiming(StandaloneTiming timing)
    {
        lock (_gate)
        {
            _timings.Add(timing);
        }
    }

    public bool TryClaimEnvironmentSelection() => Interlocked.Exchange(ref _environmentClaimed, 1) == 0;

    public async Task<Capabilities> ProbeAsync(Capabilities capabilities, CancellationToken cancellationToken)
    {
        var (oracle, oracleDetail) = await ProbeOracleAsync(cancellationToken).ConfigureAwait(false);
        OracleAvailable = oracle;

        var (flows, flowsDetail) = oracle
            ? await ProbeFlowsAsync(cancellationToken).ConfigureAwait(false)
            : (false, "needs the Dataverse oracle");
        FlowRunsAvailable = flows;

        return capabilities
            .With(Capability.StandaloneOracle, oracle, oracleDetail)
            .With(Capability.StandaloneFlowRuns, flows, flowsDetail);
    }

    private async Task<(bool Available, string Detail)> ProbeOracleAsync(CancellationToken cancellationToken)
    {
        if (Config.Mcs.DevEnvironmentId is not { Length: > 0 } dev)
        {
            return (false, "mcs.devEnvironmentId is not set");
        }

        if (!AzureCli.IsPresent())
        {
            return (false, "az is not on PATH, so deep cloud checks are skipped");
        }

        try
        {
            var instance = await Dataverse.InstanceAsync(dev, cancellationToken).ConfigureAwait(false);

            return await Dataverse.CanReadAsync(dev, cancellationToken).ConfigureAwait(false)
                ? (true, $"{instance.Host} via az, read-only")
                : (false, $"{instance.Host} refused a read; run az login for this tenant");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (false, Summarize(exception.Message));
        }
    }

    private async Task<(bool Available, string Detail)> ProbeFlowsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Flows.CanListAsync(Config.Mcs.DevEnvironmentId!, cancellationToken).ConfigureAwait(false)
                ? (true, "Power Automate API via az; runs only the suite's own no-op flow")
                : (false, "the Power Automate API refused the flow listing");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (false, Summarize(exception.Message));
        }
    }

    public async Task FinishAsync(string runSuffix, CancellationToken cancellationToken)
    {
        var leftovers = OracleAvailable ? await SweepAsync(runSuffix, cancellationToken).ConfigureAwait(false) : [];

        foreach (var leftover in leftovers)
        {
            AddFinding(new StandaloneFinding
            {
                ScenarioId = "cleanup",
                Severity = "info",
                Check = "nothing the run named is left in the cloud",
                Expected = "absent",
                Actual = leftover,
            });
        }

        Persist();
        PrintSummary(leftovers);
    }

    private async Task<IReadOnlyList<string>> SweepAsync(string runSuffix, CancellationToken cancellationToken)
    {
        var leftovers = new List<string>();
        var environments = new[] { Config.Mcs.DevEnvironmentId, Config.Mcs.TestEnvironmentId }.OfType<string>().Where(id => id.Length > 0);

        foreach (var environment in environments)
        {
            try
            {
                var flows = await Dataverse.WorkflowsPrefixedAsync(environment, FlowNamePrefix, cancellationToken).ConfigureAwait(false);
                leftovers.AddRange(flows
                    .Where(flow => (flow.Name ?? string.Empty).Contains(runSuffix, StringComparison.OrdinalIgnoreCase))
                    .Select(flow => $"flow '{flow.Name}' ({flow.WorkflowId}) in {environment}"));

                var references = await Dataverse.ConnectionReferencesPrefixedAsync(environment, ReferencePrefix + runSuffix, cancellationToken).ConfigureAwait(false);
                leftovers.AddRange(references.Select(reference => $"connection reference {reference.LogicalName} in {environment}"));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                leftovers.Add($"could not sweep {environment}: {Summarize(exception.Message)}");
            }
        }

        return leftovers;
    }

    private void Persist()
    {
        var findings = Findings;
        var timings = Timings;

        File.WriteAllText(Path.Combine(RunDirectory, "sw-findings.json"), JsonSerializer.Serialize(findings, ConfigJson.Options));
        File.WriteAllText(Path.Combine(RunDirectory, "sw-timings.json"), JsonSerializer.Serialize(timings, ConfigJson.Options));
        File.WriteAllText(Path.Combine(RunDirectory, "sw-findings.md"), Markdown(findings, timings));
    }

    private static string Markdown(IReadOnlyList<StandaloneFinding> findings, IReadOnlyList<StandaloneTiming> timings)
    {
        var markdown = new StringBuilder()
            .AppendLine("# mcs-standaloneworkflow findings")
            .AppendLine()
            .AppendLine("| Scenario | Severity | Failed | Check | Expected | Actual | Ref |")
            .AppendLine("|---|---|---|---|---|---|---|");

        foreach (var finding in findings.OrderBy(finding => finding.Severity, StringComparer.Ordinal).ThenBy(finding => finding.ScenarioId, StringComparer.Ordinal))
        {
            markdown.AppendLine(CultureInfo.InvariantCulture, $"| {finding.ScenarioId} | {finding.Severity} | {(finding.Failed ? "yes" : "no")} | {Cell(finding.Check)} | {Cell(finding.Expected)} | {Cell(finding.Actual)} | {finding.Reference} |");
        }

        markdown
            .AppendLine()
            .AppendLine("## Command timings")
            .AppendLine()
            .AppendLine("| Command | Count | Median s | Max s |")
            .AppendLine("|---|---|---|---|");

        foreach (var group in timings.GroupBy(timing => timing.Command, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var seconds = group.Select(timing => timing.Seconds).Order().ToArray();
            markdown.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {seconds.Length} | {seconds[seconds.Length / 2]:N1} | {seconds[^1]:N1} |");
        }

        return markdown.ToString();
    }

    private void PrintSummary(IReadOnlyList<string> leftovers)
    {
        var findings = Findings;
        var failed = findings.Count(finding => finding.Failed);

        Console.WriteLine();
        Console.WriteLine($"  Standalone workflow: {failed} failed expectation(s), {findings.Count - failed} observation(s)  ->  {Path.Combine(RunDirectory, "sw-findings.md")}");

        foreach (var leftover in leftovers)
        {
            Console.WriteLine($"    left in the cloud: {leftover}");
        }
    }

    private static string Cell(string value) => Redaction.Apply(value).Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");

    private static string Summarize(string value) =>
        value.ReplaceLineEndings(" ") is { Length: > 160 } longer ? longer[..160] + "…" : value.ReplaceLineEndings(" ");
}
