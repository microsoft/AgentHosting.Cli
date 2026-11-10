using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Binds a workspace to a cloud agent through the sync library's attach and connect, restating their outcomes and failures as the CLI's own.</summary>
public sealed class WorkspaceReattachAdapter : ICopilotWorkspaceAttachPort, ICopilotWorkspaceConnectPort
{
    private readonly WorkspaceAttachService _attachService;
    private readonly ISyncDataverseClient _dataverseClient;
    private readonly AgentHostDataverseHttpClientAccessor _httpClientAccessor;

    /// <summary>Creates the adapter.</summary>
    /// <param name="attachService">Attaches the workspace, creating the agent only when its schema name is free, or connects it to an agent that exists.</param>
    /// <param name="dataverseClient">Resolves and creates agents.</param>
    /// <param name="httpClientAccessor">Targets the Dataverse organization.</param>
    public WorkspaceReattachAdapter(WorkspaceAttachService attachService, ISyncDataverseClient dataverseClient, AgentHostDataverseHttpClientAccessor httpClientAccessor)
    {
        _attachService = attachService ?? throw new ArgumentNullException(nameof(attachService));
        _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
        _httpClientAccessor = httpClientAccessor ?? throw new ArgumentNullException(nameof(httpClientAccessor));
    }

    /// <inheritdoc />
    public async Task<CopilotAttachOutcome> AttachAsync(CopilotAttachCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, command.Environment);

