using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Evaluations;

/// <summary>Renders a run and every conversation it holds as readable Markdown.</summary>
public static class EvaluationTranscriptWriter
{
    /// <summary>Writes the run's summary followed by each conversation's untruncated transcript.</summary>
    /// <param name="result">The run to render.</param>
    public static string Write(EvaluationRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var builder = new StringBuilder();

        builder.Append("# Evaluation run ").AppendLine(result.Run.RunId).AppendLine();
        AppendField(builder, "Name", result.Run.Name);
        AppendField(builder, "Test set", result.TestSetName ?? result.Run.TestSetId);
        AppendField(builder, "Data type", result.Run.DataType);
        AppendField(builder, "State", result.Run.State);
        AppendField(builder, "Score", $"{EvaluationFormatting.DescribeScore(result.Run.Score)}{EvaluationFormatting.DescribeTotals(result.Run.Succeeded, result.Run.FailedCount)}");
        AppendField(builder, "Credits", EvaluationFormatting.DescribeCredits(result.Run.Credits));
        AppendField(builder, "Duration", EvaluationFormatting.DescribeDuration(result.Run.Duration));

        for (var index = 0; index < result.Cases.Count; index++)
        {
            AppendCase(builder, index + 1, result.Cases[index]);
        }

        return builder.ToString();
    }

    private static void AppendField(StringBuilder builder, string label, string? value) =>
        builder.Append("- **").Append(label).Append(":** ").AppendLine(string.IsNullOrWhiteSpace(value) ? TextGrid.Dash : value);

    private static void AppendCase(StringBuilder builder, int number, EvaluationCaseRow testCase)
    {
        builder.AppendLine().Append("## ").Append(number).Append(". ").AppendLine(EvaluationFormatting.OneLine(testCase.DisplayName)).AppendLine();

        builder.Append("- **Result:** ").AppendLine(TextGrid.DescribeOrDash(testCase.EvaluationResult));
        builder.Append("- **Messages:** ").AppendLine(testCase.MessageCount.ToString());
        AppendField(builder, "Relevance", testCase.Relevance);
        AppendField(builder, "Groundedness", testCase.Groundedness);
        AppendField(builder, "Completeness", testCase.Completeness);
        AppendField(builder, "Abstention", testCase.Abstention);

        foreach (var turn in testCase.Turns)
        {
            builder.AppendLine().Append("**User:** ").AppendLine(TextGrid.DescribeOrDash(turn.Query)).AppendLine();
            builder.Append("**Agent:** ").AppendLine(TextGrid.DescribeOrDash(turn.Answer));
        }
    }
}
