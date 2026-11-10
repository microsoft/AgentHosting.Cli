using System.Collections.Immutable;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Implements workspace synchronization by delegating to <see cref="IWorkspaceSynchronizer"/>.
/// </summary>
public sealed class SyncWorkspaceAdapter : ICopilotWorkspacePort
{
    private readonly IWorkspaceSynchronizer _synchronizer;
    private readonly IWorkspacePushService _pushService;
    private readonly ISyncDataverseClient _dataverseClient;
    private readonly ISyncComponentCollectionDataverseClient _componentCollections;
    private readonly IOperationContextProvider _operationContexts;
    private readonly AgentHostDataverseHttpClientAccessor _httpClientAccessor;
    private readonly CopilotChangeMapper _changeMapper;

    /// <summary>Creates the adapter.</summary>
    /// <param name="synchronizer">The sync library's workspace synchronizer.</param>
    /// <param name="pushService">The sync library's service that pushes a connected workspace.</param>
    /// <param name="dataverseClient">The sync library's Dataverse client.</param>
    /// <param name="componentCollections">Resolves component collections by schema name.</param>
    /// <param name="operationContexts">Supplies authoring operation contexts.</param>
    /// <param name="httpClientAccessor">Targets the Dataverse organization for a given operation.</param>
    /// <param name="changeMapper">Translates sync library changes into transport-neutral records.</param>
    public SyncWorkspaceAdapter(IWorkspaceSynchronizer synchronizer, IWorkspacePushService pushService, ISyncDataverseClient dataverseClient, ISyncComponentCollectionDataverseClient componentCollections, IOperationContextProvider operationContexts, AgentHostDataverseHttpClientAccessor httpClientAccessor, CopilotChangeMapper changeMapper)
    {
        _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
        _pushService = pushService ?? throw new ArgumentNullException(nameof(pushService));
        _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
        _componentCollections = componentCollections ?? throw new ArgumentNullException(nameof(componentCollections));
        _operationContexts = operationContexts ?? throw new ArgumentNullException(nameof(operationContexts));
        _httpClientAccessor = httpClientAccessor ?? throw new ArgumentNullException(nameof(httpClientAccessor));
        _changeMapper = changeMapper ?? throw new ArgumentNullException(nameof(changeMapper));
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
        var syncInfo = await WorkspaceCloneSequence.ForAgentAsync(_dataverseClient, agentId, command.Environment.DataverseOrganization, command.Environment.EnvironmentId, DataverseTargeting.TenantOf(command.Environment), cancellationToken).ConfigureAwait(false);

        var collectionIds = command.IncludeLinkedComponentCollections
            ? [.. agentInfo.ComponentCollections.Select(collection => collection.Id)]
            : await ResolveComponentCollectionIdsAsync(command.ComponentCollectionIds, cancellationToken).ConfigureAwait(false);

        var assets = new AssetsToClone { CloneAgent = true, ComponentCollectionIds = collectionIds };
        var operationContexts = await _operationContexts.GetAllAsync(syncInfo, assets).ConfigureAwait(false);
        var agentDisplayName = command.DisplayName ?? agentInfo.DisplayName ?? string.Empty;

        var created = new List<CopilotClonedWorkspace>();
        var cloneTargets = new List<KeyValuePair<AuthoringOperationContextBase, DirectoryPath>>();
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
                cloneTargets.Add(new(operationContext, ToDirectoryPath(path)));

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
            }

            await WorkspaceCloneSequence.CloneAsync(_synchronizer, cloneTargets, syncInfo, _dataverseClient, cancellationToken).ConfigureAwait(false);
            agentWorkspacePath ??= command.WorkspacePath;
            AgentWorkspaceIdentity.DeclareDisplayName(WorkflowWorkspace.OpenWorkspace(agentWorkspacePath), command.DisplayName);
        }
        catch
        {
            CloneStaging.Discard(staged);
            throw;
        }

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
        Directory.EnumerateFileSystemEntries(path).Any(entry => !CloneStaging.IsCliOwned(entry));

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

        var pushed = await PushWorkspaceAsync(workspace, overwrite, cancellationToken).ConfigureAwait(false);

        return new CopilotPushOutcome
        {
            Changes = _changeMapper.Map(pushed.Changes),
            AuxiliaryChangeCount = pushed.AuxiliaryChangeCount,
            UploadedFileNames = pushed.UploadedFileNames,
            Warnings = [.. pushed.Diagnostics.Select(SyncResponseGuards.Describe)],
            PublishedOn = pushed.PublishedOn,
        };
    }

    private static IReadOnlyList<string> DescribeConflicts(List<WorkspaceDiagnostic> conflicts) =>
        [.. conflicts.Select(conflict => conflict.ToString())];

    private async Task<WorkspacePushResult> PushWorkspaceAsync(CopilotWorkspaceInfo workspace, bool overwrite, CancellationToken cancellationToken)
    {
        try
        {
            return await _pushService.PushAsync(new WorkspacePushRequest { WorkspaceFolder = ToDirectoryPath(workspace.WorkspacePath), Overwrite = overwrite }, _dataverseClient, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceValidationException failure)
        {
            throw WorkspaceDefinitionReader.Translate(failure);
        }
        catch (DeclaredConnectionReferenceException failure)
        {
            throw ConnectionReferenceFailureTranslation.Translate(failure, workspace.WorkspacePath);
        }
        catch (WorkspacePushException failure) when (Translate(failure, workspace.EnvironmentId) is { } translated)
        {
            throw translated;
        }
    }

    private static CopilotStudioException? Translate(WorkspacePushException failure, string environmentId) => failure.Code switch
    {
        WorkspacePushErrorCodes.ConnectedAgentMissing => CopilotStudioException.ConnectedAgentNotInTarget(environmentId, failure),
        WorkspacePushErrorCodes.RemoteChanged => CopilotStudioException.RemoteChangedDuringPush(failure),
        WorkspacePushErrorCodes.ResponseUnreadable => CopilotStudioException.PushResponseUnreadable(environmentId, failure.Attempts, Cause(failure)),
        WorkspacePushErrorCodes.ConnectionReferenceMissing => CopilotStudioException.MissingConnectionReference(environmentId, Refusals(failure), Cause(failure)),
        WorkspacePushErrorCodes.ConnectionReferenceDuplicate => CopilotStudioException.DuplicateConnectionReference(environmentId, Refusals(failure), Cause(failure)),
        WorkspacePushErrorCodes.MissingAfterRefusals => CopilotStudioException.PushFailedAfterRefusedComponents(environmentId, Refusals(failure), Cause(failure)),
        WorkspacePushErrorCodes.WorkflowToolIdEmpty => CopilotStudioException.WorkflowToolIdEmpty(failure.WorkflowTools),
        WorkspacePushErrorCodes.KnowledgeUploadFailed => CopilotStudioException.KnowledgeUploadFailed(failure.PushedChangeCount, Cause(failure)),
        WorkspacePushErrorCodes.AgentNotProvisioned => CopilotStudioException.AgentNotProvisioned(failure.Message, failure),
        _ => null,
    };

    private static Exception Cause(WorkspacePushException failure) => failure.InnerException ?? failure;

    private static IReadOnlyList<string> Refusals(WorkspacePushException failure) => [.. failure.Refusals.Select(SyncResponseGuards.Describe)];
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
