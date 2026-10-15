using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Evaluation;

public sealed class EvaluationRenderingTests
{
    private const string Dash = "\u2014";

    private static string Render(ITextGridRenderable result) => string.Join('\n', result.ToTextLines());

    [Fact]
    public void RunRow_ProjectsTotalsAndCredits()
    {
        var row = EvaluationRunRow.From(new EvaluationRunSummary
        {
            RunId = "run-1",
            Name = "Nightly",
            State = "Completed",
            TestCaseCount = 4,
            Credits = 27.16,
            StartTime = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero),
            EndTime = new DateTimeOffset(2026, 10, 4, 12, 3, 30, TimeSpan.Zero),
            Totals = new EvaluationRunTotals { Succeeded = 3, Failed = 1 },
        });

        Assert.Equal(0.75, row.Score);
        Assert.Equal(3, row.Succeeded);
        Assert.Equal(1, row.FailedCount);
        Assert.Equal(27.16, row.Credits);
        Assert.Equal(TimeSpan.FromSeconds(210), row.Duration);
    }

    [Fact]
    public void RunRow_LeavesScoreAndDurationAbsentWhileTheRunIsInFlight()
    {
        var row = EvaluationRunRow.From(new EvaluationRunSummary { RunId = "run-1", State = "InProgress", TestCaseCount = 4 });

        Assert.Null(row.Score);
        Assert.Null(row.Succeeded);
        Assert.Null(row.FailedCount);
        Assert.Null(row.Credits);
        Assert.Null(row.Duration);
    }

    [Fact]
    public void RunTotals_ScoreIsNullWhenNothingWasScored()
    {
        Assert.Null(new EvaluationRunTotals().Score);
    }

    [Fact]
    public void Backfill_TakesWhatTheAnswerLeftOutFromTheReading()
    {
        var answered = new EvaluationRunSummary { RunId = "run-1", State = "Cancelled" };

        var merged = answered.Backfill(new EvaluationRunSummary
        {
            RunId = "run-1",
            State = "InProgress",
            Name = "Nightly",
            TestSetId = "testset-7",
            EvaluationSetType = EvaluationSetTypes.MultiTurn,
            TestCaseCount = 3,
            StartTime = DateTimeOffset.UnixEpoch,
            Credits = 13.58,
            Totals = new EvaluationRunTotals { Succeeded = 2, Failed = 1 },
        });

        Assert.Equal("Cancelled", merged.State);
        Assert.Equal("Nightly", merged.Name);
        Assert.Equal("testset-7", merged.TestSetId);
        Assert.Equal(EvaluationSetTypes.MultiTurn, merged.EvaluationSetType);
        Assert.Equal(3, merged.TestCaseCount);
        Assert.Equal(DateTimeOffset.UnixEpoch, merged.StartTime);
        Assert.Equal(13.58, merged.Credits);
        Assert.Equal(2, merged.Totals!.Succeeded);
    }

    [Fact]
    public void Backfill_KeepsEveryFieldTheAnswerReported()
    {
        var answered = new EvaluationRunSummary
        {
            RunId = "run-1",
            State = "Completed",
            Name = "Answered",
            TestSetId = "testset-answered",
            EvaluationSetType = "SingleTurn",
            TestCaseCount = 9,
            StartTime = DateTimeOffset.UnixEpoch.AddDays(2),
            EndTime = DateTimeOffset.UnixEpoch.AddDays(3),
            Credits = 1,
            Totals = new EvaluationRunTotals { Succeeded = 9 },
        };

        var merged = answered.Backfill(new EvaluationRunSummary
        {
            RunId = "run-1",
            State = "InProgress",
            Name = "Stale",
            TestSetId = "testset-stale",
            EvaluationSetType = EvaluationSetTypes.MultiTurn,
            TestCaseCount = 3,
            StartTime = DateTimeOffset.UnixEpoch,
            EndTime = DateTimeOffset.UnixEpoch.AddDays(1),
            Credits = 99,
            Totals = new EvaluationRunTotals { Failed = 3 },
        });

        Assert.Equal(answered, merged);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Backfill_TreatsAnEmptyNameAsAbsent(string? reported)
    {
        var merged = new EvaluationRunSummary { RunId = "run-1", State = "Cancelled", Name = reported }
            .Backfill(new EvaluationRunSummary { RunId = "run-1", State = "InProgress", Name = "Nightly" });

        Assert.Equal("Nightly", merged.Name);
    }

    [Fact]
    public void Backfill_TakesTheRequestedIdentityWhenAStartAnswerOmitsIt()
    {
        var merged = new EvaluationRunSummary { RunId = "run-1", State = "Queued" }
            .Backfill(new EvaluationRunStart { TestSetId = "testset-7", Name = "Nightly" });

        Assert.Equal("Nightly", merged.Name);
        Assert.Equal("testset-7", merged.TestSetId);
        Assert.Equal("Queued", merged.State);
    }

    [Fact]
    public void RunRow_CarriesTheTestSetAndDataTypeTheRunReported()
    {
        var row = EvaluationRunRow.From(new EvaluationRunSummary
        {
            RunId = "run-1",
            State = "Completed",
            TestSetId = "testset-7",
            EvaluationSetType = EvaluationSetTypes.MultiTurn,
        });

        Assert.Equal("testset-7", row.TestSetId);
        Assert.Equal("Conversation", row.DataType);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("MultiTurn", "Conversation")]
    [InlineData("multiturn", "Conversation")]
    [InlineData("SingleTurn", "SingleTurn")]
    public void DescribeDataType_UsesThePortalLabelForConversations(string? evaluationSetType, string? expected)
    {
        Assert.Equal(expected, EvaluationFormatting.DescribeDataType(evaluationSetType));
    }

    [Fact]
    public void RunResult_ShowsTheDataTypeAndFallsBackToTheTestSetId()
    {
        var text = Render(new EvaluationRunResult
        {
            Status = "evaluation-shown",
            EnvironmentId = "env-1",
            McsAgentId = "bot-1",
            Run = new EvaluationRunRow { RunId = "run-1", State = "Completed", TestSetId = "testset-7", DataType = "Conversation", TestCaseCount = 3 },
        });

        Assert.Contains("Test set:  testset-7", text, StringComparison.Ordinal);
        Assert.Contains("Data type: Conversation", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunResult_PrefersTheTestSetNameOverTheId()
    {
        var text = Render(new EvaluationRunResult
        {
            Status = "evaluation-completed",
            EnvironmentId = "env-1",
            McsAgentId = "bot-1",
            TestSetName = "Nightly",
            Run = new EvaluationRunRow { RunId = "run-1", State = "Completed", TestSetId = "testset-7" },
        });

        Assert.Contains("Test set:  Nightly", text, StringComparison.Ordinal);
        Assert.DoesNotContain("testset-7", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunResult_DashesTheTestSetAndDataTypeWhenTheServiceReportedNeither()
    {
        var text = Render(new EvaluationRunResult
        {
            Status = "evaluation-cancelled",
            EnvironmentId = "env-1",
            McsAgentId = "bot-1",
            Run = new EvaluationRunRow { RunId = "run-1", State = "Cancelled" },
        });

        Assert.Contains($"Test set:  {Dash}", text, StringComparison.Ordinal);
        Assert.Contains($"Data type: {Dash}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunTotals_CountsInvalidAndErroredCasesAgainstTheScore()
    {
        var totals = new EvaluationRunTotals { Succeeded = 1, Failed = 1, Invalid = 1, Errors = 1 };

        Assert.Equal(4, totals.Total);
        Assert.Equal(0.25, totals.Score);
    }

    [Fact]
    public void CaseRow_FlattensTheQualityDimensions()
    {
        var row = EvaluationCaseRow.From(new EvaluationTestCaseResult
        {
            TestCaseId = "case-1",
            DisplayName = "Refunds",
            EvaluationResult = "Pass",
            Dimensions = new EvaluationQualityDimensions { Relevance = "Pass", Groundedness = "Fail", Completeness = "Pass", Abstention = "Pass" },
            Queries = [new EvaluationQueryResult { Query = "Hello", Answer = "Hi" }],
        });

        Assert.Equal("Pass", row.EvaluationResult);
        Assert.Equal("Fail", row.Groundedness);
        Assert.Equal("Hi", Assert.Single(row.Turns).Answer);
    }

    [Fact]
    public void CaseRow_SurvivesAbsentGraderResults()
    {
        var row = EvaluationCaseRow.From(new EvaluationTestCaseResult { TestCaseId = "case-1" });

        Assert.Null(row.EvaluationResult);
        Assert.Null(row.Relevance);
        Assert.Null(row.Groundedness);
        Assert.Null(row.Completeness);
        Assert.Null(row.Abstention);
        Assert.Empty(row.Turns);
    }

    [Fact]
    public void CaseRow_CountsEveryUserAndAgentMessage()
    {
        var row = EvaluationCaseRow.From(new EvaluationTestCaseResult
        {
            TestCaseId = "case-1",
            Queries =
            [
                new EvaluationQueryResult { Query = "first", Answer = "replied" },
                new EvaluationQueryResult { Query = "second", Answer = "replied" },
            ],
        });

        Assert.Equal(4, row.MessageCount);
    }

    [Fact]
    public void CaseRow_CountsOnlyTheQuestionsWhileTheConversationIsStillRunning()
    {
        var row = EvaluationCaseRow.From(new EvaluationTestCaseResult
        {
            TestCaseId = "case-1",
            Queries =
            [
                new EvaluationQueryResult { Query = "first", Answer = "replied" },
                new EvaluationQueryResult { Query = "second" },
                new EvaluationQueryResult { Query = "third", Answer = "   " },
            ],
        });

        Assert.Equal(4, row.MessageCount);
    }

    [Fact]
    public void CaseRow_CountsNoMessagesForAnUnstartedConversation()
    {
        Assert.Equal(0, EvaluationCaseRow.From(new EvaluationTestCaseResult { TestCaseId = "case-1" }).MessageCount);
    }

    [Fact]
    public void ConversationRow_CountsTurnsAndNamesItselfFromTheFirstQuestion()
    {
        var row = EvaluationConversationRow.From(new EvaluationConversation
        {
            Number = 2,
            Source = EvaluationConversationSource.Generated,
            Exchanges = [new EvaluationExchange("First", "A"), new EvaluationExchange("Second", null)],
        });

        Assert.Equal(2, row.Number);
        Assert.Equal(2, row.TurnCount);
        Assert.Equal("Generated", row.Source);
        Assert.Equal("First", row.FirstQuestion);
    }

    [Fact]
    public void ConversationRow_CarriesEveryQuestionAndReferenceAnswer()
    {
        var row = EvaluationConversationRow.From(new EvaluationConversation
        {
            Number = 1,
            Source = EvaluationConversationSource.Generated,
            Exchanges = [new EvaluationExchange("First", "A"), new EvaluationExchange("Second", null)],
        });

        Assert.Equal(["First", "Second"], row.Turns.Select(turn => turn.Query));
        Assert.Equal("A", row.Turns[0].Answer);
        Assert.Null(row.Turns[1].Answer);
    }

    [Fact]
    public void ConversationRow_NamesAnEmptyConversationByItsNumber()
    {
        var row = EvaluationConversationRow.From(new EvaluationConversation { Number = 7, Exchanges = [] });

        Assert.Equal("Conversation 7", row.FirstQuestion);
        Assert.Empty(row.Turns);
    }

    [Fact]
    public void ListResult_GuidesTheOperatorWhenThereAreNoRuns()
    {
        var text = Render(new EvaluationListResult { EnvironmentId = "env-1", McsAgentId = "bot-1" });

        Assert.Contains("No evaluation runs found", text, StringComparison.Ordinal);
        Assert.Contains("msagent agent eval run", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ListResult_ReportsTruncation()
    {
        var text = Render(new EvaluationListResult
        {
            EnvironmentId = "env-1",
            McsAgentId = "bot-1",
            Limit = 1,
            Truncated = true,
            Runs = [new EvaluationRunRow { RunId = "run-1", State = "Completed", TestCaseCount = 2, Score = 1, Credits = 13.58 }],
        });

        Assert.Contains("truncated at --limit 1", text, StringComparison.Ordinal);
        Assert.Contains("100%", text, StringComparison.Ordinal);
        Assert.Contains("13.58", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ListResult_RendersInFlightRunsWithDashes()
    {
        var text = Render(new EvaluationListResult
        {
            EnvironmentId = "env-1",
            McsAgentId = "bot-1",
            Runs = [new EvaluationRunRow { RunId = "run-1", State = "InProgress", TestCaseCount = 2 }],
        });

        Assert.Contains("InProgress", text, StringComparison.Ordinal);
        Assert.Contains(Dash, text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunResult_ReportsCreditsAndTotalsAfterTheRun()
    {
        var text = Render(new EvaluationRunResult
        {
            Status = "evaluation-completed",
            EnvironmentId = "env-1",
            McsAgentId = "bot-1",
            TestSetName = "Nightly",
            Run = new EvaluationRunRow
            {
                RunId = "run-1",
                Name = "Nightly",
                State = "Completed",
                TestCaseCount = 2,
                Score = 1,
                Succeeded = 2,
                FailedCount = 0,
                Credits = 13.58,
                StartTime = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero),
                EndTime = new DateTimeOffset(2026, 10, 4, 12, 1, 0, TimeSpan.Zero),
            },
            Cases = [new EvaluationCaseRow { TestCaseId = "case-1", DisplayName = "Refunds", EvaluationResult = "Pass", Relevance = "Pass" }],
        });

        Assert.Contains("Evaluation run run-1 is Completed.", text, StringComparison.Ordinal);
        Assert.Contains("Test set:  Nightly", text, StringComparison.Ordinal);
        Assert.Contains("100% (2 passed, 0 failed)", text, StringComparison.Ordinal);
        Assert.Contains("Credits:   13.58", text, StringComparison.Ordinal);
        Assert.Contains("00:01:00", text, StringComparison.Ordinal);
        Assert.Contains("Refunds", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunResult_OmitsTheResultsTableWhenOnlyTheSummaryIsKnown()
    {
        var text = Render(new EvaluationRunResult
        {
            Status = "evaluation-started",
            EnvironmentId = "env-1",
            McsAgentId = "bot-1",
            Run = new EvaluationRunRow { RunId = "run-1", State = "Queued" },
        });

        Assert.DoesNotContain("RELEVANCE", text, StringComparison.Ordinal);
        Assert.Contains($"Credits:   {Dash}", text, StringComparison.Ordinal);
        Assert.Contains($"Duration:  {Dash}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunResult_PointsAtShowWhenPollingTimedOut()
    {
        var text = Render(new EvaluationRunResult
        {
            Status = "evaluation-running",
            EnvironmentId = "env-1",
            McsAgentId = "bot-1",
            Run = new EvaluationRunRow { RunId = "run-1", State = "InProgress" },
            TimedOut = true,
        });

        Assert.Contains("msagent agent eval show --run-id run-1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunResult_ReportsWhereItWasWritten()
    {
        var text = Render(new EvaluationRunResult
        {
            Status = "evaluation-completed",
            EnvironmentId = "env-1",
            McsAgentId = "bot-1",
            Run = new EvaluationRunRow { RunId = "run-1", State = "Completed" },
            OutputPath = "run.json",
        });

        Assert.Contains("Written to run.json.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TestSetListResult_GuidesTheOperatorWhenThereAreNoSets()
    {
        var text = Render(new EvaluationTestSetListResult { EnvironmentId = "env-1", McsAgentId = "bot-1" });

        Assert.Contains("msagent agent eval testset create", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TestSetListResult_RendersEverySet()
    {
        var text = Render(new EvaluationTestSetListResult
        {
            EnvironmentId = "env-1",
            McsAgentId = "bot-1",
            TestSets = [new EvaluationTestSetRow { TestSetId = "testset-1", DisplayName = "Nightly", ConversationCount = 13, EvaluationSetType = "MultiTurn" }],
        });

        Assert.Contains("1 test set(s).", text, StringComparison.Ordinal);
        Assert.Contains("Nightly", text, StringComparison.Ordinal);
        Assert.Contains("MultiTurn", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TestSetResult_RendersTheConversationTable()
    {
        var text = Render(new EvaluationTestSetResult
        {
            Status = "test-set-created",
            EnvironmentId = "env-1",
            McsAgentId = "bot-1",
            TestSet = new EvaluationTestSetRow { TestSetId = "testset-1", DisplayName = "Nightly", ConversationCount = 1 },
            Conversations = [new EvaluationConversationRow { Number = 1, TurnCount = 2, Source = "Imported", FirstQuestion = "What is the refund window?" }],
        });

        Assert.Contains("Test set 'Nightly' (testset-1) holds 1 conversation(s).", text, StringComparison.Ordinal);
        Assert.Contains("What is the refund window?", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("test-set-deleted", "Its conversations were removed with it.")]
    [InlineData("name-mismatch", "the typed name did not match")]
    [InlineData("cancelled", "was not deleted.")]
    public void TestSetDeleteResult_RendersEveryOutcome(string status, string expected)
    {
        var text = Render(new EvaluationTestSetDeleteResult { Status = status, EnvironmentId = "env-1", TestSetId = "testset-1", DisplayName = "Nightly" });

        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateResult_ReportsWhereItWasWritten()
    {
        var text = Render(new EvaluationGenerateResult
        {
            EnvironmentId = "env-1",
            McsAgentId = "bot-1",
            Conversations = [new EvaluationConversationRow { Number = 1, TurnCount = 3, Source = "Generated", FirstQuestion = "Hello" }],
            OutputPath = "out.csv",
        });

        Assert.Contains("Generated 1 conversation(s).", text, StringComparison.Ordinal);
        Assert.Contains("Written to out.csv.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, Dash)]
    [InlineData(0d, "0%")]
    [InlineData(0.5d, "50%")]
    [InlineData(1d, "100%")]
    [InlineData(0.666d, "67%")]
    public void DescribeScore_RendersAPercentage(double? score, string expected)
    {
        Assert.Equal(expected, EvaluationFormatting.DescribeScore(score));
    }

    [Theory]
    [InlineData(null, Dash)]
    [InlineData(0d, "0")]
    [InlineData(13.58d, "13.58")]
    [InlineData(91.2d, "91.2")]
    [InlineData(13.5839d, "13.58")]
    public void DescribeCredits_TrimsToTwoDecimals(double? credits, string expected)
    {
        Assert.Equal(expected, EvaluationFormatting.DescribeCredits(credits));
    }

    [Fact]
    public void DescribeDuration_RendersHoursMinutesAndSeconds()
    {
        Assert.Equal(Dash, EvaluationFormatting.DescribeDuration(null));
        Assert.Equal("01:02:03", EvaluationFormatting.DescribeDuration(new TimeSpan(1, 2, 3)));
    }

    [Theory]
    [InlineData(null, null, "")]
    [InlineData(2, null, "")]
    [InlineData(null, 1, "")]
    [InlineData(2, 1, " (2 passed, 1 failed)")]
    public void DescribeTotals_OnlyRendersWhenBothCountsAreKnown(int? succeeded, int? failed, string expected)
    {
        Assert.Equal(expected, EvaluationFormatting.DescribeTotals(succeeded, failed));
    }

    [Fact]
    public void Shorten_CollapsesLineEndingsAndTrimsLongText()
    {
        Assert.Equal(Dash, EvaluationFormatting.Shorten(null));
        Assert.Equal(Dash, EvaluationFormatting.Shorten("   "));
        Assert.Equal("one two", EvaluationFormatting.Shorten("one\r\ntwo"));

        var shortened = EvaluationFormatting.Shorten(new string('x', 100));

        Assert.Equal(60, shortened.Length);
        Assert.EndsWith("\u2026", shortened, StringComparison.Ordinal);
    }

    [Fact]
    public void Shorten_LeavesTextAtTheLimitIntact()
    {
        var exact = new string('x', 60);

        Assert.Equal(exact, EvaluationFormatting.Shorten(exact));
    }

    [Fact]
    public void Shorten_CollapsesRunsOfWhitespace()
    {
        Assert.Equal("one two three", EvaluationFormatting.Shorten("one\n\n  two\tthree"));
    }

    [Fact]
    public void OneLine_CollapsesWithoutTrimming()
    {
        var value = new string('x', 400);

        Assert.Equal(value, EvaluationFormatting.OneLine(value));
        Assert.Equal("one two", EvaluationFormatting.OneLine("one\n  two"));
        Assert.Equal(Dash, EvaluationFormatting.OneLine(null));
        Assert.Equal(Dash, EvaluationFormatting.OneLine("  "));
    }

    [Fact]
    public void ReplyLines_KeepsTheRepliesStructure()
    {
        var lines = EvaluationFormatting.ReplyLines("Happy to help!\n\n1. **Where** are you?\n2. Dine-in or takeout?", out var truncated);

        Assert.False(truncated);
        Assert.Equal(["Happy to help!", "1. **Where** are you?", "2. Dine-in or takeout?"], lines);
    }

    [Fact]
    public void ReplyLines_CapsLongRepliesAndReportsTheRemainder()
    {
        var lines = EvaluationFormatting.ReplyLines(string.Join('\n', Enumerable.Range(1, 10).Select(number => $"line {number}")), out var truncated);

        Assert.True(truncated);
        Assert.Equal(7, lines.Count);
        Assert.Equal("line 6", lines[5]);
        Assert.Equal("\u2026 (+4 more line(s))", lines[6]);
    }

    [Fact]
    public void ReplyLines_DashesAnAnswerTheAgentHasNotGivenYet()
    {
        Assert.Equal([Dash], EvaluationFormatting.ReplyLines(null, out var truncated));
        Assert.False(truncated);
    }

    [Fact]
    public void RunResult_OmitsTranscriptsUnlessTheyWereAskedFor()
    {
        var text = Render(RunWithCases());

        Assert.DoesNotContain("Conversation 1 —", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Replies are shortened", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunResult_PrintsEveryRequestedTranscript()
    {
        var text = Render(RunWithCases() with { TranscriptNumbers = [1, 2] });

        Assert.Contains("Conversation 1 — Pass (4 message(s))", text, StringComparison.Ordinal);
        Assert.Contains("Conversation 2 — Fail (1 message(s))", text, StringComparison.Ordinal);
        Assert.Contains("  > first question", text, StringComparison.Ordinal);
        Assert.Contains("    short reply", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunResult_PrintsOnlyTheNamedTranscript()
    {
        var text = Render(RunWithCases() with { TranscriptNumbers = [2] });

        Assert.DoesNotContain("Conversation 1 —", text, StringComparison.Ordinal);
        Assert.Contains("Conversation 2 —", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunResult_PointsAtOutputWhenAReplyWasShortened()
    {
        var text = Render(RunWithCases() with { TranscriptNumbers = [3] });

        Assert.Contains("\u2026 (+4 more line(s))", text, StringComparison.Ordinal);
        Assert.Contains("Replies are shortened. Write the full transcript with --output <file>.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunResult_IgnoresATranscriptNumberOutsideTheResults()
    {
        var text = Render(RunWithCases() with { TranscriptNumbers = [0, 9] });

        Assert.DoesNotContain("Conversation", text.Split("CONVERSATION")[1], StringComparison.Ordinal);
    }

    private static EvaluationRunResult RunWithCases() => new()
    {
        Status = "evaluation-shown",
        EnvironmentId = "env-1",
        McsAgentId = "bot-1",
        Run = new EvaluationRunRow { RunId = "run-1", State = "Completed", TestCaseCount = 3 },
        Cases =
        [
            new EvaluationCaseRow
            {
                TestCaseId = "case-1",
                DisplayName = "first question",
                EvaluationResult = "Pass",
                Turns =
                [
                    new EvaluationTurnRow { Query = "first question", Answer = "short reply" },
                    new EvaluationTurnRow { Query = "second question", Answer = "another reply" },
                ],
            },
            new EvaluationCaseRow
            {
                TestCaseId = "case-2",
                DisplayName = "unanswered",
                EvaluationResult = "Fail",
                Turns = [new EvaluationTurnRow { Query = "unanswered" }],
            },
            new EvaluationCaseRow
            {
                TestCaseId = "case-3",
                DisplayName = "long",
                Turns = [new EvaluationTurnRow { Query = "long", Answer = string.Join('\n', Enumerable.Range(1, 10).Select(number => $"line {number}")) }],
            },
        ],
    };
}