        try
        {
            var attached = await _attachService.AttachAsync(Request(command), _dataverseClient, cancellationToken).ConfigureAwait(false);

            return new CopilotAttachOutcome
            {
                AgentId = attached.AgentId.ToString(),
                SchemaName = attached.SchemaName,
                AgentCreated = attached.AgentCreated,
                Retargeted = attached.Retargeted,
                Warnings = [.. attached.ConnectorFailureReasons, .. SyncResponseGuards.DescribeRefusedWorkflows(attached.WorkflowResponses), .. SyncResponseGuards.DescribeRefusedPrompts(attached.PromptResponses)],
                Notices = attached.AdoptedExistingAgent ? [$"An agent already carried the schema name {command.SchemaName} in {command.Environment.EnvironmentId}; it was adopted rather than duplicated."] : [],
            };
        }
        catch (WorkspaceValidationException failure)
        {
            throw WorkspaceDefinitionReader.Translate(failure);
        }
        catch (AgentScaffoldException failure)
        {
            throw CopilotStudioException.WorkspaceFilesUnreadable($"{WorkspaceFileNames.Settings}: {failure.Message}", failure);
        }
        catch (WorkspaceAttachException failure)
        {
            throw Translate(failure, command);
        }
    }

    /// <inheritdoc />
    public Task<CopilotConnectOutcome> ConnectAsync(CopilotConnectCommand command, CancellationToken cancellationToken) => ConnectFoldersAsync(command, async (request, collections) => Connected(await _attachService.ConnectAsync(request, _dataverseClient, cancellationToken).ConfigureAwait(false), collections));

    /// <inheritdoc />
    public Task<CopilotConnectMergeOutcome> ConnectAndMergeAsync(CopilotConnectMergeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return ConnectFoldersAsync(command.Connect, async (request, _) => Merged(await _attachService.ConnectAndMergeAsync(MergeRequest(request, command.BaseFiles), _dataverseClient, cancellationToken).ConfigureAwait(false), command.Connect));
    }

    private async Task<TOutcome> ConnectFoldersAsync<TOutcome>(CopilotConnectCommand command, Func<WorkspaceConnectRequest, IReadOnlyDictionary<string, string>, Task<TOutcome>> connectAsync)
    {
        ArgumentNullException.ThrowIfNull(command);

        var collections = CollectionFolders(command.CollectionDirectories);
        DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, command.Environment);

        try
        {
            return await connectAsync(Request(command, collections), collections).ConfigureAwait(false);
        }
        catch (WorkspaceAttachException failure) when (failure.Failure == WorkspaceAttachFailure.AgentNotFound)
        {
            throw CopilotStudioException.AgentNotFound(command.SchemaName, command.Environment.EnvironmentId);
        }
        catch (WorkspaceAttachException failure) when (failure.Failure == WorkspaceAttachFailure.ConnectBoundToAnotherAgent)
        {
            throw CopilotStudioException.WorkspaceConnectedElsewhere(failure.Message, failure);
        }
    }

    private static CopilotConnectOutcome Connected(WorkspaceConnectResult connected, IReadOnlyDictionary<string, string> collections) => new() { AgentId = connected.AgentId.ToString(), SkippedCollectionDirectories = [.. connected.SkippedCollections.Select(schemaName => collections[schemaName])] };

    private static WorkspaceConnectMergeRequest MergeRequest(WorkspaceConnectRequest connect, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> baseFiles) => new()
    {
        Connect = connect,
        BaseFiles = baseFiles,
        IsFolderTaken = (folder, _) => ValueTask.FromResult(Path.Exists(Path.Combine(connect.WorkspaceFolder.GetParentDirectoryPath().ToString(), folder))),
    };

    private static CopilotConnectMergeOutcome Merged(WorkspaceConnectMergeResult merged, CopilotConnectCommand command) => new()
    {
        Connected = new CopilotConnectOutcome { AgentId = merged.AgentId.ToString(), SkippedCollectionDirectories = [.. command.CollectionDirectories.Where(directory => merged.SkippedCollectionFolders.Contains(Folder(directory)))] },
        AddedCollectionDirectories = [.. merged.AddedCollectionFolders.Select(SyncComponentCollectionAdapter.ToDirectory)],
        MergedPaths = Paths(merged, WorkingCopyMergeOutcome.Merged),
        AddedPaths = Paths(merged, WorkingCopyMergeOutcome.Added),
        UpdatedPaths = Paths(merged, WorkingCopyMergeOutcome.Updated),
        DeletedPaths = Paths(merged, WorkingCopyMergeOutcome.Deleted),
        ConflictedPaths = Paths(merged, WorkingCopyMergeOutcome.Conflicted),
        ReplacedPaths = Paths(merged, WorkingCopyMergeOutcome.Replaced),
    };

    private static string[] Paths(WorkspaceConnectMergeResult merged, WorkingCopyMergeOutcome outcome) => [.. merged.Outcomes.Where(entry => entry.Value == outcome).Select(entry => entry.Key).Order(StringComparer.Ordinal)];

    private static IReadOnlyDictionary<string, string> CollectionFolders(IReadOnlyList<string> directories) => ComponentCollectionFolders.ReadSchemaNames(directories) switch
    {
        { Problems: [var problem, ..] } => throw CopilotStudioException.WorkspaceFilesUnreadable(Describe(problem)),
        var read => read.Folders,
    };

    private static string Describe(ComponentCollectionFolderProblem problem) => problem.Kind switch
    {
        ComponentCollectionFolderProblemKind.SchemaNameRepeated => $"The collection folders '{problem.FirstFolder}' and '{problem.Folder}' both declare the schema name '{problem.SchemaName}'.",
        _ => $"The collection folder '{problem.Folder}' holds no {WorkspaceFileNames.Collection} that names a schemaName.",
    };

    private static DirectoryPath Folder(string directory) => new(directory.Replace('\\', '/'));

    private static WorkspaceAttachRequest Request(CopilotAttachCommand command) => new()
    {
        WorkspaceFolder = Folder(command.ProjectDirectory),
        DataverseEndpoint = command.Environment.DataverseOrganization,
        EnvironmentId = command.Environment.EnvironmentId,
        TenantId = DataverseTargeting.TenantOf(command.Environment),
        DisplayName = command.DisplayName,
        SchemaName = command.SchemaName,
        AllowRetarget = command.AllowRetarget,
        ExpectedAgentId = ExpectedAgent(command.ExpectedAgentId),
        PersistRetargetBackup = true,
    };

    private static Guid? ExpectedAgent(string? agentId) => string.IsNullOrWhiteSpace(agentId) ? null : Guid.TryParse(agentId, out var parsed) ? parsed : Guid.Empty;

    private static WorkspaceConnectRequest Request(CopilotConnectCommand command, IReadOnlyDictionary<string, string> collectionFolders) => new()
    {
        WorkspaceFolder = Folder(command.WorkspaceDirectory),
        DataverseEndpoint = command.Environment.DataverseOrganization,
        EnvironmentId = command.Environment.EnvironmentId,
        TenantId = DataverseTargeting.TenantOf(command.Environment),
        SchemaName = command.SchemaName,
        CollectionFolders = collectionFolders.ToDictionary(collection => collection.Key, collection => Folder(collection.Value), StringComparer.OrdinalIgnoreCase),
    };

    private static CopilotStudioException Translate(WorkspaceAttachException failure, CopilotAttachCommand command) => failure.Failure switch
    {
        WorkspaceAttachFailure.AlreadyConnected => CopilotStudioException.WorkspaceAlreadyConnected(command.ProjectDirectory),
        WorkspaceAttachFailure.ReattachNotSupported => CopilotStudioException.ReattachNotSupported(command.ProjectDirectory, failure.Reason ?? failure.Message),
        WorkspaceAttachFailure.AgentAlreadyExists => CopilotStudioException.BootstrapReattachFailed(AgentOf(failure), command.SchemaName, CopilotStudioException.AgentAlreadyExists(command.SchemaName)),
        WorkspaceAttachFailure.ReattachFailed when failure.InnerException is WorkspacePushException { Code: WorkspacePushErrorCodes.AgentNotProvisioned } notProvisioned => CopilotStudioException.AgentNotProvisioned(notProvisioned.Message, failure),
        WorkspaceAttachFailure.ReattachFailed => CopilotStudioException.BootstrapReattachFailed(AgentOf(failure), command.SchemaName, Cause(failure, command)),
        WorkspaceAttachFailure.AttachUncertain => CopilotStudioException.BootstrapAttachUncertain(command.SchemaName, command.Environment.EnvironmentId, Cause(failure, command)),
        _ => CopilotStudioException.BootstrapAttachFailed(command.SchemaName, command.Environment.EnvironmentId, Cause(failure, command)),
    };

    private static string AgentOf(WorkspaceAttachException failure) => failure.AgentId?.ToString() ?? string.Empty;

    private static Exception Cause(WorkspaceAttachException failure, CopilotAttachCommand command) => failure.InnerException is { } cause ? ConnectionReferenceFailureTranslation.TranslateAny(cause, command.ProjectDirectory) : failure;
}
