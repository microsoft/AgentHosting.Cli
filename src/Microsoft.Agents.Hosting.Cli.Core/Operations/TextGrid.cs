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
