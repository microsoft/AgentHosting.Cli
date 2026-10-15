using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Evaluations;

/// <summary>Reads the Copilot Studio conversation CSV into the conversations a test set is built from.</summary>
public static class EvaluationCsvReader
{
    private const string ConversationNumberColumn = "conversationnumber";
    private const string QuestionColumn = "question";
    private const string ResponseColumn = "response";

    /// <summary>Parses a conversation CSV.</summary>
    /// <param name="content">The file's text.</param>
    /// <param name="path">The file's path, used only in failure messages.</param>
    public static IReadOnlyList<EvaluationConversation> Read(string content, string path)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var rows = ReadRows(content, path);
        var headerIndex = HeaderIndex(rows);

        if (headerIndex < 0)
        {
            throw EvaluationException.ConversationFileInvalid(path, "it holds no rows.");
        }

        var columns = ResolveColumns(rows[headerIndex], path);
        var ordered = new List<(int Number, EvaluationExchange Exchange)>();

        foreach (var row in rows.Skip(headerIndex + 1).Where(HoldsValues))
        {
            if (row.Count <= Math.Max(columns.Number, columns.Question))
            {
                throw EvaluationException.ConversationFileInvalid(path, $"a row has {row.Count} column(s), and the header names {Math.Max(columns.Number, columns.Question) + 1}.");
            }

            var rawNumber = row[columns.Number].Trim();

            if (!int.TryParse(rawNumber, out var number))
            {
                throw EvaluationException.ConversationFileInvalid(path, $"'{rawNumber}' is not a conversation number.");
            }

            var question = row[columns.Question].Trim();

            if (question.Length == 0)
            {
                throw EvaluationException.ConversationFileInvalid(path, $"conversation {number} has a row with no question.");
            }

            var response = columns.Response >= 0 && row.Count > columns.Response ? row[columns.Response].Trim() : string.Empty;

            ordered.Add((number, new EvaluationExchange(question, response.Length == 0 ? null : response)));
        }

        if (ordered.Count == 0)
        {
            throw EvaluationException.ConversationFileInvalid(path, "it holds a header but no conversations.");
        }

        var conversations = ordered
            .GroupBy(entry => entry.Number)
            .OrderBy(group => group.Key)
            .Select((group, index) => new EvaluationConversation
            {
                Number = index + 1,
                Exchanges = [.. group.Select(entry => entry.Exchange)],
                Source = EvaluationConversationSource.Imported,
            })
            .ToArray();

        EvaluationConversationLimits.Validate(conversations);

        return conversations;
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadRows(string content, string path)
    {
        try
        {
            return CsvDocument.ReadRows(content.TrimStart('\uFEFF'));
        }
        catch (FormatException failure)
        {
            throw EvaluationException.ConversationFileInvalid(path, failure.Message);
        }
    }

    /// <summary>
    /// The row naming the columns, which is the first row the template's comment preamble does not
    /// claim. Only that preamble is comment-filtered: a question may legitimately open with a '#',
    /// and the column order is free, so a data row is never classified by its first field.
    /// </summary>
    /// <param name="rows">Every row the document holds.</param>
    private static int HeaderIndex(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            if (HoldsValues(rows[index]) && !IsComment(rows[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool HoldsValues(IReadOnlyList<string> row) => row.Any(field => field.Trim().Length > 0);

    private static bool IsComment(IReadOnlyList<string> row) => row.Count > 0 && row[0].TrimStart().StartsWith('#');

    private static (int Number, int Question, int Response) ResolveColumns(IReadOnlyList<string> header, string path)
    {
        var names = header.Select(name => name.Trim().ToLowerInvariant()).ToArray();
        var number = Array.IndexOf(names, ConversationNumberColumn);
        var question = Array.IndexOf(names, QuestionColumn);

        if (number < 0 || question < 0)
        {
            throw EvaluationException.ConversationFileInvalid(
                path,
                $"the header must name the columns '{ConversationNumberColumn}' and '{QuestionColumn}'; it names {string.Join(", ", names.Select(name => $"'{name}'"))}.");
        }

        return (number, question, Array.IndexOf(names, ResponseColumn));
    }
}
