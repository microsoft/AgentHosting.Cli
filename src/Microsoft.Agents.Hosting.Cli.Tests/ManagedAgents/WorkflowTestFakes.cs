using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

internal static class WorkflowFixtures
{
    public const string AgentUseWarning = "Copilot Studio agent 'NB1' uses this workflow and loses it when the workflow is deleted.";

    public static Guid Write(string workspaceRoot, string name, bool activated = true, Guid? workflowId = null, string? definition = null)
    {
        var id = workflowId ?? Guid.NewGuid();
        var folder = Path.Combine(WorkflowWorkspace.WorkflowsDirectory(workspaceRoot), WorkflowWorkspace.FolderNameFor(name, id));

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, WorkflowWorkspace.DefinitionFileName), definition ?? "{\"properties\":{}}");
        File.WriteAllText(Path.Combine(folder, WorkflowWorkspace.MetadataFileName), $"workflowId: {id}\nname: {name}\nstateCode: {(activated ? 1 : 0)}\nstatusCode: {(activated ? 2 : 1)}\n");

        return id;
    }

    public static ManagedAgentRecord Record(string agentId = "wf-1", string displayName = "WF Hook 2", string environmentId = "env-1", string? workflowId = "11111111-2222-3333-4444-555555555555", string agentLocation = AgentConfigLocator.CurrentDirectoryLocation) => new()    {
        AgentId = agentId,
        DisplayName = displayName,
        AgentType = AgentTypes.McsWorkflow,
        AgentLocation = agentLocation,
        McsWorkflowId = workflowId,
        TenantId = EnvironmentFixtures.ContosoTenant,
        EnvironmentId = environmentId,
        AgentState = EntityStates.Saved,
        EntityTag = string.Empty,
    };

    public static AgentDeploymentRecord Deployment(string agentId = "wf-1", string deploymentName = "devwf", string environmentId = "env-1", string deploymentId = "dep-1", string homeEnvironmentId = "env-1", string deploymentType = DeploymentStages.Dev) => new()
    {
        DeploymentId = deploymentId,
        AgentId = agentId,
        AgentType = AgentTypes.McsWorkflow,
        DeploymentType = deploymentType,
        DeploymentName = deploymentName,
        TenantId = EnvironmentFixtures.ContosoTenant,
        HomeEnvironmentId = homeEnvironmentId,
        EnvironmentId = environmentId,
        EntityTag = string.Empty,
    };

    public static Guid RegisterNested(JsonAgentConfigStore config, string projectDirectory, string folderName, string name = "WF Hook 2")
    {
        var workspaceRoot = Path.Combine(projectDirectory, folderName);

        Directory.CreateDirectory(workspaceRoot);

        var workflowId = Write(workspaceRoot, name);

        config.Add(projectDirectory, Record(workflowId: workflowId.ToString(), agentLocation: folderName));
        ((IAgentDeploymentRegistry)config).Add(projectDirectory, Deployment());

        return workflowId;
    }
}

internal sealed class FakeWorkflowScaffoldPort : IWorkflowScaffoldPort
{
    public List<(string WorkspaceRoot, string Name, Guid WorkflowId)> Calls { get; } = [];

    public string? ValidateName(string name) => new CopilotStudio.EmbeddedWorkflowScaffolder().ValidateName(name);

    public IReadOnlyList<string> Scaffold(string workspaceRoot, string name, Guid workflowId)
    {
        Calls.Add((workspaceRoot, name, workflowId));

        var folderName = WorkflowWorkspace.FolderNameFor(name, workflowId);
        var folderPath = Path.Combine(WorkflowWorkspace.WorkflowsDirectory(workspaceRoot), folderName);

        Directory.CreateDirectory(folderPath);
        File.WriteAllText(Path.Combine(folderPath, WorkflowWorkspace.DefinitionFileName), "{\"properties\":{}}");
        File.WriteAllText(Path.Combine(folderPath, WorkflowWorkspace.MetadataFileName), $"workflowId: {workflowId}\nname: {name}\nstateCode: 1\nstatusCode: 2\n");
        File.WriteAllText(Path.Combine(workspaceRoot, "agent.sync.yaml"), "workflowProjectVersion: 1\n");
        File.WriteAllText(Path.Combine(workspaceRoot, "workflow.sync.yaml"), "workflowProjectVersion: 1\n");

        return [$"workflows/{folderName}/{WorkflowWorkspace.DefinitionFileName}", $"workflows/{folderName}/{WorkflowWorkspace.MetadataFileName}", "agent.sync.yaml", "workflow.sync.yaml"];
    }
}

internal static class WorkflowWorkspaceFixtures
{
    public static WorkflowFolder Single(string workspaceRoot) => WorkflowWorkspace.FindAll(workspaceRoot).Single();

    public static LocalWorkflow RequireSingle(string workspaceRoot) => new FakeWorkflowWorkspacePort().RequireSingle(workspaceRoot);
}

