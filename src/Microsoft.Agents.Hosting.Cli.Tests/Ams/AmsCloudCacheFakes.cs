using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

internal sealed class CloudCacheRepository : IDisposable
{
    public const string Agent = "ContosoHR";

    public const string Identity = "env-1|agent-1";

    private readonly TemporaryWorkspace _workspace = new(withProjectFile: false);

    public CloudCacheRepository()
    {
        Git.Tree = new GitWorkingTree(Root, [], "main", true);
        Git.MergeBase = null;
    }

    public string Root => _workspace.Root;

    public FakeGitPort Git { get; } = new();

    public DateTimeOffset Now { get; set; } = new(2026, 8, 2, 10, 15, 0, TimeSpan.Zero);

    public AmsCloudCacheShelf Shelf => new(Git, new FixedClock(Now));

    public string Shelves => Path.Combine(Root, ".git", "msagent", "mcs");

    public static string Commit(int number) => number.ToString("x40", CultureInfo.InvariantCulture);

    public static string Cache(string folder) => File.ReadAllText(Path.Combine(folder, ".mcs", "botdefinition.json"));

    public static void Write(string folder, string cache) => File.WriteAllText(Path.Combine(folder, ".mcs", "botdefinition.json"), cache);

    public string Folder(string name = Agent, string cache = "v1", string? identity = Identity)
    {
        var folder = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.Combine(folder, ".mcs"));
        if (identity is not null)
        {
            File.WriteAllText(Path.Combine(folder, ".mcs", "conn.json"), identity);
        }

        Write(folder, cache);
        return folder;
    }

    public static IEnumerable<string> NewestFirst(int oldest, int newest) => Enumerable.Range(oldest, newest - oldest + 1).Reverse().Select(Commit);

    public void Checkout(string commit, string? branch = "main")
    {
        Git.Commits["HEAD"] = commit;
        Git.Tree = Git.Tree! with { Branch = branch };
        if (branch is not null)
        {
            Git.LocalBranches.Add(branch);
        }
    }

    public async Task SaveAsync(string folder, string commit, string cache, string? identity = Identity, string? branch = "main")
    {
        Checkout(commit, branch);
        Write(folder, cache);
        Assert.Null(await Shelf.SaveAsync(folder, identity, CancellationToken.None));
        Now = Now.AddMinutes(1);
    }

    public async Task SaveEachAsync(string folder, IEnumerable<int> numbers, string? branch)
    {
        foreach (var number in numbers)
        {
            await SaveAsync(folder, Commit(number), $"v{number}", branch: branch);
        }
    }

    public string KeyDirectory(string name = Agent) => Directory.GetDirectories(Shelves).Single(directory => ReadIndex(directory)["folder"]!.GetValue<string>() == name);

    public JsonNode Index(string name = Agent) => ReadIndex(KeyDirectory(name));

    public string? Saved(string commit, string name = Agent) => Path.Combine(KeyDirectory(name), commit, "botdefinition.json") is var path && File.Exists(path) ? File.ReadAllText(path) : null;

    public IEnumerable<string> SavedCommits(string name = Agent) => Index(name)["copies"]!.AsArray().Select(copy => copy!["commit"]!.GetValue<string>());

    public void Dispose() => _workspace.Dispose();

    private static JsonNode ReadIndex(string keyDirectory) => JsonNode.Parse(File.ReadAllText(Path.Combine(keyDirectory, "index.json")))!;
}

