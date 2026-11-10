using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Resolves and pushes the component collections an agent workspace declares through the sync library's collection folders and push sequence.</summary>
public sealed class SyncComponentCollectionAdapter : ICopilotComponentCollectionPort
{
    /// <inheritdoc />
    public CopilotComponentCollections Resolve(string projectDirectory, string agentWorkspaceDirectory)
    {
        var resolution = ComponentCollectionFolders.Resolve(projectDirectory, agentWorkspaceDirectory, AgentConfigLocator.PhysicalPath);

        return new CopilotComponentCollections([.. resolution.Found.Select(ToDirectory)], resolution.Missing);
    }

    /// <inheritdoc />
    public async Task<CopilotComponentCollectionPush<TPushed>> PushAsync<TPushed>(string projectDirectory, string agentWorkspaceDirectory, Func<string, CancellationToken, Task<TPushed>> pushWorkspaceAsync, Func<string, CancellationToken, Task<bool>> shouldPushCollectionAsync, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pushWorkspaceAsync);
        ArgumentNullException.ThrowIfNull(shouldPushCollectionAsync);

        var resolution = ComponentCollectionFolders.Resolve(projectDirectory, agentWorkspaceDirectory, AgentConfigLocator.PhysicalPath);
        var agentFolder = new DirectoryPath(Path.GetFullPath(agentWorkspaceDirectory).Replace('\\', '/'));
        var pushed = await ComponentCollectionPushSequence.PushAsync(resolution, agentFolder, (folder, token) => pushWorkspaceAsync(folder == agentFolder ? agentWorkspaceDirectory : ToDirectory(folder), token), (folder, token) => shouldPushCollectionAsync(ToDirectory(folder), token), cancellationToken).ConfigureAwait(false);

        return new CopilotComponentCollectionPush<TPushed>
        {
            Collections = [.. resolution.Found.Select(folder => pushed.Collections.FirstOrDefault(collection => collection.Folder == folder) is { } collection ? new CopilotComponentCollectionPushed<TPushed>(ToDirectory(folder), true, collection.Pushed) : new CopilotComponentCollectionPushed<TPushed>(ToDirectory(folder), false, default))],
            Missing = pushed.Missing,
            Agent = pushed.Agent,
        };
    }

    private static string ToDirectory(DirectoryPath folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.ToString()));
}
