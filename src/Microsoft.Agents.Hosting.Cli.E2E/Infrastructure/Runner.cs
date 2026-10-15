using System.Diagnostics;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>How a scenario ended.</summary>
public enum Outcome
{
    Pass,
    Fail,
    Skip,
    Blocked,
    Error,
}

/// <summary>The serializable identity of a scenario, so a report carries no delegates.</summary>
public sealed record ScenarioDescriptor(string Id, string Title, string Feature, SafetyTier Tier)
{
    /// <summary>Projects a scenario onto the shape a report can persist.</summary>
    public static ScenarioDescriptor From(Scenario scenario) =>
        new(scenario.Id, scenario.Title, scenario.Feature, scenario.Tier);
}

/// <summary>One scenario's result, including the named checks that passed before any failure.</summary>
public sealed record ScenarioResult
{
    public required ScenarioDescriptor Scenario { get; init; }

    public required Outcome Outcome { get; init; }

    public TimeSpan Duration { get; init; }

    public string? Reason { get; init; }

    public string? Expected { get; init; }

    public string? Actual { get; init; }

    public string? CommandLine { get; init; }

    public string? TranscriptPath { get; init; }

    public string? RetainedWorkspace { get; init; }

    public IReadOnlyList<string> PassedChecks { get; init; } = [];

    /// <summary>The cause a blocked scenario shares with others blocked the same way.</summary>
    public string? BlockedCause { get; init; }
}

/// <summary>Executes scenarios in order, one at a time, recording an outcome for every one.</summary>
public sealed class Runner
{
    private static readonly TimeSpan DefaultScenarioTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a timed-out scenario is given to unwind before the run advances anyway: its own
    /// bound over again, and never more than this.
    /// </summary>
    private static readonly TimeSpan MaximumCleanupGrace = TimeSpan.FromMinutes(2);

    private readonly RunContext _run;
    private readonly TimeSpan _scenarioTimeout;
    private readonly TimeSpan _cleanupGrace;

    public Runner(RunContext run) : this(run, DefaultScenarioTimeout)
    {
    }

    /// <summary>Creates a runner whose scenarios are bound by an explicit timeout.</summary>
    /// <param name="run">The run the scenarios execute in.</param>
    /// <param name="scenarioTimeout">How long one scenario may take.</param>
    public Runner(RunContext run, TimeSpan scenarioTimeout)
    {
        _run = run;
        _scenarioTimeout = scenarioTimeout;
        _cleanupGrace = scenarioTimeout < MaximumCleanupGrace ? scenarioTimeout : MaximumCleanupGrace;
    }

