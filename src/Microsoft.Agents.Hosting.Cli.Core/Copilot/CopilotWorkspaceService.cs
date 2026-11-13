using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Copilot;

/// <summary>
/// Workspace commands: <c>clone</c>, <c>pull</c>, and <c>push</c>.
/// </summary>
public sealed class CopilotWorkspaceService
{
    private readonly ICopilotWorkspacePort _workspaces;
    private readonly CopilotWorkspaceLocator _workspaceLocator;

    /// <summary>Creates the service.</summary>
    /// <param name="workspaces">The workspace synchronization transport.</param>
    /// <param name="workspaceLocator">Finds the workspace a command acts on.</param>
    public CopilotWorkspaceService(ICopilotWorkspacePort workspaces, CopilotWorkspaceLocator workspaceLocator)
    {
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _workspaceLocator = workspaceLocator ?? throw new ArgumentNullException(nameof(workspaceLocator));
    }

    /// <summary>Applies remote changes to a workspace.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The changes that were applied.</returns>
    public Task<CopilotPullResult> PullAsync(CopilotPullRequest request, CancellationToken cancellationToken) =>
        PullCoreAsync(request, null, cancellationToken);

    /// <summary>Pulls under the environment and authentication snapshot captured by the caller.</summary>
    /// <param name="request">The pull input.</param>
    /// <param name="environment">The captured target, including its resource tenant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The changes that were applied.</returns>
    public Task<CopilotPullResult> PullForEnvironmentAsync(CopilotPullRequest request,
        CopilotEnvironmentContext environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return PullCoreAsync(request, environment, cancellationToken);
    }

    private async Task<CopilotPullResult> PullCoreAsync(CopilotPullRequest request,
        CopilotEnvironmentContext? environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workspace = BindWorkspace(await _workspaceLocator.RequireAsync(request.ProjectDirectory, cancellationToken).ConfigureAwait(false), environment);
        var pulled = await _workspaces.PullAsync(workspace, cancellationToken).ConfigureAwait(false);

        return new CopilotPullResult
        {
            Status = pulled.Changes.Count == 0 ? "up-to-date" : "pulled",
            WorkspacePath = workspace.WorkspacePath,
            AgentId = workspace.AgentId,
            Changes = pulled.Changes,
            ChangeCount = pulled.Changes.Count,
            Warnings = pulled.Warnings,
        };
    }

    /// <summary>
    /// Uploads local changes, refusing when remote work would be overwritten.
    /// </summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The changes that were uploaded.</returns>
    public Task<CopilotPushResult> PushAsync(CopilotPushRequest request, CancellationToken cancellationToken) =>
        PushCoreAsync(request, null, cancellationToken);

    /// <summary>Pushes under the environment and authentication snapshot captured by the caller.</summary>
    /// <param name="request">The push input.</param>
    /// <param name="environment">The captured target, including its resource tenant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The changes that were uploaded.</returns>
    public Task<CopilotPushResult> PushForEnvironmentAsync(CopilotPushRequest request,
        CopilotEnvironmentContext environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return PushCoreAsync(request, environment, cancellationToken);
    }

    private async Task<CopilotPushResult> PushCoreAsync(CopilotPushRequest request,
        CopilotEnvironmentContext? environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workspace = BindWorkspace(await _workspaceLocator.RequireAsync(request.ProjectDirectory, cancellationToken).ConfigureAwait(false), environment);
        var snapshot = await _workspaces.GetChangesAsync(workspace, cancellationToken).ConfigureAwait(false);
        var conflicting = FindConflicts(snapshot);

        if (conflicting.Count > 0 && !request.Overwrite)
        {
            throw CopilotStudioException.PushConflictsRequirePull(conflicting);
        }

        var pushed = await _workspaces.PushAsync(workspace, request.Overwrite, cancellationToken).ConfigureAwait(false);
        var sentNothing = pushed.Changes.Count == 0 && pushed.AuxiliaryChangeCount == 0 && pushed.UploadedFileNames.Count == 0;

        return PushResult(sentNothing ? "no-local-changes" : "pushed", workspace, pushed.Changes) with
        {
            UploadedFileCount = pushed.UploadedFileNames.Count,
            AuxiliaryChangeCount = pushed.AuxiliaryChangeCount,
            Warnings = pushed.Warnings,
        };
    }

    private static CopilotWorkspaceInfo BindWorkspace(CopilotWorkspaceInfo workspace, CopilotEnvironmentContext? environment)
    {
        if (environment is null)
            return workspace;

        if (!string.Equals(workspace.EnvironmentId, environment.EnvironmentId, StringComparison.OrdinalIgnoreCase)
            || (workspace.TenantId is not null
                && !string.Equals(workspace.TenantId, environment.TenantId, StringComparison.OrdinalIgnoreCase)))
            throw new CloudRetargetedException("The workspace does not match the captured deployment target.");
        WorkspaceConnectionVerifier.RequireOriginMatches(workspace, environment);
        return workspace with { Context = environment.Context, TenantId = environment.TenantId };
    }

    /// <summary>Components the workspace already tracks that changed in the cloud since the last sync.</summary>
    /// <param name="snapshot">The local and remote change sets.</param>
    /// <returns>The conflicting schema names, ordered.</returns>
    public static IReadOnlyList<string> FindConflicts(CopilotChangeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var locallyChanged = snapshot.Local.Select(change => change.SchemaName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. snapshot.Remote.Where(change => IsTracked(change, locallyChanged)).Select(change => change.SchemaName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(schemaName => schemaName, StringComparer.Ordinal)];
    }

    private static bool IsTracked(CopilotChange change, HashSet<string> locallyChanged) =>
        !string.Equals(change.Kind, CopilotChangeKinds.Created, StringComparison.OrdinalIgnoreCase)
        || locallyChanged.Contains(change.SchemaName);

    private static CopilotPushResult PushResult(string status, CopilotWorkspaceInfo workspace, IReadOnlyList<CopilotChange> changes) => new()
    {
        Status = status,
        WorkspacePath = workspace.WorkspacePath,
        AgentId = workspace.AgentId,
        Changes = changes,
        ChangeCount = changes.Count,
    };
}
