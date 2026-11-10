using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Removes connection references against the copy of the folder's cloud cache that belongs to the commit the folder holds, and saves that copy again once the removal edits it.</summary>
public sealed class AmsCloudCacheConnectionPort : ICopilotConnectionPort
{
    private readonly ICopilotConnectionPort _inner;
    private readonly AmsCloudCacheShelf _shelf;
    private readonly ICopilotWorkspacePort _workspaces;
    private readonly IWorkflowWorkspacePort _workflows;

    /// <summary>Creates the port.</summary>
    /// <param name="inner">Reads and edits the connection references.</param>
    /// <param name="shelf">Keeps the copies of each folder's cloud cache.</param>
    /// <param name="workspaces">Reads the agent a folder is connected to.</param>
    /// <param name="workflows">Tells a standalone workflow's workspace root from an agent's folder.</param>
    public AmsCloudCacheConnectionPort(ICopilotConnectionPort inner, AmsCloudCacheShelf shelf, ICopilotWorkspacePort workspaces, IWorkflowWorkspacePort workflows)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _shelf = shelf ?? throw new ArgumentNullException(nameof(shelf));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
    }

    /// <inheritdoc />
    public Task<CopilotConnectionSnapshot> ReadConnectionsAsync(CopilotEnvironmentContext environment, string projectDirectory, CancellationToken cancellationToken) => _inner.ReadConnectionsAsync(environment, projectDirectory, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ReadDeclaredConnectorsAsync(CopilotEnvironmentContext environment, string projectDirectory, CancellationToken cancellationToken) => _inner.ReadDeclaredConnectorsAsync(environment, projectDirectory, cancellationToken);

    /// <inheritdoc />
    public Task BindAsync(CopilotEnvironmentContext environment, string projectDirectory, IReadOnlyList<CopilotConnectionBinding> bindings, CancellationToken cancellationToken) => _inner.BindAsync(environment, projectDirectory, bindings, cancellationToken);

    /// <inheritdoc />
    public Task<string> CreateConnectionReferenceAsync(CopilotEnvironmentContext environment, string projectDirectory, string connectorInternalId, CancellationToken cancellationToken) => _inner.CreateConnectionReferenceAsync(environment, projectDirectory, connectorInternalId, cancellationToken);

    /// <inheritdoc />
    public async Task<CopilotConnectionReferenceRemoval> RemoveConnectionReferenceAsync(CopilotEnvironmentContext environment, string projectDirectory, string logicalName, bool confirmed, CancellationToken cancellationToken)
    {
        var standalone = _workflows.IsStandaloneWorkspace(projectDirectory);
        var identity = standalone ? null : await AmsCloudCacheShelf.ReadIdentityAsync(_workspaces, projectDirectory, cancellationToken).ConfigureAwait(false);
        var tracked = standalone || identity is not null;
        var gap = tracked ? await _shelf.PrepareAsync(projectDirectory, identity, cancellationToken).ConfigureAwait(false) : null;
        var removal = await _inner.RemoveConnectionReferenceAsync(environment, projectDirectory, logicalName, confirmed, cancellationToken).ConfigureAwait(false);
        if (tracked && gap is null && removal.Removed)
        {
            await _shelf.SaveAsync(projectDirectory, identity, cancellationToken).ConfigureAwait(false);
        }

        return removal;
    }

    /// <inheritdoc />
    public IReadOnlyList<CopilotWorkflowView> ReadWorkflows(string projectDirectory, IReadOnlyCollection<string> boundReferences) => _inner.ReadWorkflows(projectDirectory, boundReferences);

    /// <inheritdoc />
    public Task<CopilotWorkflowActivation> SetWorkflowStatesAsync(CopilotEnvironmentContext environment, string projectDirectory, IReadOnlyList<CopilotWorkflowStateChange> changes, CancellationToken cancellationToken) => _inner.SetWorkflowStatesAsync(environment, projectDirectory, changes, cancellationToken);

    /// <inheritdoc />
    public Task<CopilotWorkflowActivationReport> ActivateWorkflowsAsync(CopilotEnvironmentContext environment, string projectDirectory, string? principal, CancellationToken cancellationToken) => _inner.ActivateWorkflowsAsync(environment, projectDirectory, principal, cancellationToken);
}
