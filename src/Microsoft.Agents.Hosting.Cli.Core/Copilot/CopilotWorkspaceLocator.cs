using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Copilot;

/// <summary>
/// Finds the workspace a command should act on, starting from an explicit directory or the current
/// one. Shared by the workspace commands and by <c>publish</c>, which defaults to the workspace but
/// also accepts an explicit agent.
/// </summary>
public sealed class CopilotWorkspaceLocator
{
    private readonly ICopilotWorkspacePort _workspaces;
    private readonly ICopilotFileSystem _fileSystem;

    /// <summary>Creates the locator.</summary>
    /// <param name="workspaces">The workspace synchronization transport.</param>
    /// <param name="fileSystem">Resolves directories.</param>
    public CopilotWorkspaceLocator(ICopilotWorkspacePort workspaces, ICopilotFileSystem fileSystem)
    {
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    /// <summary>Finds the workspace, failing when there is none.</summary>
    /// <param name="projectDirectory">An explicit directory, or <see langword="null"/> for the current one.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The workspace's connection details.</returns>
    public async Task<CopilotWorkspaceInfo> RequireAsync(string? projectDirectory, CancellationToken cancellationToken) =>
        await FindAsync(projectDirectory, cancellationToken).ConfigureAwait(false)
            ?? throw CopilotStudioException.WorkspaceNotFound(Resolve(projectDirectory));

    /// <summary>Finds the workspace, returning <see langword="null"/> when there is none.</summary>
    /// <param name="projectDirectory">An explicit directory, or <see langword="null"/> for the current one.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The workspace's connection details, or <see langword="null"/>.</returns>
    public async Task<CopilotWorkspaceInfo?> FindAsync(string? projectDirectory, CancellationToken cancellationToken)
    {
        var directory = Resolve(projectDirectory);

        return _workspaces.IsWorkspace(directory)
            ? await _workspaces.ReadWorkspaceInfoAsync(directory, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private string Resolve(string? projectDirectory) => _fileSystem.ResolveFullPath(projectDirectory ?? _fileSystem.CurrentDirectory);
}
