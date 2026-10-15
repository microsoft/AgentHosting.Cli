using Microsoft.Agents.Hosting.Cli.Core.Environments;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>Where a test set's conversations came from.</summary>
public enum EvaluationConversationSource
{
    /// <summary>Conversations supplied by the operator, typically from a CSV.</summary>
    Imported,

    /// <summary>Conversations the service generated from the agent's own description.</summary>
    Generated,
}

/// <summary>The run states the maker-evaluation service reports.</summary>
public static class EvaluationRunStates
{
    /// <summary>The run is accepted but has not started.</summary>
    public const string Queued = "Queued";

    /// <summary>The run is executing test cases.</summary>
    public const string InProgress = "InProgress";

    /// <summary>The run finished and carries results.</summary>
    public const string Completed = "Completed";

    /// <summary>The run stopped because of a service failure.</summary>
    public const string Failed = "Failed";

    /// <summary>The run was stopped before it finished.</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>The run was removed.</summary>
    public const string Deleted = "Deleted";

    private static readonly string[] TerminalStates = [Completed, Failed, Cancelled, Deleted];

    private static readonly string[] CancellableStates = [Queued, InProgress];

    /// <summary>Whether the service will do no further work on a run in this state.</summary>
    /// <param name="state">The state the service reported.</param>
    public static bool IsTerminal(string? state) => TerminalStates.Contains(state, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a run in this state is still in flight and can be stopped.</summary>
    /// <param name="state">The state the service reported.</param>
    public static bool IsCancellable(string? state) => CancellableStates.Contains(state, StringComparer.OrdinalIgnoreCase);
}

/// <summary>The evaluation set types the service recognises.</summary>
public static class EvaluationSetTypes
{
    /// <summary>A set of multi-turn conversations, which the portal labels "Conversation".</summary>
    public const string MultiTurn = "MultiTurn";
}

/// <summary>One question and its optional reference answer inside a conversation.</summary>
/// <param name="Question">The user prompt sent to the agent.</param>
/// <param name="Response">The reference reply, which the service records but never compares against.</param>
public sealed record EvaluationExchange(string Question, string? Response);

/// <summary>One multi-turn conversation a test set runs as a single test case.</summary>
public sealed record EvaluationConversation
{
    /// <summary>The conversation's ordinal within its test set.</summary>
    public required int Number { get; init; }

    /// <summary>The question-and-answer pairs, in the order they are sent.</summary>
    public required IReadOnlyList<EvaluationExchange> Exchanges { get; init; }

    /// <summary>Where the conversation came from.</summary>
    public EvaluationConversationSource Source { get; init; } = EvaluationConversationSource.Imported;

    /// <summary>The first question, which the service shows as the test case's name.</summary>
    public string DisplayName => Exchanges.Count == 0 ? $"Conversation {Number}" : Exchanges[0].Question;
}

/// <summary>A saved collection of conversations that runs as one evaluation.</summary>
public sealed record EvaluationTestSetSummary
{
    /// <summary>The test set's component id, used as the run's <c>testSetId</c>.</summary>
    public required string TestSetId { get; init; }

    /// <summary>The name shown in the portal.</summary>
    public required string DisplayName { get; init; }

    /// <summary>How many conversations the set holds.</summary>
    public int ConversationCount { get; init; }

    /// <summary>Whether the set holds single-turn or multi-turn cases.</summary>
    public string? EvaluationSetType { get; init; }

    /// <summary>The component state the service records.</summary>
    public string? State { get; init; }
}

/// <summary>A test set together with the conversations it holds.</summary>
public sealed record EvaluationTestSetDetail
{
    /// <summary>The set being described.</summary>
    public required EvaluationTestSetSummary Summary { get; init; }

    /// <summary>The conversations, in display order.</summary>
    public IReadOnlyList<EvaluationConversation> Conversations { get; init; } = [];

