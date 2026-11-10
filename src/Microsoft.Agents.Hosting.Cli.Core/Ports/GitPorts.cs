namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>A remote of a git working tree.</summary>
/// <param name="Name">The remote's name, such as origin.</param>
/// <param name="Url">The url git fetches from.</param>
public sealed record GitRemote(string Name, string Url);

/// <summary>The git working tree a folder is in.</summary>
/// <param name="Root">The working tree's root folder.</param>
/// <param name="Remotes">The working tree's remotes.</param>
/// <param name="Branch">The checked-out branch, or null when HEAD is detached.</param>
/// <param name="HasCommits">Whether HEAD names a commit.</param>
public sealed record GitWorkingTree(string Root, IReadOnlyList<GitRemote> Remotes, string? Branch, bool HasCommits);

/// <summary>The commit git made, or why it made none.</summary>
/// <param name="CommitSha">The new commit, or null when git made none.</param>
/// <param name="Failure">git's reason on one line, or null when it committed.</param>
public sealed record GitCommitResult(string? CommitSha, string? Failure);

/// <summary>Whether git pushed, and why it did not.</summary>
/// <param name="Pushed">Whether the push succeeded.</param>
/// <param name="Failure">git's reason on one line, or null when it pushed.</param>
public sealed record GitPushResult(bool Pushed, string? Failure);

/// <summary>Runs the git commands AMS mode needs, signing git in to GRS with msagent's credential helper.</summary>
public interface IGitPort
{
    /// <summary>Fails with GitNotFound when git is not on PATH, before anything is written.</summary>
    void RequireGit();

    /// <summary>Reads the git working tree a folder is in.</summary>
    /// <param name="directory">The folder.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <returns>The working tree, or null when the folder is in none.</returns>
    Task<GitWorkingTree?> FindWorkingTreeAsync(string directory, CancellationToken cancellationToken);

    /// <summary>Clones a repository into an empty or missing folder, whose branch is main even when the repository is empty.</summary>
    /// <param name="url">The repository's url.</param>
    /// <param name="directory">The folder to clone into.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    Task CloneAsync(string url, string directory, CancellationToken cancellationToken);

    /// <summary>Makes a folder a working tree whose branch is main.</summary>
    /// <param name="directory">The folder.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    Task InitAsync(string directory, CancellationToken cancellationToken);

    /// <summary>Adds a remote.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="name">The remote's name.</param>
    /// <param name="url">The remote's url.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    Task AddRemoteAsync(string root, string name, string url, CancellationToken cancellationToken);

    /// <summary>Removes a remote with its remote-tracking branches.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="name">The remote's name.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    Task RemoveRemoteAsync(string root, string name, CancellationToken cancellationToken);

    /// <summary>Fetches one branch of a remote.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="remote">The remote's name.</param>
    /// <param name="branch">The branch.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <returns>Whether the remote has the branch, which an empty repository does not.</returns>
    Task<bool> FetchBranchAsync(string root, string remote, string branch, CancellationToken cancellationToken);

    /// <summary>Checks out a local branch that tracks the remote's branch of the same name.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="branch">The branch.</param>
    /// <param name="remote">The remote's name.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    Task CheckoutTrackingBranchAsync(string root, string branch, string remote, CancellationToken cancellationToken);

    /// <summary>Lists every file a commit holds.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="reference">The commit, branch or remote branch.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <returns>The files, as paths relative to the root that '/' separates.</returns>
    Task<IReadOnlyList<string>> ListTreeAsync(string root, string reference, CancellationToken cancellationToken);

    /// <summary>Lists the files git tracks under some paths.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="paths">The paths, relative to the root.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <returns>The tracked files, as paths relative to the root that '/' separates.</returns>
    Task<IReadOnlyList<string>> ListTrackedAsync(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    /// <summary>Lists the files that some commits add or change under some paths.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="revisions">The revisions that select the commits, as git log takes them.</param>
    /// <param name="paths">The pathspecs that limit the files.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <returns>The files, as paths relative to the root that '/' separates.</returns>
    Task<IReadOnlyList<string>> ListCommittedPathsAsync(string root, IReadOnlyList<string> revisions, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    /// <summary>Lists the changed and untracked files under some paths.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="paths">The paths, relative to the root.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <returns>The files, as paths relative to the root that '/' separates.</returns>
    Task<IReadOnlyList<string>> StatusAsync(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    /// <summary>Commits exactly some paths, whatever else is staged, leaving out every file inside a .mcs folder.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="paths">The paths, relative to the root.</param>
    /// <param name="message">The commit message.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    Task<GitCommitResult> CommitAsync(string root, IReadOnlyList<string> paths, string message, CancellationToken cancellationToken);

    /// <summary>Pushes a branch and sets it to track the remote's branch.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="remote">The remote's name.</param>
    /// <param name="branch">The branch.</param>
    /// <param name="replaces">The commit the remote's branch must still hold, which the push then replaces with an unrelated history (git push --force-with-lease), or null for a push that only fast-forwards.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    Task<GitPushResult> PushAsync(string root, string remote, string branch, string? replaces, CancellationToken cancellationToken);

    /// <summary>Resolves a revision, such as HEAD or a short commit id, to a full commit id.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="revision">The revision.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <returns>The commit, or null when git cannot resolve the revision to one.</returns>
    Task<string?> ResolveCommitAsync(string root, string revision, CancellationToken cancellationToken);

    /// <summary>Whether a branch of a remote, as last fetched, holds a commit.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="commit">The commit.</param>
    /// <param name="remote">The remote's name.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    Task<bool> IsOnRemoteBranchAsync(string root, string commit, string remote, CancellationToken cancellationToken);

    /// <summary>Finds the newest commit two revisions share.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="first">The first revision.</param>
    /// <param name="second">The second revision.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <returns>The commit, or null when they share none or git cannot resolve one of them.</returns>
    Task<string?> MergeBaseAsync(string root, string first, string second, CancellationToken cancellationToken);

    /// <summary>Lists the local branches, each named as <see cref="Microsoft.Agents.Hosting.Cli.Core.Ports.GitWorkingTree.Branch"/> names the checked-out branch.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <returns>The branch names.</returns>
    Task<IReadOnlyList<string>> ListLocalBranchesAsync(string root, CancellationToken cancellationToken);

    /// <summary>Reads the absolute path of the working tree's own git folder.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <returns>The git folder.</returns>
    Task<string> GitDirectoryAsync(string root, CancellationToken cancellationToken);

    /// <summary>Restores the tracked files under some paths to a commit's version, deleting the ones the commit lacks and leaving the index and untracked files alone.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="commit">The commit whose version is restored.</param>
    /// <param name="paths">The paths, relative to the root.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    Task RestoreAsync(string root, string commit, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    /// <summary>Writes msagent's credential helper for a repository's host into the working tree's git config.</summary>
    /// <param name="root">The working tree's root.</param>
    /// <param name="url">The repository's url.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    Task SetRepositoryHelperAsync(string root, string url, CancellationToken cancellationToken);

    /// <summary>Writes msagent's credential helper for some hosts into the global git config, replacing the helpers already there.</summary>
    /// <param name="hostPatterns">The url patterns, such as https://*.*.environment.api.powerplatform.com.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    Task SetGlobalHelpersAsync(IReadOnlyList<string> hostPatterns, CancellationToken cancellationToken);
}
