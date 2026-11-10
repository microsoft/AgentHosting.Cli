using System.Diagnostics;
using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public sealed record McsShelfCopy(string Commit, string? Branch, DateTimeOffset? SavedAt);

public sealed record McsShelf(string Key, string KeyDirectory, string? Folder, string? Identity, string? Live, IReadOnlyList<McsShelfCopy> Copies, IReadOnlyList<string> Stored)
{
    public bool KeyIsValid => Key.Length == 16 && Key.All(char.IsAsciiHexDigit);

    public bool Complete => Copies.All(copy => GitCommits.IsSha(copy.Commit) && !string.IsNullOrEmpty(copy.Branch) && copy.SavedAt is not null);

    public bool NewestFirst => Copies.Zip(Copies.Skip(1)).All(pair => pair.First.SavedAt >= pair.Second.SavedAt);

    public bool Names(string folder) => Folder is { Length: > 0 } named && (string.Equals(Normalize(named), Normalize(folder), StringComparison.OrdinalIgnoreCase) || Normalize(named).EndsWith('/' + Normalize(folder), StringComparison.OrdinalIgnoreCase));

    public bool Holds(string? commit) => GitCommits.IsSha(commit) && Copies.Any(copy => GitCommits.Match(copy.Commit, commit)) && Stored.Any(stored => GitCommits.Match(stored, commit));

    public bool IdentifiesAs(string? environmentId, string? agentId) => AmsIds.Same(Identity, $"{environmentId}|{agentId}");

    public static McsShelf? Find(IReadOnlyList<McsShelf> shelves, string folder) => shelves.FirstOrDefault(shelf => shelf.Names(folder)) ?? (shelves is [var only] ? only : null);

    public static bool IsEmpty(McsShelf? shelf) => shelf is null || shelf is { Copies.Count: 0, Stored.Count: 0 };

    public static string Describe(McsShelf? shelf) => shelf is null ? "no shelf" : $"shelf {shelf.Key}: folder='{shelf.Folder}' identity='{shelf.Identity}' live={GitCommits.Short(shelf.Live)} copies={string.Join(',', shelf.Copies.Select(copy => $"{GitCommits.Short(copy.Commit)}@{copy.Branch}@{copy.SavedAt:O}"))} stored={string.Join(',', shelf.Stored.Select(GitCommits.Short))}";

    private static string Normalize(string folder) => folder.Replace('\\', '/').TrimEnd('/');
}

public sealed class GitCli
{
    private const string CapturesPrefix = "refs/remotes/origin/ams/captures/";

    private const string CachePathspec = ":(glob)**/.mcs/**";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private static readonly EnvironmentOverlay NoPrompts = EnvironmentOverlay.Empty.Set("GIT_TERMINAL_PROMPT", "0").Set("GCM_INTERACTIVE", "Never").Remove("GIT_ASKPASS").Remove("SSH_ASKPASS");

    private readonly Transcript _transcript;
    private readonly EnvironmentOverlay _overlay;
    private readonly string _executable;

    public GitCli(Transcript transcript, EnvironmentOverlay overlay, string executable = "git")
    {
        _transcript = transcript;
        _overlay = overlay.Then(NoPrompts);
        _executable = executable;
    }

    public ProcessStartInfo StartInfo(string? directory, IReadOnlyList<string> arguments) => ProcessRunner.StartInfo(_executable, arguments, directory, _overlay);

    public Task<MsagentResult> RunAsync(string? directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken) => ProcessRunner.RunAsync(StartInfo(directory, arguments), arguments, stdin: null, Timeout, _transcript, cancellationToken);

    public async Task<MsagentResult> RequireAsync(string? directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunAsync(directory, arguments, cancellationToken).ConfigureAwait(false);

        return result.ExitCode == 0 ? result : throw new E2EException($"git {string.Join(' ', arguments)} exited {result.ExitCode} in {directory ?? Environment.CurrentDirectory}: {Redaction.Apply(result.StdErr.Trim())}");
    }

