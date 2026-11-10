using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Binds a workspace to a cloud agent, creating one only when the schema name is free.
/// </summary>
public sealed class WorkspaceReattachAdapter : ICopilotWorkspaceAttachPort
{
    private readonly IWorkspaceSynchronizer _synchronizer;
    private readonly IWorkspaceRetargetService _retargetService;
    private readonly ISyncDataverseClient _dataverseClient;
    private readonly IOperationContextProvider _operationContexts;
    private readonly AgentHostDataverseHttpClientAccessor _httpClientAccessor;

    /// <summary>Creates the adapter.</summary>
    /// <param name="synchronizer">Reads the workspace and writes its sync info.</param>
    /// <param name="retargetService">Writes the cloud baseline and owns the retarget snapshot.</param>
    /// <param name="dataverseClient">Resolves and creates agents.</param>
    /// <param name="operationContexts">Builds the authoring operation context.</param>
    /// <param name="httpClientAccessor">Targets the Dataverse organization.</param>
    public WorkspaceReattachAdapter(IWorkspaceSynchronizer synchronizer, IWorkspaceRetargetService retargetService, ISyncDataverseClient dataverseClient, IOperationContextProvider operationContexts, AgentHostDataverseHttpClientAccessor httpClientAccessor)
    {
        _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
        _retargetService = retargetService ?? throw new ArgumentNullException(nameof(retargetService));
        _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
        _operationContexts = operationContexts ?? throw new ArgumentNullException(nameof(operationContexts));
        _httpClientAccessor = httpClientAccessor ?? throw new ArgumentNullException(nameof(httpClientAccessor));
    }

    /// <inheritdoc />
    public async Task<CopilotAttachOutcome> AttachAsync(CopilotAttachCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        using var lease = await DataverseTargeting.EnterAsync(_dataverseClient, cancellationToken).ConfigureAwait(false);
        using var scope = DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, command.Environment);

        var workspaceFolder = new DirectoryPath(command.ProjectDirectory.Replace('\\', '/'));
        var isRetarget = _synchronizer.IsSyncInfoAvailable(workspaceFolder);

        if (isRetarget && !command.AllowRetarget)
        {
            throw CopilotStudioException.WorkspaceAlreadyConnected(command.ProjectDirectory);
        }

        var definition = await TryReadDefinitionAsync(workspaceFolder, cancellationToken).ConfigureAwait(false);

        if (definition is not null)
        {
            if (WorkspaceClassifier.Classify(definition, command.ProjectDirectory) is { } classification && !classification.Allows(SyncOperation.Reattach))
            {
                throw CopilotStudioException.ReattachNotSupported(command.ProjectDirectory, AuthoringSupportGate.DescribeBlocked(classification, SyncOperation.Reattach));
            }

            await WorkspaceDefinitionReader.RequireSynchronizableAsync(_synchronizer, workspaceFolder, definition, cancellationToken).ConfigureAwait(false);
        }

        var existing = await _dataverseClient.GetAgentIdBySchemaNameAsync(command.SchemaName, cancellationToken).ConfigureAwait(false);

        RemoteBindingSnapshot? bindingSnapshot = null;
        AttachTarget? target = null;
        var refusals = new List<string>();

        try
        {
            target = await ResolveTargetAsync(command, existing, cancellationToken).ConfigureAwait(false);
            var updateWorkspaceDirectory = target.Created && target.SchemaName != command.SchemaName;

            var syncInfo = await AgentSyncInfoFactory.CreateAsync(_dataverseClient, command.Environment, target.AgentId, cancellationToken).ConfigureAwait(false);
            var operationContext = await _operationContexts.GetAsync(syncInfo).ConfigureAwait(false);

            if (isRetarget)
            {
                bindingSnapshot = _retargetService.ResetRemoteBindingState(workspaceFolder);
                _retargetService.PersistRetargetBackup(workspaceFolder, bindingSnapshot);
            }
            else
            {
                _synchronizer.ClearComponentSyncBaselines(workspaceFolder);
            }

            var pushedConnectors = await _synchronizer.PushCustomConnectorsAsync(workspaceFolder, _dataverseClient, cancellationToken, overwriteRemoteChanges: true).ConfigureAwait(false);
            refusals.AddRange(pushedConnectors.FailureReasons);

            var provisionedReferences = await DeclaredConnectionReferences.ProvisionAsync(
                command.ProjectDirectory, _synchronizer, _dataverseClient,
                pushedConnectors.PushedRowIds, cancellationToken).ConfigureAwait(false);

            var (workflowResponses, cloudFlowMetadata) = await _synchronizer.UpsertWorkflowForAgentAsync(workspaceFolder, _dataverseClient, syncInfo.AgentId, cancellationToken, WorkflowActivationMode.DraftWhenConnectionReferencesExist).ConfigureAwait(false);
            refusals.AddRange(SyncResponseGuards.DescribeRefusedWorkflows(workflowResponses));

            var (promptResponses, aiPromptMetadata) = await _synchronizer.UpsertAIPromptsForAgentAsync(workspaceFolder, _dataverseClient, syncInfo.AgentId, cancellationToken, overwriteRemoteChanges: true).ConfigureAwait(false);
            refusals.AddRange(SyncResponseGuards.DescribeRefusedPrompts(promptResponses));

            var seededFlowMetadata = DeclaredConnectionReferences.WithProvisioned(cloudFlowMetadata, provisionedReferences);

            await _retargetService.SyncWorkspaceAsync(workspaceFolder, operationContext, null, updateWorkspaceDirectory, _dataverseClient, syncInfo, seededFlowMetadata, cancellationToken, aiPromptMetadata, syncCustomConnectors: false, syncWorkflowsAndPrompts: false).ConfigureAwait(false);
            await _synchronizer.SaveSyncInfoAsync(workspaceFolder, syncInfo).ConfigureAwait(false);

            if (isRetarget)
            {
                _retargetService.FinalizeRetarget(workspaceFolder, pushSucceeded: true);
            }
        }
        catch (Exception failure)
        {
            if (bindingSnapshot is not null)
            {
                _retargetService.RestoreRemoteBindingState(workspaceFolder, bindingSnapshot);
                _retargetService.ClearRetargetBackup(workspaceFolder);
            }
            else
            {
                RemovePartialCloudCache(command.ProjectDirectory);
            }

            if (failure is OperationCanceledException)
            {
                throw;
            }

            throw target is null
                ? await DescribeAmbiguousFailureAsync(command, failure, cancellationToken).ConfigureAwait(false)
                : CopilotStudioException.BootstrapReattachFailed(target.AgentId.ToString(), command.SchemaName, failure);
        }

