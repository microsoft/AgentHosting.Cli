using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Platform.Content.Exceptions;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

internal sealed class FakeCopilotCatalogPort : ICopilotCatalogPort
{
    public List<CopilotAgentSummary> Agents { get; } = [];

    public List<int?> RequestedMaximumCounts { get; } = [];

    public Queue<string> StateSequence { get; } = new();

    public int GetAgentCalls { get; private set; }

    public int DeleteCalls { get; private set; }

    public int CreateCalls { get; private set; }

    public int PublishCalls { get; private set; }

    public Queue<DateTimeOffset?> PublishedOnSequence { get; } = new();

    public DateTimeOffset? PublishedOn { get; set; }

    public string? ResolveReturns { get; set; }

    public Exception? GetAgentThrows { get; init; }

    public bool IsManagedReturns { get; init; }

    public List<string> ResolvedValues { get; } = [];

    public Task<IReadOnlyList<CopilotAgentSummary>> ListAgentsAsync(CopilotEnvironmentContext environment, int? maximumCount, CancellationToken cancellationToken)
    {
        RequestedMaximumCounts.Add(maximumCount);

        return Task.FromResult<IReadOnlyList<CopilotAgentSummary>>(maximumCount > 0 ? [.. Agents.Take(maximumCount.Value)] : Agents);
    }

    public Task<string?> ResolveAgentIdAsync(CopilotEnvironmentContext environment, string botIdOrSchemaName, CancellationToken cancellationToken)
    {
        ResolvedValues.Add(botIdOrSchemaName);
        return Task.FromResult(ResolveReturns);
    }

    public string SchemaNameReturns { get; set; } = "con_agent";

    public Task<CopilotAgentSummary> GetAgentAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken)
    {
        GetAgentCalls++;

        if (GetAgentThrows is not null)
        {
            throw GetAgentThrows;
        }

        var state = StateSequence.Count > 0 ? StateSequence.Dequeue() : "Provisioned";
        return Task.FromResult(new CopilotAgentSummary { AgentId = agentId, SchemaName = SchemaNameReturns, DisplayName = "Contoso Agent", State = state, IsManaged = IsManagedReturns });
    }

    public Task DeleteAgentAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken)
    {
        DeleteCalls++;

        return DeleteThrows is not null ? Task.FromException(DeleteThrows) : Task.CompletedTask;
    }

    public Exception? DeleteThrows { get; set; }

    public Task<DateTimeOffset?> GetPublishedOnAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken) =>
        Task.FromResult(PublishedOnSequence.Count > 0 ? PublishedOnSequence.Dequeue() : PublishedOn);

    public Queue<Exception> PublishThrowsSequence { get; } = new();

    public Task PublishAgentAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken)
    {
        PublishCalls++;

        return PublishThrowsSequence.Count > 0 ? Task.FromException(PublishThrowsSequence.Dequeue()) : Task.CompletedTask;
    }
}

internal sealed class FakeCopilotWorkspacePort : ICopilotWorkspacePort
{
    public bool WorkspaceExists { get; set; } = true;

    public string EnvironmentIdReturns { get; init; } = "env-1";

    public string DataverseOrganizationReturns { get; init; } = "https://contoso.crm.dynamics.com/";

    public List<CopilotChange> LocalChanges { get; } = [];

    public List<CopilotChange> RemoteChanges { get; } = [];

    public int PushCalls { get; private set; }

    public bool? LastPushOverwrite { get; private set; }

    public int PullCalls { get; private set; }

    public int CloneCalls { get; private set; }


    public Exception? PushThrows { get; init; }

    public Exception? CloneThrows { get; init; }

    public CopilotCloneCommand? LastCloneCommand { get; private set; }

    public Action? OnClone { get; init; }

    public Dictionary<string, string> EnvironmentIdByDirectory { get; } = [];

    public List<string> PushedDirectories { get; } = [];

    public List<string> PulledDirectories { get; } = [];

    public bool IsWorkspace(string directory) => WorkspaceExists;

