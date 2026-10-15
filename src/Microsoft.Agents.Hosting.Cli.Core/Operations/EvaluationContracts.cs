using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>The selectors every evaluation verb accepts to address one agent.</summary>
public abstract record EvaluationSelector
{
    /// <summary>The project directory holding the configuration. Defaults to the current directory.</summary>
    public string? Project { get; init; }

    /// <summary>The record to act on, by identifier. Wins over the name.</summary>
    public string? AgentId { get; init; }

    /// <summary>The record to act on, by name.</summary>
    public string? AgentName { get; init; }

    /// <summary>The environment to act in, instead of the active one.</summary>
    public string? EnvironmentId { get; init; }
}

/// <summary>Input for <c>msagent agent eval list</c>.</summary>
public sealed record EvaluationListRequest : EvaluationSelector
{
    /// <summary>Maximum runs to return. Defaults to 20.</summary>
    public int? Limit { get; init; }
}

/// <summary>The selectors a verb addressing one existing run accepts.</summary>
public abstract record EvaluationRunSelector : EvaluationSelector
{
    /// <summary>The evaluation run to act on.</summary>
    public string? RunId { get; init; }
}

/// <summary>Input for <c>msagent agent eval show</c>.</summary>
public sealed record EvaluationShowRequest : EvaluationRunSelector
{
    /// <summary>Where to write the full, untruncated run.</summary>
    public string? Output { get; init; }

    /// <summary>Whether to print every conversation's transcript below the results.</summary>
    public bool Transcript { get; init; }

    /// <summary>The one conversation to print, by its position in the results.</summary>
    public int? Conversation { get; init; }
}

/// <summary>Input for <c>msagent agent eval cancel</c>.</summary>
public sealed record EvaluationCancelRequest : EvaluationRunSelector;

/// <summary>Input for <c>msagent agent eval delete</c>.</summary>
public sealed record EvaluationDeleteRequest : EvaluationRunSelector;

/// <summary>Input for <c>msagent agent eval generate</c>.</summary>
public sealed record EvaluationGenerateRequest : EvaluationSelector
{
    /// <summary>How many conversations to generate. Defaults to 10.</summary>
    public int? Count { get; init; }

    /// <summary>How many turns each conversation holds. Defaults to 3.</summary>
    public int? Turns { get; init; }

    /// <summary>Where to write the generated conversations as CSV.</summary>
    public string? Output { get; init; }
}

/// <summary>Input for <c>msagent agent eval run</c>.</summary>
public sealed record EvaluationRunRequest : EvaluationSelector
{
    /// <summary>An existing test set to run, by name or id.</summary>
    public string? TestSet { get; init; }

    /// <summary>A CSV of conversations to create the test set from.</summary>
    public string? FromCsv { get; init; }

    /// <summary>How many conversations to generate instead of reading a file.</summary>
    public int? Generate { get; init; }

    /// <summary>How many turns each generated conversation holds.</summary>
    public int? Turns { get; init; }

    /// <summary>The name of the test set to create.</summary>
    public string? Name { get; init; }

    /// <summary>Whether an existing test set of the same name may be replaced.</summary>
    public bool UpdateTestSet { get; init; }

    /// <summary>The Copilot Studio connection the agent runs as.</summary>
    public string? Connection { get; init; }

    /// <summary>Whether to evaluate the published agent rather than the draft.</summary>
    public bool Published { get; init; }

    /// <summary>Whether to poll until the run reaches a terminal state.</summary>
    public bool Watch { get; init; }

    /// <summary>How long to poll before giving up, in minutes. Defaults to 60.</summary>
    public int? TimeoutMinutes { get; init; }

    /// <summary>Where to write the finished run as JSON.</summary>
    public string? Output { get; init; }
}

/// <summary>Input for <c>msagent agent eval testset list</c>.</summary>
public sealed record EvaluationTestSetListRequest : EvaluationSelector;

/// <summary>Input for <c>msagent agent eval testset show</c>.</summary>
public sealed record EvaluationTestSetShowRequest : EvaluationSelector
{
    /// <summary>The test set to read, by name or id.</summary>
    public string? TestSet { get; init; }
}

