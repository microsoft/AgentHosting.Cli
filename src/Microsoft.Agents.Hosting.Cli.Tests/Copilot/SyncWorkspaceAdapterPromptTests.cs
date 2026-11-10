using System.Collections.Immutable;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncWorkspaceAdapterPromptTests
{
    private static CopilotWorkspaceInfo Workspace() => new()
    {
        WorkspacePath = Path.Combine(Path.GetTempPath(), "ah-prompt-tests"),
        AgentId = "11111111-2222-3333-4444-555555555555",
        EnvironmentId = "env-1",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static SyncWorkspaceAdapter Create(RecordingWorkspaceSynchronizer synchronizer) => CopilotTestFixtures.WorkspaceAdapter(synchronizer);

    [Fact]
    public async Task Push_WhenAPromptIsRejected_StillCompletesAndWarns()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.PromptResponses.Add(new SyncDataverseClient.AIPromptResponse { PromptName = "Summarize", ErrorMessage = "invalid model" });

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None);

        Assert.Contains(outcome.Warnings, warning => warning.Contains("Summarize", StringComparison.Ordinal));
        Assert.Contains(outcome.Warnings, warning => warning.Contains("invalid model", StringComparison.Ordinal));
    }
}