    /// <summary>The component id of each conversation, aligned with <see cref="Conversations"/>.</summary>
    public IReadOnlyList<string> ConversationComponentIds { get; init; } = [];
}

/// <summary>The counts a grader reports across a finished run.</summary>
public sealed record EvaluationRunTotals
{
    /// <summary>Test cases the grader passed.</summary>
    public int Succeeded { get; init; }

    /// <summary>Test cases the grader failed.</summary>
    public int Failed { get; init; }

    /// <summary>Test cases the grader could not score.</summary>
    public int Invalid { get; init; }

    /// <summary>Test cases that errored before they could be scored.</summary>
    public int Errors { get; init; }

    /// <summary>How many cases the totals account for.</summary>
    public int Total => Succeeded + Failed + Invalid + Errors;

    /// <summary>The pass rate, or <see langword="null"/> when nothing was scored.</summary>
    public double? Score => Total == 0 ? null : (double)Succeeded / Total;
}

/// <summary>One evaluation run.</summary>
public sealed record EvaluationRunSummary
{
    /// <summary>The run's identifier.</summary>
    public required string RunId { get; init; }

    /// <summary>The name recorded when the run started.</summary>
    public string? Name { get; init; }

    /// <summary>The run's current state.</summary>
    public required string State { get; init; }

    /// <summary>The test set the run executed.</summary>
    public string? TestSetId { get; init; }

    /// <summary>How many test cases the run covers.</summary>
    public int TestCaseCount { get; init; }

    /// <summary>When the run started.</summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>When the run reached a terminal state.</summary>
    public DateTimeOffset? EndTime { get; init; }

    /// <summary>Whether the run targeted the published agent rather than the draft.</summary>
    public bool RunOnPublishedBot { get; init; }

    /// <summary>Whether the run executed single-turn or multi-turn cases.</summary>
    public string? EvaluationSetType { get; init; }

    /// <summary>The grader counts, absent until the run finishes.</summary>
    public EvaluationRunTotals? Totals { get; init; }

    /// <summary>The Copilot Credits the run consumed, absent until the run finishes.</summary>
    public double? Credits { get; init; }

    /// <summary>Whether the service will do no further work on this run.</summary>
    public bool IsTerminal => EndTime is not null || EvaluationRunStates.IsTerminal(State);

    /// <summary>
    /// Fills in what a command's answer left out, from a reading of the same run.
    ///
    /// <para>Starting, stopping and deleting a run are each answered with the run's new state and
    /// little else, while reading one carries its name, test set, type and grader counts. The answer
    /// is what just happened, so it wins wherever it says anything at all, and the reading supplies
    /// only the fields it is silent about.</para>
    /// </summary>
    /// <param name="known">A fuller reading of the same run.</param>
    public EvaluationRunSummary Backfill(EvaluationRunSummary known)
    {
        ArgumentNullException.ThrowIfNull(known);

        return this with
        {
            Name = Prefer(Name, known.Name),
            TestSetId = Prefer(TestSetId, known.TestSetId),
            TestCaseCount = TestCaseCount == 0 ? known.TestCaseCount : TestCaseCount,
            StartTime = StartTime ?? known.StartTime,
            EndTime = EndTime ?? known.EndTime,
            EvaluationSetType = Prefer(EvaluationSetType, known.EvaluationSetType),
            Totals = Totals ?? known.Totals,
            Credits = Credits ?? known.Credits,
        };
    }

    /// <summary>
    /// Fills in the identity the caller asked for when the start answer leaves it out.
    ///
    /// <para>A run has no prior reading to draw on, but the request named the test set it executes
    /// and the name to record against it, so those are known without asking the service again.</para>
    /// </summary>
    /// <param name="start">The request the run was started from.</param>
    public EvaluationRunSummary Backfill(EvaluationRunStart start)
    {
        ArgumentNullException.ThrowIfNull(start);

        return this with
        {
            Name = Prefer(Name, start.Name),
            TestSetId = Prefer(TestSetId, start.TestSetId),
        };
    }