    public Task<MsagentResult> CloneAsync(string url, string directory, CancellationToken cancellationToken) => RequireAsync(null, ["clone", "--quiet", url, directory], cancellationToken);

    public Task<MsagentResult> CloneWithHelperAsync(string url, string directory, string gitHost, string helper, CancellationToken cancellationToken) => RequireAsync(null, ["-c", $"credential.https://{gitHost}.helper=", "-c", $"credential.https://{gitHost}.helper={helper}", "clone", "--quiet", url, directory], cancellationToken);

    public Task<MsagentResult> InitAsync(string directory, CancellationToken cancellationToken) => RequireAsync(Directory.CreateDirectory(directory).FullName, ["init", "--quiet", "-b", "main"], cancellationToken);

    public Task<MsagentResult> SwitchAsync(string directory, string branch, bool create, CancellationToken cancellationToken) => RequireAsync(directory, create ? ["switch", "--quiet", "-c", branch] : ["switch", "--quiet", branch], cancellationToken);

    public Task<MsagentResult> AddAsync(string directory, IReadOnlyList<string> paths, bool force, CancellationToken cancellationToken) => RequireAsync(directory, force ? ["add", "--force", "--", .. paths] : ["add", "--", .. paths], cancellationToken);

    public Task<MsagentResult> RemoveAsync(string directory, IReadOnlyList<string> paths, bool cached, CancellationToken cancellationToken) => RequireAsync(directory, cached ? ["rm", "-r", "--quiet", "--cached", "--", .. paths] : ["rm", "-r", "--quiet", "--", .. paths], cancellationToken);

    public async Task<string> CommitAsync(string directory, string message, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        await AddAsync(directory, paths, force: false, cancellationToken).ConfigureAwait(false);
        return await CommitStagedAsync(directory, message, cancellationToken).ConfigureAwait(false);
    }

    public Task<string> CommitStagedAsync(string directory, string message, CancellationToken cancellationToken) => HeadAfterAsync(directory, ["commit", "--quiet", "-m", message], cancellationToken);

    public async Task<string> SquashAsync(string directory, string message, CancellationToken cancellationToken)
    {
        await RequireAsync(directory, ["reset", "--quiet", "--soft", Lines(await RequireAsync(directory, ["rev-list", "--max-parents=0", "HEAD"], cancellationToken).ConfigureAwait(false))[0]], cancellationToken).ConfigureAwait(false);

        return await HeadAfterAsync(directory, ["commit", "--amend", "--quiet", "-m", message], cancellationToken).ConfigureAwait(false);
    }

    public Task<MsagentResult> SetConfigAsync(string directory, string key, string value, CancellationToken cancellationToken) => RequireAsync(directory, ["config", key, value], cancellationToken);

    public Task<MsagentResult> PushAsync(string directory, string branch, CancellationToken cancellationToken) => RequireAsync(directory, ["push", "--quiet", "origin", branch], cancellationToken);

    public Task<MsagentResult> PullAsync(string directory, string branch, CancellationToken cancellationToken) => RequireAsync(directory, ["pull", "--ff-only", "--quiet", "origin", branch], cancellationToken);

