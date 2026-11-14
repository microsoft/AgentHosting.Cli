using System.Collections.Immutable;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.McsCore;
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

    private static SyncWorkspaceAdapter Create(RecordingWorkspaceSynchronizer synchronizer) =>
        new(
            synchronizer,
            synchronizer,
            new RecordingRetargetService(),
            new StubSolutionVersionsClient(),
            new StubComponentCollectionClient(),
            new StubOperationContextProvider(),
            new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider()),
            new CopilotChangeMapper());

    private static RecordingWorkspaceSynchronizer SynchronizerWithPrompt(string? errorMessage = null)
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.PromptResponses.Add(new SyncDataverseClient.AIPromptResponse { PromptName = "Summarize", ErrorMessage = errorMessage });

        if (errorMessage is null)
        {
            synchronizer.PromptMetadata.Add(new SyncDataverseClient.AIPromptMetadata { Name = "Summarize" });
        }

        return synchronizer;
    }

    [Fact]
    public async Task Push_UpsertsAiPrompts()
    {
        var synchronizer = SynchronizerWithPrompt();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.UpsertAIPromptsCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Push_LetsThePromptUpsertOverwriteTheCloudOnlyWhenAskedTo(bool overwrite)
    {
        var synchronizer = SynchronizerWithPrompt();

        await Create(synchronizer).PushAsync(Workspace(), overwrite, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(overwrite, synchronizer.LastPromptUpsertOverwroteRemoteChanges);
    }

    [Fact]
    public async Task Push_PassesTheServiceConfirmedPromptsToThePush()
    {
        var synchronizer = SynchronizerWithPrompt();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Single(synchronizer.LastPushedPrompts);
        Assert.Equal("Summarize", synchronizer.LastPushedPrompts[0].Name);
    }

    [Fact]
    public async Task Push_UpsertsPromptsBeforePushingSoTheCacheReflectsTheService()
    {
        var synchronizer = SynchronizerWithPrompt();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(
            [nameof(IWorkspaceSynchronizer.UpsertAIPromptsForAgentAsync), nameof(IWorkspaceSynchronizer.PushLocalChangesAsync)],
            synchronizer.Calls.Where(call => call is nameof(IWorkspaceSynchronizer.UpsertAIPromptsForAgentAsync) or nameof(IWorkspaceSynchronizer.PushLocalChangesAsync)));
    }

    [Fact]
    public async Task Push_WithNoPrompts_PassesAnEmptySet()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Empty(synchronizer.LastPushedPrompts);
    }

    [Fact]
    public async Task Push_WhenAPromptIsRejected_StillCompletesAndWarns()
    {
        var synchronizer = SynchronizerWithPrompt(errorMessage: "invalid model");

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Contains(outcome.Warnings, warning => warning.Contains("Summarize", StringComparison.Ordinal));
        Assert.Contains(outcome.Warnings, warning => warning.Contains("invalid model", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Push_WhenAPromptIsRejected_StillPushesTheComponents()
    {
        var synchronizer = SynchronizerWithPrompt(errorMessage: "invalid model");

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
        Assert.Equal(1, synchronizer.UploadKnowledgeFilesCalls);
    }

    [Fact]
    public async Task Push_WhenAPromptIsRejected_KeepsItOutOfTheCloudCache()
    {
        var synchronizer = SynchronizerWithPrompt(errorMessage: "invalid model");

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Empty(synchronizer.LastPushedPrompts);
    }

    [Fact]
    public async Task Push_NeverProjectsAPromptTheServiceDidNotConfirm()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.PromptResponses.Add(new SyncDataverseClient.AIPromptResponse { PromptName = "Accepted" });
        synchronizer.PromptMetadata.Add(new SyncDataverseClient.AIPromptMetadata { Name = "Accepted" });

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal(
            synchronizer.PromptMetadata.Select(prompt => prompt.Name),
            synchronizer.LastPushedPrompts.Select(prompt => prompt.Name));
    }

    [Fact]
    public async Task Push_DoesNotSynthesizePromptMetadataLocally()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.PromptResponses.Add(new SyncDataverseClient.AIPromptResponse { PromptName = "Unconfirmed" });

        await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Empty(synchronizer.LastPushedPrompts);
    }

    [Fact]
    public async Task Push_DoesNotReportAnyFileOfAPromptTheServiceRefused()
    {
        var promptId = Guid.NewGuid();
        var synchronizer = new RecordingWorkspaceSynchronizer();
        synchronizer.LocalChanges.Add(new Change { Name = "Summarize", SchemaName = $"{AiPromptLayout.SchemaNamePrefix}{promptId}", ChangeKind = AiPromptLayout.ChangeKind, ChangeType = ChangeType.Update });
        synchronizer.LocalChanges.Add(new Change { Name = "Summarize", SchemaName = $"{AiPromptLayout.SchemaNamePrefix}{promptId}.metadata", ChangeKind = AiPromptLayout.ChangeKind, ChangeType = ChangeType.Update });
        synchronizer.LocalChanges.Add(new Change { Name = "Greeting", SchemaName = "cre98_agent.topic.Greeting", ChangeKind = "DialogComponent", ChangeType = ChangeType.Update });
        synchronizer.PromptResponses.Add(new SyncDataverseClient.AIPromptResponse { AIModelId = promptId, PromptName = "Summarize", ErrorMessage = "invalid model" });

        var outcome = await Create(synchronizer).PushAsync(Workspace(), overwrite: false, overwriteSharedEnvironmentVariables: false, CancellationToken.None);

        Assert.Equal("cre98_agent.topic.Greeting", Assert.Single(outcome.Changes).SchemaName);
    }
}
