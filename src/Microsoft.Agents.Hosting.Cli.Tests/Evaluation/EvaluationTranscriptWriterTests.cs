using Microsoft.Agents.Hosting.Cli.Core.Evaluations;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests.Evaluation;

public sealed class EvaluationTranscriptWriterTests
{
    private static EvaluationRunResult Run() => new()
    {
        Status = "evaluation-shown",
        EnvironmentId = "env-1",
        McsAgentId = "bot-1",
        TestSetName = "Nightly",
        Run = new EvaluationRunRow
        {
            RunId = "run-1",
            Name = "EvalCliV1",
            State = "Completed",
            TestSetId = "testset-7",
            DataType = "Conversation",
            TestCaseCount = 1,
            Score = 1,
            Succeeded = 1,
            FailedCount = 0,
            Credits = 13.58,
        },
        Cases =
        [
            new EvaluationCaseRow
            {
                TestCaseId = "case-1",
                DisplayName = "What is the refund window?",
                EvaluationResult = "Pass",
                Relevance = "Yes",
                Groundedness = "Yes",
                Completeness = "Yes",
                Abstention = "No",
                Turns =
                [
                    new EvaluationTurnRow { Query = "What is the refund window?", Answer = string.Join('\n', Enumerable.Range(1, 20).Select(number => $"line {number}")) },
                    new EvaluationTurnRow { Query = "And for sale items?" },
                ],
            },
        ],
    };

    [Fact]
    public void WritesTheRunSummaryAsMarkdown()
    {
        var markdown = EvaluationTranscriptWriter.Write(Run());

        Assert.StartsWith("# Evaluation run run-1", markdown, StringComparison.Ordinal);
        Assert.Contains("- **Name:** EvalCliV1", markdown, StringComparison.Ordinal);
        Assert.Contains("- **Test set:** Nightly", markdown, StringComparison.Ordinal);
        Assert.Contains("- **Data type:** Conversation", markdown, StringComparison.Ordinal);
        Assert.Contains("- **Score:** 100% (1 passed, 0 failed)", markdown, StringComparison.Ordinal);
        Assert.Contains("- **Credits:** 13.58", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void WritesEveryReplyInFull()
    {
        var markdown = EvaluationTranscriptWriter.Write(Run());

        Assert.Contains("line 20", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("more line(s)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void WritesOneHeadingPerConversationWithItsGraderDetail()
    {
        var markdown = EvaluationTranscriptWriter.Write(Run());

        Assert.Contains("## 1. What is the refund window?", markdown, StringComparison.Ordinal);
        Assert.Contains("- **Result:** Pass", markdown, StringComparison.Ordinal);
        Assert.Contains("- **Messages:** 3", markdown, StringComparison.Ordinal);
        Assert.Contains("- **Groundedness:** Yes", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void MarksAnAnswerTheAgentNeverGave()
    {
        var markdown = EvaluationTranscriptWriter.Write(Run());

        Assert.Contains("**User:** And for sale items?", markdown, StringComparison.Ordinal);
        Assert.Contains($"**Agent:** {TextGrid.Dash}", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void FallsBackToTheTestSetIdWhenNoNameIsKnown()
    {
        var markdown = EvaluationTranscriptWriter.Write(Run() with { TestSetName = null });

        Assert.Contains("- **Test set:** testset-7", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void WritesTheSummaryEvenWhenTheRunHasNoCases()
    {
        var markdown = EvaluationTranscriptWriter.Write(Run() with { Cases = [] });

        Assert.Contains("# Evaluation run run-1", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("## 1.", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("run.json")]
    [InlineData("RUN.JSON")]
    public void RenderWritesJsonForAJsonName(string path)
    {
        Assert.StartsWith("{", EvaluationRunReport.Render(path, Run()).TrimStart(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("run.md")]
    [InlineData("run.txt")]
    [InlineData("run")]
    public void RenderWritesMarkdownForEveryOtherName(string path)
    {
        Assert.StartsWith("# Evaluation run", EvaluationRunReport.Render(path, Run()), StringComparison.Ordinal);
    }

    [Fact]
    public void WritesTheWholeQuestionInTheHeading()
    {
        var question = new string('x', 120);
        var markdown = EvaluationTranscriptWriter.Write(Run() with
        {
            Cases = [new EvaluationCaseRow { TestCaseId = "case-1", DisplayName = $"line\n{question}", Turns = [] }],
        });

        Assert.Contains($"## 1. line {question}", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2026", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void WritesEveryQuestionExactlyAsItWasAsked()
    {
        var query = "Summarise:\n\n  - first item\n  - second item";
        var markdown = EvaluationTranscriptWriter.Write(Run() with
        {
            Cases = [new EvaluationCaseRow { TestCaseId = "case-1", DisplayName = "Summarise", Turns = [new EvaluationTurnRow { Query = query, Answer = "Done" }] }],
        });

        Assert.Contains($"**User:** {query}", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void MarksAQuestionTheRunNeverRecorded()
    {
        var markdown = EvaluationTranscriptWriter.Write(Run() with
        {
            Cases = [new EvaluationCaseRow { TestCaseId = "case-1", DisplayName = "Summarise", Turns = [new EvaluationTurnRow { Query = "  ", Answer = "Done" }] }],
        });

        Assert.Contains($"**User:** {TextGrid.Dash}", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ToJsonCanBeCalledRepeatedly()
    {
        Assert.Equal(EvaluationRunReport.ToJson(Run()), EvaluationRunReport.ToJson(Run()));
    }

    [Fact]
    public void RenderRefusesAnEmptyPath()
    {
        Assert.ThrowsAny<ArgumentException>(() => EvaluationRunReport.Render("  ", Run()));
    }
}
