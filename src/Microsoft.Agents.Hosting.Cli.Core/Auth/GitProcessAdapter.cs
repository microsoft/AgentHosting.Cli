using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Playground;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>Runs git as a child process without a shell, with its prompts off and msagent's credential helper on every command that reaches a remote.</summary>
public sealed class GitProcessAdapter : IGitPort
{
    /// <summary>The longest a git command that stays on this machine may take.</summary>
    public static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest a git command that reaches a remote may take.</summary>
    public static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(10);

    private const string MainBranch = "main";

    private readonly IPlaygroundProcessRunner _processes;
    private readonly PlaygroundToolLocator _tools;
    private readonly GitCredentialHelper _helper;
    private string? _git;

    /// <summary>Creates the adapter.</summary>
    /// <param name="processes">Runs git.</param>
    /// <param name="tools">Finds git on PATH.</param>
    /// <param name="helper">Builds msagent's credential helper.</param>
    public GitProcessAdapter(IPlaygroundProcessRunner processes, PlaygroundToolLocator tools, GitCredentialHelper helper)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
    }

    /// <inheritdoc />
    public void RequireGit() => Git();

    /// <inheritdoc />
    public async Task<GitWorkingTree?> FindWorkingTreeAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var topLevel = await RunAsync(directory, ["rev-parse", "--show-toplevel"], LocalTimeout, null, cancellationToken).ConfigureAwait(false);
        if (topLevel.ExitCode != 0)
        {
            return topLevel.StandardError.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) ? null : throw Failed("rev-parse", directory, topLevel);
        }

        var root = Path.GetFullPath(topLevel.StandardOutput.Trim());
        var remotes = (await RequireAsync(root, ["remote", "-v"], LocalTimeout, null, cancellationToken).ConfigureAwait(false)).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line => line.Split('\t', 2)).Where(parts => parts.Length == 2 && parts[1].EndsWith(" (fetch)", StringComparison.Ordinal)).Select(parts => new GitRemote(parts[0], parts[1][..^" (fetch)".Length])).ToArray();
        var branch = await RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], LocalTimeout, null, cancellationToken).ConfigureAwait(false);
        var head = await RunAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD"], LocalTimeout, null, cancellationToken).ConfigureAwait(false);
        return new GitWorkingTree(root, remotes, branch.ExitCode == 0 && branch.StandardOutput.Trim() is { Length: > 0 } name ? name : null, head.ExitCode == 0);
    }

    /// <inheritdoc />
    public async Task CloneAsync(string url, string directory, CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(directory);
        await RequireAsync(Path.GetDirectoryName(target) ?? target, ["clone", url, target], NetworkTimeout, url, cancellationToken).ConfigureAwait(false);
        if ((await RunAsync(target, ["rev-parse", "--verify", "--quiet", "HEAD"], LocalTimeout, null, cancellationToken).ConfigureAwait(false)).ExitCode != 0)
        {
            await RequireAsync(target, ["symbolic-ref", "HEAD", $"refs/heads/{MainBranch}"], LocalTimeout, null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task InitAsync(string directory, CancellationToken cancellationToken) => RequireAsync(directory, ["init", $"--initial-branch={MainBranch}"], LocalTimeout, null, cancellationToken);

    /// <inheritdoc />
    public Task AddRemoteAsync(string root, string name, string url, CancellationToken cancellationToken) => RequireAsync(root, ["remote", "add", name, url], LocalTimeout, null, cancellationToken);

    /// <inheritdoc />
    public Task RemoveRemoteAsync(string root, string name, CancellationToken cancellationToken) => RequireAsync(root, ["remote", "remove", name], LocalTimeout, null, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> FetchBranchAsync(string root, string remote, string branch, CancellationToken cancellationToken)
    {
        var fetched = await RunAsync(root, ["fetch", remote, branch], NetworkTimeout, await RemoteUrlAsync(root, remote, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return fetched.ExitCode == 0 || (fetched.StandardError.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase) ? false : throw Failed("fetch", root, fetched));
    }

    /// <inheritdoc />
    public Task CheckoutTrackingBranchAsync(string root, string branch, string remote, CancellationToken cancellationToken) => RequireAsync(root, ["checkout", "-b", branch, "--track", $"{remote}/{branch}"], LocalTimeout, null, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListTreeAsync(string root, string reference, CancellationToken cancellationToken) => Entries((await RequireAsync(root, ["ls-tree", "-r", "-z", "--name-only", reference], LocalTimeout, null, cancellationToken).ConfigureAwait(false)).StandardOutput);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListTrackedAsync(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken) => paths.Count == 0 ? [] : Entries((await RequireAsync(root, ["ls-files", "-z", "--", .. paths], LocalTimeout, null, cancellationToken).ConfigureAwait(false)).StandardOutput);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListCommittedPathsAsync(string root, IReadOnlyList<string> revisions, IReadOnlyList<string> paths, CancellationToken cancellationToken) => Entries((await RequireAsync(root, ["log", "--format=", "--name-only", "-z", "--diff-filter=AM", .. revisions, "--", .. paths], LocalTimeout, null, cancellationToken).ConfigureAwait(false)).StandardOutput);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> StatusAsync(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken) => paths.Count == 0 ? [] : StatusPaths((await RequireAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--", .. paths], LocalTimeout, null, cancellationToken).ConfigureAwait(false)).StandardOutput);

    /// <inheritdoc />
    public async Task<GitCommitResult> CommitAsync(string root, IReadOnlyList<string> paths, string message, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> pathspecs = [.. paths, .. SyncStatePathspecs.Excluded];
        await RequireAsync(root, ["add", "--", .. pathspecs], LocalTimeout, null, cancellationToken).ConfigureAwait(false);
        var committed = await RunAsync(root, ["commit", "-m", message, "--", .. pathspecs], LocalTimeout, null, cancellationToken).ConfigureAwait(false);
        return committed.ExitCode == 0 ? new GitCommitResult(await ResolveCommitAsync(root, "HEAD", cancellationToken).ConfigureAwait(false), null) : new GitCommitResult(null, Reason(committed));
    }

    /// <inheritdoc />
    public async Task<GitPushResult> PushAsync(string root, string remote, string branch, string? replaces, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> lease = replaces is null ? [] : [$"--force-with-lease={branch}:{replaces}"];
        var pushed = await RunAsync(root, ["push", .. lease, "-u", remote, branch], NetworkTimeout, await RemoteUrlAsync(root, remote, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return pushed.ExitCode == 0 ? new GitPushResult(true, null) : new GitPushResult(false, Reason(pushed));
    }

    /// <inheritdoc />
    public async Task<string?> ResolveCommitAsync(string root, string revision, CancellationToken cancellationToken)
    {
        var resolved = await RunAsync(root, ["rev-parse", "--verify", "--quiet", $"{revision}^{{commit}}"], LocalTimeout, null, cancellationToken).ConfigureAwait(false);
        return resolved.ExitCode == 0 && resolved.StandardOutput.Trim() is { Length: > 0 } commit ? commit : null;
    }

    /// <inheritdoc />
    public async Task<bool> IsOnRemoteBranchAsync(string root, string commit, string remote, CancellationToken cancellationToken) => (await RequireAsync(root, ["branch", "-r", "--contains", commit, "--list", $"{remote}/*"], LocalTimeout, null, cancellationToken).ConfigureAwait(false)).StandardOutput.Trim().Length > 0;

    /// <inheritdoc />
    public async Task<string?> MergeBaseAsync(string root, string first, string second, CancellationToken cancellationToken)
    {
        var mergeBase = await RunAsync(root, ["merge-base", first, second], LocalTimeout, null, cancellationToken).ConfigureAwait(false);
        return mergeBase.ExitCode == 0 && mergeBase.StandardOutput.Trim() is { Length: > 0 } commit ? commit : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListLocalBranchesAsync(string root, CancellationToken cancellationToken) => (await RequireAsync(root, ["-c", "core.warnAmbiguousRefs=false", "for-each-ref", "--format=%(refname:short)", "refs/heads/"], LocalTimeout, null, cancellationToken).ConfigureAwait(false)).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <inheritdoc />
    public async Task<string> GitDirectoryAsync(string root, CancellationToken cancellationToken) => Path.GetFullPath((await RequireAsync(root, ["rev-parse", "--absolute-git-dir"], LocalTimeout, null, cancellationToken).ConfigureAwait(false)).StandardOutput.Trim());

    /// <inheritdoc />
    public Task RestoreAsync(string root, string commit, IReadOnlyList<string> paths, CancellationToken cancellationToken) => RequireAsync(root, ["restore", $"--source={commit}", "--worktree", "--", .. paths], LocalTimeout, null, cancellationToken);

    /// <inheritdoc />
    public async Task SetRepositoryHelperAsync(string root, string url, CancellationToken cancellationToken)
    {
        if (HelperKey(url) is { } key)
        {
            await RequireAsync(root, ["config", "--local", "--replace-all", key, string.Empty], LocalTimeout, null, cancellationToken).ConfigureAwait(false);
            await RequireAsync(root, ["config", "--local", "--add", key, _helper.Command()], LocalTimeout, null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task SetGlobalHelpersAsync(IReadOnlyList<string> hostPatterns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hostPatterns);

        var git = Git();
        var helper = _helper.Command();
        foreach (var hostPattern in hostPatterns)
        {
            await ConfigureGlobalAsync(git, ["--replace-all", $"credential.{hostPattern}.helper", string.Empty], cancellationToken).ConfigureAwait(false);
            await ConfigureGlobalAsync(git, ["--add", $"credential.{hostPattern}.helper", helper], cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? HelperKey(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? $"credential.https://{uri.Host}.helper" : null;

    private static string[] Entries(string output) => output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static List<string> StatusPaths(string output)
    {
        var entries = output.Split('\0');
        var paths = new List<string>();
        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index].Length > 3)
            {
                paths.Add(entries[index][3..]);
                index += entries[index][0] is 'R' or 'C' || entries[index][1] is 'R' or 'C' ? 1 : 0;
            }
        }

        return paths;
    }

    private static string Reason(PlaygroundProcessResult result) => GitErrors.Reason(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);

    private static AmsAgentException Failed(string subcommand, string directory, PlaygroundProcessResult result) => AmsAgentException.GitCommandFailed($"git {subcommand}", directory, Reason(result) is { Length: > 0 } reason ? reason : $"git exited with code {result.ExitCode}.");

    private static string Subcommand(IReadOnlyList<string> arguments) => arguments[0] == "-c" ? arguments[2] : arguments[0];

    private string Git() => _git ??= _tools.FindOnPath("git") ?? throw GitErrors.NotFound("git was not found on PATH, and AMS mode needs it.", "Install git from https://git-scm.com/downloads, open a new terminal, then run the command again.");

    private async Task<string> RemoteUrlAsync(string root, string remote, CancellationToken cancellationToken) => (await RequireAsync(root, ["remote", "get-url", remote], LocalTimeout, null, cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();

    private async Task<PlaygroundProcessResult> RequireAsync(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout, string? remoteUrl, CancellationToken cancellationToken)
    {
        var result = await RunAsync(workingDirectory, arguments, timeout, remoteUrl, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result : throw Failed(Subcommand(arguments), workingDirectory, result);
    }

    private async Task<PlaygroundProcessResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout, string? remoteUrl, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> helper = remoteUrl is not null && HelperKey(remoteUrl) is { } key ? ["-c", $"{key}=", "-c", $"{key}={_helper.Command()}"] : [];
        try
        {
            return await _processes.RunAsync(new PlaygroundProcessRequest { ExecutablePath = Git(), Arguments = [.. helper, .. arguments], WorkingDirectory = Path.GetFullPath(workingDirectory), Timeout = timeout, EnvironmentOverrides = new Dictionary<string, string?>(StringComparer.Ordinal) { ["GIT_TERMINAL_PROMPT"] = "0" } }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is InvalidOperationException or TimeoutException)
        {
            throw AmsAgentException.GitCommandFailed($"git {Subcommand(arguments)}", workingDirectory, failure.Message);
        }
    }

    private async Task ConfigureGlobalAsync(string git, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        PlaygroundProcessResult result;
        try
        {
            result = await _processes.RunAsync(new PlaygroundProcessRequest { ExecutablePath = git, Arguments = ["config", "--global", .. arguments], Timeout = LocalTimeout }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is InvalidOperationException or TimeoutException)
        {
            throw GitErrors.ConfigurationFailed(failure.Message);
        }

        if (result.ExitCode != 0)
        {
            throw GitErrors.ConfigurationFailed(result.StandardError);
        }
    }
}