internal sealed class FakeWorkflowWorkspacePort : IWorkflowWorkspacePort
{
    public IReadOnlyList<LocalWorkflow> FindAll(string workspaceRoot) =>
        [.. WorkflowWorkspace.FindAll(workspaceRoot).Select(folder => new LocalWorkflow(folder.WorkflowId, folder.FolderName, folder.FolderPath))];

    public LocalWorkflow RequireSingle(string workspaceRoot) => new CopilotStudio.WorkflowWorkspaceAdapter().RequireSingle(workspaceRoot);

    public bool IsStandaloneWorkspace(string directory) => WorkflowWorkspace.IsStandaloneWorkspace(directory);

    public bool HasAgentProject(string directory) => new CopilotStudio.WorkflowWorkspaceAdapter().HasAgentProject(directory);

    public string ProjectFolderName(string displayName) => new CopilotStudio.WorkflowWorkspaceAdapter().ProjectFolderName(displayName);
}

internal sealed class FakeWorkflowStorePort : IWorkflowStorePort
{
    public List<string> Calls { get; } = [];

    public Dictionary<Guid, WorkflowDocument> Cloud { get; } = [];

    public Dictionary<Guid, string> CloudDefinition { get; } = [];

    public bool DeactivatedBeforeDelete { get; private set; }

    public Task<WorkflowDocument> ReadLocalAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        var folder = Single(workspaceRoot);
        Calls.Add($"read-local:{folder.WorkflowId}");