/// <summary>Input for <c>msagent agent eval testset create</c>.</summary>
public sealed record EvaluationTestSetCreateRequest : EvaluationSelector
{
    /// <summary>The name to record against the set.</summary>
    public string? Name { get; init; }

    /// <summary>A CSV of conversations to seed the set with.</summary>
    public string? FromCsv { get; init; }

    /// <summary>How many conversations to generate instead of reading a file.</summary>
    public int? Generate { get; init; }

    /// <summary>How many turns each generated conversation holds.</summary>
    public int? Turns { get; init; }
}

/// <summary>Input for <c>msagent agent eval testset update</c>.</summary>
public sealed record EvaluationTestSetUpdateRequest : EvaluationSelector
{
    /// <summary>The test set to change, by name or id.</summary>
    public string? TestSet { get; init; }

    /// <summary>A CSV of conversations to add.</summary>
    public string? FromCsv { get; init; }

    /// <summary>How many conversations to generate instead of reading a file.</summary>
    public int? Generate { get; init; }

    /// <summary>How many turns each generated conversation holds.</summary>
    public int? Turns { get; init; }

    /// <summary>Whether to remove the existing conversations before adding the new ones.</summary>
    public bool Replace { get; init; }
}

/// <summary>Input for <c>msagent agent eval testset delete</c>.</summary>
public sealed record EvaluationTestSetDeleteRequest : EvaluationSelector
{
    /// <summary>The test set to delete, by name or id.</summary>
    public string? TestSet { get; init; }

    /// <summary>Whether to skip the confirmation prompt.</summary>
    public bool Force { get; init; }

    /// <summary>Whether the operator asked not to be prompted.</summary>
    public bool NonInteractive { get; init; }
}

/// <summary>Input for <c>msagent agent eval testset export</c>.</summary>
public sealed record EvaluationTestSetExportRequest : EvaluationSelector
{
    /// <summary>The test set to export, by name or id.</summary>
    public string? TestSet { get; init; }

    /// <summary>Where to write the CSV.</summary>
    public string? Output { get; init; }
}

/// <summary>One run row in the shared run rendering.</summary>
public sealed record EvaluationRunRow
{
    /// <summary>The run's identifier.</summary>
    public required string RunId { get; init; }

    /// <summary>The name recorded when the run started.</summary>
    public string? Name { get; init; }

    /// <summary>The run's current state.</summary>
    public required string State { get; init; }

    /// <summary>The test set the run executed.</summary>
    public string? TestSetId { get; init; }

    /// <summary>The portal's data-type label for the cases the run executed.</summary>
    public string? DataType { get; init; }

    /// <summary>How many test cases the run covers.</summary>
    public int TestCaseCount { get; init; }

    /// <summary>The pass rate, absent until the run finishes.</summary>
    public double? Score { get; init; }

    /// <summary>Test cases the grader passed.</summary>
    public int? Succeeded { get; init; }

    /// <summary>Test cases the grader failed.</summary>
    public int? FailedCount { get; init; }

    /// <summary>The Copilot Credits the run consumed.</summary>
    public double? Credits { get; init; }

    /// <summary>When the run started.</summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>When the run reached a terminal state.</summary>
    public DateTimeOffset? EndTime { get; init; }

    /// <summary>How long the run took, when it has finished.</summary>
    public TimeSpan? Duration => StartTime is null || EndTime is null ? null : EndTime - StartTime;

    /// <summary>Projects a port summary into the rendered row.</summary>
    /// <param name="summary">The run the service reported.</param>
    public static EvaluationRunRow From(EvaluationRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new EvaluationRunRow
        {
            RunId = summary.RunId,
            Name = summary.Name,
            State = summary.State,
            TestSetId = summary.TestSetId,
            DataType = EvaluationFormatting.DescribeDataType(summary.EvaluationSetType),
            TestCaseCount = summary.TestCaseCount,
            Score = summary.Totals?.Score,
            Succeeded = summary.Totals?.Succeeded,
            FailedCount = summary.Totals?.Failed,
            Credits = summary.Credits,
            StartTime = summary.StartTime,
            EndTime = summary.EndTime,
        };
    }
}

