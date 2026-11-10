using System.Text.RegularExpressions;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public enum AmsNoticeReason
{
    OtherCommit,
    Uncommitted,
    Unpushed,
    GitCannotTell,
}

public static partial class AmsMcsText
{
    private const string Uncommitted = "has uncommitted changes";

    private const string Unpushed = "holds commits that aren't pushed to GRS";

    private const string GitCannotTell = "Git can't tell which commit the agent's folder holds";

    private const string KeptConnection = "left the folder's connection to Dev as it was; `msagent agent pull` brings the deployed changes into it";

    private const string NotConnected = "didn't connect the folder to Dev";

    public static string? Value(string text, string label) => Labelled(text, label, @"(.+?)\s*$");

    public static string? Word(string text, string label) => Labelled(text, label, @"(\S+)");

    public static string? DisplayName(string? yaml) => YamlValue(DisplayNameLine(), yaml);

    public static string? WorkflowName(string? yaml) => YamlValue(NameLine(), yaml);

    public static string WithDisplayName(string yaml, string name) => WithValue(DisplayNameLine(), yaml, "displayName", name, -1);

    public static string? Description(string? yaml) => YamlValue(DescriptionLine(), yaml);

    public static string WithDescription(string yaml, string description) => WithValue(DescriptionLine(), yaml, "  description", description, 1);

    public static bool HasConflictMarkers(string? text) => ConflictMarkers().IsMatch(text ?? string.Empty);

    public static IReadOnlyList<string> AmsWarnings(IEnumerable<string>? warnings) => [.. (warnings ?? []).Where(warning => AmsWarning().IsMatch(warning))];

    public static bool HasQuestion(string text) => Question().IsMatch(text);

    public static bool AsksToClone(string text, string agentName) => Regex.IsMatch(text, $@"Clone repository .+ into .+ and add agent '{Regex.Escape(agentName)}' to it\? \[Y/n\]", RegexOptions.IgnoreCase);

    public static bool AsksToRestore(string text, string repositoryId) => Regex.IsMatch(text, $@"Clone repository .+ \({Regex.Escape(repositoryId)}\) into .+, restore its agents and connect them\? \[Y/n\]", RegexOptions.IgnoreCase);

    public static bool Says(string? text, string phrase) => text?.Contains(phrase, StringComparison.OrdinalIgnoreCase) == true;

    public static bool Mentions(IEnumerable<string>? lines, string phrase) => (lines ?? []).Any(line => Says(line, phrase));

    public static bool NamesCommit(string? text, string? sha) => GitCommits.IsSha(sha) && HexRun().Matches(text ?? string.Empty).Any(match => sha!.StartsWith(match.Value, StringComparison.OrdinalIgnoreCase));

    public static bool Explains(string? notice, AmsNoticeReason reason, string? held = null, string? deployed = null) => reason switch
    {
        AmsNoticeReason.OtherCommit => HeldNotDeployed().Match(notice ?? string.Empty) is { Success: true } match && NamesCommit(match.Groups[1].Value, held) && NamesCommit(match.Groups[2].Value, deployed),
        AmsNoticeReason.Uncommitted => Says(notice, Uncommitted),
        AmsNoticeReason.Unpushed => Says(notice, Unpushed),
        _ => Says(notice, GitCannotTell),
    };

    public static bool AdvisesPull(string? notice, bool connected) => Says(notice, connected ? KeptConnection : NotConnected);

    public static bool WarnsLocalChanges(IEnumerable<string>? warnings, string? deployed) => deployed is { Length: >= 7 } && (warnings ?? []).Any(warning => string.Equals(warning, $"--remote deploys {deployed[..7]} from GRS; your local changes aren't in it.", StringComparison.OrdinalIgnoreCase));

    public static string? SavedOnMain(string? message) => SavedEdits().Match(message ?? string.Empty) is var match && match.Success ? match.Groups[1].Value : null;

    public static string? LastCommit(string text, string deploymentId) => text.ReplaceLineEndings("\n").Split('\n').SkipWhile(line => !Regex.IsMatch(line, $@"\({Regex.Escape(deploymentId)}\)\s*$", RegexOptions.IgnoreCase)).Skip(1).Select(line => LastCommitLine().Match(line)).FirstOrDefault(match => match.Success)?.Groups[1].Value;

    public static bool NamesAmsRemedy(string? text) => Says(text, "agent capture") && Says(text, "git pull");

    public static bool NamesMainRemedy(string? text) => !Says(text, "agent capture") && Says(text, "not connected to Copilot Studio");

    private static string? Labelled(string text, string label, string value) => text.ReplaceLineEndings("\n").Split('\n').Select(line => Regex.Match(line, $@"^\s+{Regex.Escape(label)}\s+{value}", RegexOptions.IgnoreCase)).FirstOrDefault(match => match.Success)?.Groups[1].Value;

    private static string? YamlValue(Regex line, string? yaml) => line.Match(yaml ?? string.Empty) is var match && match.Success ? Unquoted(match.Groups[1].Value.Trim()) : null;

    private static string WithValue(Regex line, string yaml, string key, string value, int count) => line.Replace(yaml, match => $"{key}: {Quoted(match.Groups[1].Value.Trim(), value)}", count);

    private static string Unquoted(string value) => IsQuoted(value) ? value[1..^1] : value;

    private static string Quoted(string current, string name) => IsQuoted(current) ? $"{current[0]}{name}{current[0]}" : name;

    private static bool IsQuoted(string value) => value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'';

    [GeneratedRegex(@"(?m)^displayName:[ \t]*([^\r\n]*)")]
    private static partial Regex DisplayNameLine();

    [GeneratedRegex(@"(?m)^name:[ \t]*([^\r\n]*)")]
    private static partial Regex NameLine();

    [GeneratedRegex(@"(?m)^  description:[ \t]*([^\r\n]*)")]
    private static partial Regex DescriptionLine();

    [GeneratedRegex(@"(?ms)^<{7}.*?^={7}.*?^>{7}")]
    private static partial Regex ConflictMarkers();

    [GeneratedRegex("AMS doesn't know|holds no git commit|shares no commit|wasn't told|AMS wasn't updated", RegexOptions.IgnoreCase)]
    private static partial Regex AmsWarning();

    [GeneratedRegex(@"\[[Yy]/[Nn]\]")]
    private static partial Regex Question();

    [GeneratedRegex("(?<![0-9A-Fa-f])[0-9A-Fa-f]{7,40}(?![0-9A-Fa-f])")]
    private static partial Regex HexRun();

    [GeneratedRegex("holds ([0-9A-Fa-f]{7,40}), not the deployed ([0-9A-Fa-f]{7,40})")]
    private static partial Regex HeldNotDeployed();

    [GeneratedRegex(@"Nothing was deployed: Dev had edits that commit [0-9a-fA-F]{7,40} doesn't hold\. They were saved on main as commit ([0-9a-fA-F]{7,40})\.", RegexOptions.IgnoreCase)]
    private static partial Regex SavedEdits();

    [GeneratedRegex(@"^\s+lastCommit\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex LastCommitLine();
}