    public Task<MsagentResult> RestoreAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken) => RequireAsync(directory, ["restore", "--", .. paths], cancellationToken);

    public async Task<IReadOnlyList<string>> StatusAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken) => Lines(await RequireAsync(directory, Status(paths), cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<string>> LogAsync(string directory, string revision, string format, CancellationToken cancellationToken) => Lines(await RequireAsync(directory, ["log", $"--format={format}", revision], cancellationToken).ConfigureAwait(false));

    public Task<string?> RevParseAsync(string directory, string revision, CancellationToken cancellationToken) => ValueAsync(directory, ["rev-parse", "--verify", "--quiet", revision], cancellationToken);

    public async Task<string?> RemoteHeadAsync(string directory, string branch, CancellationToken cancellationToken) => Sha(Lines(await RequireAsync(directory, RemoteHead(branch), cancellationToken).ConfigureAwait(false)));

    public async Task<bool> ExistsAsync(string directory, string objectName, CancellationToken cancellationToken) => (await RunAsync(directory, ["cat-file", "-e", objectName], cancellationToken).ConfigureAwait(false)).ExitCode == 0;

    public async Task<IReadOnlyList<string>> ConfigAsync(string directory, string key, CancellationToken cancellationToken) => await LinesAsync(directory, ["config", "--get-all", key], cancellationToken).ConfigureAwait(false) ?? [];

    public Task<MsagentResult> FetchAsync(string directory, CancellationToken cancellationToken) => RequireAsync(directory, ["fetch", "--quiet", "origin"], cancellationToken);

    public async Task<GitTip> TipAsync(string directory, string branch, CancellationToken cancellationToken) => new(await RevParseAsync(directory, "HEAD", cancellationToken).ConfigureAwait(false), await RemoteHeadAsync(directory, branch, cancellationToken).ConfigureAwait(false), await StatusAsync(directory, [], cancellationToken).ConfigureAwait(false));

    public Task<string?> ShowFileAsync(string directory, string revision, string path, CancellationToken cancellationToken) => ValueAsync(directory, ["show", $"{revision}:{path}"], cancellationToken);

    public async Task<string?> CapturesTipAsync(string directory, string deploymentId, CancellationToken cancellationToken)
    {
        await FetchAsync(directory, cancellationToken).ConfigureAwait(false);

        return Lines(await RequireAsync(directory, ["for-each-ref", "--format=%(refname)%09%(objectname)", CapturesPrefix], cancellationToken).ConfigureAwait(false)).Select(line => line.Split('\t')).Where(parts => AmsIds.Same(parts[0][CapturesPrefix.Length..], deploymentId)).Select(parts => parts[^1]).FirstOrDefault(GitCommits.IsSha);
    }

    public async Task<bool> IsAncestorAsync(string directory, string ancestor, string descendant, CancellationToken cancellationToken) => (await RunAsync(directory, ["merge-base", "--is-ancestor", ancestor, descendant], cancellationToken).ConfigureAwait(false)).ExitCode == 0;

    public async Task<bool> IsModifiedAsync(string directory, string path, CancellationToken cancellationToken) => (await RunAsync(directory, ["diff", "--quiet", "HEAD", "--", path], cancellationToken).ConfigureAwait(false)).ExitCode == 1;

    public async Task<long?> BlobSizeAsync(string directory, string objectName, CancellationToken cancellationToken) => long.TryParse(await ValueAsync(directory, ["cat-file", "-s", objectName], cancellationToken).ConfigureAwait(false), out var size) ? size : null;

    public async Task<string?> CurrentBranchAsync(string directory, CancellationToken cancellationToken) => await ValueAsync(directory, ["branch", "--show-current"], cancellationToken).ConfigureAwait(false) is { Length: > 0 } branch ? branch : null;

    public async Task<IReadOnlyList<string>> RevListAsync(string directory, string revision, CancellationToken cancellationToken) => await LinesAsync(directory, ["rev-list", revision], cancellationToken).ConfigureAwait(false) ?? [];

    public Task<IReadOnlyList<string>?> FilesAsync(string directory, string revision, CancellationToken cancellationToken) => LinesAsync(directory, ["ls-tree", "-r", "--name-only", revision], cancellationToken);

    public async Task<IReadOnlyList<string>> CacheHistoryAsync(string directory, CancellationToken cancellationToken) => Lines(await RequireAsync(directory, ["log", "--all", "--format=", "--name-only", "--", CachePathspec], cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<string>?> HelpersAsync(string directory, string gitHost, CancellationToken cancellationToken) => (await ValueAsync(directory, ["config", "--local", "--get-all", $"credential.https://{gitHost}.helper"], cancellationToken).ConfigureAwait(false))?.Split('\n');

    public async Task<string> LastHelperAsync(string directory, string gitHost, CancellationToken cancellationToken) => await HelpersAsync(directory, gitHost, cancellationToken).ConfigureAwait(false) is [.., var last] ? last : string.Empty;

    public async Task<GitCloneFacts> InspectAsync(string directory, string gitHost, CancellationToken cancellationToken)
    {
        var parent = await ReadCommitAsync(directory, "HEAD~1", cancellationToken).ConfigureAwait(false);

        return new GitCloneFacts(await ValueAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false), await ValueAsync(directory, ["remote", "get-url", "origin"], cancellationToken).ConfigureAwait(false), await ReadCommitAsync(directory, "HEAD", cancellationToken).ConfigureAwait(false), parent, parent is null ? null : await FilesAsync(directory, parent.Sha, cancellationToken).ConfigureAwait(false), await LinesAsync(directory, ["diff-tree", "--no-commit-id", "--name-only", "-r", "--root", "HEAD"], cancellationToken).ConfigureAwait(false), await LinesAsync(directory, Status([]), cancellationToken).ConfigureAwait(false), AmsMcsFiles.ReadRootIgnore(directory) ?? [], await LinesAsync(directory, ["ls-files", "--", ".config"], cancellationToken).ConfigureAwait(false), await LinesAsync(directory, ["ls-files", "--", CachePathspec], cancellationToken).ConfigureAwait(false), await HelpersAsync(directory, gitHost, cancellationToken).ConfigureAwait(false), Sha(await LinesAsync(directory, RemoteHead("main"), cancellationToken).ConfigureAwait(false)));
    }

    public static IReadOnlyList<McsShelf> ReadShelves(string project)
    {
        var root = Path.Combine(project, ".git", "msagent", "mcs");

        return Directory.Exists(root) ? [.. Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal).Select(ReadShelf)] : [];
    }

    public static McsShelf? ShelfOf(string project, string folder) => McsShelf.Find(ReadShelves(project), folder);

    private static McsShelf ReadShelf(string keyDirectory)
    {
        var index = ReadIndex(Path.Combine(keyDirectory, "index.json"));
        var stored = Directory.EnumerateDirectories(keyDirectory).Where(copy => Directory.EnumerateFiles(copy, "*", SearchOption.AllDirectories).Any()).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal);

        return new McsShelf(Path.GetFileName(keyDirectory), keyDirectory, index?.Folder, index?.Identity, index?.Live, index?.Copies ?? [], [.. stored]);
    }

    private static ShelfIndex? ReadIndex(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ShelfIndex>(File.ReadAllText(path), ConfigJson.Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string[] Lines(MsagentResult result) => result.StdOut.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string[] Status(IReadOnlyList<string> paths) => ["status", "--porcelain", "--", .. paths];

    private static string[] RemoteHead(string branch) => ["ls-remote", "origin", $"refs/heads/{branch}"];

    private static string? Sha(IReadOnlyList<string>? lines) => lines is [var line, ..] ? line.Split('\t')[0] : null;

    private async Task<string?> ValueAsync(string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken) => await RunAsync(directory, arguments, cancellationToken).ConfigureAwait(false) is { ExitCode: 0 } result ? result.StdOut.ReplaceLineEndings("\n").TrimEnd('\n') : null;

    private async Task<string> HeadAfterAsync(string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        await RequireAsync(directory, arguments, cancellationToken).ConfigureAwait(false);

        return (await RequireAsync(directory, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false)).StdOut.Trim();
    }

    private async Task<IReadOnlyList<string>?> LinesAsync(string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken) => await RunAsync(directory, arguments, cancellationToken).ConfigureAwait(false) is { ExitCode: 0 } result ? Lines(result) : null;

    private async Task<GitCommitFacts?> ReadCommitAsync(string directory, string revision, CancellationToken cancellationToken) => await ValueAsync(directory, ["log", "-1", "--format=%H%x09%ae%x09%P%x09%s", revision], cancellationToken).ConfigureAwait(false) is { } line ? Commit(line.Split('\t', 4)) : null;

    private static GitCommitFacts Commit(string[] parts) => new(parts[0], parts[1], parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries), parts[3]);

    private sealed record ShelfIndex(string? Folder, string? Identity, string? Live, IReadOnlyList<McsShelfCopy>? Copies);
}
