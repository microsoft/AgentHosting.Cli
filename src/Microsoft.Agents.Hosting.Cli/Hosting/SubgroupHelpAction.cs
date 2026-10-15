using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.RegularExpressions;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

/// <summary>
/// A help action that splits a command group's subcommands into two sections: subcommands that are
/// themselves groups (a <c>[CommandGroup]</c> container) are listed under a <c>Subgroups:</c> heading,
/// and leaf verbs under <c>Commands:</c>. Group membership is supplied by the <c>isSubgroup</c>
/// predicate so a leaf verb that merely nests a subcommand (for example <c>deployment create
/// connection</c>) stays under Commands. It only reshapes the help for commands the supplied
/// <c>shouldCustomize</c> predicate opts in; every other command is delegated to the captured default
/// help action unchanged.
///
/// System.CommandLine 2.0's layout customization (<c>HelpBuilder.CustomizeLayout</c> / <c>HelpContext</c>)
/// is internal, so this reuses the public <see cref="HelpBuilder.Write(Command, TextWriter)"/> to
/// produce the default help, then splits its single <c>Commands:</c> section into <c>Subgroups:</c> and
/// <c>Commands:</c>. A wide builder width keeps every entry on one line so the split is a clean,
/// per-line partition.
/// </summary>
internal sealed partial class SubgroupHelpAction : SynchronousCommandLineAction
{
    private const int WideWidth = 1000;
    private const int MinimumDescriptionWidth = 24;

    private readonly SynchronousCommandLineAction _defaultAction;
    private readonly Func<Command, bool> _shouldCustomize;
    private readonly Func<Command, bool> _isSubgroup;
    private readonly Func<Command, IReadOnlyList<string>> _usageLines;

    public SubgroupHelpAction(
        SynchronousCommandLineAction defaultAction,
        Func<Command, bool> shouldCustomize,
        Func<Command, bool> isSubgroup,
        Func<Command, IReadOnlyList<string>>? usageLines = null)
    {
        _defaultAction = defaultAction ?? throw new ArgumentNullException(nameof(defaultAction));
        _shouldCustomize = shouldCustomize ?? throw new ArgumentNullException(nameof(shouldCustomize));
        _isSubgroup = isSubgroup ?? throw new ArgumentNullException(nameof(isSubgroup));
        _usageLines = usageLines ?? (_ => []);
    }

    public override int Invoke(ParseResult parseResult)
    {
        var command = parseResult.CommandResult.Command;
        var splitSubgroups = _shouldCustomize(command);
        var usage = _usageLines(command);

        // HelpBuilder/HelpContext are internal in System.CommandLine 2.0, so render the default help by
        // running the captured default action into a temporary writer, then reorder its sections.
        var configuration = parseResult.InvocationConfiguration;
        var finalOutput = configuration.Output ?? Console.Out;
        var capture = new StringWriter();
        var helpAction = _defaultAction as HelpAction;
        var originalWidth = helpAction?.MaxWidth;
        configuration.Output = capture;
        try
        {
            // Keep every entry on one line so the split below is a clean per-line partition.
            if (helpAction is not null)
            {
                helpAction.MaxWidth = WideWidth;
            }

            _defaultAction.Invoke(parseResult);
        }
        finally
        {
             if (helpAction is not null && originalWidth is not null)
             {
                 helpAction.MaxWidth = originalWidth.Value;
             }
            configuration.Output = finalOutput;
        }

        var rendered = Reorder(capture.ToString(), command, splitSubgroups, originalWidth ?? WideWidth);

        finalOutput.Write(usage.Count == 0 ? rendered : ReplaceUsage(rendered, usage));
        return 0;
    }

    private static string ReplaceUsage(string help, IReadOnlyList<string> usage)
    {
        var lines = help.Replace("\r\n", "\n").Split('\n');
        var heading = Array.FindIndex(lines, line => line.Trim() == "Usage:");

        if (heading < 0)
        {
            return help;
        }

        var after = heading + 1;

        while (after < lines.Length && lines[after].Trim().Length > 0)
        {
            after++;
        }

        var indent = new string(' ', lines[heading].Length - lines[heading].TrimStart().Length + 2);

        return string.Join(
            '\n',
            lines.Take(heading + 1)
                .Concat(usage.Select(line => indent + line))
                .Concat(lines.Skip(after)));
    }

