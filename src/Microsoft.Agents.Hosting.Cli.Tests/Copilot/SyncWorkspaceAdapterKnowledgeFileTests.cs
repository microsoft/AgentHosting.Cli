using System.Collections.Immutable;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncWorkspaceAdapterKnowledgeFileTests
{
    private static readonly Guid AgentId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static CopilotWorkspaceInfo Workspace() => new()
    {
        WorkspacePath = Path.Combine(Path.GetTempPath(), "ah-knowledge-tests"),
        AgentId = AgentId.ToString(),
        EnvironmentId = "env-1",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static SyncWorkspaceAdapter Create(RecordingWorkspaceSynchronizer synchronizer) => CopilotTestFixtures.WorkspaceAdapter(synchronizer);

    [Fact]
    public async Task Push_WhenAComponentReferencesAnAgentAbsentFromTheTarget_IsTranslated()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            PushLocalChangesThrows = CopilotTestFixtures.ConnectedAgentMissing(),
        };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.ConnectedAgentNotInTarget, failure.ErrorKind);
        Assert.Equal(0, synchronizer.UploadKnowledgeFilesCalls);
    }

    [Fact]
    public async Task Push_WhenTheKnowledgeUploadFails_SaysComponentsWereAlreadyPushed()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer
        {
            UploadKnowledgeFilesThrows = new InvalidOperationException("Improper response, not implemented"),
        };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(synchronizer).PushAsync(Workspace(), overwrite: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.KnowledgeUploadFailed, failure.ErrorKind);
        Assert.Equal(1, synchronizer.PushLocalChangesCalls);
    }

    [Fact]
    public async Task Pull_DownloadsKnowledgeFileContent()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PullAsync(Workspace(), CancellationToken.None);

        Assert.Equal(1, synchronizer.DownloadKnowledgeFilesCalls);
    }

    [Fact]
    public async Task Pull_DownloadsEveryKnowledgeFileComponent()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PullAsync(Workspace(), CancellationToken.None);

        Assert.Null(synchronizer.LastDownloadSchemaNames);
    }

    [Fact]
    public async Task Pull_DownloadsContentAfterApplyingRemoteChanges()
    {
        var synchronizer = new RecordingWorkspaceSynchronizer();

        await Create(synchronizer).PullAsync(Workspace(), CancellationToken.None);

        Assert.Equal(
            [nameof(IWorkspaceSynchronizer.PullExistingChangesAsync), nameof(IWorkspaceSynchronizer.DownloadKnowledgeFilesAsync)],
            synchronizer.Calls.Where(call => call is nameof(IWorkspaceSynchronizer.PullExistingChangesAsync) or nameof(IWorkspaceSynchronizer.DownloadKnowledgeFilesAsync)));
    }
}

internal sealed class StubComponentCollectionClient : ISyncComponentCollectionDataverseClient
{
    public Task<ComponentCollectionInfo> CreateComponentCollectionAsync(string displayName, string schemaName, CancellationToken cancellationToken) =>
        Task.FromResult(new ComponentCollectionInfo());

    public Task<Guid> GetComponentCollectionIdBySchemaNameAsync(string schemaName, CancellationToken cancellationToken) =>
        Task.FromResult(Guid.Empty);

