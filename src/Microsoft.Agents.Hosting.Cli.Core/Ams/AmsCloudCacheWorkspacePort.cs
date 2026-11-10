using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Syncs each Copilot Studio folder against the copy of its cloud cache that belongs to the commit the folder holds, and saves a copy for that commit after every pull or push.</summary>
public sealed class AmsCloudCacheWorkspacePort : ICopilotWorkspacePort
{
    private readonly ICopilotWorkspacePort _inner;
    private readonly AmsCloudCacheShelf _shelf;

    /// <summary>Creates the port.</summary>
    /// <param name="inner">Syncs the folders.</param>
    /// <param name="shelf">Keeps the copies of each folder's cloud cache.</param>
    public AmsCloudCacheWorkspacePort(ICopilotWorkspacePort inner, AmsCloudCacheShelf shelf)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _shelf = shelf ?? throw new ArgumentNullException(nameof(shelf));
    }

    /// <inheritdoc />
    public bool IsWorkspace(string directory) => _inner.IsWorkspace(directory);

    /// <inheritdoc />
    public string ProjectFolderName(string displayName) => _inner.ProjectFolderName(displayName);

    /// <inheritdoc />
    public Task<CopilotWorkspaceInfo> ReadWorkspaceInfoAsync(string directory, CancellationToken cancellationToken) => _inner.ReadWorkspaceInfoAsync(directory, cancellationToken);

    /// <inheritdoc />
    public Task<CopilotCloneOutcome> CloneAsync(CopilotCloneCommand command, CancellationToken cancellationToken) => _inner.CloneAsync(command, cancellationToken);

    /// <inheritdoc />
    public async Task<CopilotChangeSnapshot> GetChangesAsync(CopilotWorkspaceInfo workspace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        await _shelf.PrepareAsync(workspace.WorkspacePath, AmsCloudCacheShelf.IdentityOf(workspace), cancellationToken).ConfigureAwait(false);
        return await _inner.GetChangesAsync(workspace, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CopilotPullOutcome> PullAsync(CopilotWorkspaceInfo workspace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var identity = AmsCloudCacheShelf.IdentityOf(workspace);
        var restored = await _shelf.PrepareAsync(workspace.WorkspacePath, identity, cancellationToken).ConfigureAwait(false) is { } gap ? await RestoreAsync(gap, cancellationToken).ConfigureAwait(false) : null;
        var pulled = await _inner.PullAsync(workspace, cancellationToken).ConfigureAwait(false);
        var unsaved = await _shelf.SaveAsync(workspace.WorkspacePath, identity, cancellationToken).ConfigureAwait(false);

        return pulled with { Warnings = [.. pulled.Warnings, .. new[] { restored, unsaved }.OfType<string>()] };
    }

    /// <inheritdoc />
    public async Task<CopilotPushOutcome> PushAsync(CopilotWorkspaceInfo workspace, bool overwrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var identity = AmsCloudCacheShelf.IdentityOf(workspace);
        await _shelf.PrepareAsync(workspace.WorkspacePath, identity, cancellationToken).ConfigureAwait(false);
        var pushed = await _inner.PushAsync(workspace, overwrite, cancellationToken).ConfigureAwait(false);
        await _shelf.SaveAsync(workspace.WorkspacePath, identity, cancellationToken).ConfigureAwait(false);
        return pushed;
    }

    private async Task<string> RestoreAsync(AmsCloudCacheGap gap, CancellationToken cancellationToken)
    {
        var place = gap.Place;
        if (!gap.Clean)
        {
            throw AmsAgentException.UnsyncedCommitHasChanges(place.Folder, place.Head);
        }

        var synced = gap.Behind ?? throw AmsAgentException.SyncedCommitNotMerged(place.Folder, place.Head, place.Branch, gap.Newest.Commit, gap.Newest.Branch);
        await _shelf.RestoreAsync(gap, synced, cancellationToken).ConfigureAwait(false);
        return $"{TerminalText.Sanitize(place.Folder)}'s commit ({McsAmsCommits.Short(place.Head)}) had never synced with Copilot Studio, so msagent brought its files to {AmsAgentException.CommitOf(synced.Commit, synced.Branch)}, the commit Copilot Studio was last synced from, before pulling; `git diff` shows the result and `git restore {DeploymentCommandHints.Quoted(place.Folder)}` undoes it.";
    }
}
