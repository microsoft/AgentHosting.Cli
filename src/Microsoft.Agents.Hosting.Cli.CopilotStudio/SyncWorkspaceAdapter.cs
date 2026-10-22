using System.Collections.Immutable;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.Agents.Platform.Content.Exceptions;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Implements workspace synchronization by delegating to <see cref="IWorkspaceSynchronizer"/>.
/// </summary>
public sealed class SyncWorkspaceAdapter : ICopilotWorkspacePort
{
    private const string MissingParentAgentMarker = "references a parent agent that could not be created";
    private const string RemoteChangedMarker = "was changed in the cloud while you were editing it";
    private const string ImproperResponseMarker = "Improper response, not implemented";
    private const string MissingEntityMarker = "does not exist";
    private const string DuplicateConnectionReferenceMarker = "ExportKeyInvalidCreate";
    private const string ConnectionReferenceEntity = "connectionreference";
    private const string ObjectDoesNotExistCode = "0x80040217";
    private const int UnreadableResponseAttempts = 4;
    private readonly IWorkspaceSynchronizer _synchronizer;
    private readonly IWorkspaceRetargetService _retargetService;
    private readonly ISyncDataverseClient _dataverseClient;
    private readonly ISyncComponentCollectionDataverseClient _componentCollections;
    private readonly IOperationContextProvider _operationContexts;
    private readonly AgentHostDataverseHttpClientAccessor _httpClientAccessor;
    private readonly CopilotChangeMapper _changeMapper;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates the adapter.</summary>
    /// <param name="synchronizer">The sync library's workspace synchronizer.</param>
    /// <param name="retargetService">Re-images the cloud baseline for an overwriting push.</param>
    /// <param name="dataverseClient">The sync library's Dataverse client.</param>
    /// <param name="componentCollections">Resolves component collections by schema name.</param>
    /// <param name="operationContexts">Supplies authoring operation contexts.</param>
    /// <param name="httpClientAccessor">Targets the Dataverse organization for a given operation.</param>
    /// <param name="changeMapper">Translates sync library changes into transport-neutral records.</param>
    /// <param name="delay">Waits between push attempts.</param>
    public SyncWorkspaceAdapter(IWorkspaceSynchronizer synchronizer, IWorkspaceRetargetService retargetService, ISyncDataverseClient dataverseClient, ISyncComponentCollectionDataverseClient componentCollections, IOperationContextProvider operationContexts, AgentHostDataverseHttpClientAccessor httpClientAccessor, CopilotChangeMapper changeMapper, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
        _retargetService = retargetService ?? throw new ArgumentNullException(nameof(retargetService));
        _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
        _componentCollections = componentCollections ?? throw new ArgumentNullException(nameof(componentCollections));
        _operationContexts = operationContexts ?? throw new ArgumentNullException(nameof(operationContexts));
        _httpClientAccessor = httpClientAccessor ?? throw new ArgumentNullException(nameof(httpClientAccessor));
        _changeMapper = changeMapper ?? throw new ArgumentNullException(nameof(changeMapper));
        _delay = delay ?? ((interval, cancellationToken) => Task.Delay(interval, cancellationToken));
    }

    /// <inheritdoc />
    public bool IsWorkspace(string directory) => _synchronizer.IsSyncInfoAvailable(ToDirectoryPath(directory));

    /// <inheritdoc />
    public string ProjectFolderName(string displayName) => WorkspaceFolderNames.ForProject(displayName);

    /// <inheritdoc />
    public async Task<CopilotWorkspaceInfo> ReadWorkspaceInfoAsync(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var syncInfo = await _synchronizer.GetSyncInfoAsync(ToDirectoryPath(directory)).ConfigureAwait(false);

        return new CopilotWorkspaceInfo
        {
            WorkspacePath = directory,
            AgentId = syncInfo.AgentId?.ToString() ?? string.Empty,
            EnvironmentId = syncInfo.EnvironmentId,
            DataverseOrganization = syncInfo.DataverseEndpoint,
            TenantId = syncInfo.AccountInfo?.TenantId is { } tenantId && tenantId != Guid.Empty ? tenantId.ToString() : null,
        };
    }

    /// <inheritdoc />
    public async Task<CopilotCloneOutcome> CloneAsync(CopilotCloneCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, command.Environment);

        var agentId = Guid.Parse(command.AgentId);
        var agentInfo = await _dataverseClient.GetAgentInfoAsync(agentId, cancellationToken).ConfigureAwait(false);
        var syncInfo = await AgentSyncInfoFactory.CreateAsync(_dataverseClient, command.Environment, agentId, cancellationToken).ConfigureAwait(false);

