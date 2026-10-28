using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Evaluations;

/// <summary>The run verbs: <c>eval run</c>, <c>eval generate</c>, <c>eval cancel</c> and <c>eval delete</c>.</summary>
public sealed class EvaluationRunService
{
    private const int DefaultTimeoutMinutes = 60;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IEvaluationTargetResolver _targets;
    private readonly EvaluationTestSetLookup _testSets;
    private readonly EvaluationTestSetService _testSetWrites;
    private readonly EvaluationConversationProvider _conversations;
    private readonly IAgentEvaluationPort _evaluations;
    private readonly ICopilotFileSystem _files;
    private readonly IInteractiveMessagePresenter _presenter;
    private readonly IClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly IProgressStatus _progress;

    /// <summary>Creates the service.</summary>
    /// <param name="targets">Resolves the agent being addressed.</param>
    /// <param name="testSets">Resolves a test set from a name or id.</param>
    /// <param name="testSetWrites">Creates and fills test sets.</param>
    /// <param name="conversations">Reads or generates the conversations to run.</param>
    /// <param name="evaluations">Starts, polls, stops and deletes runs.</param>
    /// <param name="files">Writes the finished run when an output path was given.</param>
    /// <param name="presenter">Reports progress while <c>--watch</c> polls.</param>
    /// <param name="clock">Bounds how long polling runs.</param>
    /// <param name="delay">Waits between polls, replaced in tests.</param>
    /// <param name="progress">Shows progress on the status line while <c>--watch</c> polls, in place of the presenter.</param>
    public EvaluationRunService(IEvaluationTargetResolver targets, EvaluationTestSetLookup testSets, EvaluationTestSetService testSetWrites, EvaluationConversationProvider conversations, IAgentEvaluationPort evaluations, ICopilotFileSystem files, IInteractiveMessagePresenter presenter, IClock clock, Func<TimeSpan, CancellationToken, Task>? delay = null, IProgressStatus? progress = null)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _testSets = testSets ?? throw new ArgumentNullException(nameof(testSets));
        _testSetWrites = testSetWrites ?? throw new ArgumentNullException(nameof(testSetWrites));
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delay = delay ?? Task.Delay;
        _progress = progress ?? NoProgressStatus.Instance;
    }

    /// <summary>Starts an evaluation run, creating the test set first when one was not named.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<EvaluationRunResult> RunAsync(EvaluationRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var namesSource = !string.IsNullOrWhiteSpace(request.FromCsv) || request.Generate is not null;

        if (!string.IsNullOrWhiteSpace(request.TestSet) && namesSource)
        {
            throw new UsageException("--testset names an existing test set, so --from-csv and --generate do not apply.");
        }

        if (string.IsNullOrWhiteSpace(request.TestSet) && !namesSource)
        {
            throw new UsageException("Name an existing test set with --testset, or supply conversations with --from-csv or --generate.");
        }

        if (request.TimeoutMinutes is <= 0)
        {
            throw new UsageException("--timeout-minutes must be a positive integer.");
        }

        if (!request.Watch && !string.IsNullOrWhiteSpace(request.Output))
        {
            throw new UsageException("--output writes the finished run, which only --watch waits for. Add --watch, or read the run later with `msagent agent eval show --output`.");
        }

        var target = await _targets.ResolveAsync(request, "agent eval run", cancellationToken).ConfigureAwait(false);
        var resolved = string.IsNullOrWhiteSpace(request.TestSet)
            ? await PrepareTestSetAsync(target, request, cancellationToken).ConfigureAwait(false)
            : await ExistingTestSetAsync(target, request.TestSet, cancellationToken).ConfigureAwait(false);

        var start = new EvaluationRunStart
        {
            TestSetId = resolved.TestSetId,
            Name = resolved.DisplayName,
            ConnectionId = request.Connection,
            RunOnPublishedBot = request.Published,
        };

        var started = (await _evaluations.StartRunAsync(target.Environment, target.McsAgentId, start, cancellationToken).ConfigureAwait(false)).Backfill(start);

        if (!request.Watch)
        {
            return EvaluationRunProjection.Describe(target, started, "evaluation-started", resolved.DisplayName);
        }

        return await WatchAsync(target, started.RunId, resolved.DisplayName, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Generates conversations and writes them out without touching the agent's test sets.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<EvaluationGenerateResult> GenerateAsync(EvaluationGenerateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Count is <= 0)
        {
            throw new UsageException("--count must be a positive integer.");
        }

        if (request.Turns is <= 0)
        {
            throw new UsageException("--turns must be a positive integer.");
        }

        var target = await _targets.ResolveAsync(request, "agent eval generate", cancellationToken).ConfigureAwait(false);
        var conversations = await _conversations.GenerateAsync(target, request.Count, request.Turns, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(request.Output))
        {
            _files.WriteAllText(request.Output, EvaluationCsvWriter.Write(conversations));
        }

        return new EvaluationGenerateResult
        {
            EnvironmentId = target.Environment.EnvironmentId,
            McsAgentId = target.McsAgentId,
            Conversations = [.. conversations.Select(EvaluationConversationRow.From)],
            OutputPath = string.IsNullOrWhiteSpace(request.Output) ? null : request.Output,
        };
    }

    /// <summary>Stops an in-flight run.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<EvaluationRunResult> CancelAsync(EvaluationCancelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (target, runId, current) = await RequireRunAsync(request, request.RunId, "agent eval cancel", cancellationToken).ConfigureAwait(false);

        if (!EvaluationRunStates.IsCancellable(current.Summary.State))
        {
            throw EvaluationException.RunNotCancellable(runId, current.Summary.State);
        }

        return EvaluationRunProjection.Describe(
            target,
            (await _evaluations.CancelRunAsync(target.Environment, target.McsAgentId, runId, cancellationToken).ConfigureAwait(false)).Backfill(current.Summary),
            "evaluation-cancelled");
    }

    /// <summary>Deletes a run that has already reached a terminal state.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<EvaluationRunResult> DeleteAsync(EvaluationDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (target, runId, current) = await RequireRunAsync(request, request.RunId, "agent eval delete", cancellationToken).ConfigureAwait(false);

        if (!current.Summary.IsTerminal)
        {
            throw EvaluationException.RunNotTerminal(runId, current.Summary.State);
        }

        return EvaluationRunProjection.Describe(
            target,
            (await _evaluations.DeleteRunAsync(target.Environment, target.McsAgentId, runId, cancellationToken).ConfigureAwait(false)).Backfill(current.Summary),
            "evaluation-deleted");
    }

    private async Task<(EvaluationTarget Target, string RunId, EvaluationRunDetail Detail)> RequireRunAsync(EvaluationSelector selector, string? runId, string verb, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new UsageException("A run id is required. Run `msagent agent eval list` to see the available runs.");
        }

        var trimmed = runId.Trim();
        var target = await _targets.ResolveAsync(selector, verb, cancellationToken).ConfigureAwait(false);
        var detail = await _evaluations.GetRunAsync(target.Environment, target.McsAgentId, trimmed, cancellationToken).ConfigureAwait(false)
            ?? throw EvaluationException.RunNotFound(trimmed);

        return (target, trimmed, detail);
    }

    private async Task<(string TestSetId, string DisplayName)> ExistingTestSetAsync(EvaluationTarget target, string selector, CancellationToken cancellationToken)
    {
        var summary = await _testSets.RequireAsync(target, selector, cancellationToken).ConfigureAwait(false);

        return summary.ConversationCount == 0
            ? throw EvaluationException.TestSetEmpty(summary.DisplayName)
            : (summary.TestSetId, summary.DisplayName);
    }

    private async Task<(string TestSetId, string DisplayName)> PrepareTestSetAsync(EvaluationTarget target, EvaluationRunRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new UsageException("--name is required with --from-csv or --generate: it names the test set being created.");
        }

        var name = request.Name.Trim();
        var existing = await _testSets.FindByNameAsync(target, name, cancellationToken).ConfigureAwait(false);

        if (existing is not null && !request.UpdateTestSet)
        {
            throw EvaluationException.TestSetAlreadyExists(existing.DisplayName, existing.ConversationCount);
        }

        var conversations = await _conversations.ResolveAsync(target, request.FromCsv, request.Generate, request.Turns, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            return (await _testSetWrites.CreateAsync(target, name, conversations, cancellationToken).ConfigureAwait(false), name);
        }

        await _testSetWrites.ReplaceConversationsAsync(target, existing, conversations, replace: true, cancellationToken).ConfigureAwait(false);

        return (existing.TestSetId, existing.DisplayName);
    }

    private async Task<EvaluationRunResult> WatchAsync(EvaluationTarget target, string runId, string testSetName, EvaluationRunRequest request, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMinutes(request.TimeoutMinutes ?? DefaultTimeoutMinutes);
        var deadline = _clock.UtcNow + timeout;
        EvaluationRunDetail? detail = null;
        string? reported = null;

        await _presenter.PresentAsync($"Evaluation run {runId} started against test set '{testSetName}'. Waiting for results.", cancellationToken).ConfigureAwait(false);

        while (_clock.UtcNow < deadline)
        {
            detail = await _evaluations.GetRunAsync(target.Environment, target.McsAgentId, runId, cancellationToken).ConfigureAwait(false)
                ?? throw EvaluationException.RunNotFound(runId);

            var status = DescribeProgress(detail);

            if (!string.Equals(status, reported, StringComparison.Ordinal))
            {
                reported = status;

                if (!_progress.Report(status))
                {
                    await _presenter.PresentAsync($"  {status}.", cancellationToken).ConfigureAwait(false);
                }
            }

            if (detail.Summary.IsTerminal)
            {
                return Complete(target, detail, testSetName, request.Output, timedOut: false);
            }

            await _delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return detail is null
            ? throw EvaluationException.RunTimedOut(runId, timeout)
            : Complete(target, detail, testSetName, request.Output, timedOut: true);
    }

    private static string DescribeProgress(EvaluationRunDetail detail) =>
        detail.TestCases.Count == 0
            ? detail.Summary.State
            : $"{detail.Summary.State}: {detail.EvaluatedCount} of {detail.TestCases.Count} conversation(s) evaluated";

    private EvaluationRunResult Complete(EvaluationTarget target, EvaluationRunDetail detail, string testSetName, string? output, bool timedOut)
    {
        var written = timedOut || string.IsNullOrWhiteSpace(output) ? null : output;
        var result = EvaluationRunProjection.Describe(target, detail, timedOut ? "evaluation-running" : "evaluation-completed", testSetName, written, timedOut);

        if (written is not null)
        {
            _files.WriteAllText(written, EvaluationRunReport.ToJson(result));
        }

        return result;
    }
}