    public Task InstallComponentCollectionOnAgentAsync(Guid agentId, Guid componentCollectionId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task UninstallComponentCollectionFromAgentAsync(Guid agentId, Guid componentCollectionId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<Guid>> GetAgentIdsForComponentCollectionAsync(Guid componentCollectionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);
}

internal sealed class StubOperationContextProvider : IOperationContextProvider
{
    public Task<AuthoringOperationContextBase> GetAsync(AgentSyncInfo agentInfo) =>
        Task.FromResult<AuthoringOperationContextBase>(agentInfo.ComponentCollectionId is { } collectionId ? CreateCollectionContext(agentInfo, collectionId) : CreateContext(agentInfo));

    public Task<ImmutableArray<AuthoringOperationContextBase>> GetAllAsync(AgentSyncInfo agentInfo, AssetsToClone assetsToClone) =>
        Task.FromResult<ImmutableArray<AuthoringOperationContextBase>>(
        [
            CreateContext(agentInfo),
            .. assetsToClone.ComponentCollectionIds.Select(id => CreateCollectionContext(agentInfo, id)),
        ]);

    private static AuthoringOperationContext CreateContext(AgentSyncInfo agentInfo) => new(
        new PrincipalObjectReference(agentInfo.AccountInfo!.TenantId, Guid.Empty),
        Organization(agentInfo),
        agentInfo.BotReference,
        null,
        false);

    private static BotComponentCollectionAuthoringOperationContext CreateCollectionContext(AgentSyncInfo agentInfo, Guid collectionId) => new(
        new PrincipalObjectReference(agentInfo.AccountInfo!.TenantId, Guid.Empty),
        Organization(agentInfo),
        new BotComponentCollectionReference("collection", collectionId),
        null);

    private static CdsOrganizationInfo Organization(AgentSyncInfo agentInfo) => new(
        agentInfo.AccountInfo!.TenantId,
        agentInfo.DataverseEndpoint,
        pvaSolutionVersion: new Version(1, 0),
        dvTableSearchGlossaryAndSynonymsSolutionVersion: new Version(1, 0),
        dvTableSearchSolutionVersion: new Version(1, 0));
}

internal sealed class RecordingWorkspaceSynchronizer : IWorkspaceSynchronizer
{
    public List<string> Calls { get; } = [];

    public int PushLocalChangesCalls { get; private set; }

    public int UploadKnowledgeFilesCalls { get; private set; }

    public int DownloadKnowledgeFilesCalls { get; private set; }

    public int GetWorkflowsCalls { get; private set; }

    public int GetAIPromptsCalls { get; private set; }

    public CloudFlowMetadata? FetchedWorkflowMetadata { get; init; }

    public List<SyncDataverseClient.AIPromptMetadata> FetchedPromptMetadata { get; } = [];

    public IFileAccessor? LastWorkflowFileAccessor { get; private set; }

    public IFileAccessor? LastPromptFileAccessor { get; private set; }

    public Exception? PushLocalChangesThrows { get; init; }

    public bool PushLocalChangesThrowsOnlyOnce { get; init; }

    public Exception? ReadWorkspaceDefinitionThrows { get; init; }

    public DefinitionBase? DefinitionReturns { get; init; }

    public Exception? UploadKnowledgeFilesThrows { get; init; }

    public ImmutableArray<string> UploadedFileNames { get; init; } = ImmutableArray<string>.Empty;

    public Exception? CloneChangesThrows { get; init; }

    public IReadOnlyDictionary<string, string> ClonedFiles { get; init; } = new Dictionary<string, string>();

    public Exception? ApplyTouchupsThrows { get; init; }

    public List<SyncDataverseClient.AIPromptResponse> PromptResponses { get; } = [];

    public List<WorkflowResponse> WorkflowResponses { get; } = [];

    public IReadOnlyCollection<string>? LastDownloadSchemaNames { get; private set; } = ["unset"];

    public bool SyncInfoAvailable { get; init; } = true;

    public bool IsSyncInfoAvailable(DirectoryPath workspaceFolder) => SyncInfoAvailable;

    public Task<AgentSyncInfo> GetSyncInfoAsync(DirectoryPath workspaceFolder) => Task.FromResult(new AgentSyncInfo
    {
        AgentId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        DataverseEndpoint = new Uri("https://contoso.crm.dynamics.com/"),
        EnvironmentId = "env-1",
        SolutionVersions = new SolutionInfo(),
        AccountInfo = new AccountInfo { TenantId = Guid.Empty },
    });

    public List<(string Folder, AgentSyncInfo SyncInfo)> SavedSyncInfo { get; } = [];

    public Task SaveSyncInfoAsync(DirectoryPath workspaceFolder, AgentSyncInfo connectionDetails)
    {
        Calls.Add(nameof(SaveSyncInfoAsync));
        SavedSyncInfo.Add((workspaceFolder.ToString(), connectionDetails));
        return Task.CompletedTask;
    }

    public Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetLocalChangesAsync(DirectoryPath workspaceFolder, DefinitionBase workspaceDefinition, CancellationToken cancellationToken) =>
        Task.FromResult((new PvaComponentChangeSet(null, null, null), ImmutableArray<Change>.Empty));

    public Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetStandaloneWorkflowRemoteChangesAsync(DirectoryPath workspaceFolder, Func<Guid, CancellationToken, Task<SyncDataverseClient.WorkflowMetadata?>> readWorkflow, CancellationToken cancellationToken) =>
        Task.FromResult((new PvaComponentChangeSet(null, null, null), ImmutableArray<Change>.Empty));

    public DiscardResult DiscardStandaloneWorkflowChanges(DirectoryPath workspaceFolder, IReadOnlyCollection<Change> changes) => new();

    public Task<(ImmutableArray<Change> Changes, ImmutableArray<Change> UnreadableChanges)> GetStandaloneWorkflowChangesForDiscardAsync(DirectoryPath workspaceFolder, CancellationToken cancellationToken) =>
        Task.FromResult((ImmutableArray<Change>.Empty, ImmutableArray<Change>.Empty));

    public List<Change> LocalChanges { get; } = [];

    public Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetLocalChangesAsync(DirectoryPath workspaceFolder, DefinitionBase workspaceDefinition, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(GetLocalChangesAsync));
        return Task.FromResult((new PvaComponentChangeSet(null, null, null), LocalChanges.ToImmutableArray()));
    }

    public DiscardResult DiscardLocalChanges(DirectoryPath workspaceFolder, IReadOnlyCollection<Change> changes) => new();

    public DiscardResult DiscardLocalChanges(DirectoryPath workspaceFolder, DefinitionBase workspaceDefinition, IReadOnlyCollection<Change> changes) => new();

    public Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetRemoteChangesAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(GetRemoteChangesAsync));
        return Task.FromResult((new PvaComponentChangeSet(null, null, null), ImmutableArray<Change>.Empty));
    }

    public Task CloneChangesAsync(DirectoryPath workspaceFolder, ReferenceTracker referenceTracker, AuthoringOperationContextBase operationContext, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(CloneChangesAsync));

        if (CloneChangesThrows is null)
        {
            Directory.CreateDirectory(Path.Combine(workspaceFolder.ToString(), ".mcs"));

            foreach (var (relativePath, content) in ClonedFiles)
            {
                File.WriteAllText(Path.Combine(workspaceFolder.ToString(), relativePath), content);
            }
        }

        return CloneChangesThrows is not null ? Task.FromException(CloneChangesThrows) : Task.CompletedTask;
    }

    public Task ApplyTouchupsAsync(DirectoryPath workspaceFolder, ReferenceTracker referenceTracker, CancellationToken cancellation)
    {
        Calls.Add(nameof(ApplyTouchupsAsync));
        return ApplyTouchupsThrows is not null ? Task.FromException(ApplyTouchupsThrows) : Task.CompletedTask;
    }

    public ICollection<WorkspaceDiagnostic>? LastPullConflicts { get; private set; }

    public IReadOnlyList<WorkspaceDiagnostic> PullConflicts { get; init; } = [];

    public Task<DefinitionBase> PullExistingChangesAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, DefinitionBase localWorkspaceDefinition, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, CancellationToken cancellationToken, bool downloadAllKnowledgeFiles = false, ICollection<WorkspaceDiagnostic>? conflicts = null)
    {
        Calls.Add(nameof(PullExistingChangesAsync));
        LastPullConflicts = conflicts;

        foreach (var conflict in PullConflicts)
        {
            conflicts?.Add(conflict);
        }

        return Task.FromResult(localWorkspaceDefinition);
    }

    public int ThrowIfWorkspaceInvalidCalls { get; private set; }

    public Exception? ThrowIfWorkspaceInvalidThrows { get; init; }

    public IWorkspaceSynchronizer? WorkspaceValidator { get; init; }

    public Task ThrowIfWorkspaceInvalidAsync(DirectoryPath workspaceFolder, DefinitionBase workspaceDefinition, CancellationToken cancellationToken)
    {
        ThrowIfWorkspaceInvalidCalls++;
        Calls.Add(nameof(ThrowIfWorkspaceInvalidAsync));

        return ThrowIfWorkspaceInvalidThrows is not null ? Task.FromException(ThrowIfWorkspaceInvalidThrows) : WorkspaceValidator?.ThrowIfWorkspaceInvalidAsync(workspaceFolder, workspaceDefinition, cancellationToken) ?? Task.CompletedTask;
    }

    public Task<(DefinitionBase Definition, ImmutableArray<Change> UnreadableChanges)> ReadWorkspaceDefinitionForDiscardAsync(DirectoryPath workspaceFolder, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(ReadWorkspaceDefinitionForDiscardAsync));
        return Task.FromResult<(DefinitionBase, ImmutableArray<Change>)>((new BotDefinition(), ImmutableArray<Change>.Empty));
    }

    public Task<PushChangesetResult> PushChangesetAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, PvaComponentChangeSet localWorkspaceDefinition, ISyncDataverseClient dataverseClient, Guid? agentId, CloudFlowMetadata? cloudFlowMetadata, ImmutableArray<SyncDataverseClient.AIPromptMetadata> aiPrompts, CancellationToken cancellationToken, bool uploadAllKnowledgeFiles = false)
    {
        Calls.Add(nameof(PushChangesetAsync));
        return Task.FromResult(new PushChangesetResult());
    }

    public Queue<Exception> PushLocalChangesFailures { get; } = new();

    public bool? LastOverwriteRemoteChanges { get; private set; }

    public Task PushLocalChangesAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, DefinitionBase workspaceDefinition, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, CloudFlowMetadata? cloudFlowMetadata, ImmutableArray<SyncDataverseClient.AIPromptMetadata> aiPrompts, CancellationToken cancellationToken, AuthoringOperationContextBase? contentSaveContextOverride = null, bool overwriteRemoteChanges = false)
    {
        PushLocalChangesCalls++;
        LastOverwriteRemoteChanges = overwriteRemoteChanges;
        Calls.Add(nameof(PushLocalChangesAsync));

        if (PushLocalChangesFailures.Count > 0)
        {
            return Task.FromException(PushLocalChangesFailures.Dequeue());
        }

        return PushLocalChangesThrows is not null && (!PushLocalChangesThrowsOnlyOnce || PushLocalChangesCalls == 1)
            ? Task.FromException(PushLocalChangesThrows)
            : Task.CompletedTask;
    }

    public Task<ImmutableArray<KnowledgeFileInfo>> ListKnowledgeFilesAsync(DirectoryPath workspaceFolder, CancellationToken cancellationToken) =>
        Task.FromResult(ImmutableArray<KnowledgeFileInfo>.Empty);

    public Task<ImmutableArray<KnowledgeFileInfo>> DownloadKnowledgeFilesAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, IReadOnlyCollection<string>? schemaNames, CancellationToken cancellationToken)
    {
        DownloadKnowledgeFilesCalls++;
        LastDownloadSchemaNames = schemaNames;
        Calls.Add(nameof(DownloadKnowledgeFilesAsync));
        return Task.FromResult(ImmutableArray<KnowledgeFileInfo>.Empty);
    }

    public Task<ImmutableArray<string>> UploadKnowledgeFilesAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        UploadKnowledgeFilesCalls++;
        Calls.Add(nameof(UploadKnowledgeFilesAsync));

        return UploadKnowledgeFilesThrows is not null ? Task.FromException<ImmutableArray<string>>(UploadKnowledgeFilesThrows) : Task.FromResult(UploadedFileNames);
    }

    public Task<WorkspaceSyncInfo> SyncWorkspaceAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, string? changeToken, bool updateWorkspaceDirectory, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, CloudFlowMetadata? cloudFlowMetadata, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<(ImmutableArray<WorkflowResponse>, CloudFlowMetadata)> UpsertWorkflowForAgentAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken, WorkflowActivationMode activationMode = WorkflowActivationMode.PreserveSavedState)
    {
        Calls.Add(nameof(UpsertWorkflowForAgentAsync));
        return Task.FromResult((WorkflowResponses.ToImmutableArray(), new CloudFlowMetadata()));
    }

    public Task<ImmutableArray<SyncDataverseClient.AIPromptMetadata>> GetAIPromptsAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, IFileAccessor fileAccessor, CancellationToken cancellationToken)
    {
        GetAIPromptsCalls++;
        LastPromptFileAccessor = fileAccessor;
        return Task.FromResult(FetchedPromptMetadata.ToImmutableArray());
    }

    public Task<(ImmutableArray<SyncDataverseClient.AIPromptResponse>, ImmutableArray<SyncDataverseClient.AIPromptMetadata>)> UpsertAIPromptsForAgentAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken, bool overwriteRemoteChanges = false)
    {
        Calls.Add(nameof(UpsertAIPromptsForAgentAsync));
        return Task.FromResult((PromptResponses.ToImmutableArray(), ImmutableArray<SyncDataverseClient.AIPromptMetadata>.Empty));
    }

    public Task<CloudFlowMetadata> GetWorkflowsAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, IFileAccessor fileAccessor, CancellationToken cancellationToken)
    {
        GetWorkflowsCalls++;
        LastWorkflowFileAccessor = fileAccessor;
        return Task.FromResult(FetchedWorkflowMetadata ?? new CloudFlowMetadata());
    }

    public async Task ProvisionConnectionReferencesAsync(DefinitionBase definition, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken, IReadOnlyDictionary<string, Guid>? pushedConnectorIds = null)
    {
        Calls.Add(nameof(ProvisionConnectionReferencesAsync));
        foreach (var reference in definition.ConnectionReferences)
        {
            await dataverseClient.EnsureConnectionReferenceExistsAsync(
                reference.ConnectionReferenceLogicalName.Value, reference.ConnectorId.Value, cancellationToken);
        }
    }

    public Task ProvisionConnectionReferencesAsync(DirectoryPath workspaceFolder, DefinitionBase definition, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken, IReadOnlyDictionary<string, Guid>? pushedConnectorIds = null)
    {
        Calls.Add(nameof(ProvisionConnectionReferencesAsync));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConnectionNeeded>> GetAgentConnectionReferencesAsync(DirectoryPath workspaceFolder, DefinitionBase definition, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ConnectionNeeded>>([]);

    public Task<IReadOnlyList<ConnectionNeeded>> GetNewAgentConnectionReferencesAsync(DirectoryPath workspaceFolder, DefinitionBase definition, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ConnectionNeeded>>([]);

    public void ClearComponentSyncBaselines(DirectoryPath workspaceFolder)
    {
    }

    public Dictionary<string, Guid> ConnectorRowIds { get; } = [];

    public List<Change> FailedConnectors { get; } = [];

    public List<string> ConnectorFailureReasons { get; } = [];

    public Task<CustomConnectorPushResult> PushCustomConnectorsAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken, bool overwriteRemoteChanges = false)
    {
        Calls.Add(nameof(PushCustomConnectorsAsync));

        return Task.FromResult(new CustomConnectorPushResult { PushedRowIds = ConnectorRowIds, Failed = FailedConnectors, FailureReasons = ConnectorFailureReasons });
    }

    public Task<DefinitionBase> ReadWorkspaceDefinitionAsync(DirectoryPath workspaceFolder, CancellationToken cancellationToken, bool checkKnowledgeFiles = false)
    {
        Calls.Add(nameof(ReadWorkspaceDefinitionAsync));

        return ReadWorkspaceDefinitionThrows is not null ? Task.FromException<DefinitionBase>(ReadWorkspaceDefinitionThrows) : Task.FromResult(DefinitionReturns ?? new BotDefinition());
    }

    public Task<ImmutableArray<DirectoryPath>> CloneAllAssetsAsync(DirectoryPath rootFolder, AgentSyncInfo syncInfo, AssetsToClone assetsToClone, AgentInfo agentInfo, IOperationContextProvider operationContextProvider, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
