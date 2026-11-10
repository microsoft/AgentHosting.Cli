using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Reads and writes standalone workflows against the copy of the workspace root's cloud cache that belongs to the commit the root holds, and saves a copy for that commit after every pull or upload.</summary>
public sealed class AmsCloudCacheWorkflowStore : IWorkflowStorePort
{
    private readonly IWorkflowStorePort _inner;
    private readonly AmsCloudCacheShelf _shelf;

    /// <summary>Creates the store.</summary>
    /// <param name="inner">Reads and writes the workflows.</param>
    /// <param name="shelf">Keeps the copies of each workspace root's cloud cache.</param>
    public AmsCloudCacheWorkflowStore(IWorkflowStorePort inner, AmsCloudCacheShelf shelf)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _shelf = shelf ?? throw new ArgumentNullException(nameof(shelf));
    }

    /// <inheritdoc />
    public Task<WorkflowDocument> ReadLocalAsync(string workspaceRoot, CancellationToken cancellationToken) => _inner.ReadLocalAsync(workspaceRoot, cancellationToken);

    /// <inheritdoc />
    public Task<WorkflowDocument?> GetAsync(CopilotEnvironmentContext environment, Guid workflowId, CancellationToken cancellationToken) => _inner.GetAsync(environment, workflowId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkflowDocument>> ListAsync(CopilotEnvironmentContext environment, string? nameFilter, int? maximumCount, CancellationToken cancellationToken) => _inner.ListAsync(environment, nameFilter, maximumCount, cancellationToken);

    /// <inheritdoc />
    public async Task<WorkflowDownloadOutcome> DownloadAsync(CopilotEnvironmentContext environment, Guid workflowId, string workspaceRoot, CancellationToken cancellationToken, bool overwriteLocalChanges = false)
    {
        var gap = await _shelf.PrepareAsync(workspaceRoot, null, cancellationToken).ConfigureAwait(false);
        var replacing = !overwriteLocalChanges && gap is { Clean: true, Behind: not null } ? gap.Place : null;
        var downloaded = await _inner.DownloadAsync(environment, workflowId, workspaceRoot, cancellationToken, overwriteLocalChanges || replacing is not null).ConfigureAwait(false);
        var unsaved = await _shelf.SaveAsync(workspaceRoot, null, cancellationToken).ConfigureAwait(false);
        var replaced = replacing is not null && downloaded.Changed
            ? $"{TerminalText.Sanitize(replacing.Folder)}'s commit ({McsAmsCommits.Short(replacing.Head)}) had never synced with Copilot Studio, so msagent replaced its workflow with Copilot Studio's version; `git diff` shows the change and `git restore {DeploymentCommandHints.Quoted(replacing.Folder)}` undoes it."
            : null;

        return downloaded with { Warnings = [.. downloaded.Warnings, .. new[] { replaced, unsaved }.OfType<string>()] };
    }

    /// <inheritdoc />
    public Task<WorkflowDownloadOutcome> CloneAsync(CopilotEnvironmentContext environment, Guid workflowId, string workspaceRoot, string? name, CancellationToken cancellationToken) => _inner.CloneAsync(environment, workflowId, workspaceRoot, name, cancellationToken);

    /// <inheritdoc />
    public async Task<WorkflowUpsertOutcome> UpsertAsync(CopilotEnvironmentContext environment, string workspaceRoot, CancellationToken cancellationToken)
    {
        await _shelf.PrepareAsync(workspaceRoot, null, cancellationToken).ConfigureAwait(false);
        var upserted = await _inner.UpsertAsync(environment, workspaceRoot, cancellationToken).ConfigureAwait(false);
        await _shelf.SaveAsync(workspaceRoot, null, cancellationToken).ConfigureAwait(false);
        return upserted;
    }

    /// <inheritdoc />
    public async Task RequireNoUnpulledChangesAsync(CopilotEnvironmentContext environment, string workspaceRoot, CancellationToken cancellationToken)
    {
        await _shelf.PrepareAsync(workspaceRoot, null, cancellationToken).ConfigureAwait(false);
        await _inner.RequireNoUnpulledChangesAsync(environment, workspaceRoot, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void WriteConnectionReferenceDeclaration(string workspaceRoot) => _inner.WriteConnectionReferenceDeclaration(workspaceRoot);

    /// <inheritdoc />
    public Task<bool> DeleteAsync(CopilotEnvironmentContext environment, Guid workflowId, CancellationToken cancellationToken) => _inner.DeleteAsync(environment, workflowId, cancellationToken);

    /// <inheritdoc />
    public Task<string> GetAgentUseWarningAsync(CopilotEnvironmentContext environment, Guid workflowId, CancellationToken cancellationToken) => _inner.GetAgentUseWarningAsync(environment, workflowId, cancellationToken);
}