        return new CopilotAttachOutcome
        {
            AgentId = target.AgentId.ToString(),
            SchemaName = target.SchemaName,
            AgentCreated = target.Created,
            Retargeted = isRetarget,
            Warnings = refusals,
            Notices = target.Notices,
        };
    }

    private sealed record AttachTarget(Guid AgentId, string SchemaName, bool Created, IReadOnlyList<string> Notices);

    /// <summary>
    /// Describes a failure that happened before any agent identifier was known.
    /// </summary>
    private async Task<CopilotStudioException> DescribeAmbiguousFailureAsync(CopilotAttachCommand command, Exception failure, CancellationToken cancellationToken)
    {
        try
        {
            var existing = await _dataverseClient.GetAgentIdBySchemaNameAsync(command.SchemaName, cancellationToken).ConfigureAwait(false);

            if (existing != Guid.Empty)
            {
                return CopilotStudioException.BootstrapReattachFailed(existing.ToString(), command.SchemaName, failure);
            }
        }
        catch (Exception probe) when (probe is not OperationCanceledException)
        {
            return CopilotStudioException.BootstrapAttachUncertain(command.SchemaName, command.Environment.EnvironmentId, failure);
        }

        return CopilotStudioException.BootstrapAttachFailed(command.SchemaName, command.Environment.EnvironmentId, failure);
    }

    /// <summary>
    /// Reads the workspace definition, or returns <see langword="null"/> when there is nothing to read
    /// it from.
    /// </summary>
    private async Task<DefinitionBase?> TryReadDefinitionAsync(DirectoryPath workspaceFolder, CancellationToken cancellationToken)
    {
        try
        {
            return await WorkspaceDefinitionReader.ReadAsync(_synchronizer, workspaceFolder, checkKnowledgeFiles: false, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates the agent when the schema name is free, and adopts the one already carrying it.
    /// </summary>
    private async Task<AttachTarget> ResolveTargetAsync(CopilotAttachCommand command, Guid existing, CancellationToken cancellationToken)
    {
        if (existing == Guid.Empty)
        {
            var created = await _dataverseClient.CreateNewAgentAsync(command.DisplayName, command.SchemaName, AgentClassifier.DetectAuthoringShapeFromFolder(command.ProjectDirectory), cancellationToken).ConfigureAwait(false);

            return new AttachTarget(created.AgentId, created.SchemaName ?? command.SchemaName, Created: true, []);
        }

        if (!string.IsNullOrWhiteSpace(command.ExpectedAgentId) && !string.Equals(command.ExpectedAgentId, existing.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw CopilotStudioException.AgentAlreadyExists(command.SchemaName);
        }

        return new AttachTarget(
            existing,
            command.SchemaName,
            Created: false,
            string.IsNullOrWhiteSpace(command.ExpectedAgentId)
                ? [$"An agent already carried the schema name {command.SchemaName} in {command.Environment.EnvironmentId}; it was adopted rather than duplicated."]
                : []);
    }

    private static void RemovePartialCloudCache(string projectDirectory)
    {
        var cloudCache = Path.Combine(projectDirectory, ".mcs");

        if (!Directory.Exists(cloudCache))
        {
            return;
        }

        try
        {
            Directory.Delete(cloudCache, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
