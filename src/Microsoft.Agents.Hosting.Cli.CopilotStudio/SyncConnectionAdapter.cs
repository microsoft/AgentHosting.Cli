using System.Runtime.ExceptionServices;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Implements connection discovery and binding over the sync library's connection-management service.
/// </summary>
public sealed class SyncConnectionAdapter : ICopilotConnectionPort, IConnectorCatalogPort
{
    private const string ConnectionReferencesRoute = "api/data/v9.2/connectionreferences";

    private readonly IConnectionManagementService _connections;
    private readonly IWorkflowActivationService _workflows;
    private readonly IWorkspaceSynchronizer _synchronizer;
    private readonly ISyncDataverseClient _dataverseClient;
    private readonly IConnectionCatalogClient _catalogClient;
    private readonly AgentHostDataverseHttpClientAccessor _httpClientAccessor;
    private readonly IFileAccessorFactory _fileAccessors;
    private readonly DataverseWebApiClient _webApi;
    private readonly ConnectivityContextFactory _contexts;
    private readonly WorkflowActivationPass _activationPass;

    /// <summary>Creates the adapter.</summary>
    /// <param name="connections">The library's connection-management service.</param>
    /// <param name="workflows">Reports which workflows exist and which references they need.</param>
    /// <param name="activationPass">Binds what can be bound without asking and activates the drafts a push left.</param>
    /// <param name="synchronizer">Reads the workspace definition that declares the references.</param>
    /// <param name="dataverseClient">Reads and writes the binding state.</param>
    /// <param name="catalogClient">Lists existing connections per connector.</param>
    /// <param name="tokenProvider">Acquires the Power Platform API token the connector catalog needs.</param>
    /// <param name="httpClientAccessor">Targets the Dataverse organization.</param>
    /// <param name="fileAccessors">Opens the workspace the sync library stores its state in.</param>
    /// <param name="webApi">Deletes the connectionreference record.</param>
    /// <param name="apiTarget">The Power Platform API that names the catalog token's audience and the environment's endpoint.</param>
    public SyncConnectionAdapter(IConnectionManagementService connections, IWorkflowActivationService workflows, WorkflowActivationPass activationPass, IWorkspaceSynchronizer synchronizer, ISyncDataverseClient dataverseClient, IConnectionCatalogClient catalogClient, ITokenProvider tokenProvider, AgentHostDataverseHttpClientAccessor httpClientAccessor, IFileAccessorFactory fileAccessors, DataverseWebApiClient webApi, Core.Environments.PowerPlatformApiTargetConfig apiTarget)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _activationPass = activationPass ?? throw new ArgumentNullException(nameof(activationPass));
        _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
        _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
        _httpClientAccessor = httpClientAccessor ?? throw new ArgumentNullException(nameof(httpClientAccessor));
        _fileAccessors = fileAccessors ?? throw new ArgumentNullException(nameof(fileAccessors));
        _webApi = webApi ?? throw new ArgumentNullException(nameof(webApi));
        _contexts = new ConnectivityContextFactory(tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider)), apiTarget ?? throw new ArgumentNullException(nameof(apiTarget)));
    }

    /// <inheritdoc />
    public async Task<CopilotConnectionSnapshot> ReadConnectionsAsync(CopilotEnvironmentContext environment, string projectDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, environment);

        var (workspaceFolder, definition, context) = await OpenAsync(environment, projectDirectory, cancellationToken).ConfigureAwait(false);
        var views = await _connections.GetAgentConnectionViewsAsync(workspaceFolder, definition, _dataverseClient, _catalogClient, context, cancellationToken).ConfigureAwait(false);

        RecordConnectionsCache(workspaceFolder, views);

        return new CopilotConnectionSnapshot
        {
            References = Translate(views),
            Workflows = WorkflowsOf(workspaceFolder, projectDirectory, views),
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<CopilotWorkflowView> ReadWorkflows(string projectDirectory, IReadOnlyCollection<string> boundReferences)
    {
        ArgumentNullException.ThrowIfNull(boundReferences);

        return WorkflowsOf(Folder(projectDirectory), projectDirectory, [.. boundReferences.Select(logicalName => new AgentConnectionView { ConnectionReferenceLogicalName = logicalName, BoundConnectionExists = true })]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ReadDeclaredConnectorsAsync(CopilotEnvironmentContext environment, string projectDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, environment);

        var (workspaceFolder, definition) = await ReadWorkspaceAsync(projectDirectory, cancellationToken).ConfigureAwait(false);

        return [.. (await _connections.GetAgentConnectionViewsAsync(workspaceFolder, definition, _dataverseClient, _catalogClient, new PowerAppsContext { EnvironmentId = environment.EnvironmentId }, cancellationToken).ConfigureAwait(false)).Where(view => view.IsDeclared).Select(view => string.IsNullOrWhiteSpace(view.ConnectorId) ? view.ConnectorName ?? string.Empty : view.ConnectorId).Where(connectorId => !string.IsNullOrWhiteSpace(connectorId))];
    }

    /// <summary>
    /// Writes what the service just reported into the library's connections cache.
    /// </summary>
    /// <param name="workspaceFolder">The workspace the views belong to.</param>
    /// <param name="views">The connection views the service returned.</param>
    private void RecordConnectionsCache(DirectoryPath workspaceFolder, IReadOnlyList<AgentConnectionView> views)
    {
        try
        {
            _connections.WriteConnectionsCache(workspaceFolder, views);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw CopilotStudioException.ConnectionStateNotRecorded(workspaceFolder.ToString(), failure.Message);
        }
    }

    /// <inheritdoc />
    public async Task BindAsync(CopilotEnvironmentContext environment, string projectDirectory, IReadOnlyList<CopilotConnectionBinding> bindings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(bindings);

        DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, environment);

        var (workspaceFolder, definition, context) = await OpenAsync(environment, projectDirectory, cancellationToken).ConfigureAwait(false);

        var requests = bindings
            .Select(binding => new ConnectionBindingRequest { ConnectionReferenceLogicalName = binding.LogicalName, ConnectionId = binding.ConnectionId })
            .ToArray();

        await _connections.ApplyConnectionBindingsAsync(workspaceFolder, definition, _dataverseClient, _catalogClient, context, requests, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CopilotConnectorInfo>> ListConnectorsAsync(CopilotEnvironmentContext environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return [.. (await _catalogClient.ListConnectorsAsync(await _contexts.CreateAsync(environment, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false)).Select(connector => new CopilotConnectorInfo
        {
            InternalId = connector.InternalId,
            DisplayName = string.IsNullOrWhiteSpace(connector.DisplayName) ? connector.InternalId : connector.DisplayName,
            Publisher = string.IsNullOrWhiteSpace(connector.Publisher) ? null : connector.Publisher,
            Tier = string.IsNullOrWhiteSpace(connector.Tier) ? null : connector.Tier,
        })];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CopilotConnectionCandidate>> ListConnectionsAsync(CopilotEnvironmentContext environment, string connectorInternalId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorInternalId);

        return [.. (await ListConnectionsCoreAsync(await _contexts.CreateAsync(environment, cancellationToken).ConfigureAwait(false), connectorInternalId, cancellationToken).ConfigureAwait(false)).Select(Candidate)];
    }

    private async Task<IReadOnlyList<ConnectionInstance>> ListConnectionsCoreAsync(PowerAppsContext context, string connectorInternalId, CancellationToken cancellationToken)
    {
        try
        {
            return await _catalogClient.ListConnectionsAsync(context, connectorInternalId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (PowerAppsFailure.IsNotFound(failure))
        {
            throw new ConnectorNotFoundException(connectorInternalId, context.EnvironmentId, failure);
        }
    }

    /// <inheritdoc />
    public async Task<string> CreateConnectionReferenceAsync(CopilotEnvironmentContext environment, string projectDirectory, string connectorInternalId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorInternalId);

        DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, environment);

        var (workspaceFolder, definition) = await ReadWorkspaceAsync(projectDirectory, cancellationToken).ConfigureAwait(false);

        return await _connections.CreateConnectionReferenceForConnectorAsync(workspaceFolder, definition, connectorInternalId, _dataverseClient, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CopilotConnectionReferenceRemoval> RemoveConnectionReferenceAsync(CopilotEnvironmentContext environment, string projectDirectory, string logicalName, bool confirmed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, environment);

        var (workspaceFolder, definition) = await ReadWorkspaceAsync(projectDirectory, cancellationToken).ConfigureAwait(false);
        var result = await _connections.RemoveConnectionReferenceAsync(workspaceFolder, definition, logicalName, confirmed, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<CopilotConnectionUsage> usages = [.. result.Usages.Select(Usage)];

        return result.Removed
            ? new CopilotConnectionReferenceRemoval(true, usages) { RecordDeleted = await RetireAsync(environment, workspaceFolder, logicalName, cancellationToken).ConfigureAwait(false) }
            : new CopilotConnectionReferenceRemoval(false, usages);
    }

    /// <summary>
    /// Retires a reference the workspace no longer declares, from the environment and then from the
    /// baseline. The baseline is only edited once the environment is known to hold no record, so a
    /// delete that failed never leaves the baseline claiming the reference is gone.
    /// </summary>
    private async Task<bool> RetireAsync(CopilotEnvironmentContext environment, DirectoryPath workspaceFolder, string logicalName, CancellationToken cancellationToken)
    {
        var deleted = await DeleteRecordAsync(environment, logicalName, cancellationToken).ConfigureAwait(false);

        RetireFromBaseline(workspaceFolder, logicalName);

        return deleted;
    }

    private async Task<bool> DeleteRecordAsync(CopilotEnvironmentContext environment, string logicalName, CancellationToken cancellationToken)
    {
        try
        {
            var recordId = await RecordIdAsync(logicalName, cancellationToken).ConfigureAwait(false);

            if (recordId == Guid.Empty)
            {
                return false;
            }

            (await _webApi.SendAsync(environment, HttpMethod.Delete, $"{ConnectionReferencesRoute}({recordId})", cancellationToken).ConfigureAwait(false))?.Dispose();

            return true;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw CopilotStudioException.ConnectionReferenceNotDeleted(logicalName, environment.EnvironmentId, failure);
        }
    }

    private async Task<Guid> RecordIdAsync(string logicalName, CancellationToken cancellationToken) =>
        (await _dataverseClient.GetConnectionReferencesByLogicalNamesAsync([logicalName], cancellationToken).ConfigureAwait(false))
            .Where(row => logicalName.Equals(row.ConnectionReferenceLogicalName, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.ConnectionReferenceId)
            .FirstOrDefault();

    private void RetireFromBaseline(DirectoryPath workspaceFolder, string logicalName)
    {
        try
        {
            CloudCacheBaseline.RetireConnectionReference(_fileAccessors.Create(workspaceFolder), logicalName);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw CopilotStudioException.ConnectionReferenceCacheStale(logicalName, failure);
        }
    }

    /// <inheritdoc />
    public async Task<CopilotWorkflowActivation> SetWorkflowStatesAsync(CopilotEnvironmentContext environment, string projectDirectory, IReadOnlyList<CopilotWorkflowStateChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.Count == 0)
        {
            return new CopilotWorkflowActivation();
        }

        DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, environment);

        var workspaceFolder = Folder(projectDirectory);
        var requests = changes
            .Where(change => Guid.TryParse(change.WorkflowId, out _))
            .Select(change => new WorkflowActivationRequest { WorkflowId = Guid.Parse(change.WorkflowId), Activate = change.Activate })
            .ToArray();

        var result = await _workflows.SetWorkflowActivationsAsync(workspaceFolder, requests, _dataverseClient, cancellationToken).ConfigureAwait(false);

        return new CopilotWorkflowActivation
        {
            Activated = [.. result.Workflows.Where(workflow => workflow.State == WorkflowState.Activated).Select(workflow => workflow.DisplayName).OrderBy(name => name, StringComparer.Ordinal)],
            Refused = string.IsNullOrWhiteSpace(result.Message) ? [] : [result.Message],
            Observed = WorkflowResponseStates.Observed(result.Workflows),
            ObservedById = WorkflowResponseStates.ObservedById(result.Workflows),
        };
    }

    /// <inheritdoc />
    public async Task<CopilotWorkflowActivationReport> ActivateWorkflowsAsync(CopilotEnvironmentContext environment, string projectDirectory, string? principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, environment);

        var (workspaceFolder, definition, context) = await OpenAsync(environment, projectDirectory, cancellationToken).ConfigureAwait(false);
        var pass = await _activationPass.RunAsync(workspaceFolder, definition, _dataverseClient, _catalogClient, context, principal, cancellationToken).ConfigureAwait(false);

        if (pass.Failure is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        return new CopilotWorkflowActivationReport
        {
            Workflows = [.. pass.Workflows.Select(workflow => workflow.DisplayName)],
            Activated = pass.Activated,
            Refused = pass.RefusalMessage is null ? [] : [pass.RefusalMessage],
            ObservedById = pass.Workflows.Where(workflow => workflow.IsObserved && !string.IsNullOrWhiteSpace(workflow.WorkflowId)).DistinctBy(workflow => workflow.WorkflowId, StringComparer.OrdinalIgnoreCase).ToDictionary(workflow => workflow.WorkflowId, workflow => workflow.State == WorkflowState.Activated, StringComparer.OrdinalIgnoreCase),
            NotRunningCount = pass.NotRunningCount,
            UnboundConnectionCount = pass.UnresolvedConnections.Length,
        };
    }

    /// <summary>
    /// Targets the Dataverse organization for the operations that follow.
    /// </summary>
    private async Task<(DirectoryPath Folder, DefinitionBase Definition, PowerAppsContext Context)> OpenAsync(CopilotEnvironmentContext environment, string projectDirectory, CancellationToken cancellationToken)
    {
        var (workspaceFolder, definition) = await ReadWorkspaceAsync(projectDirectory, cancellationToken).ConfigureAwait(false);

        return (workspaceFolder, definition, await _contexts.CreateAsync(environment, cancellationToken).ConfigureAwait(false));
    }

    private async Task<(DirectoryPath Folder, DefinitionBase Definition)> ReadWorkspaceAsync(string projectDirectory, CancellationToken cancellationToken)
    {
        var workspaceFolder = Folder(projectDirectory);

        return WorkflowWorkspace.IsStandaloneWorkspace(projectDirectory)
            ? (workspaceFolder, BotDefinition.Empty)
            : (workspaceFolder, await WorkspaceDefinitionReader.ReadAsync(_synchronizer, workspaceFolder, checkKnowledgeFiles: false, cancellationToken).ConfigureAwait(false));
    }

    private static DirectoryPath Folder(string projectDirectory) => new(projectDirectory.Replace('\\', '/'));

    private IReadOnlyList<CopilotWorkflowView> WorkflowsOf(DirectoryPath workspaceFolder, string projectDirectory, IReadOnlyList<AgentConnectionView> views)
    {
        var standalone = WorkflowWorkspace.IsStandaloneWorkspace(projectDirectory);

        return [.. _workflows.GetWorkflowStatusViews(workspaceFolder, views).Select(workflow => Workflow(workflow, standalone)).OrderBy(workflow => workflow.DisplayName, StringComparer.Ordinal)];
    }

    private static CopilotWorkflowView Workflow(WorkflowStatusView workflow, bool standalone) => new()
    {
        WorkflowId = workflow.WorkflowId,
        DisplayName = string.IsNullOrWhiteSpace(workflow.DisplayName) ? workflow.WorkflowId : workflow.DisplayName,
        FilePath = string.IsNullOrWhiteSpace(workflow.FilePath) ? null : workflow.FilePath,
        ConnectionReferences = [.. workflow.ConnectionReferenceLogicalNames],
        CanEnable = workflow.CanEnable,
        IsActivatedLocally = standalone ? null : LocallyActivated(workflow.State),
    };

    /// <summary>Reads the workspace's recorded state, leaving one it cannot settle unanswered.</summary>
    /// <param name="state">The state the workspace scan produced.</param>
    /// <returns>Whether it is running, or <see langword="null"/> when that is not one of the two.</returns>
    private static bool? LocallyActivated(WorkflowState state) => state switch
    {
        WorkflowState.Activated => true,
        WorkflowState.Draft => false,
        _ => null,
    };

    private static IReadOnlyList<CopilotConnectionView> Translate(IReadOnlyList<AgentConnectionView> views) => [.. views.Select(View).OrderBy(view => view.LogicalName, StringComparer.Ordinal)];

    private static CopilotConnectionView View(AgentConnectionView view) => new()
    {
        LogicalName = view.ConnectionReferenceLogicalName,
        ConnectorId = string.IsNullOrWhiteSpace(view.ConnectorId) ? view.ConnectorName ?? string.Empty : view.ConnectorId,
        ConnectorName = string.IsNullOrWhiteSpace(view.ConnectorName) ? null : view.ConnectorName,
        BoundConnectionId = string.IsNullOrWhiteSpace(view.BoundConnectionId) ? null : view.BoundConnectionId,
        BoundConnectionExists = view.BoundConnectionExists,
        CatalogUnavailable = view.CatalogUnavailable,
        IsDeclared = view.IsDeclared,
        Usages = [.. view.Usages.Select(Usage)],
        Candidates = [.. view.Candidates.Select(Candidate)],
    };

    private static CopilotConnectionUsage Usage(ConnectionReferenceUsage usage) => new() { FilePath = usage.FilePath, DisplayName = string.IsNullOrWhiteSpace(usage.DisplayName) ? null : usage.DisplayName };

    private static CopilotConnectionCandidate Candidate(ConnectionInstance connection) => new() { ConnectionId = connection.Name, DisplayName = string.IsNullOrWhiteSpace(connection.DisplayName) ? connection.Name : connection.DisplayName, Status = connection.Status, Owner = string.IsNullOrWhiteSpace(connection.Owner) ? null : connection.Owner };
}