internal sealed class SyncedFolders : ICopilotWorkspacePort, ICopilotWorkspaceAttachPort, ICopilotWorkspaceConnectPort, IWorkflowStorePort, ICopilotConnectionPort
{
    public static readonly CopilotEnvironmentContext Environment = new() { EnvironmentId = "env-1", TenantId = "tenant-1", DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/") };

    private static readonly WorkflowDocument Workflow = new() { WorkflowId = Guid.Empty, Name = "Expense approvals", State = "Activated" };

    public List<string> Calls { get; } = [];

    public string Cloud { get; set; } = "cloud";

    public bool Changed { get; set; } = true;

    public bool Removed { get; set; } = true;

    public List<string> Skipped { get; } = [];

    public List<string> Added { get; } = [];

    public static CopilotWorkspaceInfo Info(string folder, string identity = CloudCacheRepository.Identity) => new() { WorkspacePath = folder, EnvironmentId = identity.Split('|')[0], AgentId = identity.Split('|')[1], DataverseOrganization = Environment.DataverseOrganization };

    public bool IsWorkspace(string directory) => Record("is-workspace", true);

    public string ProjectFolderName(string displayName) => Record("folder-name", displayName);

    public Task<CopilotWorkspaceInfo> ReadWorkspaceInfoAsync(string directory, CancellationToken cancellationToken) => Task.FromResult(Info(directory, File.ReadAllText(Path.Combine(directory, ".mcs", "conn.json"))));

    public Task<CopilotCloneOutcome> CloneAsync(CopilotCloneCommand command, CancellationToken cancellationToken)
    {
        string[] folders = [.. command.ComponentCollectionIds.Prepend(CloudCacheRepository.Agent).Select(name => Path.Combine(command.WorkspacePath, name))];
        Array.ForEach(folders, folder => Sync("clone", folder));
        return Task.FromResult(new CopilotCloneOutcome { SchemaName = "cr1a2_contosoHr", DisplayName = "Contoso HR", AgentWorkspacePath = folders[0], Workspaces = [.. folders.Select(folder => new CopilotClonedWorkspace { Kind = "agent", Path = folder, DisplayName = Path.GetFileName(folder) })] });
    }

    public Task<CopilotChangeSnapshot> GetChangesAsync(CopilotWorkspaceInfo workspace, CancellationToken cancellationToken) => Task.FromResult(See("changes", workspace.WorkspacePath, new CopilotChangeSnapshot { Local = [], Remote = [] }));

    public Task<CopilotPullOutcome> PullAsync(CopilotWorkspaceInfo workspace, CancellationToken cancellationToken) => Task.FromResult(Sync("pull", workspace.WorkspacePath, new CopilotPullOutcome { Changes = [], Warnings = ["merge left conflicts"] }));

    public Task<CopilotPushOutcome> PushAsync(CopilotWorkspaceInfo workspace, bool overwrite, CancellationToken cancellationToken) => Task.FromResult(Sync("push", workspace.WorkspacePath, new CopilotPushOutcome { Changes = [] }));

    public Task<CopilotAttachOutcome> AttachAsync(CopilotAttachCommand command, CancellationToken cancellationToken) => Task.FromResult(Sync("attach", command.ProjectDirectory, new CopilotAttachOutcome { AgentId = "agent-1", SchemaName = command.SchemaName, Notices = ["attach notice"] }));

    public Task<CopilotConnectOutcome> ConnectAsync(CopilotConnectCommand command, CancellationToken cancellationToken) => Task.FromResult(Connect("connect", command, []));

    public Task<CopilotConnectMergeOutcome> ConnectAndMergeAsync(CopilotConnectMergeCommand command, CancellationToken cancellationToken) => Task.FromResult(new CopilotConnectMergeOutcome { Connected = Connect("connect-merge", command.Connect, Added), AddedCollectionDirectories = Added, MergedPaths = [.. command.BaseFiles.Keys] });

    public Task<WorkflowDocument> ReadLocalAsync(string workspaceRoot, CancellationToken cancellationToken) => Task.FromResult(Record("read-local", Workflow));

    public Task<WorkflowDocument?> GetAsync(CopilotEnvironmentContext environment, Guid workflowId, CancellationToken cancellationToken) => Task.FromResult<WorkflowDocument?>(Record("get", Workflow));

    public Task<IReadOnlyList<WorkflowDocument>> ListAsync(CopilotEnvironmentContext environment, string? nameFilter, int? maximumCount, CancellationToken cancellationToken) => Task.FromResult(Record<IReadOnlyList<WorkflowDocument>>("list", [Workflow]));

    public Task<WorkflowDownloadOutcome> DownloadAsync(CopilotEnvironmentContext environment, Guid workflowId, string workspaceRoot, CancellationToken cancellationToken, bool overwriteLocalChanges = false) =>
        Task.FromResult(Sync($"download overwrite={overwriteLocalChanges}", workspaceRoot, new WorkflowDownloadOutcome { Workflow = Workflow, FolderPath = workspaceRoot, Changed = Changed, Warnings = ["renamed"] }));

    public Task<WorkflowDownloadOutcome> CloneAsync(CopilotEnvironmentContext environment, Guid workflowId, string workspaceRoot, string? name, CancellationToken cancellationToken) =>
        Task.FromResult(Sync($"clone {name}", workspaceRoot, new WorkflowDownloadOutcome { Workflow = Workflow, FolderPath = workspaceRoot, Changed = true }));

    public Task<WorkflowUpsertOutcome> UpsertAsync(CopilotEnvironmentContext environment, string workspaceRoot, CancellationToken cancellationToken) => Task.FromResult(Sync("upsert", workspaceRoot, new WorkflowUpsertOutcome { Workflow = Workflow }));

    public Task RequireNoUnpulledChangesAsync(CopilotEnvironmentContext environment, string workspaceRoot, CancellationToken cancellationToken) => Task.FromResult(See("unpulled", workspaceRoot, true));

    public void WriteConnectionReferenceDeclaration(string workspaceRoot) => Record("declare", true);

    public Task<bool> DeleteAsync(CopilotEnvironmentContext environment, Guid workflowId, CancellationToken cancellationToken) => Task.FromResult(Record("delete", true));

    public Task<string> GetAgentUseWarningAsync(CopilotEnvironmentContext environment, Guid workflowId, CancellationToken cancellationToken) => Task.FromResult(Record("agent-use", string.Empty));

    public Task<CopilotConnectionSnapshot> ReadConnectionsAsync(CopilotEnvironmentContext environment, string projectDirectory, CancellationToken cancellationToken) => Task.FromResult(Record("connections", new CopilotConnectionSnapshot()));

    public Task<IReadOnlyList<string>> ReadDeclaredConnectorsAsync(CopilotEnvironmentContext environment, string projectDirectory, CancellationToken cancellationToken) => Task.FromResult(Record<IReadOnlyList<string>>("connectors", []));

    public Task BindAsync(CopilotEnvironmentContext environment, string projectDirectory, IReadOnlyList<CopilotConnectionBinding> bindings, CancellationToken cancellationToken) => Task.FromResult(Record("bind", true));

    public Task<string> CreateConnectionReferenceAsync(CopilotEnvironmentContext environment, string projectDirectory, string connectorInternalId, CancellationToken cancellationToken) => Task.FromResult(Record("create-reference", "cr_new"));

    public Task<CopilotConnectionReferenceRemoval> RemoveConnectionReferenceAsync(CopilotEnvironmentContext environment, string projectDirectory, string logicalName, bool confirmed, CancellationToken cancellationToken) =>
        Task.FromResult(Removed ? Sync("remove-reference", projectDirectory, new CopilotConnectionReferenceRemoval(true, [])) : See("remove-reference", projectDirectory, new CopilotConnectionReferenceRemoval(false, [])));

    public IReadOnlyList<CopilotWorkflowView> ReadWorkflows(string projectDirectory, IReadOnlyCollection<string> boundReferences) => Record<IReadOnlyList<CopilotWorkflowView>>("workflows", []);

    public Task<CopilotWorkflowActivation> SetWorkflowStatesAsync(CopilotEnvironmentContext environment, string projectDirectory, IReadOnlyList<CopilotWorkflowStateChange> changes, CancellationToken cancellationToken) => Task.FromResult(Record("states", new CopilotWorkflowActivation()));

    public Task<CopilotWorkflowActivationReport> ActivateWorkflowsAsync(CopilotEnvironmentContext environment, string projectDirectory, string? principal, CancellationToken cancellationToken) => Task.FromResult(Record("activate", new CopilotWorkflowActivationReport()));

    private T Record<T>(string call, T result)
    {
        Calls.Add(call);
        return result;
    }

    private T See<T>(string call, string folder, T result) => Record($"{call} {CloudCacheRepository.Cache(folder)}", result);

    private T Sync<T>(string call, string folder, T result)
    {
        See(call, folder, result);
        CloudCacheRepository.Write(folder, Cloud);
        return result;
    }

    private void Sync(string call, string folder) => Sync(call, folder, true);

    private CopilotConnectOutcome Connect(string call, CopilotConnectCommand command, IEnumerable<string> added)
    {
        foreach (var folder in command.CollectionDirectories.Except(Skipped).Prepend(command.WorkspaceDirectory).Concat(added))
        {
            Sync(call, folder);
        }

        return new CopilotConnectOutcome { AgentId = "agent-1", SkippedCollectionDirectories = Skipped };
    }
}
