using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Reads and writes standalone workflows by delegating to the sync library.</summary>
public sealed class WorkflowDataverseClient : IWorkflowStorePort
{
    private readonly IStandaloneWorkflowDataverseClient _dataverseClient;
    private readonly IStandaloneWorkflowSynchronizer _synchronizer;
    private readonly AgentHostDataverseHttpClientAccessor _httpClientAccessor;

    /// <summary>Creates the client.</summary>
    /// <param name="dataverseClient">The library's Dataverse client.</param>
    /// <param name="synchronizer">The library's standalone workflow orchestrator.</param>
    /// <param name="httpClientAccessor">Targets the client at an environment.</param>
    public WorkflowDataverseClient(IStandaloneWorkflowDataverseClient dataverseClient, IStandaloneWorkflowSynchronizer synchronizer, AgentHostDataverseHttpClientAccessor httpClientAccessor)
    {
        _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
        _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
        _httpClientAccessor = httpClientAccessor ?? throw new ArgumentNullException(nameof(httpClientAccessor));
    }

    /// <inheritdoc />
    public Task<WorkflowDocument> ReadLocalAsync(string workspaceRoot, CancellationToken cancellationToken) =>
        TranslateAsync(async () => Describe(await _synchronizer.ReadLocalAsync(Workspace(workspaceRoot), cancellationToken).ConfigureAwait(false)));

    /// <inheritdoc />
    public Task<WorkflowDocument?> GetAsync(CopilotEnvironmentContext environment, Guid workflowId, CancellationToken cancellationToken) =>
        TranslateAsync(async () =>
        {
            Connect(environment);

            return await _synchronizer.GetAsync(workflowId, cancellationToken).ConfigureAwait(false) is { } workflow ? Describe(workflow) : null;
        });

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkflowDocument>> ListAsync(CopilotEnvironmentContext environment, string? nameFilter, int? maximumCount, CancellationToken cancellationToken) =>
        TranslateAsync(async () =>
        {
            Connect(environment);

            return (IReadOnlyList<WorkflowDocument>)[.. (await _synchronizer.ListAsync(nameFilter, maximumCount, cancellationToken).ConfigureAwait(false)).Select(Describe)];
        });

    /// <inheritdoc />
    public Task<WorkflowDownloadOutcome> DownloadAsync(CopilotEnvironmentContext environment, Guid workflowId, string workspaceRoot, CancellationToken cancellationToken, bool overwriteLocalChanges = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        return TranslateAsync(async () =>
        {
            Connect(environment);

            var download = await _synchronizer.DownloadAsync(workflowId, Workspace(workspaceRoot), environment.EnvironmentId, cancellationToken, new StandaloneWorkflowDownloadOptions { OverwriteLocalChanges = overwriteLocalChanges }).ConfigureAwait(false);

            return new WorkflowDownloadOutcome
            {
                Workflow = Describe(download.Workflow),
                FolderPath = Path.GetFullPath(Path.Combine(workspaceRoot, download.Folder)),
                Changed = download.Changed,
            };
        });
    }

    /// <inheritdoc />
    public Task<WorkflowUpsertOutcome> UpsertAsync(CopilotEnvironmentContext environment, string workspaceRoot, CancellationToken cancellationToken) =>
        TranslateAsync(async () =>
        {
            Connect(environment);

            var upsert = await _synchronizer.UpsertAsync(Workspace(workspaceRoot), environment.EnvironmentId, cancellationToken).ConfigureAwait(false);

            return new WorkflowUpsertOutcome
            {
                Workflow = Describe(upsert.Workflow),
                Created = upsert.Created,
                Replaced = upsert.Replaced,
                CreatedConnectionReferences = upsert.CreatedConnectionReferences,
                ActivationRequested = upsert.ActivationRequested,
                DraftedForConnections = upsert.DraftedForConnections,
                Warning = upsert.Warning,
            };
        });

    /// <inheritdoc />
    public void WriteConnectionReferenceDeclaration(string workspaceRoot) => WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(workspaceRoot);

    /// <inheritdoc />
    public Task<bool> DeleteAsync(CopilotEnvironmentContext environment, Guid workflowId, CancellationToken cancellationToken) =>
        TranslateAsync(async () =>
        {
            Connect(environment);

            return await _synchronizer.DeleteAsync(workflowId, null, cancellationToken).ConfigureAwait(false);
        });

    private static IFileAccessor Workspace(string workspaceRoot) => WorkflowWorkspace.OpenWorkspace(workspaceRoot);

    private static Task<T> TranslateAsync<T>(Func<Task<T>> operation) => WorkflowFailureTranslation.TranslateAsync(operation);

    private void Connect(CopilotEnvironmentContext environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, environment);
    }

    private static WorkflowDocument Describe(StandaloneWorkflowDocument workflow) => new()
    {
        WorkflowId = workflow.WorkflowId,
        Name = workflow.Name,
        Description = workflow.Description,
        State = WorkflowStateProjection.FromStateCode(workflow.StateCode),
        ModifiedOn = workflow.ModifiedOn,
        ConnectionReferences = workflow.ConnectionReferences,
    };
}