    private string Reorder(string defaultHelp, Command command, bool splitSubgroups, int width)
    {
        var lines = defaultHelp.Replace("\r\n", "\n").Split('\n');

        var commandsHeadingIndex = Array.FindIndex(lines, line => line.Trim() == "Commands:");
        var optionsHeadingIndex = Array.FindIndex(lines, line => line.Trim() == "Options:");

        if (commandsHeadingIndex < 0 && optionsHeadingIndex < 0)
        {
            return defaultHelp;
        }

        var headLength = commandsHeadingIndex < 0 ? optionsHeadingIndex
            : optionsHeadingIndex < 0 ? commandsHeadingIndex
            : Math.Min(optionsHeadingIndex, commandsHeadingIndex);
        var head = lines.Take(headLength);
        var optionRows = optionsHeadingIndex >= 0 ? WithoutValuePlaceholders(Section(lines, optionsHeadingIndex), width) : [];
        var rows = commandsHeadingIndex >= 0 ? Section(lines, commandsHeadingIndex) : [];

        var subgroupNames = splitSubgroups
            ? command.Subcommands.Where(c => !c.Hidden && _isSubgroup(c)).Select(c => c.Name).ToHashSet(StringComparer.Ordinal)
            : [];

        var subgroupRows = rows.Where(r => subgroupNames.Contains(FirstToken(r))).ToList();
        var commandRows = rows.Where(r => !subgroupNames.Contains(FirstToken(r))).ToList();

        var result = new StringBuilder();
        var hasSubgroups = subgroupRows.Count > 0;
        foreach (var line in head)
        {
            result.Append(hasSubgroups ? AddSubgroupToUsage(line) : line).Append('\n');
        }

        Append(result, "Subgroups:", subgroupRows);
        Append(result, "Commands:", commandRows);
        Append(result, "Options:", optionRows);

        return result.ToString();
    }

    private static List<string> WithoutValuePlaceholders(List<string> rows, int width)
    {
        var column = rows.Select(DescriptionColumn).Where(index => index > 0).DefaultIfEmpty(0).Min();

        if (column == 0)
        {
            return rows;
        }

        var split = rows
            .Select(row =>
            {
                var boundary = DescriptionColumn(row);
                var name = boundary == 0 ? row.Trim() : ValuePlaceholder().Replace(row[..boundary].Trim(), string.Empty);
                var (aliases, option) = Split(name);

                return (Aliases: aliases, Option: option, Description: boundary == 0 ? string.Empty : row[boundary..].TrimEnd());
            })
            .ToList();

        var aliasWidth = split.Max(entry => entry.Aliases.Length);
        var optionWidth = split.Max(entry => entry.Option.Length);
        var gap = aliasWidth == 0 ? string.Empty : "  ";
        var indent = 2 + aliasWidth + gap.Length + optionWidth + 2;

        return
        [
            .. split.SelectMany(entry => Wrap(
                "  " + entry.Aliases.PadRight(aliasWidth) + gap + entry.Option.PadRight(optionWidth) + "  ",
                entry.Description,
                indent,
                width)),
        ];
    }

    internal static List<string> Wrap(string lead, string description, int indent, int width)
    {
        var available = width - indent;

        if (description.Length == 0)
        {
            return [lead.TrimEnd()];
        }

        if (available < MinimumDescriptionWidth || description.Length <= available)
        {
            return [(lead + description).TrimEnd()];
        }

        var padding = new string(' ', indent);
        var lines = new List<string>();
        var start = 0;

        while (start < description.Length)
        {
            if (description.Length - start <= available)
            {
                lines.Add((start == 0 ? lead : padding) + description[start..]);
                break;
            }

            var breakAt = description.LastIndexOf(' ', start + available, available);
            var end = breakAt > start ? breakAt : start + available;

            lines.Add((start == 0 ? lead : padding) + description[start..end].TrimEnd());
            start = breakAt > start ? end + 1 : end;
        }

        return lines;
    }

    private static (string Aliases, string Option) Split(string name)
    {
        if (name.StartsWith("--", StringComparison.Ordinal))
        {
            return (string.Empty, name);
        }

        var longIndex = name.IndexOf(" --", StringComparison.Ordinal);

        return longIndex < 0 ? (string.Empty, name) : (name[..longIndex].TrimEnd().TrimEnd(','), name[(longIndex + 1)..]);
    }

    private static int DescriptionColumn(string row)
    {
        var indent = row.Length - row.TrimStart().Length;

        if (indent >= row.Length || row[indent] != '-')
        {
            return 0;
        }

        var gap = row.IndexOf("  ", indent + 1, StringComparison.Ordinal);

        if (gap < 0)
        {
            return 0;
        }

        var start = gap;

        while (start < row.Length && row[start] == ' ')
        {
            start++;
        }

        return start < row.Length ? start : 0;
    }

    [GeneratedRegex(@"\s+<[^>]*>(?=\s|$)")]
    private static partial Regex ValuePlaceholder();


    private static void Append(StringBuilder result, string heading, List<string> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        result.Append(heading).Append('\n');

        foreach (var row in rows)
        {
            result.Append(row).Append('\n');
        }

        result.Append('\n');
    }

    private static List<string> Section(string[] lines, int headingIndex) =>
        [.. lines
            .Skip(headingIndex + 1)
            .TakeWhile(line => line.Length == 0 || char.IsWhiteSpace(line[0]))
            .Where(line => line.Trim().Length > 0)];

    private static string FirstToken(string row)
    {
        var trimmed = row.TrimStart();
        var space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed[..space];
    }

    /// <summary>
    /// Advertises subgroups in the default usage line. System.CommandLine renders a group's usage as
    /// "<c>&lt;path&gt; [command] [options]</c>"; when the group holds subgroups this inserts a
    /// <c>[subgroup]</c> placeholder before <c>[command]</c> so the usage mirrors the Subgroups/Commands split.
    /// </summary>
    private static string AddSubgroupToUsage(string line)
    {
        const string commandToken = "[command]";
        var index = line.IndexOf(commandToken, StringComparison.Ordinal);
        return index < 0 ? line : line.Insert(index, "[subgroup] ");
    }
}
