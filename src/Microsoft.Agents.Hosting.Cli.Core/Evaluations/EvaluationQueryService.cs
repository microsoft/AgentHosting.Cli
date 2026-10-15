using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Evaluations;

/// <summary>The read-only evaluation verbs: <c>eval list</c>, <c>eval show</c> and the test-set readers.</summary>
public sealed class EvaluationQueryService
{
    private const int DefaultLimit = 20;
    private const int TruncationProbe = 1;

    private readonly IEvaluationTargetResolver _targets;
    private readonly EvaluationTestSetLookup _testSets;
    private readonly IAgentEvaluationPort _evaluations;
    private readonly ICopilotFileSystem _files;

    /// <summary>Creates the service.</summary>
    /// <param name="targets">Resolves the agent being addressed.</param>
    /// <param name="testSets">Resolves a test set from a name or id.</param>
    /// <param name="evaluations">Reads runs and test sets.</param>
    /// <param name="files">Writes exported files.</param>
    public EvaluationQueryService(IEvaluationTargetResolver targets, EvaluationTestSetLookup testSets, IAgentEvaluationPort evaluations, ICopilotFileSystem files)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _testSets = testSets ?? throw new ArgumentNullException(nameof(testSets));
        _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <summary>Lists the agent's evaluation runs.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<EvaluationListResult> ListAsync(EvaluationListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Limit is <= 0)
        {
            throw new UsageException("--limit must be a positive integer.");
        }

        var limit = request.Limit ?? DefaultLimit;
        var target = await _targets.ResolveAsync(request, "agent eval list", cancellationToken).ConfigureAwait(false);
        var runs = await _evaluations.ListRunsAsync(target.Environment, target.McsAgentId, limit == int.MaxValue ? limit : limit + TruncationProbe, cancellationToken).ConfigureAwait(false);

        return new EvaluationListResult
        {
            EnvironmentId = target.Environment.EnvironmentId,
            McsAgentId = target.McsAgentId,
            Limit = limit,
            Truncated = runs.Count > limit,
            Runs = [.. runs.Take(limit).Select(EvaluationRunRow.From)],
        };
    }

    /// <summary>Reads one run and its per-case results.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<EvaluationRunResult> ShowAsync(EvaluationShowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            throw new UsageException("A run id is required. Run `msagent agent eval list` to see the available runs.");
        }

        var target = await _targets.ResolveAsync(request, "agent eval show", cancellationToken).ConfigureAwait(false);
        var detail = await _evaluations.GetRunAsync(target.Environment, target.McsAgentId, request.RunId.Trim(), cancellationToken).ConfigureAwait(false) ?? throw EvaluationException.RunNotFound(request.RunId.Trim());

        var result = EvaluationRunProjection.Describe(target, detail, "evaluation-shown") with
        {
            TranscriptNumbers = TranscriptNumbers(request, detail.TestCases.Count),
            OutputPath = string.IsNullOrWhiteSpace(request.Output) ? null : request.Output,
        };

        if (result.OutputPath is not null)
        {
            _files.WriteAllText(result.OutputPath, EvaluationRunReport.Render(result.OutputPath, result));
        }

        return result;
    }

    private static IReadOnlyList<int> TranscriptNumbers(EvaluationShowRequest request, int caseCount)
    {
        if (request.Conversation is not { } number)
        {
            return request.Transcript ? [.. Enumerable.Range(1, caseCount)] : [];
        }

        if (caseCount == 0)
        {
            throw new UsageException("This run holds no conversations to print.");
        }

        return number >= 1 && number <= caseCount ? [number] : throw new UsageException($"--conversation must be between 1 and {caseCount}.");
    }

    /// <summary>Lists the agent's saved test sets.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<EvaluationTestSetListResult> ListTestSetsAsync(EvaluationTestSetListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await _targets.ResolveAsync(request, "agent eval testset list", cancellationToken).ConfigureAwait(false);
        var testSets = await _evaluations.ListTestSetsAsync(target.Environment, target.McsAgentId, cancellationToken).ConfigureAwait(false);

        return new EvaluationTestSetListResult
        {
            EnvironmentId = target.Environment.EnvironmentId,
            McsAgentId = target.McsAgentId,
            TestSets = [.. testSets.Select(EvaluationTestSetRow.From)],
        };
    }

    /// <summary>Reads one test set and the conversations it holds.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<EvaluationTestSetResult> ShowTestSetAsync(EvaluationTestSetShowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (target, detail) = await RequireTestSetAsync(request, request.TestSet, "agent eval testset show", cancellationToken).ConfigureAwait(false);

        return Describe(target, detail, "test-set-shown", outputPath: null);
    }

    /// <summary>Writes a test set's conversations out as CSV.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<EvaluationTestSetResult> ExportTestSetAsync(EvaluationTestSetExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Output))
        {
            throw new UsageException("--output is required: it names the CSV file to write.");
        }

        var (target, detail) = await RequireTestSetAsync(request, request.TestSet, "agent eval testset export", cancellationToken).ConfigureAwait(false);

        _files.WriteAllText(request.Output, EvaluationCsvWriter.Write(detail.Conversations));

        return Describe(target, detail, "test-set-exported", request.Output);
    }

    private async Task<(EvaluationTarget Target, EvaluationTestSetDetail Detail)> RequireTestSetAsync(EvaluationSelector selector, string? testSet, string verb, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(testSet))
        {
            throw new UsageException("--testset is required: it names the test set by name or id.");
        }

        var target = await _targets.ResolveAsync(selector, verb, cancellationToken).ConfigureAwait(false);
        var summary = await _testSets.RequireAsync(target, testSet, cancellationToken).ConfigureAwait(false);
        var detail = await _evaluations.GetTestSetAsync(target.Environment, target.McsAgentId, summary, cancellationToken).ConfigureAwait(false) ?? throw EvaluationException.TestSetNotFound(testSet);

        return (target, detail);
    }

    private static EvaluationTestSetResult Describe(EvaluationTarget target, EvaluationTestSetDetail detail, string status, string? outputPath) => new()
    {
        Status = status,
        EnvironmentId = target.Environment.EnvironmentId,
        McsAgentId = target.McsAgentId,
        TestSet = EvaluationTestSetRow.From(detail.Summary),
        Conversations = [.. detail.Conversations.Select(EvaluationConversationRow.From)],
        OutputPath = outputPath,
    };
}