/// <summary>Result of <c>msagent agent eval list</c>.</summary>
public sealed record EvaluationListResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string.</summary>
    public string Status { get; init; } = "evaluations-listed";

    /// <summary>The environment the runs live in.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The agent the runs belong to.</summary>
    public required string McsAgentId { get; init; }

    /// <summary>The row limit that was applied.</summary>
    public int Limit { get; init; }

    /// <summary>Whether collection stopped at the limit.</summary>
    public bool Truncated { get; init; }

    /// <summary>The runs found, newest first.</summary>
    public IReadOnlyList<EvaluationRunRow> Runs { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        if (Runs.Count == 0)
        {
            return ["No evaluation runs found. Start one with `msagent agent eval run`."];
        }

        return
        [
            $"{Runs.Count} evaluation run(s){(Truncated ? $", truncated at --limit {Limit}" : string.Empty)}.",
            .. TextGrid.RenderTable(
                "Evaluation runs",
                ["RUN ID", "NAME", "STATE", "CASES", "SCORE", "CREDITS", "STARTED"],
                [.. Runs.Select(run => new[]
                {
                    run.RunId,
                    TextGrid.DescribeOrDash(run.Name),
                    run.State,
                    run.TestCaseCount.ToString(),
                    EvaluationFormatting.DescribeScore(run.Score),
                    EvaluationFormatting.DescribeCredits(run.Credits),
                    TextGrid.DescribeTimestamp(run.StartTime),
                })]),
        ];
    }
}

/// <summary>Result of the verbs that report one run.</summary>
public sealed record EvaluationRunResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string.</summary>
    public required string Status { get; init; }

    /// <summary>The environment the run lives in.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The agent the run belongs to.</summary>
    public required string McsAgentId { get; init; }

    /// <summary>The run being reported.</summary>
    public required EvaluationRunRow Run { get; init; }

    /// <summary>The test set the run executed.</summary>
    public string? TestSetName { get; init; }

    /// <summary>The per-conversation outcomes, empty when only the summary was read.</summary>
    public IReadOnlyList<EvaluationCaseRow> Cases { get; init; } = [];

    /// <summary>Where the run was written, when an output path was given.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Whether polling stopped before the run reached a terminal state.</summary>
    public bool TimedOut { get; init; }

    /// <summary>The conversations to print in full, by their position in <see cref="Cases"/>.</summary>
    public IReadOnlyList<int> TranscriptNumbers { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        var lines = new List<string>
        {
            $"Evaluation run {Run.RunId} is {Run.State}.",
            $"  Name:      {TextGrid.DescribeOrDash(Run.Name)}",
            $"  Test set:  {TextGrid.DescribeOrDash(TestSetName ?? Run.TestSetId)}",
            $"  Data type: {TextGrid.DescribeOrDash(Run.DataType)}",
            $"  Cases:     {Run.TestCaseCount}",
            $"  Score:     {EvaluationFormatting.DescribeScore(Run.Score)}{EvaluationFormatting.DescribeTotals(Run.Succeeded, Run.FailedCount)}",
            $"  Credits:   {EvaluationFormatting.DescribeCredits(Run.Credits)}",
            $"  Duration:  {EvaluationFormatting.DescribeDuration(Run.Duration)}",
        };

        if (Cases.Count > 0)
        {
            lines.AddRange(TextGrid.RenderTable(
                "Results",
                ["RESULT", "MESSAGES", "RELEVANCE", "GROUNDEDNESS", "COMPLETENESS", "ABSTENTION", "CONVERSATION"],
                [.. Cases.Select(testCase => new[]
                {
                    TextGrid.DescribeOrDash(testCase.EvaluationResult),
                    testCase.MessageCount.ToString(),
                    TextGrid.DescribeOrDash(testCase.Relevance),
                    TextGrid.DescribeOrDash(testCase.Groundedness),
                    TextGrid.DescribeOrDash(testCase.Completeness),
                    TextGrid.DescribeOrDash(testCase.Abstention),
                    EvaluationFormatting.Shorten(testCase.DisplayName),
                })]));
        }

        AddTranscripts(lines);

        if (OutputPath is not null)
        {
            lines.Add($"Written to {OutputPath}.");
        }

        if (TimedOut)
        {
            lines.Add($"Still running. Check it later with `msagent agent eval show --run-id {Run.RunId}`.");
        }

        return lines;
    }

    private void AddTranscripts(List<string> lines)
    {
        var truncated = false;

        foreach (var number in TranscriptNumbers.Where(number => number >= 1 && number <= Cases.Count))
        {
            var testCase = Cases[number - 1];

            lines.Add(string.Empty);
            lines.Add($"  Conversation {number} — {TextGrid.DescribeOrDash(testCase.EvaluationResult)} ({testCase.MessageCount} message(s))");

            foreach (var turn in testCase.Turns)
            {
                lines.Add($"  > {EvaluationFormatting.Shorten(turn.Query)}");
                lines.AddRange(EvaluationFormatting.ReplyLines(turn.Answer, out var replyTruncated).Select(line => $"    {line}"));
                truncated |= replyTruncated;
            }
        }

        if (truncated)
        {
            lines.Add(string.Empty);
            lines.Add("Replies are shortened. Write the full transcript with --output <file>.");
        }
    }
}

