using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Reads workspace content through the sync library, reporting an unreadable file as a typed failure that names the file and position.
/// </summary>
internal static class WorkspaceDefinitionReader
{
    /// <summary>Reads the workspace definition.</summary>
    internal static Task<DefinitionBase> ReadAsync(IWorkspaceSynchronizer synchronizer, DirectoryPath workspaceFolder, bool checkKnowledgeFiles, CancellationToken cancellationToken) =>
        TranslateAsync(() => synchronizer.ReadWorkspaceDefinitionAsync(workspaceFolder, cancellationToken, checkKnowledgeFiles));

    internal static CopilotStudioException Translate(WorkspaceValidationException failure) => CopilotStudioException.WorkspaceFilesUnreadable(SingleLine(failure.Message), failure);

    private static async Task<DefinitionBase> TranslateAsync(Func<Task<DefinitionBase>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (WorkspaceValidationException failure)
        {
            throw Translate(failure);
        }
    }

    private static string SingleLine(string summary) =>
        string.Join(' ', summary.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