    public Task<CopilotWorkspaceInfo> ReadWorkspaceInfoAsync(string directory, CancellationToken cancellationToken) =>
        Task.FromResult(new CopilotWorkspaceInfo
        {
            WorkspacePath = directory,
            AgentId = "agent-1",
            EnvironmentId = EnvironmentIdByDirectory.TryGetValue(Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)), out var mapped) ? mapped : EnvironmentIdReturns,
            DataverseOrganization = new Uri(DataverseOrganizationReturns),
        });

    public Task<CopilotCloneOutcome> CloneAsync(CopilotCloneCommand command, CancellationToken cancellationToken)
    {
        CloneCalls++;
        LastCloneCommand = command;

        if (CloneThrows is not null)
        {
            throw CloneThrows;
        }

        var agentWorkspacePath = Path.Combine(command.WorkspacePath, command.AgentFolderName ?? "Contoso Agent");
        Directory.CreateDirectory(agentWorkspacePath);
        File.WriteAllText(Path.Combine(agentWorkspacePath, AgentConfigLocator.ProjectSettingsFileName), "displayName: \"Contoso Agent\"\nschemaName: \"con_agent\"\n");

        OnClone?.Invoke();

        return Task.FromResult(new CopilotCloneOutcome
        {
            SchemaName = "con_agent",
            DisplayName = "Contoso Agent",
            ComponentCount = 3,
            AgentWorkspacePath = Path.Combine(command.WorkspacePath, command.AgentFolderName ?? "Contoso Agent"),
            Workspaces = [.. Workspaces(command)],
        });
    }

    private static IEnumerable<CopilotClonedWorkspace> Workspaces(CopilotCloneCommand command)
    {
        yield return new CopilotClonedWorkspace
        {
            Kind = "agent",
            Path = Path.Combine(command.WorkspacePath, command.AgentFolderName ?? "Contoso Agent"),
            DisplayName = "Contoso Agent",
        };

        foreach (var collection in command.ComponentCollectionIds)
        {
            yield return new CopilotClonedWorkspace
            {
                Kind = "component-collection",
                Path = $"{command.WorkspacePath}/{collection}",
                DisplayName = collection,
            };
        }
    }

    public Task<CopilotChangeSnapshot> GetChangesAsync(CopilotWorkspaceInfo workspace, CancellationToken cancellationToken) =>
        Task.FromResult(new CopilotChangeSnapshot { Local = LocalChanges, Remote = RemoteChanges });

    public List<string> PullWarnings { get; } = [];

    public Task<CopilotPullOutcome> PullAsync(CopilotWorkspaceInfo workspace, CancellationToken cancellationToken)
    {
        PullCalls++;
        PulledDirectories.Add(workspace.WorkspacePath);
        return Task.FromResult(new CopilotPullOutcome { Changes = RemoteChanges, Warnings = PullWarnings });
    }

    public Queue<Exception> PushThrowsSequence { get; } = new();

    public List<string> PushWarnings { get; } = [];

    public int AuxiliaryChangeCountReturns { get; init; }

    public IReadOnlyList<string> UploadedFileNamesReturns { get; init; } = [];

    public Task<CopilotPushOutcome> PushAsync(CopilotWorkspaceInfo workspace, bool overwrite, CancellationToken cancellationToken)
    {
        PushCalls++;
        LastPushOverwrite = overwrite;
        PushedDirectories.Add(workspace.WorkspacePath);

        if (PushThrows is not null)
        {
            throw PushThrows;
        }

        if (PushThrowsSequence.Count > 0)
        {
            throw PushThrowsSequence.Dequeue();
        }

        return Task.FromResult(new CopilotPushOutcome { Changes = LocalChanges, AuxiliaryChangeCount = AuxiliaryChangeCountReturns, UploadedFileNames = UploadedFileNamesReturns, Warnings = PushWarnings });
    }
}

internal sealed class FakeCopilotFileSystem : ICopilotFileSystem
{
    public string CurrentDirectory { get; set; } = "/work";

    public bool EmptyDirectory { get; set; } = true;

    public Dictionary<string, string> Files { get; } = [];

    public List<string> Written { get; } = [];

    public string ResolveFullPath(string path) => path.StartsWith('/') ? path : $"{CurrentDirectory}/{path}";

    public bool IsEmptyDirectory(string path) => EmptyDirectory;

    public bool FileExists(string path) => Files.ContainsKey(path);

    public string ReadAllText(string path) => Files[path];

    public void WriteAllText(string path, string content)
    {
        Files[path] = content;
        Written.Add(path);
    }

    public string GetDirectoryName(string path)
    {
        var index = path.LastIndexOf('/');
        return index <= 0 ? string.Empty : path[..index];
    }

    public string Combine(string left, string right) => string.IsNullOrEmpty(left) ? right : $"{left}/{right}";
}

internal sealed class RecordingScaffoldPort : ICopilotScaffoldPort
{
    public CopilotScaffoldCommand? LastCommand { get; private set; }

    public IReadOnlyList<string> Scaffold(CopilotScaffoldCommand command)
    {
        LastCommand = command;
        return ["agent.sync.yaml", "settings.mcs.yml"];
    }
}