/// <summary>One conversation's outcome in a reported run.</summary>
public sealed record EvaluationCaseRow
{
    /// <summary>The test case's identifier.</summary>
    public required string TestCaseId { get; init; }

    /// <summary>The conversation's name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The grader's verdict.</summary>
    public string? EvaluationResult { get; init; }

    /// <summary>Whether the reply addressed the question.</summary>
    public string? Relevance { get; init; }

    /// <summary>Whether the reply was supported by the agent's sources.</summary>
    public string? Groundedness { get; init; }

    /// <summary>Whether the reply covered what the question asked for.</summary>
    public string? Completeness { get; init; }

    /// <summary>Whether the agent declined to answer.</summary>
    public string? Abstention { get; init; }

    /// <summary>The questions and answers for this conversation.</summary>
    public IReadOnlyList<EvaluationTurnRow> Turns { get; init; } = [];

    /// <summary>How many user and agent messages the conversation has produced so far.</summary>
    public int MessageCount => Turns.Count + Turns.Count(turn => !string.IsNullOrWhiteSpace(turn.Answer));

    /// <summary>Projects a port result into the rendered row.</summary>
    /// <param name="testCase">The case the service reported.</param>
    public static EvaluationCaseRow From(EvaluationTestCaseResult testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);

        return new EvaluationCaseRow
        {
            TestCaseId = testCase.TestCaseId,
            DisplayName = testCase.DisplayName,
            EvaluationResult = testCase.EvaluationResult,
            Relevance = testCase.Dimensions?.Relevance,
            Groundedness = testCase.Dimensions?.Groundedness,
            Completeness = testCase.Dimensions?.Completeness,
            Abstention = testCase.Dimensions?.Abstention,
            Turns = [.. testCase.Queries.Select(query => new EvaluationTurnRow { Query = query.Query, Answer = query.Answer })],
        };
    }
}

/// <summary>One question and answer inside a reported result.</summary>
public sealed record EvaluationTurnRow
{
    /// <summary>The question that was sent.</summary>
    public required string Query { get; init; }

    /// <summary>The agent's reply.</summary>
    public string? Answer { get; init; }
}

/// <summary>Result of <c>msagent agent eval testset list</c>.</summary>
public sealed record EvaluationTestSetListResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string.</summary>
    public string Status { get; init; } = "test-sets-listed";

    /// <summary>The environment the test sets live in.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The agent the test sets belong to.</summary>
    public required string McsAgentId { get; init; }

    /// <summary>The test sets found.</summary>
    public IReadOnlyList<EvaluationTestSetRow> TestSets { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        if (TestSets.Count == 0)
        {
            return ["No evaluation test sets found. Create one with `msagent agent eval testset create`."];
        }

        return
        [
            $"{TestSets.Count} test set(s).",
            .. TextGrid.RenderTable(
                "Test sets",
                ["TEST SET ID", "NAME", "CONVERSATIONS", "TYPE"],
                [.. TestSets.Select(testSet => new[]
                {
                    testSet.TestSetId,
                    testSet.DisplayName,
                    testSet.ConversationCount.ToString(),
                    TextGrid.DescribeOrDash(testSet.EvaluationSetType),
                })]),
        ];
    }
}

/// <summary>One test set row.</summary>
public sealed record EvaluationTestSetRow
{
    /// <summary>The test set's component id.</summary>
    public required string TestSetId { get; init; }