    private static string? Prefer(string? reported, string? known) => string.IsNullOrWhiteSpace(reported) ? known : reported;
}

/// <summary>The quality dimensions the general-quality grader scores.</summary>
public sealed record EvaluationQualityDimensions
{
    /// <summary>Whether the agent declined to answer.</summary>
    public string? Abstention { get; init; }

    /// <summary>Whether the reply addressed the question.</summary>
    public string? Relevance { get; init; }

    /// <summary>Whether the reply was supported by the agent's sources.</summary>
    public string? Groundedness { get; init; }

    /// <summary>Whether the reply covered what the question asked for.</summary>
    public string? Completeness { get; init; }
}

/// <summary>One question and the answer the agent gave during a run.</summary>
public sealed record EvaluationQueryResult
{
    /// <summary>The question that was sent.</summary>
    public required string Query { get; init; }

    /// <summary>The agent's reply, absent until the turn executes.</summary>
    public string? Answer { get; init; }

    /// <summary>The turn's execution state.</summary>
    public string? ExecutionState { get; init; }
}

/// <summary>One conversation's outcome within a run.</summary>
public sealed record EvaluationTestCaseResult
{
    /// <summary>The test case's identifier.</summary>
    public required string TestCaseId { get; init; }

    /// <summary>The name shown for the conversation.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The case's execution state, which ends at <c>Evaluated</c>.</summary>
    public string? ExecutionState { get; init; }

    /// <summary>The grader's verdict, <c>Pass</c> or <c>Fail</c>.</summary>
    public string? EvaluationResult { get; init; }

    /// <summary>The quality dimensions behind the verdict.</summary>
    public EvaluationQualityDimensions? Dimensions { get; init; }

    /// <summary>The questions and answers for this conversation.</summary>
    public IReadOnlyList<EvaluationQueryResult> Queries { get; init; } = [];

    /// <summary>Whether the grader has produced a verdict for this case.</summary>
    public bool IsEvaluated => EvaluationResult is not null;
}

/// <summary>A run together with its per-case results.</summary>
public sealed record EvaluationRunDetail
{
    /// <summary>The run being described.</summary>
    public required EvaluationRunSummary Summary { get; init; }

    /// <summary>The per-conversation outcomes, in run order.</summary>
    public IReadOnlyList<EvaluationTestCaseResult> TestCases { get; init; } = [];

    /// <summary>How many cases the grader has finished scoring.</summary>
    public int EvaluatedCount => TestCases.Count(testCase => testCase.IsEvaluated);
}

/// <summary>What one <c>POST</c> to the evaluations endpoint asks for.</summary>
public sealed record EvaluationRunStart
{
    /// <summary>The test set to execute.</summary>
    public required string TestSetId { get; init; }

    /// <summary>The name recorded against the run.</summary>
    public required string Name { get; init; }

    /// <summary>The Copilot Studio connection the agent runs as, omitted for an unauthenticated profile.</summary>
    public string? ConnectionId { get; init; }

    /// <summary>Whether to evaluate the published agent rather than the draft.</summary>
    public bool RunOnPublishedBot { get; init; }
}

/// <summary>Reads and writes Copilot Studio maker evaluations for one agent.</summary>
public interface IAgentEvaluationPort
{
    /// <summary>Lists the agent's evaluation runs, newest first.</summary>
    /// <param name="environment">The environment holding the agent.</param>
    /// <param name="agentId">The Dataverse bot id.</param>
    /// <param name="maximumCount">How many runs to ask the service for.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<EvaluationRunSummary>> ListRunsAsync(CopilotEnvironmentContext environment, string agentId, int maximumCount, CancellationToken cancellationToken);

    /// <summary>Reads one run and its per-case results.</summary>
    /// <param name="environment">The environment holding the agent.</param>
    /// <param name="agentId">The Dataverse bot id.</param>
    /// <param name="runId">The run to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<EvaluationRunDetail?> GetRunAsync(CopilotEnvironmentContext environment, string agentId, string runId, CancellationToken cancellationToken);

