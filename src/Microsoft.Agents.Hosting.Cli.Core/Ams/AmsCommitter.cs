using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>The commit AMS mode made, and whether it reached the repository.</summary>
/// <param name="CommitSha">The commit msagent made, or the unpushed HEAD that already held the folders; null when there was nothing to commit or push, or git couldn't commit.</param>
/// <param name="Pushed">Whether the commit was pushed.</param>
/// <param name="Warnings">Why git couldn't commit or push.</param>
/// <param name="Branch">The branch the project folder is on once linked, or null when it isn't linked.</param>
public sealed record AmsCommitOutcome(string? CommitSha, bool Pushed, IReadOnlyList<string> Warnings, string? Branch);

/// <summary>Commits exactly the agent folders AMS mode added, their untracked collection folders and .gitignore, then pushes them, or pushes the unpushed HEAD that already holds them; git's failures are warnings, since the agents and their records exist.</summary>
public sealed class AmsCommitter
{
    private const string GitIgnoreFileName = ".gitignore";

    private readonly IGitPort _git;
    private readonly ICopilotComponentCollectionPort _collections;

    /// <summary>Creates the committer.</summary>
    /// <param name="git">Runs git.</param>
    /// <param name="collections">Finds the collection folders an agent workspace declares.</param>
    public AmsCommitter(IGitPort git, ICopilotComponentCollectionPort collections)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _collections = collections ?? throw new ArgumentNullException(nameof(collections));
    }

    /// <summary>Commits and pushes agent folders. The push only fast-forwards, except for a folder whose own history replaces a new repository's initial commit: that push is leased on the commit main holds when msagent fetches it.</summary>
    /// <param name="root">The working tree's root, which is the project folder.</param>
    /// <param name="agentFolders">The agent folders, relative to the root.</param>
    /// <param name="message">The commit message.</param>
    /// <param name="newRepository">The link to the repository register just created, or null for an existing repository.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    public async Task<AmsCommitOutcome> CommitAndPushAsync(string root, IReadOnlyList<string> agentFolders, string message, AmsNewRepositoryLink? newRepository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentFolders);

        var collections = agentFolders.SelectMany(folder => _collections.Resolve(root, Path.Combine(root, folder)).Found).Select(folder => Path.GetRelativePath(root, folder).Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (newRepository is { Linked: false })
        {
            return new(null, false, [$"Nothing was committed. Once {root} is linked, run `git add -- {string.Join(' ', Paths(root, agentFolders, collections))}`, `git commit` and `git push -u {AmsProjectLink.Origin} {AmsProjectLink.Branch}` there."], null);
        }

        var tracked = (await _git.ListTrackedAsync(root, collections, cancellationToken).ConfigureAwait(false)).ToArray();
        var paths = Paths(root, agentFolders, [.. collections.Where(collection => !tracked.Any(path => path.StartsWith($"{collection}/", StringComparison.OrdinalIgnoreCase)))]);
        var branch = (await _git.FindWorkingTreeAsync(root, cancellationToken).ConfigureAwait(false))?.Branch ?? AmsProjectLink.Branch;
        string commit;
        if ((await _git.StatusAsync(root, paths, cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            if (await _git.ResolveCommitAsync(root, "HEAD", cancellationToken).ConfigureAwait(false) is not { } head || await _git.IsOnRemoteBranchAsync(root, head, AmsProjectLink.Origin, cancellationToken).ConfigureAwait(false))
            {
                return new(null, false, [], branch);
            }

            commit = head;
        }
        else
        {
            var committed = await _git.CommitAsync(root, paths, message, cancellationToken).ConfigureAwait(false);
            if (committed.CommitSha is null)
            {
                return new(null, false, [$"git could not commit: {committed.Failure} {(IsIdentityMissing(committed.Failure) ? "Set user.name and user.email with `git config --global`" : "Fix what git reports")}, then run `git commit` and `git push -u {AmsProjectLink.Origin} {branch}` in {root}."], branch);
            }

            commit = committed.CommitSha;
        }

        string? replaces = null;
        if (newRepository is { ReplacesInitialCommit: true } && branch == AmsProjectLink.Branch)
        {
            try
            {
                replaces = await _git.FetchBranchAsync(root, AmsProjectLink.Origin, branch, cancellationToken).ConfigureAwait(false) ? await _git.ResolveCommitAsync(root, $"{AmsProjectLink.Origin}/{branch}", cancellationToken).ConfigureAwait(false) : null;
            }
            catch (AmsAgentException failure)
            {
                return new(commit, false, [$"git couldn't read {branch} of the new repository: {failure.Message} Run `git fetch {AmsProjectLink.Origin} {branch}` and `git push --force-with-lease={branch} -u {AmsProjectLink.Origin} {branch}` in {root}."], branch);
            }
        }

        var pushed = await _git.PushAsync(root, AmsProjectLink.Origin, branch, replaces, cancellationToken).ConfigureAwait(false);
        return new(commit, pushed.Pushed, pushed.Pushed ? [] : [replaces is null ? $"git push failed: {pushed.Failure} Run `git push -u {AmsProjectLink.Origin} {branch}` in {root}." : $"git push failed: {pushed.Failure} Run `git push --force-with-lease={branch}:{replaces} -u {AmsProjectLink.Origin} {branch}` in {root}, or, when {branch} has moved since, `git pull --allow-unrelated-histories {AmsProjectLink.Origin} {branch}` and then `git push -u {AmsProjectLink.Origin} {branch}`."], branch);
    }

    private static string[] Paths(string root, IReadOnlyList<string> agentFolders, IReadOnlyList<string> collections) =>
        [.. agentFolders.Select(folder => folder.Replace('\\', '/')), .. collections, .. File.Exists(Path.Combine(root, GitIgnoreFileName)) ? [GitIgnoreFileName] : Array.Empty<string>()];

    private static bool IsIdentityMissing(string? failure) => failure is not null && (failure.Contains("identity unknown", StringComparison.OrdinalIgnoreCase) || failure.Contains("empty ident", StringComparison.OrdinalIgnoreCase));
}