    /// <summary>The name shown in the portal.</summary>
    public required string DisplayName { get; init; }

    /// <summary>How many conversations the set holds.</summary>
    public int ConversationCount { get; init; }

    /// <summary>Whether the set holds single-turn or multi-turn cases.</summary>
    public string? EvaluationSetType { get; init; }

    /// <summary>Projects a port summary into the rendered row.</summary>
    /// <param name="summary">The test set the service reported.</param>
    public static EvaluationTestSetRow From(EvaluationTestSetSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new EvaluationTestSetRow
        {
            TestSetId = summary.TestSetId,
            DisplayName = summary.DisplayName,
            ConversationCount = summary.ConversationCount,
            EvaluationSetType = summary.EvaluationSetType,
        };
    }
}

/// <summary>Result of the verbs that report one test set.</summary>
public sealed record EvaluationTestSetResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string.</summary>
    public required string Status { get; init; }

    /// <summary>The environment the test set lives in.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The agent the test set belongs to.</summary>
    public required string McsAgentId { get; init; }

    /// <summary>The test set being reported.</summary>
    public required EvaluationTestSetRow TestSet { get; init; }

    /// <summary>The conversations the set holds.</summary>
    public IReadOnlyList<EvaluationConversationRow> Conversations { get; init; } = [];

    /// <summary>Where the set was written, when an output path was given.</summary>
    public string? OutputPath { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        var lines = new List<string>
        {
            $"Test set '{TestSet.DisplayName}' ({TestSet.TestSetId}) holds {TestSet.ConversationCount} conversation(s).",
        };

        if (Conversations.Count > 0)
        {
            lines.AddRange(TextGrid.RenderTable(
                "Conversations",
                ["#", "TURNS", "SOURCE", "FIRST QUESTION"],
                [.. Conversations.Select(conversation => new[]
                {
                    conversation.Number.ToString(),
                    conversation.TurnCount.ToString(),
                    conversation.Source,
                    EvaluationFormatting.Shorten(conversation.FirstQuestion),
                })]));
        }

        if (OutputPath is not null)
        {
            lines.Add($"Written to {OutputPath}.");
        }

        return lines;
    }
}

/// <summary>One conversation row in a reported test set.</summary>
public sealed record EvaluationConversationRow
{
    /// <summary>The conversation's ordinal.</summary>
    public required int Number { get; init; }

    /// <summary>How many question-and-answer pairs it holds.</summary>
    public int TurnCount { get; init; }

    /// <summary>Where the conversation came from.</summary>
    public required string Source { get; init; }

    /// <summary>The first question, which names the conversation.</summary>
    public required string FirstQuestion { get; init; }

    /// <summary>Every question and reference answer the conversation holds, in order.</summary>
    public IReadOnlyList<EvaluationTurnRow> Turns { get; init; } = [];

    /// <summary>Projects a conversation into the rendered row.</summary>
    /// <param name="conversation">The conversation to project.</param>
    public static EvaluationConversationRow From(EvaluationConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        return new EvaluationConversationRow
        {
            Number = conversation.Number,
            TurnCount = conversation.Exchanges.Count,
            Source = conversation.Source.ToString(),
            FirstQuestion = conversation.DisplayName,
            Turns = [.. conversation.Exchanges.Select(exchange => new EvaluationTurnRow { Query = exchange.Question, Answer = exchange.Response })],
        };
    }
}

/// <summary>Result of <c>msagent agent eval testset delete</c>.</summary>
public sealed record EvaluationTestSetDeleteResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string.</summary>
    public string Status { get; init; } = "test-set-deleted";

    /// <summary>The environment the test set lived in.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The test set that was deleted.</summary>
    public required string TestSetId { get; init; }

    /// <summary>The name the test set carried.</summary>
    public required string DisplayName { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() => Status switch
    {
        "test-set-deleted" =>
        [
            $"Deleted test set '{DisplayName}' ({TestSetId}). Its conversations were removed with it.",
            "Evaluation runs that used it are unaffected.",
        ],
        "name-mismatch" => [$"Test set '{DisplayName}' was not deleted: the typed name did not match."],
        _ => [$"Test set '{DisplayName}' was not deleted."],
    };
}