internal sealed class RecordingAttachPort : ICopilotWorkspaceAttachPort
{
    public CopilotAttachCommand? LastCommand { get; private set; }

    public int Calls { get; private set; }

    public string AgentId { get; init; } = "11111111-1111-1111-1111-111111111111";

    public string? SchemaNameReturns { get; init; }

    public bool AgentCreated { get; init; } = true;

    public Action? OnAttach { get; init; }

    public Exception? Throws { get; init; }

    public List<string> AttachWarnings { get; } = [];

    public List<string> AttachNotices { get; } = [];

    public Task<CopilotAttachOutcome> AttachAsync(CopilotAttachCommand command, CancellationToken cancellationToken)
    {
        Calls++;
        LastCommand = command;

        if (Throws is not null)
        {
            throw Throws;
        }

        OnAttach?.Invoke();

        return Task.FromResult(new CopilotAttachOutcome
        {
            AgentId = AgentId,
            SchemaName = SchemaNameReturns ?? command.SchemaName,
            AgentCreated = AgentCreated,
            Retargeted = command.AllowRetarget,
            Warnings = AttachWarnings,
            Notices = AttachNotices,
        });
    }
}

internal sealed class WorkspaceFileAccessorFactory : IFileAccessorFactory
{
    public bool IsMemoryBacked => false;

    public IFileAccessor Create(DirectoryPath root) => WorkflowWorkspace.OpenWorkspace(root.ToString());

    public void Release(DirectoryPath root)
    {
    }
}

internal static class CopilotTestFixtures
{
    public const string DataverseOrganization = "https://contoso.crm.dynamics.com/";

    public static CopilotEnvironmentResolver Resolver(string? instanceUrl = DataverseOrganization)
    {
        var client = new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-1", instanceUrl: instanceUrl));
        var selection = new InMemoryEnvironmentSelectionStore();
        var profiles = InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant);
        var service = EnvironmentFixtures.Service(client, selection, profiles);

        service.SelectAsync("env-1", CancellationToken.None).GetAwaiter().GetResult();

        return new CopilotEnvironmentResolver(service, PowerPlatformCloudMap.ForCloud(CloudInstance.Public));
    }

    public static CopilotChange Change(string schemaName, string kind = "updated", string componentType = "topic") =>
        new() { Kind = kind, ComponentType = componentType, SchemaName = schemaName };

    public const string DuplicateConnectionReferenceMessage = "[0x80072013:ExportKeyInvalidCreate] violates a database constraint on cre98_AgentC1.cr.WX3p-EQ4";

    public static DataverseNotFoundException MissingEntity(string entityName = "connectionreference") =>
        new("ObjectDoesNotExist", "0x80040217", "service-request-1", $"Entity '{entityName}' With Id = 2a5f0c1e-0000-0000-0000-000000000001 Does Not Exist", null);

    public static RecordingWorkspaceSynchronizer FailingOnce(Exception failure) =>
        new() { PushLocalChangesThrows = failure, PushLocalChangesThrowsOnlyOnce = true };

    public static SyncConnectionAdapter ConnectionAdapter(
        IConnectionManagementService? connections = null,
        IWorkflowActivationService? workflows = null,
        IWorkspaceSynchronizer? synchronizer = null,
        ISyncDataverseClient? dataverseClient = null,
        IConnectionCatalogClient? catalogClient = null,
        ITokenProvider? tokenProvider = null,
        IFileAccessorFactory? fileAccessors = null,
        Func<HttpMessageHandler>? handlerFactory = null,
        Core.Environments.CoreServicesClusterCategory cluster = Core.Environments.CoreServicesClusterCategory.Prod)
    {
        var dataverse = dataverseClient ?? new RecordingConnectionReferenceClient();
        var httpClientAccessor = handlerFactory is null
            ? new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider())
            : new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), handlerFactory);

        return new SyncConnectionAdapter(
            connections ?? new RecordingConnectionManagementService(),
            workflows ?? new RecordingWorkflowActivationService(),
            synchronizer ?? new RecordingWorkspaceSynchronizer(),
            dataverse,
            catalogClient ?? new StubConnectionCatalogClient(),
            tokenProvider ?? new StubTokenProvider(),
            httpClientAccessor,
            fileAccessors ?? new WorkspaceFileAccessorFactory(),
            new DataverseWebApiClient(dataverse, httpClientAccessor),
            cluster);
    }

    public static CopilotWorkspaceLocator Locator(ICopilotWorkspacePort workspaces, ICopilotFileSystem fileSystem) =>
        new(workspaces, fileSystem);
}