        var collectionIds = command.IncludeLinkedComponentCollections
            ? [.. agentInfo.ComponentCollections.Select(collection => collection.Id)]
            : await ResolveComponentCollectionIdsAsync(command.ComponentCollectionIds, cancellationToken).ConfigureAwait(false);

        var assets = new AssetsToClone { CloneAgent = true, ComponentCollectionIds = collectionIds };
        var operationContexts = await _operationContexts.GetAllAsync(syncInfo, assets).ConfigureAwait(false);
        var agentDisplayName = agentInfo.DisplayName ?? string.Empty;

        var referenceTracker = new ReferenceTracker();
        var created = new List<CopilotClonedWorkspace>();
        var folders = new List<DirectoryPath>();
        var staged = new List<(string Path, bool Existed)>();
        string? agentWorkspacePath = null;

        try
        {
            foreach (var operationContext in operationContexts)
            {
                var isAgent = operationContext is AuthoringOperationContext;
                var displayName = isAgent ? agentDisplayName : DisplayNameFor(agentInfo, operationContext);
                var folderName = isAgent && !string.IsNullOrWhiteSpace(command.AgentFolderName) ? command.AgentFolderName : ProjectFolderName(displayName);
                var path = Path.Combine(command.WorkspacePath, folderName);

                if (Directory.Exists(path) && HoldsWorkspaceContent(path))
                {
                    throw CopilotStudioException.DestinationNotEmpty(path);
                }

                staged.Add((path, Directory.Exists(path)));
                Directory.CreateDirectory(path);
                var folder = ToDirectoryPath(path);
                folders.Add(folder);

                if (isAgent)
                {
                    agentWorkspacePath = path;
                }

                created.Add(new CopilotClonedWorkspace
                {
                    Kind = isAgent ? "agent" : "component-collection",
                    Path = path,
                    DisplayName = displayName,
                });

                var folderSyncInfo = ScopeSyncInfo(syncInfo, operationContext);

                await _synchronizer.SaveSyncInfoAsync(folder, folderSyncInfo).ConfigureAwait(false);
                await _synchronizer.CloneChangesAsync(folder, referenceTracker, operationContext, _dataverseClient, folderSyncInfo, cancellationToken).ConfigureAwait(false);
            }

            foreach (var folder in folders)
            {
                await _synchronizer.ApplyTouchupsAsync(folder, referenceTracker, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            DiscardStagedClone(staged);
            throw;
        }

        agentWorkspacePath ??= command.WorkspacePath;

        return new CopilotCloneOutcome
        {
            SchemaName = agentInfo.SchemaName ?? string.Empty,
            DisplayName = agentDisplayName,
            ComponentCount = CountWorkspaceComponents(agentWorkspacePath),
            AgentWorkspacePath = agentWorkspacePath,
            Workspaces = created,
        };
    }

    /// <summary>
    /// Whether a directory already holds content a clone would overwrite.
    /// </summary>
    private static bool HoldsWorkspaceContent(string path) =>
        Directory.EnumerateFileSystemEntries(path).Any(entry => !IsCliOwned(entry));

    private static bool IsCliOwned(string entry) =>
        string.Equals(Path.GetFileName(entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), AgentConfigLocator.ConfigDirectoryName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes what a failed clone wrote.
    /// </summary>
    private static void DiscardStagedClone(IReadOnlyList<(string Path, bool Existed)> staged)
    {
        foreach (var (path, existed) in staged)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    continue;
                }

                if (existed)
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(path).Where(candidate => !IsCliOwned(candidate)))
                    {
                        if (Directory.Exists(entry))
                        {
                            Directory.Delete(entry, recursive: true);
                        }
                        else
                        {
                            File.Delete(entry);
                        }
                    }
                }
                else
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static AgentSyncInfo ScopeSyncInfo(AgentSyncInfo syncInfo, AuthoringOperationContextBase operationContext) =>
        operationContext is not BotComponentCollectionAuthoringOperationContext collection
            ? syncInfo
            : new AgentSyncInfo
            {
                AgentId = null,
                ComponentCollectionId = collection.BotComponentCollectionReference.CdsId,
                DataverseEndpoint = syncInfo.DataverseEndpoint,
                EnvironmentId = syncInfo.EnvironmentId,
                EnvironmentDisplayName = syncInfo.EnvironmentDisplayName,
                SolutionVersions = syncInfo.SolutionVersions,
                AccountInfo = syncInfo.AccountInfo,
                AgentManagementEndpoint = syncInfo.AgentManagementEndpoint,
            };

