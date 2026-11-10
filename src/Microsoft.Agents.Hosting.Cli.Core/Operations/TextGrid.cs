using System.Globalization;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>The shared text helpers every <see cref="ITextGridRenderable"/> draws with.</summary>
public static class TextGrid
{
    /// <summary>The placeholder shown for an absent value.</summary>
    public const string Dash = "\u2014";

    /// <summary>Renders a titled table, padded to its widest cell per column.</summary>
    /// <param name="title">The table caption.</param>
    /// <param name="headers">The column headers.</param>
    /// <param name="rows">The rows, each with one cell per header.</param>
    public static IReadOnlyList<string> RenderTable(string title, string[] headers, IReadOnlyList<string[]> rows) =>
        [string.Empty, $"  {title}", .. RenderRows(headers, rows)];

    /// <summary>Renders a header row and its rows, padded to the widest cell per column.</summary>
    /// <param name="headers">The column headers.</param>
    /// <param name="rows">The rows, each with one cell per header.</param>
    public static IReadOnlyList<string> RenderRows(string[] headers, IReadOnlyList<string[]> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        var columnWidths = ColumnWidths(headers, rows);
        var lines = new List<string> { RenderRow(headers, columnWidths) };

        lines.AddRange(rows.Select(row => RenderRow(row, columnWidths)));

        return lines;
    }

    /// <summary>Renders a timestamp, or a dash when absent.</summary>
    /// <param name="value">The timestamp.</param>
    public static string DescribeTimestamp(DateTimeOffset? value) => value is null ? Dash : value.Value.ToString("u");

    /// <summary>Renders a value, or a dash when it is absent or blank.</summary>
    /// <param name="value">The value.</param>
    public static string DescribeOrDash(string? value) => string.IsNullOrWhiteSpace(value) ? Dash : value;

    /// <summary>Renders a count, or a dash when absent.</summary>
    /// <param name="value">The count.</param>
    public static string DescribeCount(int? value) => value is null ? Dash : value.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Renders a principal, or a dash when absent.</summary>
    /// <param name="principal">The principal.</param>
    public static string DescribePrincipal(PrincipalReference? principal) =>
        principal is null ? Dash : $"{principal.ObjectId} ({principal.ObjectType})";

    /// <summary>Names a standalone workflow by its Dataverse name and by the managed-agent record that holds it.</summary>
    /// <param name="workflowName">The workflow's Dataverse name, when it is known.</param>
    /// <param name="displayName">The record's name.</param>
    /// <returns>Both names quoted, or the record's name alone when the workflow's name is not known.</returns>
    public static string DescribeWorkflow(string? workflowName, string displayName) => string.IsNullOrWhiteSpace(workflowName) ? $"'{displayName}'" : $"'{workflowName}' (record '{displayName}')";

    /// <summary>Says whether a deploy published the agent, which published version stays live when it did not, and how to publish content it left as a draft.</summary>
    /// <param name="published">Whether the deploy published the agent.</param>
    /// <param name="lastPublishedTime">When the agent was last published, or null when it never was or the time is not known.</param>
    public static string DescribePublished(bool published, DateTimeOffset? lastPublishedTime) => published ? "yes \u2014 the content is live" : lastPublishedTime is { } publishedTime ? $"no \u2014 this deploy's content is a draft; the version published {DescribeTimestamp(publishedTime)} stays live. Re-run with --publish to make it live." : "no \u2014 the content is a draft. Re-run with --publish to make it live.";

    /// <summary>Formats a warning line whose label is padded to the width the other labels at that indent use.</summary>
    /// <param name="indent">The spaces the line starts with.</param>
    /// <param name="warning">The warning to show.</param>
    /// <returns>The line.</returns>
    public static string WarningLine(string indent, string warning) => $"{indent}warning      {warning}";

    /// <summary>Formats a warning line under a status line, as pull and deploy print each warning.</summary>
    /// <param name="warning">The warning to show.</param>
    /// <returns>The line.</returns>
    public static string StatusWarningLine(string warning) => $"  warning    {warning}";

    private static int[] ColumnWidths(string[] headers, IReadOnlyList<string[]> rows)
    {
        var columnWidths = new int[headers.Length];

        for (var column = 0; column < headers.Length; column++)
        {
            var width = headers[column].Length;

            foreach (var row in rows)
            {
                width = Math.Max(width, row[column].Length);
            }

            columnWidths[column] = width;
        }

        return columnWidths;
    }

    private static string RenderRow(string[] cells, int[] columnWidths)
    {
        var last = cells.Length - 1;

        return "  " + string.Join("  ", cells.Select((cell, index) => index == last ? cell : cell.PadRight(columnWidths[index])));
    }
}
