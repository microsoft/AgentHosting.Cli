using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Which commit a deploy from the working folder holds, as git sees the folder.</summary>
/// <param name="CommitSha">The pushed commit the folder is based on, or null when git names none.</param>
/// <param name="Exact">Whether the folder holds exactly that commit.</param>
/// <param name="HeadPushed">Whether the remote holds the folder's HEAD.</param>
/// <param name="HasLocalChanges">Whether the agent or its collection folders hold changes git hasn't committed.</param>
/// <param name="Note">Why git named no commit, as a clause a caller completes with what that means for it, or null.</param>
public sealed record AmsGitDeployState(string? CommitSha, bool Exact, bool HeadPushed, bool HasLocalChanges, string? Note);

/// <summary>Reads, with one set of git commands, which commit a deploy from the working folder holds; the deploy record, and the remote deploy's local-changes warning and folder connect, share it.</summary>
public sealed class AmsGitDeployStates
{
    private readonly IGitPort _git;
    private readonly ICopilotComponentCollectionPort _collections;

    /// <summary>Creates the reader.</summary>
    /// <param name="git">Runs git.</param>
    /// <param name="collections">Finds the collection folders the agent workspace declares.</param>
    public AmsGitDeployStates(IGitPort git, ICopilotComponentCollectionPort collections)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _collections = collections ?? throw new ArgumentNullException(nameof(collections));
    }

    /// <summary>Reads the state of an agent's folder.</summary>
    /// <param name="projectDirectory">The project folder.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    public async Task<AmsGitDeployState> ReadAsync(string projectDirectory, ManagedAgentRecord agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        try
        {
            if (await _git.FindWorkingTreeAsync(projectDirectory, cancellationToken).ConfigureAwait(false) is not { } tree || await _git.ResolveCommitAsync(tree.Root, "HEAD", cancellationToken).ConfigureAwait(false) is not { } head)
            {
                return new(null, false, false, false, $"{projectDirectory} holds no git commit");
            }

            var headPushed = await _git.IsOnRemoteBranchAsync(tree.Root, head, AmsProjectLink.Origin, cancellationToken).ConfigureAwait(false);
            var commit = headPushed ? head : await _git.MergeBaseAsync(tree.Root, "HEAD", $"{AmsProjectLink.Origin}/{AmsProjectLink.Branch}", cancellationToken).ConfigureAwait(false);
            var workspace = Path.Combine(projectDirectory, agent.AgentLocation);
            IReadOnlyList<string> paths = [agent.AgentLocation.Replace('\\', '/'), .. _collections.Resolve(projectDirectory, workspace).Found.Select(folder => Path.GetRelativePath(tree.Root, folder).Replace('\\', '/'))];
            var hasLocalChanges = (await _git.StatusAsync(tree.Root, paths, cancellationToken).ConfigureAwait(false)).Count > 0;
            return new(commit, commit == head && !hasLocalChanges, headPushed, hasLocalChanges, commit is null ? "The folder shares no commit with the repository" : null);
        }
        catch (AuthenticationException failure) when (failure.ErrorCode == ErrorCodes.Authentication.GitNotFound)
        {
            return new(null, false, false, false, "git was not found");
        }
    }
}