    /// <summary>Lists the agent's saved test sets.</summary>
    /// <param name="environment">The environment holding the agent.</param>
    /// <param name="agentId">The Dataverse bot id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<EvaluationTestSetSummary>> ListTestSetsAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken);

    /// <summary>Reads the conversations a resolved test set holds.</summary>
    /// <param name="environment">The environment holding the agent.</param>
    /// <param name="agentId">The Dataverse bot id.</param>
    /// <param name="testSet">The set to read, already resolved from the agent's listing.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<EvaluationTestSetDetail?> GetTestSetAsync(CopilotEnvironmentContext environment, string agentId, EvaluationTestSetSummary testSet, CancellationToken cancellationToken);

    /// <summary>Asks the service to invent conversations from the agent's own description.</summary>
    /// <param name="environment">The environment holding the agent.</param>
    /// <param name="agentId">The Dataverse bot id.</param>
    /// <param name="conversationCount">How many conversations to generate.</param>
    /// <param name="turnCount">How many turns each conversation should hold.</param>
    /// <param name="locale">The LCID the conversations are written in.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<IReadOnlyList<EvaluationConversation>> GenerateConversationsAsync(CopilotEnvironmentContext environment, string agentId, int conversationCount, int turnCount, string locale, CancellationToken cancellationToken);

    /// <summary>Creates an empty test set and returns its component id.</summary>
    /// <param name="environment">The environment holding the agent.</param>
    /// <param name="agentId">The Dataverse bot id.</param>
    /// <param name="name">The name to record against the set.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<string> CreateTestSetAsync(CopilotEnvironmentContext environment, string agentId, string name, CancellationToken cancellationToken);

    /// <summary>Adds conversations to a test set in one request.</summary>
    /// <param name="environment">The environment holding the agent.</param>
    /// <param name="agentId">The Dataverse bot id.</param>
    /// <param name="testSetId">The set the conversations belong to.</param>
    /// <param name="conversations">The conversations to add.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<IReadOnlyList<string>> AddConversationsAsync(CopilotEnvironmentContext environment, string agentId, string testSetId, IReadOnlyList<EvaluationConversation> conversations, CancellationToken cancellationToken);

    /// <summary>Starts an evaluation run.</summary>
    /// <param name="environment">The environment holding the agent.</param>
    /// <param name="agentId">The Dataverse bot id.</param>
    /// <param name="start">What the run should execute.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<EvaluationRunSummary> StartRunAsync(CopilotEnvironmentContext environment, string agentId, EvaluationRunStart start, CancellationToken cancellationToken);

    /// <summary>Stops an in-flight run.</summary>
    /// <param name="environment">The environment holding the agent.</param>
    /// <param name="agentId">The Dataverse bot id.</param>
    /// <param name="runId">The run to stop.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<EvaluationRunSummary> CancelRunAsync(CopilotEnvironmentContext environment, string agentId, string runId, CancellationToken cancellationToken);

    /// <summary>Deletes a run that has already reached a terminal state.</summary>
    /// <param name="environment">The environment holding the agent.</param>
    /// <param name="agentId">The Dataverse bot id.</param>
    /// <param name="runId">The run to delete.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<EvaluationRunSummary> DeleteRunAsync(CopilotEnvironmentContext environment, string agentId, string runId, CancellationToken cancellationToken);
}

/// <summary>Deletes evaluation components, which the evaluations API does not expose.</summary>
public interface IEvaluationComponentStore
{
    /// <summary>Deletes a test set or conversation component, cascading to the components it owns.</summary>
    /// <param name="environment">The environment holding the component.</param>
    /// <param name="componentId">The component to delete.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    Task DeleteAsync(CopilotEnvironmentContext environment, string componentId, CancellationToken cancellationToken);
}
