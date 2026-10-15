using System.Text;

namespace Microsoft.Agents.Hosting.Cli.Core.Evaluations;

/// <summary>Reads and writes RFC 4180 comma-separated values, shared by every evaluation file format.</summary>
public static class CsvDocument
{
    private const char Quote = '"';

    /// <summary>Splits a document into rows of fields, honouring quoted commas, quotes and newlines.</summary>
    /// <param name="content">The whole file.</param>
    /// <exception cref="FormatException">A quoted value runs past the end of the document.</exception>
    public static IReadOnlyList<IReadOnlyList<string>> ReadRows(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var rows = new List<IReadOnlyList<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closed = false;
        var fieldStarted = false;

        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];

            if (quoted)
            {
                if (character != Quote)
                {
                    field.Append(character);
                    continue;
                }

                if (index + 1 < content.Length && content[index + 1] == Quote)
                {
                    field.Append(Quote);
                    index++;
                    continue;
                }

                quoted = false;
                closed = true;
                continue;
            }

            switch (character)
            {
                case Quote:
                    if (fieldStarted)
                    {
                        throw new FormatException("a value carries a quote outside a quoted value.");
                    }

                    quoted = true;
                    fieldStarted = true;
                    continue;

                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                    closed = false;
                    continue;

                case '\r':
                    continue;

                case '\n':
                    CompleteRow(rows, fields, field, fieldStarted);
                    fieldStarted = false;
                    closed = false;
                    continue;

                default:
                    if (closed)
                    {
                        throw new FormatException("a quoted value carries text after its closing quote.");
                    }

                    field.Append(character);
                    fieldStarted = true;
                    continue;
            }
        }

        if (quoted)
        {
            throw new FormatException("a quoted value is never closed.");
        }

        CompleteRow(rows, fields, field, fieldStarted);

        return rows;
    }

    /// <summary>Renders one row, quoting every field so the output matches the portal's template.</summary>
    /// <param name="fields">The field values in column order.</param>
    public static string WriteRow(IEnumerable<string?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        return string.Join(",", fields.Select(field => $"{Quote}{(field ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}{Quote}"));
    }

    private static void CompleteRow(List<IReadOnlyList<string>> rows, List<string> fields, StringBuilder field, bool fieldStarted)
    {
        if (field.Length == 0 && !fieldStarted && fields.Count == 0)
        {
            return;
        }

        fields.Add(field.ToString());
        field.Clear();
        rows.Add([.. fields]);
        fields.Clear();
    }
}
