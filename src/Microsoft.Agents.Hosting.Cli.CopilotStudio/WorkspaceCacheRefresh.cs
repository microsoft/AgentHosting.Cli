using System.Collections.Immutable;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Re-reads the cloud baseline from the service, writing only the cache.</summary>
internal static class WorkspaceCacheRefresh
{
    /// <summary>Re-reads the cloud baseline, leaving every workspace file as it is.</summary>
    /// <param name="retargetService">Performs the read.</param>
    /// <param name="workspaceFolder">The workspace whose cache is rewritten.</param>
    /// <param name="operationContext">The authoring context the agent is read under.</param>
    /// <param name="dataverseClient">Reads the environment.</param>
    /// <param name="syncInfo">Identifies the agent being read.</param>
    /// <param name="cloudFlowMetadata">The flows and references this run already confirmed the target holds.</param>
    /// <param name="aiPrompts">The prompts this run already upserted.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What the service reported.</returns>
    public static Task<WorkspaceSyncInfo> RunAsync(IWorkspaceRetargetService retargetService, DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, CloudFlowMetadata? cloudFlowMetadata, ImmutableArray<SyncDataverseClient.AIPromptMetadata> aiPrompts, CancellationToken cancellationToken) =>
        retargetService.SyncWorkspaceAsync(workspaceFolder, operationContext, null, false, dataverseClient, syncInfo, cloudFlowMetadata, cancellationToken, aiPrompts, syncCustomConnectors: false, syncWorkflowsAndPrompts: false);
}