/// <summary>Result of <c>msagent agent eval generate</c>.</summary>
public sealed record EvaluationGenerateResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string.</summary>
    public string Status { get; init; } = "conversations-generated";

    /// <summary>The environment the agent lives in.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The agent the conversations were generated from.</summary>
    public required string McsAgentId { get; init; }

    /// <summary>The conversations that were generated.</summary>
    public IReadOnlyList<EvaluationConversationRow> Conversations { get; init; } = [];

    /// <summary>Where the conversations were written.</summary>
    public string? OutputPath { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        var lines = new List<string> { $"Generated {Conversations.Count} conversation(s)." };

        lines.AddRange(TextGrid.RenderTable(
            "Conversations",
            ["#", "TURNS", "FIRST QUESTION"],
            [.. Conversations.Select(conversation => new[]
            {
                conversation.Number.ToString(),
                conversation.TurnCount.ToString(),
                EvaluationFormatting.Shorten(conversation.FirstQuestion),
            })]));

        if (OutputPath is not null)
        {
            lines.Add($"Written to {OutputPath}.");
        }

        return lines;
    }
}

/// <summary>The shared renderings the evaluation results draw with.</summary>
public static class EvaluationFormatting
{
    private const int MaximumCellLength = 60;

    private const int MaximumReplyLines = 6;

    /// <summary>Renders a pass rate as a percentage, or a dash when nothing was scored.</summary>
    /// <param name="score">The pass rate.</param>
    public static string DescribeScore(double? score) => score is null ? TextGrid.Dash : $"{score.Value * 100:0}%";

    /// <summary>Renders an evaluation set type as the data-type label the portal shows.</summary>
    /// <param name="evaluationSetType">The set type the service reported.</param>
    public static string? DescribeDataType(string? evaluationSetType) => string.IsNullOrWhiteSpace(evaluationSetType)
        ? null
        : string.Equals(evaluationSetType, EvaluationSetTypes.MultiTurn, StringComparison.OrdinalIgnoreCase) ? "Conversation" : evaluationSetType;

    /// <summary>Renders a credit cost, or a dash when the service reported none.</summary>
    /// <param name="credits">The credits consumed.</param>
    public static string DescribeCredits(double? credits) => credits is null ? TextGrid.Dash : credits.Value.ToString("0.##");

    /// <summary>Renders a duration, or a dash when the run has not finished.</summary>
    /// <param name="duration">How long the run took.</param>
    public static string DescribeDuration(TimeSpan? duration) => duration is null ? TextGrid.Dash : duration.Value.ToString(@"hh\:mm\:ss");

    /// <summary>Renders the passed and failed counts as a parenthetical, or nothing when absent.</summary>
    /// <param name="succeeded">Cases the grader passed.</param>
    /// <param name="failed">Cases the grader failed.</param>
    public static string DescribeTotals(int? succeeded, int? failed) =>
        succeeded is null || failed is null ? string.Empty : $" ({succeeded} passed, {failed} failed)";

    /// <summary>Collapses a value onto one line and trims it to the table's cell width.</summary>
    /// <param name="value">The text to shorten.</param>
    public static string Shorten(string? value) => Collapse(value, MaximumCellLength);

    /// <summary>Collapses a value onto one line without trimming it.</summary>
    /// <param name="value">The text to collapse.</param>
    public static string OneLine(string? value) => Collapse(value, int.MaxValue);

    /// <summary>Splits an agent reply into the lines the transcript shows, keeping its structure.</summary>
    /// <param name="value">The reply to render.</param>
    /// <param name="truncated">Whether lines were dropped.</param>
    public static IReadOnlyList<string> ReplyLines(string? value, out bool truncated)
    {
        truncated = false;

        if (string.IsNullOrWhiteSpace(value))
        {
            return [TextGrid.Dash];
        }

        var lines = value.ReplaceLineEndings("\n").Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0).ToArray();

        if (lines.Length <= MaximumReplyLines)
        {
            return lines;
        }

        truncated = true;

        return [.. lines.Take(MaximumReplyLines), $"\u2026 (+{lines.Length - MaximumReplyLines} more line(s))"];
    }

    private static string Collapse(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TextGrid.Dash;
        }

        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length <= maximumLength ? collapsed : $"{collapsed[..(maximumLength - 1)]}\u2026";
    }
}