    private async Task<ImmutableArray<Guid>> ResolveComponentCollectionIdsAsync(IReadOnlyList<string> values, CancellationToken cancellationToken)
    {
        if (values.Count == 0)
        {
            return ImmutableArray<Guid>.Empty;
        }

        var resolved = ImmutableArray.CreateBuilder<Guid>(values.Count);

        foreach (var value in values)
        {
            if (Guid.TryParse(value, out var parsed))
            {
                resolved.Add(parsed);
                continue;
            }

            var bySchemaName = await _componentCollections.GetComponentCollectionIdBySchemaNameAsync(value, cancellationToken).ConfigureAwait(false);

            resolved.Add(bySchemaName == Guid.Empty ? throw CopilotStudioException.ComponentCollectionNotFound(value) : bySchemaName);
        }

        return resolved.ToImmutable();
    }

    private static string DisplayNameFor(AgentInfo agentInfo, AuthoringOperationContextBase operationContext) =>
        operationContext is BotComponentCollectionAuthoringOperationContext collection
            ? agentInfo.ComponentCollections.FirstOrDefault(candidate => candidate.Id == collection.BotComponentCollectionReference.CdsId)?.DisplayName
                ?? collection.BotComponentCollectionReference.CdsId.ToString()
            : string.Empty;

    /// <inheritdoc />
    public async Task<CopilotChangeSnapshot> GetChangesAsync(CopilotWorkspaceInfo workspace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, workspace);

        var (workspaceFolder, syncInfo, operationContext) = await ReadWorkspaceAsync(workspace).ConfigureAwait(false);
        var localDefinition = await WorkspaceDefinitionReader.ReadAsync(_synchronizer, workspaceFolder, checkKnowledgeFiles: true, cancellationToken).ConfigureAwait(false);
        var (_, localChanges) = await _synchronizer.GetLocalChangesAsync(workspaceFolder, localDefinition, _dataverseClient, syncInfo, cancellationToken).ConfigureAwait(false);
        var (_, remoteChanges) = await _synchronizer.GetRemoteChangesAsync(workspaceFolder, operationContext, _dataverseClient, syncInfo, cancellationToken).ConfigureAwait(false);

