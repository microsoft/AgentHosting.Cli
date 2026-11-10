using System.Text.RegularExpressions;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public static class GitCommits
{
    public static bool IsSha(string? value) => value is { Length: 40 } && value.All(char.IsAsciiHexDigit);

    public static bool Match(string? first, string? second) => first is { Length: >= 7 } && second is { Length: >= 7 } && (first.StartsWith(second, StringComparison.OrdinalIgnoreCase) || second.StartsWith(first, StringComparison.OrdinalIgnoreCase));

    public static string Short(string? sha) => sha is { Length: > 7 } ? sha[..7] : sha ?? string.Empty;
}

public sealed record GitCommitFacts(string Sha, string AuthorEmail, IReadOnlyList<string> Parents, string Subject);

public sealed record GitTip(string? Head, string? Remote, IReadOnlyList<string> Status)
{
    public bool HoldsRemote => GitCommits.Match(Head, Remote) && Status.Count == 0;

    public static string Describe(IReadOnlyList<string> status) => status.Count == 0 ? "clean" : string.Join("; ", status);
}

public sealed record GitCloneFacts(string? TopLevel, string? Origin, GitCommitFacts? Head, GitCommitFacts? Parent, IReadOnlyList<string>? ParentTree, IReadOnlyList<string>? Changed, IReadOnlyList<string>? Status, IReadOnlyList<string> Ignore, IReadOnlyList<string>? TrackedConfig, IReadOnlyList<string>? TrackedCache, IReadOnlyList<string>? Helpers, string? RemoteMain)
{
    public const string GrsAuthor = "grs@powerplatform.com";

    public const string GrsReadme = "README.md";

    public bool IsRootOf(string directory) => TopLevel is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(TopLevel)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase);

    public bool OriginIs(string? gitUrl) => AmsIds.Same(Origin, gitUrl);

    public bool HeadIs(string? commitSha, string subject) => Head is not null && GitCommits.Match(Head.Sha, commitSha) && string.Equals(Head.Subject, subject, StringComparison.Ordinal);

    public bool ParentIs(string? commitSha) => GitCommits.Match(Parent?.Sha, commitSha);

    public bool ParentIsGrsInitialCommit => Parent is not null && string.Equals(Parent.AuthorEmail, GrsAuthor, StringComparison.OrdinalIgnoreCase) && Parent.Parents.Count == 0 && ParentTree is [var only] && string.Equals(only, GrsReadme, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> ChangedOutside(IReadOnlyList<string> folders) => [.. (Changed ?? []).Where(path => path != ".gitignore" && !folders.Any(folder => path.StartsWith(folder + "/", StringComparison.Ordinal)))];

    public bool ChangesOnly(IReadOnlyList<string> folders) => Changed is { Count: > 0 } && ChangedOutside(folders).Count == 0;

    public bool IsClean => Status is { Count: 0 };

    public static bool HoldsOnlySeed(IReadOnlyList<string>? files) => files is not null && files.All(file => file is AmsMcsFiles.IgnoreFileName or GrsReadme);

    public bool KeepsConfigOut => AmsMcsFiles.HasLine(Ignore, AmsMcsFiles.ConfigIgnoreLine) && TrackedConfig is { Count: 0 };

    public bool KeepsCacheOut => AmsMcsFiles.HasLine(Ignore, AmsMcsFiles.CacheIgnoreLine) && TrackedCache is { Count: 0 };

    public bool PushedMain(string? commitSha) => GitCommits.Match(RemoteMain, commitSha);

    public bool HasMsagentHelper(string gitHost)
    {
        if (Helpers is not ["", var helper])
        {
            return false;
        }

        var match = Regex.Match(helper, $"^!AH_CLI_FEATURES=AmsMcsAgents MSAGENT_GIT_HOST={Regex.Escape(gitHost)} '(.+)' auth git-credential$");

        return match.Success && File.Exists(match.Groups[1].Value);
    }
}