        return Task.FromResult(Describe(folder));
    }

    public Task<WorkflowDocument?> GetAsync(CopilotEnvironmentContext environment, Guid workflowId, CancellationToken cancellationToken)
    {
        Calls.Add($"get:{workflowId}");

        return Task.FromResult(Cloud.TryGetValue(workflowId, out var document) ? document : null);
    }

    public Task<IReadOnlyList<WorkflowDocument>> ListAsync(CopilotEnvironmentContext environment, string? nameFilter, int? maximumCount, CancellationToken cancellationToken)
    {
        Calls.Add($"list:{environment.EnvironmentId}:{nameFilter ?? "*"}:{maximumCount?.ToString() ?? "*"}");

        var rows = Cloud.Values
            .Where(workflow => string.IsNullOrWhiteSpace(nameFilter) || workflow.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(workflow => workflow.Name, StringComparer.Ordinal);

        return Task.FromResult<IReadOnlyList<WorkflowDocument>>([.. maximumCount > 0 ? rows.Take(maximumCount.Value) : rows]);
    }

    public Task<WorkflowDownloadOutcome> DownloadAsync(CopilotEnvironmentContext environment, Guid workflowId, string workspaceRoot, CancellationToken cancellationToken, bool overwriteLocalChanges = false)
    {
        Calls.Add($"download:{workflowId}");

        var document = Cloud[workflowId];
        var folderPath = Path.Combine(WorkflowWorkspace.WorkflowsDirectory(workspaceRoot), WorkflowWorkspace.FolderNameFor(document.Name, workflowId));

        if (!overwriteLocalChanges
            && WorkflowWorkspace.FindAll(workspaceRoot).FirstOrDefault(candidate => candidate.WorkflowId == workflowId) is { } tracked
            && LastSynced.TryGetValue(workflowId, out var synced)
            && !string.Equals(File.ReadAllText(tracked.DefinitionPath), synced, StringComparison.Ordinal))
        {
            throw ManagedAgentException.WorkflowLocalChangesWouldBeLost(tracked.FolderName);
        }

        var previousFolder = WorkflowWorkspace.FindAll(workspaceRoot).FirstOrDefault(candidate => candidate.WorkflowId == workflowId && !string.Equals(candidate.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase))?.FolderPath;

        if (previousFolder is not null)
        {
            Directory.Move(previousFolder, folderPath);
        }

        Directory.CreateDirectory(folderPath);

        var definition = CloudDefinition.TryGetValue(workflowId, out var body) ? body : "{\"properties\":{}}";
        var metadata = $"workflowId: {workflowId}\nname: {document.Name}\nstateCode: {(document.IsActivated ? 1 : 0)}\nstatusCode: {(document.IsActivated ? 2 : 1)}\n";
        var definitionPath = Path.Combine(folderPath, WorkflowWorkspace.DefinitionFileName);
        var metadataPath = Path.Combine(folderPath, WorkflowWorkspace.MetadataFileName);
        var changed = !File.Exists(definitionPath) || File.ReadAllText(definitionPath) != definition || !File.Exists(metadataPath) || File.ReadAllText(metadataPath) != metadata;

        File.WriteAllText(definitionPath, definition);
        File.WriteAllText(metadataPath, metadata);
        LastSynced[workflowId] = definition;

        return Task.FromResult(new WorkflowDownloadOutcome { Workflow = document, FolderPath = folderPath, PreviousFolder = previousFolder, Changed = changed, Warnings = WarningsOnDownload });
    }

    public IReadOnlyList<string> WarningsOnDownload { get; set; } = [];

    public Dictionary<Guid, string> LastSynced { get; } = [];

    public Task<WorkflowUpsertOutcome> UpsertAsync(CopilotEnvironmentContext environment, string workspaceRoot, CancellationToken cancellationToken)
    {
        var folder = Single(workspaceRoot);
        Calls.Add($"upsert:{folder.WorkflowId}");

        var created = !Cloud.ContainsKey(folder.WorkflowId);
        var document = Describe(folder);
        var activationRequested = document.IsActivated;

        if (DraftOnPush && activationRequested)
        {
            document = document with { State = WorkflowStateProjection.Draft };
        }

        if (ObservedStateOnPush is { Length: > 0 } observed)
        {
            document = document with { State = observed };
        }

        Cloud[folder.WorkflowId] = document;
        CloudDefinition[folder.WorkflowId] = File.ReadAllText(folder.DefinitionPath);
        LastSynced[folder.WorkflowId] = File.ReadAllText(folder.DefinitionPath);

        return Task.FromResult(new WorkflowUpsertOutcome { Workflow = document, Created = created, CreatedConnectionReferences = DeclaredConnectionReferences, ActivationRequested = activationRequested, DraftedForConnections = DraftedForConnectionsOnPush, Warning = WarningOnPush, UnboundConnectionReferences = UnboundConnectionReferencesOnPush });
    }

    public HashSet<Guid> UnpulledChanges { get; } = [];

    public Task RequireNoUnpulledChangesAsync(CopilotEnvironmentContext environment, string workspaceRoot, CancellationToken cancellationToken)
    {
        var folder = Single(workspaceRoot);
        Calls.Add($"unpulled:{folder.WorkflowId}");

        return UnpulledChanges.Contains(folder.WorkflowId) ? Task.FromException(ManagedAgentException.WorkflowCloudChangesWouldBeLost(folder.FolderName)) : Task.CompletedTask;
    }

    public List<string> UnboundConnectionReferencesOnPush { get; } = [];

    public string WarningOnPush { get; set; } = string.Empty;

    public bool DraftedForConnectionsOnPush { get; set; }

    public string? ObservedStateOnPush { get; set; }

    public bool DraftOnPush { get; set; }

    public List<string> DeclaredConnectionReferences { get; } = [];

    public List<string> DeclarationWrites { get; } = [];

    public void WriteConnectionReferenceDeclaration(string workspaceRoot) => DeclarationWrites.Add($"{workspaceRoot}|{Single(workspaceRoot).WorkflowId}");

    public Task<bool> DeleteAsync(CopilotEnvironmentContext environment, Guid workflowId, CancellationToken cancellationToken)
    {
        if (!Cloud.TryGetValue(workflowId, out var document))
        {
            Calls.Add($"delete-absent:{workflowId}");

            return Task.FromResult(false);
        }

        DeactivatedBeforeDelete = !document.IsActivated || Calls.Contains($"deactivate:{workflowId}");
        Calls.Add($"deactivate:{workflowId}");
        Calls.Add($"delete:{workflowId}");
        Cloud.Remove(workflowId);
        CloudDefinition.Remove(workflowId);

        return Task.FromResult(true);
    }

    public Dictionary<Guid, string> AgentUseWarnings { get; } = [];

    public List<(string EnvironmentId, Guid WorkflowId)> AgentUseReads { get; } = [];

    public Exception? AgentUseThrows { get; set; }

    public Task<string> GetAgentUseWarningAsync(CopilotEnvironmentContext environment, Guid workflowId, CancellationToken cancellationToken)
    {
        AgentUseReads.Add((environment.EnvironmentId, workflowId));

        return AgentUseThrows is not null ? Task.FromException<string>(AgentUseThrows) : Task.FromResult(AgentUseWarnings.GetValueOrDefault(workflowId, string.Empty));
    }

    private static WorkflowFolder Single(string workspaceRoot) => WorkflowWorkspace.FindAll(workspaceRoot).Single();

    private static WorkflowDocument Describe(WorkflowFolder folder)
    {
        var metadata = File.ReadAllText(folder.MetadataPath);

        return new WorkflowDocument
        {
            WorkflowId = folder.WorkflowId,
            Name = ValueOf(metadata, "name") ?? folder.WorkflowId.ToString(),
            State = ValueOf(metadata, "stateCode") == "1" ? WorkflowStateProjection.Activated : WorkflowStateProjection.Draft,
        };
    }

    private static string? ValueOf(string yaml, string key) =>
        yaml.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith($"{key}:", StringComparison.Ordinal))?[(key.Length + 1)..].Trim();
}
