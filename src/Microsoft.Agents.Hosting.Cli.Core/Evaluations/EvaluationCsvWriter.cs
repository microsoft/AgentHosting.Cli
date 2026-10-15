using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Evaluations;

/// <summary>Writes conversations back out in the Copilot Studio CSV template's format.</summary>
public static class EvaluationCsvWriter
{
    private static readonly string[] Preamble =
    [
        "# Import conversations to test your agent.",
        "#",
        "# Limitations",
        $"# - {EvaluationConversationLimits.MaximumExchangesPerConversation} question-and-answer pairs max per conversation.",
        $"# - {EvaluationConversationLimits.MaximumConversations} conversations max.",
        $"# - {EvaluationConversationLimits.MaximumQuestionLength} characters max per question, including spaces.",
        "#",
        "# Imported columns",
        "# conversationNumber - Identifies each conversation. All questions and responses with the same conversation number will run as a single test case against the agent.",
        "# question - The user prompt that the agent will respond to.",
        "# response - The reference agent reply. This field is optional. The agent response isn't compared to this reference answer.",
        "#",
    ];

    /// <summary>Renders conversations as a CSV the portal and this CLI both accept.</summary>
    /// <param name="conversations">The conversations to write.</param>
    public static string Write(IReadOnlyList<EvaluationConversation> conversations)
    {
        ArgumentNullException.ThrowIfNull(conversations);

        var builder = new StringBuilder();

        foreach (var line in Preamble)
        {
            builder.AppendLine(CsvDocument.WriteRow([line]));
        }

        builder.AppendLine(CsvDocument.WriteRow(["conversationNumber", "question", "response"]));

        foreach (var conversation in conversations)
        {
            foreach (var exchange in conversation.Exchanges)
            {
                builder.AppendLine(CsvDocument.WriteRow([conversation.Number.ToString(), exchange.Question, exchange.Response ?? string.Empty]));
            }
        }

        return builder.ToString();
    }
}
