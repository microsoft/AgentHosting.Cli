using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Evaluations;

/// <summary>Projects a run the service reported into the shape every run-reporting verb renders.</summary>
public static class EvaluationRunProjection
{
    /// <summary>Describes a run and its per-case results.</summary>
    /// <param name="target">The agent the run belongs to.</param>
    /// <param name="detail">The run the service reported.</param>
    /// <param name="status">The machine-readable status string.</param>
    /// <param name="testSetName">The name of the test set that ran.</param>
    /// <param name="outputPath">Where the run was written, when it was.</param>
    /// <param name="timedOut">Whether polling stopped before the run finished.</param>
    public static EvaluationRunResult Describe(EvaluationTarget target, EvaluationRunDetail detail, string status, string? testSetName = null, string? outputPath = null, bool timedOut = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(detail);

        return new EvaluationRunResult
        {
            Status = status,
            EnvironmentId = target.Environment.EnvironmentId,
            McsAgentId = target.McsAgentId,
            Run = EvaluationRunRow.From(detail.Summary),
            TestSetName = testSetName,
            Cases = [.. detail.TestCases.Select(EvaluationCaseRow.From)],
            OutputPath = outputPath,
            TimedOut = timedOut,
        };
    }

    /// <summary>Describes a run that carries no per-case results, such as one just started or stopped.</summary>
    /// <param name="target">The agent the run belongs to.</param>
    /// <param name="summary">The run the service reported.</param>
    /// <param name="status">The machine-readable status string.</param>
    /// <param name="testSetName">The name of the test set that ran.</param>
    public static EvaluationRunResult Describe(EvaluationTarget target, EvaluationRunSummary summary, string status, string? testSetName = null)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return Describe(target, new EvaluationRunDetail { Summary = summary }, status, testSetName);
    }
}

/// <summary>Builds the conversations a test-set verb was asked to use, from a file or the generator.</summary>
public sealed class EvaluationConversationProvider
{
    private const string DefaultLocale = "1033";
    private const int DefaultTurnCount = 3;
    private const int DefaultConversationCount = 10;

    private readonly IAgentEvaluationPort _evaluations;
    private readonly ICopilotFileSystem _files;

    /// <summary>Creates the provider.</summary>
    /// <param name="evaluations">Generates conversations when a file was not supplied.</param>
    /// <param name="files">Reads a conversation CSV.</param>
    public EvaluationConversationProvider(IAgentEvaluationPort evaluations, ICopilotFileSystem files)
    {
        _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <summary>Reads a CSV or generates conversations, rejecting a request that names neither or both.</summary>
    /// <param name="target">The agent the conversations are for.</param>
    /// <param name="fromCsv">The CSV to read.</param>
    /// <param name="generate">How many conversations to generate.</param>
    /// <param name="turns">How many turns each generated conversation holds.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<IReadOnlyList<EvaluationConversation>> ResolveAsync(EvaluationTarget target, string? fromCsv, int? generate, int? turns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var hasFile = !string.IsNullOrWhiteSpace(fromCsv);

        if (hasFile && generate is not null)
        {
            throw new UsageException("--from-csv and --generate cannot be combined: each names a different source of conversations.");
        }

        if (!hasFile && generate is null)
        {
            throw new UsageException("Supply conversations with --from-csv, or ask for generated ones with --generate.");
        }

        if (hasFile)
        {
            return ReadFile(fromCsv!);
        }

        if (generate is <= 0)
        {
            throw new UsageException("--generate must be a positive integer.");
        }

        if (turns is <= 0)
        {
            throw new UsageException("--turns must be a positive integer.");
        }

        var generated = await GenerateAsync(target, generate, turns, cancellationToken).ConfigureAwait(false);

        EvaluationConversationLimits.Validate(generated);

        return generated;
    }

    /// <summary>
    /// Asks the service to invent conversations, refusing an empty answer.
    ///
    /// <para>An empty result is never a usable one, and a caller that replaces a test set with it
    /// would delete the conversations the set holds and store nothing in their place.</para>
    /// </summary>
    /// <param name="target">The agent the conversations are for.</param>
    /// <param name="count">How many conversations to ask for.</param>
    /// <param name="turns">How many turns each conversation holds.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<IReadOnlyList<EvaluationConversation>> GenerateAsync(EvaluationTarget target, int? count, int? turns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var generated = await _evaluations
            .GenerateConversationsAsync(target.Environment, target.McsAgentId, count ?? DefaultConversationCount, turns ?? DefaultTurnCount, DefaultLocale, cancellationToken)
            .ConfigureAwait(false);

        return generated.Count > 0 ? generated : throw EvaluationException.ConversationsNotGenerated();
    }

    private IReadOnlyList<EvaluationConversation> ReadFile(string path)
    {
        var resolved = _files.ResolveFullPath(path);

        if (!_files.FileExists(resolved))
        {
            throw EvaluationException.ConversationFileInvalid(path, "the file does not exist.");
        }

        return EvaluationCsvReader.Read(_files.ReadAllText(resolved), path);
    }
}
