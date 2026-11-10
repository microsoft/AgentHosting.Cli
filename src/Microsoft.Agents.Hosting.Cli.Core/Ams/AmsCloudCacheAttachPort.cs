using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Attaches and connects Copilot Studio folders, then saves a copy of each connected folder's cloud cache for the commit the folder holds.</summary>
public sealed class AmsCloudCacheAttachPort : ICopilotWorkspaceAttachPort, ICopilotWorkspaceConnectPort
{
    private readonly ICopilotWorkspaceAttachPort _attach;
    private readonly ICopilotWorkspaceConnectPort _connect;
    private readonly AmsCloudCacheShelf _shelf;
    private readonly ICopilotWorkspacePort _workspaces;

    /// <summary>Creates the port.</summary>
    /// <param name="attach">Attaches a folder to a new agent.</param>
    /// <param name="connect">Connects folders to an existing agent.</param>
    /// <param name="shelf">Keeps the copies of each folder's cloud cache.</param>
    /// <param name="workspaces">Reads the agent each folder is connected to.</param>
    public AmsCloudCacheAttachPort(ICopilotWorkspaceAttachPort attach, ICopilotWorkspaceConnectPort connect, AmsCloudCacheShelf shelf, ICopilotWorkspacePort workspaces)
    {
        _attach = attach ?? throw new ArgumentNullException(nameof(attach));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _shelf = shelf ?? throw new ArgumentNullException(nameof(shelf));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
    }

    /// <inheritdoc />
    public async Task<CopilotAttachOutcome> AttachAsync(CopilotAttachCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var attached = await _attach.AttachAsync(command, cancellationToken).ConfigureAwait(false);
        return await _shelf.SaveConnectedAsync(_workspaces, command.ProjectDirectory, cancellationToken).ConfigureAwait(false) is { } unsaved
            ? attached with { Notices = [.. attached.Notices, unsaved] }
            : attached;
    }

    /// <inheritdoc />
    public async Task<CopilotConnectOutcome> ConnectAsync(CopilotConnectCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var connected = await _connect.ConnectAsync(command, cancellationToken).ConfigureAwait(false);
        foreach (var folder in command.CollectionDirectories.Except(connected.SkippedCollectionDirectories, StringComparer.Ordinal).Prepend(command.WorkspaceDirectory))
        {
            await _shelf.SaveConnectedAsync(_workspaces, folder, cancellationToken).ConfigureAwait(false);
        }

        return connected;
    }
}