        return new CopilotChangeSnapshot
        {
            Local = _changeMapper.Map(localChanges),
            Remote = _changeMapper.Map(remoteChanges),
        };
    }

    /// <inheritdoc />
    public async Task<CopilotPullOutcome> PullAsync(CopilotWorkspaceInfo workspace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, workspace);

        var (workspaceFolder, syncInfo, operationContext) = await ReadWorkspaceAsync(workspace).ConfigureAwait(false);
        var (_, remoteChanges) = await _synchronizer.GetRemoteChangesAsync(workspaceFolder, operationContext, _dataverseClient, syncInfo, cancellationToken).ConfigureAwait(false);
        var localDefinition = await WorkspaceDefinitionReader.ReadAsync(_synchronizer, workspaceFolder, checkKnowledgeFiles: true, cancellationToken).ConfigureAwait(false);
        var conflicts = new List<WorkspaceDiagnostic>();

        await _synchronizer.PullExistingChangesAsync(workspaceFolder, operationContext, localDefinition, _dataverseClient, syncInfo, cancellationToken, conflicts: conflicts).ConfigureAwait(false);
        await _synchronizer.DownloadKnowledgeFilesAsync(workspaceFolder, _dataverseClient, schemaNames: null, cancellationToken).ConfigureAwait(false);

        return new CopilotPullOutcome
        {
            Changes = _changeMapper.Map(remoteChanges),
            Warnings = DescribeConflicts(conflicts),
        };
    }

    /// <inheritdoc />
    public async Task<CopilotPushOutcome> PushAsync(CopilotWorkspaceInfo workspace, bool overwrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, workspace);

        var (workspaceFolder, syncInfo, operationContext) = await ReadWorkspaceAsync(workspace).ConfigureAwait(false);
        var localDefinition = await WorkspaceDefinitionReader.ReadAsync(_synchronizer, workspaceFolder, checkKnowledgeFiles: true, cancellationToken).ConfigureAwait(false);
        await WorkspaceDefinitionReader.RequireSynchronizableAsync(_synchronizer, workspaceFolder, localDefinition, cancellationToken).ConfigureAwait(false);
        WorkflowToolIds.RequireNotEmpty(localDefinition);
        var (_, localChanges) = await _synchronizer.GetLocalChangesAsync(workspaceFolder, localDefinition, _dataverseClient, syncInfo, cancellationToken).ConfigureAwait(false);

        CloudFlowMetadata? cloudFlowMetadata = null;
        var aiPrompts = ImmutableArray<SyncDataverseClient.AIPromptMetadata>.Empty;
        var warnings = new List<string>();
        var pushedConnectors = await _synchronizer.PushCustomConnectorsAsync(workspaceFolder, _dataverseClient, cancellationToken).ConfigureAwait(false);
        var auxiliaryChangeCount = pushedConnectors.PushedRowIds.Count;

        var provisionedReferences = await DeclaredConnectionReferences.ProvisionAsync(
            workspace.WorkspacePath, _synchronizer, _dataverseClient,
            pushedConnectors.PushedRowIds, cancellationToken).ConfigureAwait(false);

        if (syncInfo.AgentId.HasValue)
        {
            var (workflowResponses, workflowMetadata) = await _synchronizer.UpsertWorkflowForAgentAsync(workspaceFolder, _dataverseClient, syncInfo.AgentId, cancellationToken, WorkflowActivationMode.DraftWhenConnectionsUnbound).ConfigureAwait(false);
            warnings.AddRange(SyncResponseGuards.DescribeRefusedWorkflows(workflowResponses));
            cloudFlowMetadata = workflowMetadata;
            auxiliaryChangeCount += workflowResponses.Length;

            var (promptResponses, promptMetadata) = await _synchronizer.UpsertAIPromptsForAgentAsync(workspaceFolder, _dataverseClient, syncInfo.AgentId, cancellationToken).ConfigureAwait(false);
            warnings.AddRange(SyncResponseGuards.DescribeRefusedPrompts(promptResponses));
            aiPrompts = promptMetadata;
            auxiliaryChangeCount += promptResponses.Length;
        }

        await PushComponentsAsync(workspaceFolder, operationContext, localDefinition, syncInfo, DeclaredConnectionReferences.WithProvisioned(cloudFlowMetadata, provisionedReferences), aiPrompts, workspace.EnvironmentId, overwrite, warnings, cancellationToken).ConfigureAwait(false);

        ImmutableArray<string> uploadedFiles;

        try
        {
            uploadedFiles = await _synchronizer.UploadKnowledgeFilesAsync(workspaceFolder, _dataverseClient, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw CopilotStudioException.KnowledgeUploadFailed(localChanges.Length, failure);
        }

        warnings.AddRange(await DescribeUnacceptedAsync(workspaceFolder, operationContext, syncInfo, cancellationToken).ConfigureAwait(false));

        return new CopilotPushOutcome
        {
            Changes = _changeMapper.Map(localChanges),
            AuxiliaryChangeCount = auxiliaryChangeCount,
            UploadedFileNames = uploadedFiles,
            Warnings = warnings,
        };
    }

    private static IReadOnlyList<string> DescribeConflicts(List<WorkspaceDiagnostic> conflicts) =>
        [.. conflicts.Select(conflict => conflict.ToString())];

    /// <summary>
    /// Asks the service what it actually holds, and describes anything the push did not land.
    /// </summary>
    private async Task<IReadOnlyList<string>> DescribeUnacceptedAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, AgentSyncInfo syncInfo, CancellationToken cancellationToken)
    {
        PushVerificationResult verification;

        try
        {
            verification = await _synchronizer.VerifyPushAsync(workspaceFolder, operationContext, _dataverseClient, syncInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return [$"The push could not be confirmed against the service: {failure.Message}"];
        }

        return verification.IsFullyAccepted
            ? []
            : [.. verification.EntityTypes
                .Where(entity => entity.VerifiedCount < entity.PushedCount)
                .Select(entity => $"The service did not report {entity.PushedCount - entity.VerifiedCount} of {entity.PushedCount} pushed {entity.ChangeKind} change(s) as stored.")];
    }

    /// <summary>
    /// Sends the component changes, re-imaging the baseline and sending once more when the service
    /// reports the agent changed underneath.
    /// </summary>
    private async Task PushComponentsAsync(
        DirectoryPath workspaceFolder,
        AuthoringOperationContextBase operationContext,
        DefinitionBase localDefinition,
        AgentSyncInfo syncInfo,
        CloudFlowMetadata? cloudFlowMetadata,
        ImmutableArray<SyncDataverseClient.AIPromptMetadata> aiPrompts,
        string environmentId,
        bool overwrite,
        List<string> priorWarnings,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _synchronizer.PushLocalChangesAsync(workspaceFolder, operationContext, localDefinition, _dataverseClient, syncInfo, cloudFlowMetadata, aiPrompts, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException failure) when (failure.Message.Contains(MissingParentAgentMarker, StringComparison.OrdinalIgnoreCase))
            {
                throw CopilotStudioException.ConnectedAgentNotInTarget(environmentId, failure);
            }
            catch (InvalidOperationException failure) when (failure.Message.Contains(RemoteChangedMarker, StringComparison.OrdinalIgnoreCase))
            {
                if (!overwrite || attempt > 1)
                {
                    throw CopilotStudioException.RemoteChangedDuringPush(failure);
                }
            }
            catch (ArgumentException failure) when (failure.Message.Contains(ImproperResponseMarker, StringComparison.OrdinalIgnoreCase))
            {
                if (attempt >= UnreadableResponseAttempts)
                {
                    throw CopilotStudioException.PushResponseUnreadable(environmentId, attempt, failure);
                }

                await _delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException && TypedConnectionReferenceFailure(failure, environmentId, priorWarnings) is { } typedFailure)
            {
                if (attempt > 1)
                {
                    throw typedFailure;
                }
            }
            catch (Exception failure) when (
                priorWarnings.Count > 0
                && failure is not OperationCanceledException
                && ChainContains(failure, MissingEntityMarker))
            {
                throw CopilotStudioException.PushFailedAfterRefusedComponents(environmentId, priorWarnings, failure);
            }

            await RefreshBaselineAsync(workspaceFolder, operationContext, syncInfo, cloudFlowMetadata, aiPrompts, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool ChainContains(Exception failure, string marker) => AnyInExceptionChain(failure, current => current.Message.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsMissingConnectionReference(Exception failure) => AnyInExceptionChain(failure, NamesAMissingConnectionReference);

    private static bool NamesAMissingConnectionReference(Exception failure) => (failure is DataverseNotFoundException || IsObjectDoesNotExist(failure)) && failure.Message.Contains(ConnectionReferenceEntity, StringComparison.OrdinalIgnoreCase);

    private static bool IsObjectDoesNotExist(Exception failure) => failure is DataverseException dataverse && string.Equals(dataverse.ErrorCodeValue, ObjectDoesNotExistCode, StringComparison.OrdinalIgnoreCase);

    private static CopilotStudioException? TypedConnectionReferenceFailure(Exception failure, string environmentId, IReadOnlyList<string> refusals) => failure switch
    {
        _ when IsMissingConnectionReference(failure) => CopilotStudioException.MissingConnectionReference(environmentId, refusals, failure),
        _ when ChainContains(failure, DuplicateConnectionReferenceMarker) => CopilotStudioException.DuplicateConnectionReference(environmentId, refusals, failure),
        _ => null,
    };

    private static bool AnyInExceptionChain(Exception failure, Func<Exception, bool> predicate)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (predicate(current))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Re-reads the cloud baseline from the service, writing only the cache.
    /// </summary>
    /// <remarks>
    /// The components come from the service, and the flows and references are the ones this run
    /// already confirmed the target holds, so nothing the workspace authored locally is recorded as
    /// though the service had accepted it. Supplying them also keeps the sync from re-downloading
    /// workflows and prompts, which rewrites and deletes the workspace's own component folders even
    /// when the directory is not being updated.
    /// </remarks>
    private Task<WorkspaceSyncInfo> RefreshBaselineAsync(
        DirectoryPath workspaceFolder,
        AuthoringOperationContextBase operationContext,
        AgentSyncInfo syncInfo,
        CloudFlowMetadata? cloudFlowMetadata,
        ImmutableArray<SyncDataverseClient.AIPromptMetadata> aiPrompts,
        CancellationToken cancellationToken) =>
        WorkspaceCacheRefresh.RunAsync(_retargetService, workspaceFolder, operationContext, _dataverseClient, syncInfo, cloudFlowMetadata, aiPrompts, cancellationToken);

    private async Task<(DirectoryPath Folder, AgentSyncInfo SyncInfo, AuthoringOperationContextBase OperationContext)> ReadWorkspaceAsync(CopilotWorkspaceInfo workspace)
    {
        var workspaceFolder = ToDirectoryPath(workspace.WorkspacePath);
        var syncInfo = await _synchronizer.GetSyncInfoAsync(workspaceFolder).ConfigureAwait(false);

        return (workspaceFolder, syncInfo, await _operationContexts.GetAsync(syncInfo).ConfigureAwait(false));
    }

    private static int CountWorkspaceComponents(string workspacePath) =>
        Directory.Exists(workspacePath)
            ? Directory.GetFiles(workspacePath, "*.mcs.yml", SearchOption.AllDirectories).Length
            : 0;

    private static DirectoryPath ToDirectoryPath(string directory) => new(directory.Replace('\\', '/'));
}