    /// <summary>Runs the scheduled scenarios, continuing past failures.</summary>
    public async Task<IReadOnlyList<ScenarioResult>> RunAsync(IReadOnlyList<Scenario> scenarios, CancellationToken cancellationToken)
    {
        var results = new List<ScenarioResult>();
        var blockedCapabilities = new HashSet<Capability>();

        foreach (var scenario in scenarios)
        {
            var result = await RunOneAsync(scenario, blockedCapabilities, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            _run.Reporter.Line(result);
        }

        return results;
    }

    private async Task<ScenarioResult> RunOneAsync(Scenario scenario, HashSet<Capability> blockedCapabilities, CancellationToken cancellationToken)
    {
        if (_run.Capabilities.FirstMissing(scenario.Requires) is { } missing)
        {
            return new ScenarioResult
            {
                Scenario = ScenarioDescriptor.From(scenario),
                Outcome = Outcome.Skip,
                Reason = _run.Capabilities.Reason(missing),
            };
        }

        if (scenario.Requires.FirstOrDefault(blockedCapabilities.Contains) is var blocked && blockedCapabilities.Contains(blocked))
        {
            return new ScenarioResult
            {
                Scenario = ScenarioDescriptor.From(scenario),
                Outcome = Outcome.Blocked,
                Reason = $"{blocked} became unusable earlier in the run",
                BlockedCause = blocked.ToString(),
            };
        }

        var assertions = new Assertions(_run.Transcript);
        var scenarioDirectory = Path.Combine(_run.RunDirectory, scenario.Id);
        var transcriptPath = Path.Combine(scenarioDirectory, "transcript.log");
        _run.Transcript.BeginScenario(transcriptPath);
        _run.Transcript.Note($"=== {scenario.Id} {scenario.Title} ===");

        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_scenarioTimeout);

        var running = null as Task;

        try
        {
            running = scenario.Run(_run.ContextFor(scenario, assertions, timeout.Token));
            await running.WaitAsync(timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();

            return new ScenarioResult
            {
                Scenario = ScenarioDescriptor.From(scenario),
                Outcome = Outcome.Pass,
                Duration = stopwatch.Elapsed,
                TranscriptPath = transcriptPath,
                PassedChecks = assertions.Passed,
            };
        }
        catch (AssertionException exception)
        {
            stopwatch.Stop();

            return new ScenarioResult
            {
                Scenario = ScenarioDescriptor.From(scenario),
                Outcome = Outcome.Fail,
                Duration = stopwatch.Elapsed,
                Reason = exception.Message,
                Expected = exception.Expected,
                Actual = exception.Actual,
                CommandLine = exception.CommandLine,
                TranscriptPath = transcriptPath,
                RetainedWorkspace = RetainWorkspace(scenario),
                PassedChecks = assertions.Passed,
            };
        }
        catch (ScenarioBlockedException exception)
        {
            stopwatch.Stop();
            RecordBlockedCapability(scenario, exception, blockedCapabilities);

            return new ScenarioResult
            {
                Scenario = ScenarioDescriptor.From(scenario),
                Outcome = Outcome.Blocked,
                Duration = stopwatch.Elapsed,
                Reason = exception.Message,
                BlockedCause = exception.Cause,
                TranscriptPath = transcriptPath,
                PassedChecks = assertions.Passed,
            };
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();

            if (running is not null)
            {
                await DrainAsync(running).ConfigureAwait(false);
            }

            return new ScenarioResult
            {
                Scenario = ScenarioDescriptor.From(scenario),
                Outcome = Outcome.Error,
                Duration = stopwatch.Elapsed,
                Reason = $"the scenario exceeded its {_scenarioTimeout} bound",
                TranscriptPath = transcriptPath,
                RetainedWorkspace = RetainWorkspace(scenario),
                PassedChecks = assertions.Passed,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();

            return new ScenarioResult
            {
                Scenario = ScenarioDescriptor.From(scenario),
                Outcome = Outcome.Error,
                Duration = stopwatch.Elapsed,
                Reason = exception.Message,
                TranscriptPath = transcriptPath,
                RetainedWorkspace = RetainWorkspace(scenario),
                PassedChecks = assertions.Passed,
            };
        }
        finally
        {
            _run.Transcript.EndScenario();
        }
    }

    /// <summary>
    /// Waits for a timed-out scenario to finish unwinding before the run advances.
    /// </summary>
    private async Task DrainAsync(Task running)
    {
        await Task.WhenAny(running, Task.Delay(_cleanupGrace)).ConfigureAwait(false);

        if (running.IsCompleted)
        {
            _ = running.Exception;
        }
    }

    private static void RecordBlockedCapability(Scenario scenario, ScenarioBlockedException exception, HashSet<Capability> blockedCapabilities)
    {
        if (exception.BlocksCapability is { } capability && scenario.Requires.Contains(capability))
        {
            blockedCapabilities.Add(capability);
        }
    }

    private string RetainWorkspace(Scenario scenario) => Path.Combine(_run.WorkspaceRoot, scenario.Id);
}

/// <summary>A scenario that could not run because a prerequisite turned out to be unusable.</summary>
public sealed class ScenarioBlockedException : Exception
{
    public ScenarioBlockedException(string message, string cause, Capability? blocksCapability = null) : base(message)
    {
        Cause = cause;
        BlocksCapability = blocksCapability;
    }

    /// <summary>The shared cause, so rows blocked the same way collapse into one report entry.</summary>
    public string Cause { get; }

    /// <summary>The capability to treat as unusable for the rest of the run.</summary>
    public Capability? BlocksCapability { get; }
}