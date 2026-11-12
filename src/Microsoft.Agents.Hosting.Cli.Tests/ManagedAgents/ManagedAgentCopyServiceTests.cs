using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

[Collection("CurrentDirectory")]
public sealed class ManagedAgentCopyServiceTests
{
    private const string SourceAgentId = "50505050-1111-2222-3333-444444444444";

    private const string OtherAgentId = "60606060-1111-2222-3333-444444444444";

    private const string OtherCopyId = "70707070-1111-2222-3333-444444444444";

    private const string RegisteredCopyId = "80808080-1111-2222-3333-444444444444";

    private const string SourceSettings = "displayName: \"Source Agent\"\nschemaName: \"con_source\"\n";

    private const string SourceWorkflowId = "a1a1a1a1-1111-2222-3333-444444444444";

    private const string SourceWorkflowRecordId = "b2b2b2b2-1111-2222-3333-444444444444";

    private const string SourceWorkflowDefinition = "{\"properties\":{\"definition\":{\"actions\":{}}}}";

    private static ManagedAgentCopyService Create(JsonAgentConfigStore store, FakeCopyPort copies, FakeCopilotWorkspacePort? workspaces = null, FakeCopilotCatalogPort? catalog = null, IManagedAgentRegistry? agents = null, CopilotEnvironmentResolver? resolver = null, FakeWorkflowStorePort? workflows = null)
    {
        var resolvedWorkspaces = workspaces ?? new FakeCopilotWorkspacePort { WorkspaceExists = false };

        return new ManagedAgentCopyService(
            ManagedAgentFixtures.AgentResolver(store),
            agents ?? store,
            new AgentConfigLocator(),
            resolver ?? ManagedAgentFixtures.Resolver(),
            catalog ?? new FakeCopilotCatalogPort(),
            ManagedAgentFixtures.Materialiser(resolvedWorkspaces),
            CopilotTestFixtures.Locator(resolvedWorkspaces, new SystemCopilotFileSystem()),
            ManagedAgentFixtures.Identities(),
            new CopilotSchemaName(new SyncNamingRulesAdapter()),
            ManagedAgentFixtures.AgentNames(),
            copies,
            ManagedAgentFixtures.Principals(),
            resolvedWorkspaces,
            workflows ?? new FakeWorkflowStorePort(),
            new FakeWorkflowWorkspacePort(),
            new FakeWorkflowScaffoldPort());
    }

    private static (TemporaryWorkspace Workspace, JsonAgentConfigStore Store, string SourceWorkspace) LocalSource(string environmentId = "env-1")
    {
        var workspace = new TemporaryWorkspace(withProjectFile: false);
        var store = ManagedAgentFixtures.Store();

        workspace.AddFile(Path.Combine("Source Agent", AgentConfigLocator.ProjectSettingsFileName), SourceSettings);
        workspace.AddFile(Path.Combine("Source Agent", "topics", "Greeting.mcs.yml"), "kind: AdaptiveDialog\n");
        workspace.AddFile(Path.Combine("Source Agent", ".git", "HEAD"), "ref: refs/heads/main\n");
        workspace.AddFile(Path.Combine("Source Agent", ".mcs", "botdefinition.json"), "{}");
        store.Add(workspace.Root, ManagedAgentFixtures.Agent("agent-src", "Source Agent", environmentId) with { AgentLocation = "Source Agent", McsAgentId = SourceAgentId });

        return (workspace, store, Path.Combine(workspace.Root, "Source Agent"));
    }

    private static (TemporaryWorkspace Workspace, JsonAgentConfigStore Store, string Project) ProjectWithTwoAgents()
    {
        var workspace = new TemporaryWorkspace(withProjectFile: false);
        var store = ManagedAgentFixtures.Store();
        var project = Path.Combine(workspace.Root, "project");

        workspace.AddFile(Path.Combine("project", "Source Agent", AgentConfigLocator.ProjectSettingsFileName), SourceSettings);
        workspace.AddFile(Path.Combine("project", "Source Agent", "topics", "Greeting.mcs.yml"), "kind: AdaptiveDialog\n");
        workspace.AddFile(Path.Combine("project", "Other Agent", AgentConfigLocator.ProjectSettingsFileName), "displayName: \"Other Agent\"\nschemaName: \"con_other\"\n");
        store.Add(project, ManagedAgentFixtures.Agent("agent-src", "Source Agent") with { AgentLocation = "Source Agent", McsAgentId = SourceAgentId });
        store.Add(project, ManagedAgentFixtures.Agent("agent-other", "Other Agent") with { AgentLocation = "Other Agent" });

        return (workspace, store, project);
    }

    private static ManagedAgentCopyRequest LocalRequest() => new() { Name = "Copy Agent" };

    private static ManagedAgentCopyRequest CloudRequest() => new() { CloneAgentId = SourceAgentId, Name = "Copy Agent" };

    private static (TemporaryWorkspace Workspace, JsonAgentConfigStore Store, string SourceWorkspace) LocalWorkflowSource()
    {
        var workspace = new TemporaryWorkspace(withProjectFile: false);
        var store = ManagedAgentFixtures.Store();
        var sourceWorkspace = Path.Combine(workspace.Root, "Source Flow");

        WorkflowFixtures.Write(sourceWorkspace, "Source Flow", workflowId: Guid.Parse(SourceWorkflowId), definition: SourceWorkflowDefinition);
        workspace.AddFile(Path.Combine("Source Flow", ".git", "HEAD"), "ref: refs/heads/main\n");
        workspace.AddFile(Path.Combine("Source Flow", ".config", "settings.json"), "{}");
        workspace.AddFile(Path.Combine("Source Flow", ".mcs", "botdefinition.json"), "{}");
        store.Add(workspace.Root, WorkflowFixtures.Record(SourceWorkflowRecordId, "Source Flow", "env-1", SourceWorkflowId, "Source Flow"));

        return (workspace, store, sourceWorkspace);
    }

    private static ManagedAgentCopyRequest LocalWorkflowRequest() => new() { Name = "Copy Flow" };

    private static ManagedAgentCopyRequest CloudWorkflowRequest() => new() { CloneWorkflowId = SourceWorkflowId, Name = "Copy Flow" };

    private static (TemporaryWorkspace Workspace, JsonAgentConfigStore Store, string Project) SourceThatFillsItsProject(string agentType)
    {
        var workspace = new TemporaryWorkspace(withProjectFile: false);
        var store = ManagedAgentFixtures.Store();
        var project = Path.Combine(workspace.Root, "projects", "source-project");

        if (agentType == AgentTypes.McsWorkflow)
        {
            WorkflowFixtures.Write(project, "Source Flow", workflowId: Guid.Parse(SourceWorkflowId), definition: SourceWorkflowDefinition);
            store.Add(project, WorkflowFixtures.Record(SourceWorkflowRecordId, "Source Flow", "env-1", SourceWorkflowId));
        }
        else
        {
            workspace.AddFile(Path.Combine("projects", "source-project", AgentConfigLocator.ProjectSettingsFileName), SourceSettings);
            store.Add(project, ManagedAgentFixtures.Agent("agent-src", "Source Agent"));
        }

        return (workspace, store, project);
    }

    private static string? ThroughALinkToItsParent(string directory)
    {
        var parent = Path.GetFullPath(Path.Combine(directory, ".."));

        return DirectoryLink($"{parent}-link", parent) is { } link ? Path.Combine(link, Path.GetFileName(directory)) : null;
    }

    private static string? DirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }

        return link;
    }

    private static FakeWorkflowStorePort CloudWorkflows(params string[] downloadWarnings)
    {
        var workflows = new FakeWorkflowStorePort { WritesCloudCacheOnDownload = true, WarningsOnDownload = downloadWarnings };
        workflows.Cloud[Guid.Parse(SourceWorkflowId)] = new WorkflowDocument { WorkflowId = Guid.Parse(SourceWorkflowId), Name = "Source Flow", Description = "Approves expenses", State = WorkflowStateProjection.Activated };
        workflows.CloudDefinition[Guid.Parse(SourceWorkflowId)] = SourceWorkflowDefinition;

        return workflows;
    }

    private static IReadOnlyList<string> CopyStateFolders(string projectDirectory) =>
        [.. Directory.EnumerateDirectories(CopyProjectState.Root(projectDirectory)).SelectMany(Directory.EnumerateFileSystemEntries).Select(entry => Path.GetFileName(entry)).Order(StringComparer.Ordinal)];

    private static FakeCopilotCatalogPort CloudCatalog(params CopilotAgentSummary[] others)
    {
        var catalog = new FakeCopilotCatalogPort();
        catalog.Agents.Add(new CopilotAgentSummary { AgentId = SourceAgentId, SchemaName = "con_source", DisplayName = "Source Agent" });
        catalog.Agents.AddRange(others);

        return catalog;
    }

    private static FakeCopilotWorkspacePort CloningWithCloudCache()
    {
        FakeCopilotWorkspacePort workspaces = null!;
        workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false, OnClone = () => WriteCloudCache(workspaces.LastCloneCommand!) };

        return workspaces;
    }

    private static void WriteCloudCache(CopilotCloneCommand command)
    {
        var cache = Path.Combine(command.WorkspacePath, command.AgentFolderName!, ".mcs");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "botdefinition.json"), "{}");
        File.WriteAllText(Path.Combine(cache, "conn.json"), "{}");
    }

    private static string Snapshot(string directory) =>
        string.Join("\n", Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(file => $"{Path.GetRelativePath(directory, file)}={File.ReadAllText(file)}"));

    private static void AssertNothingLeftBehind(string projectDirectory, JsonAgentConfigStore store, int registeredAgents = 1, string copyFolder = "Copy Agent")
    {
        Assert.False(Directory.Exists(Path.Combine(projectDirectory, copyFolder)));
        Assert.False(Directory.Exists(CopyProjectState.Root(projectDirectory)));
        Assert.Equal(registeredAgents, store.Read(projectDirectory).ManagedAgents.Count);
    }

    private static void AssertOnlyTheProjectLockIsLeft(string projectDirectory, JsonAgentConfigStore store, int registeredAgents = 1, string copyFolder = "Copy Agent")
    {
        Assert.False(Directory.Exists(Path.Combine(projectDirectory, copyFolder)));
        Assert.Equal([CopyProjectState.IgnoreFileName, CopyProjectState.LockFileName], Entries(CopyProjectState.Root(projectDirectory)));
        Assert.Equal(registeredAgents, store.Read(projectDirectory).ManagedAgents.Count);
    }

    private static async Task AssertCopyIsRefusedForOverlappingTheSourceAsync(JsonAgentConfigStore store, string projectDirectory, string sourceWorkspace, string newName, string copyFolder)
    {
        using var scope = new CurrentDirectoryScope(projectDirectory);
        var copies = new FakeCopyPort();
        var sourceFiles = Snapshot(sourceWorkspace);
        var sourceRecord = Assert.Single(store.Read(projectDirectory).ManagedAgents);
        var projectEntries = Entries(projectDirectory);

        Assert.Equal(
            $"The copy's workspace {Path.Combine(projectDirectory, copyFolder)} would overlap the source's workspace {sourceWorkspace}. Choose another --agent-name.",
            (await Assert.ThrowsAsync<UsageException>(() => Create(store, copies).CopyAsync(new ManagedAgentCopyRequest { Name = newName, Project = projectDirectory }, CancellationToken.None))).Message);
        Assert.Empty(copies.Prepared);
        Assert.Empty(copies.Proposals);
        Assert.False(Directory.Exists(CopyProjectState.Root(projectDirectory)));
        Assert.Equal(sourceFiles, Snapshot(sourceWorkspace));
        Assert.Equal(sourceRecord, Assert.Single(store.Read(projectDirectory).ManagedAgents));
        Assert.Equal(projectEntries, Entries(projectDirectory));
    }

    private static IReadOnlyList<string> CopyStateOnceTheProjectLockIsFree(string projectDirectory)
    {
        while (true)
        {
            using var acquired = CopyProjectState.TryLockProject(projectDirectory);

            if (acquired is not null)
            {
                return Entries(CopyProjectState.Root(projectDirectory));
            }
        }
    }

    private static string PlantCopyState(string projectDirectory, string agentId, params string[] files)
    {
        var stateDirectory = CopyProjectState.AgentDirectory(projectDirectory, agentId);

        foreach (var file in files.Select(file => Path.Combine(stateDirectory, file)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{}");
        }

        return stateDirectory;
    }

    private static IReadOnlyList<string> Entries(string directory) =>
        [.. Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Select(entry => Path.GetRelativePath(directory, entry)).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Copy_OfALocalSource_CopiesTheWorkspaceWithoutGitOrTheCloudCache()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(store, new FakeCopyPort()).CopyAsync(LocalRequest(), CancellationToken.None);

        var copyWorkspace = Path.Combine(workspace.Root, "Copy Agent");
        Assert.Equal(copyWorkspace, result.WorkspaceDirectory);
        Assert.True(File.Exists(Path.Combine(copyWorkspace, "topics", "Greeting.mcs.yml")));
        Assert.False(Directory.Exists(Path.Combine(copyWorkspace, ".git")));
        Assert.False(Directory.Exists(Path.Combine(copyWorkspace, ".mcs")));
    }

    [Theory]
    [InlineData(AgentTypes.McsAgent)]
    [InlineData(AgentTypes.McsWorkflow)]
    public async Task Copy_OfALocalSourceWithACloudCacheInANestedFolder_CopiesThatFolderWithoutTheCloudCache(string agentType)
    {
        var (workspace, store, sourceWorkspace) = agentType == AgentTypes.McsWorkflow ? LocalWorkflowSource() : LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var nestedFolder = Path.Combine(Path.GetFileName(sourceWorkspace), "Assistant");
        workspace.AddFile(Path.Combine(nestedFolder, ".mcs", "conn.json"), "{}");
        workspace.AddFile(Path.Combine(nestedFolder, ".git", "HEAD"), "ref: refs/heads/main\n");
        workspace.AddFile(Path.Combine(nestedFolder, "notes.md"), "# Notes\n");
        var sourceFiles = Snapshot(sourceWorkspace);

        var result = await Create(store, new FakeCopyPort()).CopyAsync(LocalRequest(), CancellationToken.None);

        Assert.Equal([".git", Path.Combine(".git", "HEAD"), "notes.md"], Entries(Path.Combine(result.WorkspaceDirectory, "Assistant")));
        Assert.Equal(sourceFiles, Snapshot(sourceWorkspace));
    }

    [Fact]
    public async Task Copy_OfALocalSource_ReadsItsCloudDefinitionWhilePreparingAndKeepsNoCopyOfIt()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        var result = await Create(store, copies).CopyAsync(LocalRequest(), CancellationToken.None);

        Assert.Equal("{}", Assert.Single(copies.SourceDefinitions));
        Assert.False(File.Exists(Assert.Single(copies.Prepared).SourceDefinitionPath));
        Assert.False(Directory.Exists(CopyProjectState.AgentDirectory(workspace.Root, result.AgentId)));
        Assert.True(File.Exists(Path.Combine(CopyProjectState.Root(workspace.Root), ".gitignore")));
    }

    [Fact]
    public async Task Copy_ReportsAgentCopiedAndLeavesOnlyTheProjectLockInTheProjectsConfigCopyFolder()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(store, new FakeCopyPort()).CopyAsync(LocalRequest(), CancellationToken.None);

        Assert.Equal("agent-copied", result.Status);
        Assert.Equal([CopyProjectState.IgnoreFileName, CopyProjectState.LockFileName], Entries(Path.Combine(workspace.Root, ".config", "copy")));
    }

    [Fact]
    public async Task Copy_OfALocalSourceHoldingALink_IsRefusedAndLeavesOnlyTheProjectLockBehind()
    {
        var (workspace, store, sourceWorkspace) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var outside = Path.Combine(Path.GetTempPath(), $"ah-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);

        try
        {
            var link = Path.Combine(sourceWorkspace, "Linked Topics");
            Directory.CreateSymbolicLink(link, outside);
            var copies = new FakeCopyPort();

            var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store, copies).CopyAsync(LocalRequest(), CancellationToken.None));

            Assert.Equal($"The source workspace holds a link ({link}). Only real files can be copied; replace the link with its content and copy again.", failure.Message);
            Assert.Empty(copies.Prepared);
            AssertOnlyTheProjectLockIsLeft(workspace.Root, store);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Copy_OfALocalSource_LeavesTheSourceUnchanged()
    {
        var (workspace, store, sourceWorkspace) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var before = Snapshot(sourceWorkspace);
        var sourceRecord = store.Read(workspace.Root).ManagedAgents[0];

        await Create(store, new FakeCopyPort()).CopyAsync(LocalRequest(), CancellationToken.None);

        Assert.Equal(before, Snapshot(sourceWorkspace));
        Assert.Equal(sourceRecord, store.Read(workspace.Root).ManagedAgents.Single(agent => agent.AgentId == "agent-src"));
    }

    [Fact]
    public async Task Copy_OfALocalSource_PreparesTheStagedCopyUnderItsNewNames()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        var result = await Create(store, copies).CopyAsync(LocalRequest(), CancellationToken.None);

        var prepared = Assert.Single(copies.Prepared);
        Assert.Equal("con_source", prepared.SourceSchemaName);
        Assert.Equal("Copy Agent", prepared.DisplayName);
        Assert.Equal(result.SchemaName, prepared.SchemaName);
        Assert.DoesNotContain(Assert.Single(copies.StagedEntries), entry => entry.StartsWith(".git", StringComparison.Ordinal) || entry.StartsWith(".mcs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Copy_OfALocalSourceWithNoEnvironment_CopiesInTheNamedEnvironment()
    {
        var (workspace, store, _) = LocalSource(environmentId: string.Empty);
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();
        var resolver = ManagedAgentFixtures.Resolver(EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization), EnvironmentFixtures.Environment("env-2", instanceUrl: CopilotTestFixtures.DataverseOrganization));

        var result = await Create(store, copies, resolver: resolver).CopyAsync(LocalRequest() with { EnvironmentId = " env-2 " }, CancellationToken.None);

        var prepared = Assert.Single(copies.Prepared);
        Assert.Equal("env-2", prepared.Environment?.EnvironmentId);
        Assert.False(prepared.CapturedInTargetEnvironment);
        Assert.Equal("env-2", result.EnvironmentId);
        Assert.Equal("env-2", store.Read(workspace.Root).ManagedAgents.Single(agent => agent.AgentId == result.AgentId).EnvironmentId);
    }

    [Fact]
    public async Task Copy_OfALocalSourceWithNoEnvironment_WithoutAnEnvironmentId_AsksForOne()
    {
        var (workspace, store, _) = LocalSource(environmentId: string.Empty);
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();
        var catalog = new FakeCopilotCatalogPort();
        var (resolver, client) = ManagedAgentFixtures.TrackedResolver();
        var before = client.ListCalls;

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store, copies, catalog: catalog, resolver: resolver).CopyAsync(LocalRequest(), CancellationToken.None));

        Assert.Contains("'Source Agent'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("--environment-id", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("()", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("agent create", failure.Message, StringComparison.Ordinal);
        Assert.Equal(before, client.ListCalls);
        Assert.Empty(catalog.RequestedMaximumCounts);
        Assert.Empty(copies.Prepared);
        AssertNothingLeftBehind(workspace.Root, store);
    }

    [Fact]
    public async Task Copy_OfALocalSourceWithABlankBindingEnvironment_UsesTheRecordsEnvironment()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        var result = await Create(store, copies, new FakeCopilotWorkspacePort { WorkspaceExists = true, EnvironmentIdReturns = " " }).CopyAsync(LocalRequest() with { EnvironmentId = "ENV-1" }, CancellationToken.None);

        var prepared = Assert.Single(copies.Prepared);
        Assert.Equal("env-1", prepared.Environment?.EnvironmentId);
        Assert.False(prepared.CapturedInTargetEnvironment);
        Assert.Equal("env-1", result.EnvironmentId);
    }

    [Fact]
    public async Task Copy_OfAnUnboundLocalSource_PreparesForItsEnvironmentWithoutClaimingItWasCapturedThere()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        await Create(store, copies).CopyAsync(LocalRequest(), CancellationToken.None);

        var prepared = Assert.Single(copies.Prepared);
        Assert.False(prepared.CapturedInTargetEnvironment);
        Assert.Equal("env-1", prepared.Environment?.EnvironmentId);
    }

    [Fact]
    public async Task Copy_OfALocalSourceConnectedToItsEnvironment_PreparesItAsCapturedThere()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        await Create(store, copies, new FakeCopilotWorkspacePort { WorkspaceExists = true }).CopyAsync(LocalRequest(), CancellationToken.None);

        var prepared = Assert.Single(copies.Prepared);
        Assert.True(prepared.CapturedInTargetEnvironment);
        Assert.Equal("env-1", prepared.Environment?.EnvironmentId);
    }

    [Fact]
    public async Task Copy_OfACloudSource_PreparesItAsCapturedInItsEnvironment()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        await Create(ManagedAgentFixtures.Store(), copies, CloningWithCloudCache(), CloudCatalog()).CopyAsync(CloudRequest(), CancellationToken.None);

        var prepared = Assert.Single(copies.Prepared);
        Assert.True(prepared.CapturedInTargetEnvironment);
        Assert.Equal("env-1", prepared.Environment?.EnvironmentId);
    }

    [Fact]
    public async Task Copy_OfALocalSourceAskedForAnotherEnvironment_NamesItsOwn()
    {
        var (workspace, store, _) = LocalSource(environmentId: "env-3");
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store, copies, new FakeCopilotWorkspacePort { WorkspaceExists = true }).CopyAsync(LocalRequest() with { EnvironmentId = "env-2" }, CancellationToken.None));

        Assert.Contains("--environment-id env-2", failure.Message, StringComparison.Ordinal);
        Assert.Contains("(env-1)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("--mcs-clone-agent-id", failure.Message, StringComparison.Ordinal);
        Assert.Empty(copies.Prepared);
        AssertNothingLeftBehind(workspace.Root, store);
    }

    [Fact]
    public async Task Copy_RegistersARecordThatIsNotBoundToAnyCloudAgent()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(store, new FakeCopyPort()).CopyAsync(LocalRequest(), CancellationToken.None);

        var record = store.Read(workspace.Root).ManagedAgents.Single(agent => agent.AgentId == result.AgentId);
        Assert.Equal("Copy Agent", record.DisplayName);
        Assert.Equal("Copy Agent", record.AgentLocation);
        Assert.Null(record.McsAgentId);
        Assert.Equal(result.SchemaName, record.McsSchemaName);
        Assert.Equal("env-1", record.EnvironmentId);
    }

    [Fact]
    public async Task Copy_UsesTheSchemaNameProposedForItsStagedWorkspace()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort { ProposedSchemaName = "con_copyag_QwErTy" };

        var result = await Create(store, copies).CopyAsync(LocalRequest(), CancellationToken.None);

        var proposal = Assert.Single(copies.Proposals);
        var prepared = Assert.Single(copies.Prepared);
        Assert.Equal(prepared.WorkspaceDirectory, proposal.WorkspaceDirectory);
        Assert.Contains(Path.Combine("topics", "Greeting.mcs.yml"), proposal.StagedEntries);
        Assert.Equal("con_source", proposal.SourceSchemaName);
        Assert.Equal("Copy Agent", proposal.DisplayName);
        Assert.Equal("con_copyag_QwErTy", prepared.SchemaName);
        Assert.Equal("con_copyag_QwErTy", result.SchemaName);
    }

    [Fact]
    public async Task Copy_WhenNoGeneratedSchemaNameFits_AsksForASchemaNameAndLeavesOnlyTheProjectLockBehind()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort { ProposedSchemaName = null };

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store, copies).CopyAsync(LocalRequest(), CancellationToken.None));

        Assert.Contains("'Copy Agent'", failure.Message, StringComparison.Ordinal);
        Assert.EndsWith("Pass --schema-name.", failure.Message, StringComparison.Ordinal);
        Assert.Single(copies.Proposals);
        Assert.Empty(copies.Prepared);
        AssertOnlyTheProjectLockIsLeft(workspace.Root, store);
    }

    [Fact]
    public async Task Copy_WithANameOfFortyTwoCharacters_RegistersIt()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();
        var newName = "Copy Agent".PadRight(42, 'x');

        var result = await Create(store, copies).CopyAsync(LocalRequest() with { Name = newName }, CancellationToken.None);

        Assert.Equal(newName, result.DisplayName);
        Assert.Equal(newName, Assert.Single(copies.Prepared).DisplayName);
        Assert.Equal(newName, Assert.Single(copies.Proposals).DisplayName);
    }

    [Fact]
    public async Task Copy_WithANameLongerThanFortyTwoCharacters_IsRefusedBeforeReadingAnything()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();
        var catalog = new FakeCopilotCatalogPort();
        var (resolver, client) = ManagedAgentFixtures.TrackedResolver();
        var before = client.ListCalls;
        var newName = "Copy Agent".PadRight(43, 'x');

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store, copies, catalog: catalog, resolver: resolver).CopyAsync(LocalRequest() with { Name = newName }, CancellationToken.None));

        Assert.Equal("--agent-name is 43 characters long; Copilot Studio allows at most 42.", failure.Message);
        Assert.Equal(before, client.ListCalls);
        Assert.Empty(catalog.RequestedMaximumCounts);
        Assert.Empty(copies.Proposals);
        Assert.Empty(copies.Prepared);
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, newName)));
        AssertNothingLeftBehind(workspace.Root, store);
    }

    [Fact]
    public async Task Copy_WithAnAngleBracketInTheName_IsRefused()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store, copies).CopyAsync(LocalRequest() with { Name = "Copy <Agent>" }, CancellationToken.None));

        Assert.Equal("--agent-name contains < or >, which Copilot Studio does not allow.", failure.Message);
        Assert.Empty(copies.Prepared);
        AssertNothingLeftBehind(workspace.Root, store);
    }

    [Fact]
    public async Task Copy_WithAnExplicitSchemaName_UsesIt()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        var result = await Create(store, copies).CopyAsync(LocalRequest() with { SchemaName = "con_mine" }, CancellationToken.None);

        Assert.Equal("con_mine", result.SchemaName);
        Assert.Equal("con_mine", Assert.Single(copies.Prepared).SchemaName);
        Assert.Empty(copies.Proposals);
    }

    [Fact]
    public async Task Copy_SendsNothingToCopilotStudio()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var catalog = new FakeCopilotCatalogPort();

        await Create(store, new FakeCopyPort(), workspaces, catalog).CopyAsync(LocalRequest(), CancellationToken.None);

        Assert.Equal(0, workspaces.PushCalls);
        Assert.Equal(0, workspaces.PullCalls);
        Assert.Equal(0, workspaces.CloneCalls);
        Assert.Equal(0, catalog.DeleteCalls);
        Assert.Equal(0, catalog.PublishCalls);
    }

    [Fact]
    public async Task Copy_PrintsAShortSummaryAndLeavesTheRewritesAndDraftsToTheResult()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(store, new FakeCopyPort { PreparedDraftWorkflows = ["Copy Agent - Weather"] }).CopyAsync(LocalRequest(), CancellationToken.None);

        Assert.Equal(["Copy Agent - Weather"], result.DraftWorkflows);
        Assert.Equal(1, result.RewrittenFileCount);
        Assert.Equal([$"Copied con_source (local) into {result.SchemaName} (Copy Agent). Nothing was sent to Copilot Studio.", $"  agent id      {result.AgentId}", "  environment   env-1", $"  workspace     {result.WorkspaceDirectory}"], result.ToTextLines());
    }

    [Fact]
    public async Task Copy_WhenPreparationIsBlocked_LeavesOnlyTheProjectLockBehind()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort { PreparationBlockers = [new CopilotCopyFinding("leftover-source-identity", "Greeting.mcs.yml still names con_source.")] };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, copies).CopyAsync(LocalRequest(), CancellationToken.None));

        Assert.Equal("copy-not-prepared", failure.ErrorKind);
        Assert.Contains("still names con_source", failure.Message, StringComparison.Ordinal);
        AssertOnlyTheProjectLockIsLeft(workspace.Root, store);
    }

    [Fact]
    public async Task Copy_ThatFails_RemovesWhatItStagedBeforeReleasingTheProjectLock()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        foreach (var topic in Enumerable.Range(0, 300))
        {
            workspace.AddFile(Path.Combine("Source Agent", "topics", $"Topic{topic}.mcs.yml"), "kind: AdaptiveDialog\n");
        }

        Task<IReadOnlyList<string>>? observed = null;
        var copies = new FakeCopyPort { PreparationBlockers = [new CopilotCopyFinding("identity-taken", "The schema name is taken.")], OnPrepare = () => observed = Task.Run(() => CopyStateOnceTheProjectLockIsFree(workspace.Root)) };

        await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, copies).CopyAsync(LocalRequest(), CancellationToken.None));

        Assert.Equal([CopyProjectState.IgnoreFileName, CopyProjectState.LockFileName], await observed!);
    }

    [Fact]
    public async Task Copy_RemovesTheStagingOfAnEarlierCopyThatWasKilled()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var killed = PlantCopyState(workspace.Root, OtherCopyId, Path.Combine("staging", "Copy Agent", "topics", "Greeting.mcs.yml"), Path.Combine("source", ".mcs", "botdefinition.json"));

        await Create(store, new FakeCopyPort()).CopyAsync(LocalRequest(), CancellationToken.None);

        Assert.False(Directory.Exists(killed));
        Assert.Equal([CopyProjectState.IgnoreFileName, CopyProjectState.LockFileName], Entries(CopyProjectState.Root(workspace.Root)));
    }

    [Fact]
    public async Task Copy_RemovesTheStateEarlierCopiesLeftForRegisteredAndUnregisteredAgents()
    {
        var (workspace, store, sourceWorkspace) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Agent(RegisteredCopyId, "Earlier Copy") with { AgentLocation = "Earlier Copy" });
        PlantCopyState(workspace.Root, RegisteredCopyId, Path.Combine("source", ".mcs", "botdefinition.json"), CopyProjectState.LockFileName);
        PlantCopyState(workspace.Root, OtherCopyId, Path.Combine("operations", $"{Guid.NewGuid()}.json"), CopyProjectState.LockFileName);

        await Create(store, new FakeCopyPort()).CopyAsync(LocalRequest() with { Project = sourceWorkspace }, CancellationToken.None);

        Assert.Equal([CopyProjectState.IgnoreFileName, CopyProjectState.LockFileName], Entries(CopyProjectState.Root(workspace.Root)));
    }

    [Fact]
    public async Task Copy_ThatCannotRemoveALeftoverCopyState_StillCopies()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var leftover = PlantCopyState(workspace.Root, OtherCopyId, Path.Combine("source", ".mcs", "botdefinition.json"));
        using var held = new FileStream(Path.Combine(leftover, "source", ".mcs", "botdefinition.json"), FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await Create(store, new FakeCopyPort()).CopyAsync(LocalRequest(), CancellationToken.None);

        Assert.Contains(store.Read(workspace.Root).ManagedAgents, agent => agent.AgentId == result.AgentId);
        Assert.True(Directory.Exists(result.WorkspaceDirectory));
    }

    [Fact]
    public async Task Copy_IntoAProjectWhereAnotherCopyIsRunning_IsRefusedAndLeavesNothingBehind()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();
        using var running = CopyProjectState.TryLockProject(workspace.Root);
        var runningStaging = PlantCopyState(workspace.Root, OtherCopyId, Path.Combine("staging", "Other Copy", AgentConfigLocator.ProjectSettingsFileName));
        var before = Entries(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, copies).CopyAsync(LocalRequest(), CancellationToken.None));

        Assert.NotNull(running);
        Assert.Equal("copy-busy", failure.ErrorKind);
        Assert.Equal(ExitCodes.Failure, failure.ExitCode);
        Assert.Contains($"into {workspace.Root} is running", failure.Message, StringComparison.Ordinal);
        Assert.Empty(copies.Prepared);
        Assert.True(Directory.Exists(runningStaging));
        Assert.Equal(before, Entries(workspace.Root));
        Assert.Single(store.Read(workspace.Root).ManagedAgents);
    }

    [Fact]
    public async Task Copy_ReadsTheProjectConfigurationOnce()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent(RegisteredCopyId, "Earlier Copy") with { AgentLocation = "Earlier Copy" });
        PlantCopyState(workspace.Root, OtherCopyId, Path.Combine("staging", "Copy Agent", "topics", "Greeting.mcs.yml"));
        var agents = new CountingAgentRegistry(store);

        await Create(store, new FakeCopyPort(), CloningWithCloudCache(), CloudCatalog(), agents: agents).CopyAsync(CloudRequest(), CancellationToken.None);

        Assert.Equal(1, agents.Lists);
        Assert.False(Directory.Exists(CopyProjectState.AgentDirectory(workspace.Root, OtherCopyId)));
        Assert.Equal(2, store.Read(workspace.Root).ManagedAgents.Count);
    }

    [Fact]
    public async Task Copy_ReportsTheIdentitiesItsPreparationMapped()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort { PreparedIdentities = [new CopilotCopyIdentity(CopilotCopyIdentityKinds.Connector, OtherAgentId, SourceAgentId)] };

        var result = await Create(store, copies).CopyAsync(LocalRequest(), CancellationToken.None);

        Assert.Equal(copies.PreparedIdentities, result.Identities);
    }

    [Fact]
    public async Task Copy_UnderTheSourcesOwnName_IsRefused()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        await Assert.ThrowsAsync<UsageException>(() => Create(store, copies).CopyAsync(LocalRequest() with { Name = "source agent" }, CancellationToken.None));

        Assert.Empty(copies.Prepared);
    }

    [Fact]
    public async Task Copy_UnderTheSourcesOwnSchemaName_IsRefused()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        await Assert.ThrowsAsync<UsageException>(() => Create(store, copies).CopyAsync(LocalRequest() with { SchemaName = "con_source" }, CancellationToken.None));

        Assert.Empty(copies.Prepared);
    }

    [Fact]
    public async Task Copy_UnderANameAlreadyRegisteredInTheProject_IsRefused()
    {
        var (workspace, store, sourceWorkspace) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Agent("agent-2", "Copy Agent") with { AgentLocation = "Elsewhere" });
        var copies = new FakeCopyPort();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, copies).CopyAsync(LocalRequest() with { Project = sourceWorkspace }, CancellationToken.None));

        Assert.Equal("already-registered", failure.ErrorKind);
        Assert.Empty(copies.Prepared);
    }

    [Fact]
    public async Task Copy_OfALocalAgentIntoAFolderThatAlreadyHasContent_AsksOnlyForAnotherName()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        workspace.AddFile(Path.Combine("Copy Agent", "notes.txt"), "mine");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, new FakeCopyPort()).CopyAsync(LocalRequest(), CancellationToken.None));

        Assert.Equal("workspace-not-empty", failure.ErrorKind);
        Assert.Equal("Choose a different --agent-name.", failure.Remediation);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(workspace.Root, "Copy Agent", "notes.txt")));
    }

    [Fact]
    public async Task Copy_OfACloudAgentIntoAFolderThatAlreadyHasContent_AlsoOffersAnotherProject()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        workspace.AddFile(Path.Combine("Copy Agent", "notes.txt"), "mine");
        var workspaces = CloningWithCloudCache();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopyPort(), workspaces, CloudCatalog()).CopyAsync(CloudRequest(), CancellationToken.None));

        Assert.Equal("workspace-not-empty", failure.ErrorKind);
        Assert.Equal("Choose a different --agent-name, or a --project directory that does not hold a folder by that name.", failure.Remediation);
        Assert.Equal(0, workspaces.CloneCalls);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(workspace.Root, "Copy Agent", "notes.txt")));
    }

    [Fact]
    public async Task Copy_WithAProjectThatAnAgentFills_RegistersTheCopyInASiblingProject()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sourceProject = Path.Combine(workspace.Root, "source-project");
        var store = ManagedAgentFixtures.Store();
        workspace.AddFile(Path.Combine("source-project", AgentConfigLocator.ProjectSettingsFileName), SourceSettings);
        store.Add(sourceProject, ManagedAgentFixtures.Agent("agent-src", "Source Agent"));

        var result = await Create(store, new FakeCopyPort()).CopyAsync(LocalRequest() with { Project = sourceProject }, CancellationToken.None);

        Assert.Equal(Path.Combine(workspace.Root, "Copy Agent"), result.ProjectDirectory);
        Assert.Equal(Path.Combine(workspace.Root, "Copy Agent", "Copy Agent"), result.WorkspaceDirectory);
        Assert.Single(store.Read(result.ProjectDirectory).ManagedAgents);
        Assert.Single(store.Read(sourceProject).ManagedAgents);
    }

    [Theory]
    [InlineData(AgentTypes.McsAgent, "Source Agent.", "Source Agent")]
    [InlineData(AgentTypes.McsWorkflow, "source flow", "source flow")]
    public async Task Copy_UnderANameWhoseFolderIsTheSourcesOwn_IsRefusedBeforeAnythingIsWritten(string agentType, string newName, string copyFolder)
    {
        var (workspace, store, sourceWorkspace) = agentType == AgentTypes.McsWorkflow ? LocalWorkflowSource() : LocalSource();
        using var disposable = workspace;

        await AssertCopyIsRefusedForOverlappingTheSourceAsync(store, workspace.Root, sourceWorkspace, newName, copyFolder);
    }

    [Theory]
    [InlineData(AgentTypes.McsAgent, false)]
    [InlineData(AgentTypes.McsWorkflow, false)]
    [InlineData(AgentTypes.McsAgent, true)]
    [InlineData(AgentTypes.McsWorkflow, true)]
    public async Task Copy_UnderTheFolderNameOfTheProjectItsSourceFills_IsRefusedBeforeAnythingIsWritten(string agentType, bool reachedThroughALink)
    {
        var (workspace, store, project) = SourceThatFillsItsProject(agentType);
        using var disposable = workspace;

        if ((reachedThroughALink ? ThroughALinkToItsParent(project) : project) is not { } projectDirectory)
        {
            return;
        }

        await AssertCopyIsRefusedForOverlappingTheSourceAsync(store, projectDirectory, project, "source-project", "source-project");
    }

    [Theory]
    [InlineData(AgentTypes.McsAgent)]
    [InlineData(AgentTypes.McsWorkflow)]
    public async Task Copy_OfASourceThatFillsAProjectReachedThroughALink_LandsInTheSiblingProject(string agentType)
    {
        var (workspace, store, project) = SourceThatFillsItsProject(agentType);
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sourceFiles = Snapshot(project);

        if (ThroughALinkToItsParent(project) is not { } linkedProject)
        {
            return;
        }

        Assert.Equal(Path.Combine(workspace.Root, "projects-link", "Source Copy", "Source Copy"), (await Create(store, new FakeCopyPort()).CopyAsync(new ManagedAgentCopyRequest { Name = "Source Copy", Project = linkedProject }, CancellationToken.None)).WorkspaceDirectory);
        Assert.True(Directory.Exists(Path.Combine(workspace.Root, "projects", "Source Copy", "Source Copy")));
        Assert.Equal(sourceFiles, Snapshot(project));
    }

    [Theory]
    [InlineData("Other Agent", AgentTypes.McsAgent, "Other Agent")]
    [InlineData("workflows", AgentTypes.McsWorkflow, "Source Flow")]
    public async Task Copy_WithAProjectFolderReachedThroughALink_CopiesTheAgentOrWorkflowWhoseFolderHoldsIt(string folder, string sourceType, string sourceName)
    {
        var (workspace, store, project) = ProjectWithTwoAgents();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        WorkflowFixtures.Write(project, "Source Flow", workflowId: Guid.Parse(SourceWorkflowId));
        store.Add(project, WorkflowFixtures.Record(SourceWorkflowRecordId, "Source Flow", "env-1", SourceWorkflowId));

        if (ThroughALinkToItsParent(Path.Combine(project, folder)) is not { } linkedFolder)
        {
            return;
        }

        var result = await Create(store, new FakeCopyPort()).CopyAsync(LocalRequest() with { Project = linkedFolder }, CancellationToken.None);

        Assert.Equal(sourceType, result.AgentType);
        Assert.Equal(sourceName, result.SourceDisplayName);
    }

    [Fact]
    public async Task Copy_FromAnAgentFolderLinkedIntoAWorkflowsFolder_CopiesTheAgent()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        WorkflowFixtures.Write(Path.Combine(workspace.Root, "LongWorkflow"), "Source Flow", workflowId: Guid.Parse(SourceWorkflowId));
        workspace.AddFile(Path.Combine("LongWorkflow", "Agent", AgentConfigLocator.ProjectSettingsFileName), SourceSettings);
        store.Add(workspace.Root, WorkflowFixtures.Record(SourceWorkflowRecordId, "Source Flow", "env-1", SourceWorkflowId, "LongWorkflow"));
        store.Add(workspace.Root, ManagedAgentFixtures.Agent("agent-src", "Source Agent") with { AgentLocation = "a", McsAgentId = SourceAgentId });

        if (DirectoryLink(Path.Combine(workspace.Root, "a"), Path.Combine(workspace.Root, "LongWorkflow", "Agent")) is not { } agentFolder)
        {
            return;
        }

        var result = await Create(store, new FakeCopyPort()).CopyAsync(LocalRequest() with { Project = agentFolder }, CancellationToken.None);

        Assert.Equal(AgentTypes.McsAgent, result.AgentType);
        Assert.Equal("Source Agent", result.SourceDisplayName);
    }

    [Fact]
    public async Task Copy_UnderANameWhoseFolderHoldsTheSourceWorkflow_IsRefusedBeforeAnythingIsWritten()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        var store = ManagedAgentFixtures.Store();
        var sourceWorkspace = Path.Combine(workspace.Root, "Flows", "Source Flow");
        WorkflowFixtures.Write(sourceWorkspace, "Source Flow", workflowId: Guid.Parse(SourceWorkflowId), definition: SourceWorkflowDefinition);
        store.Add(workspace.Root, WorkflowFixtures.Record(SourceWorkflowRecordId, "Source Flow", "env-1", SourceWorkflowId, "Flows/Source Flow"));

        await AssertCopyIsRefusedForOverlappingTheSourceAsync(store, workspace.Root, sourceWorkspace, "Flows", "Flows");
    }

    [Theory]
    [InlineData(AgentTypes.McsAgent, "Source Agent 2")]
    [InlineData(AgentTypes.McsAgent, "Source")]
    [InlineData(AgentTypes.McsWorkflow, "Source Flow 2")]
    [InlineData(AgentTypes.McsWorkflow, "Source")]
    public async Task Copy_UnderANameWhoseFolderOnlySharesAPrefixWithTheSourcesOwn_CopiesBesideTheSource(string agentType, string newName)
    {
        var (workspace, store, _) = agentType == AgentTypes.McsWorkflow ? LocalWorkflowSource() : LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        Assert.Equal(Path.Combine(workspace.Root, newName), (await Create(store, new FakeCopyPort()).CopyAsync(new ManagedAgentCopyRequest { Name = newName }, CancellationToken.None)).WorkspaceDirectory);
    }

    [Fact]
    public async Task Copy_FromInsideAnAgentsFolder_CopiesThatAgentIntoItsProject()
    {
        var (workspace, store, project) = ProjectWithTwoAgents();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(Path.Combine(project, "Source Agent", "topics"));
        var copies = new FakeCopyPort();

        var result = await Create(store, copies).CopyAsync(LocalRequest(), CancellationToken.None);

        Assert.Equal("con_source", Assert.Single(copies.Prepared).SourceSchemaName);
        Assert.Equal(project, result.ProjectDirectory);
        Assert.Equal(Path.Combine(project, "Copy Agent"), result.WorkspaceDirectory);
        Assert.Equal(3, store.Read(project).ManagedAgents.Count);
    }

    [Fact]
    public async Task Copy_WithAProjectNamingAnAgentsFolder_CopiesThatAgentFromAnywhere()
    {
        var (workspace, store, project) = ProjectWithTwoAgents();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        var result = await Create(store, copies).CopyAsync(LocalRequest() with { Project = Path.Combine(project, "Other Agent") }, CancellationToken.None);

        Assert.Equal("con_other", Assert.Single(copies.Prepared).SourceSchemaName);
        Assert.Equal(project, result.ProjectDirectory);
    }

    [Fact]
    public async Task Copy_FromAProjectHoldingSeveralAgents_AsksForTheAgentsFolderAndWritesNothing()
    {
        var (workspace, store, project) = ProjectWithTwoAgents();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(project);
        var copies = new FakeCopyPort();
        var before = Entries(project);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, copies).CopyAsync(LocalRequest(), CancellationToken.None));

        Assert.Equal("agent-ambiguous", failure.ErrorKind);
        Assert.Equal($"The project {project} holds several agents: Source Agent ({Path.Combine(project, "Source Agent")}), Other Agent ({Path.Combine(project, "Other Agent")}).", failure.Message);
        Assert.Equal("Choose the agent with --project <its folder>.", failure.Remediation);
        Assert.Empty(copies.Prepared);
        Assert.Equal(before, Entries(project));
    }

    [Fact]
    public async Task Copy_FromAProjectHoldingOneAgentAndAWorkflow_CopiesTheAgent()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Agent("wf-1", "Flow") with { AgentType = AgentTypes.McsWorkflow, AgentLocation = "Flow" });
        var copies = new FakeCopyPort();

        await Create(store, copies).CopyAsync(LocalRequest(), CancellationToken.None);

        Assert.Equal("con_source", Assert.Single(copies.Prepared).SourceSchemaName);
    }

    [Fact]
    public async Task Copy_FromAProjectHoldingOnlyAWorkflow_CopiesTheWorkflow()
    {
        var (workspace, store, _) = LocalWorkflowSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        var result = await Create(store, copies).CopyAsync(LocalWorkflowRequest(), CancellationToken.None);

        Assert.Equal(Guid.Parse(SourceWorkflowId), Assert.Single(copies.Prepared).SourceWorkflowId);
        Assert.Equal(AgentTypes.McsWorkflow, result.AgentType);
        Assert.Equal(Path.Combine(workspace.Root, "Copy Flow"), result.WorkspaceDirectory);
    }

    [Fact]
    public async Task Copy_FromAProjectHoldingSeveralWorkflowsAndNoAgent_AsksForTheWorkflowsFolder()
    {
        var (workspace, store, sourceWorkspace) = LocalWorkflowSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var otherWorkspace = Path.Combine(workspace.Root, "Other Flow");
        store.Add(workspace.Root, WorkflowFixtures.Record("wf-other", "Other Flow", "env-1", WorkflowFixtures.Write(otherWorkspace, "Other Flow").ToString(), "Other Flow"));
        var copies = new FakeCopyPort();
        var before = Entries(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, copies).CopyAsync(LocalWorkflowRequest(), CancellationToken.None));

        Assert.Equal("agent-ambiguous", failure.ErrorKind);
        Assert.Equal($"The project {workspace.Root} holds several agents: Source Flow ({sourceWorkspace}), Other Flow ({otherWorkspace}).", failure.Message);
        Assert.Empty(copies.Prepared);
        Assert.Equal(before, Entries(workspace.Root));
    }

    [Fact]
    public async Task Copy_FromAProjectHoldingNoRecord_IsRefused()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var removed = store.Add(workspace.Root, WorkflowFixtures.Record("wf-gone", "Gone Flow", "env-1", SourceWorkflowId, "Gone Flow"));
        store.RemoveWithDeployments(workspace.Root, removed.AgentId, removed.EntityTag);
        var copies = new FakeCopyPort();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, copies).CopyAsync(LocalWorkflowRequest(), CancellationToken.None));

        Assert.Equal("agent-not-found", failure.ErrorKind);
        Assert.Empty(copies.Prepared);
        AssertNothingLeftBehind(workspace.Root, store, registeredAgents: 0, copyFolder: "Copy Flow");
    }

    [Fact]
    public async Task Copy_FromAProjectWhoseOnlyWorkflowsFolderIsGone_IsWorkspaceMissing()
    {
        var (workspace, store, sourceWorkspace) = LocalWorkflowSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        Directory.Delete(sourceWorkspace, recursive: true);
        var workflows = new FakeWorkflowStorePort();
        var copies = new FakeCopyPort();
        var before = Entries(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, copies, workflows: workflows).CopyAsync(LocalWorkflowRequest(), CancellationToken.None));

        Assert.Equal("workspace-missing", failure.ErrorKind);
        Assert.Equal($"Agent '{SourceWorkflowRecordId}' records its workspace at 'Source Flow', which holds no Copilot Studio project under {workspace.Root}.", failure.Message);
        Assert.Empty(workflows.Calls);
        Assert.Empty(copies.Prepared);
        Assert.Equal(before, Entries(workspace.Root));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Copy_WithAProjectNamingAWorkflowsFolderInAProjectWithAnAgent_CopiesTheWorkflow(bool namedByTheProjectOption)
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        var flowWorkspace = Path.Combine(workspace.Root, "Source Flow");
        var workflowId = WorkflowFixtures.Write(flowWorkspace, "Source Flow");
        store.Add(workspace.Root, WorkflowFixtures.Record(SourceWorkflowRecordId, "Source Flow", "env-1", workflowId.ToString(), "Source Flow"));
        using var scope = new CurrentDirectoryScope(namedByTheProjectOption ? workspace.Root : Path.Combine(flowWorkspace, "workflows"));
        var copies = new FakeCopyPort();

        var result = await Create(store, copies).CopyAsync(LocalWorkflowRequest() with { Project = namedByTheProjectOption ? flowWorkspace : null }, CancellationToken.None);

        Assert.Equal(workflowId, Assert.Single(copies.Prepared).SourceWorkflowId);
        Assert.Equal(AgentTypes.McsWorkflow, result.AgentType);
        Assert.Equal(workspace.Root, result.ProjectDirectory);
    }

    [Fact]
    public async Task Copy_FromTheRootOfAProjectAWorkflowFills_CopiesTheWorkflowAndNotTheAgentInASubfolder()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        var project = Path.Combine(workspace.Root, "project");
        var store = ManagedAgentFixtures.Store();
        var workflowId = WorkflowFixtures.Write(project, "Source Flow");
        workspace.AddFile(Path.Combine("project", "Source Agent", AgentConfigLocator.ProjectSettingsFileName), SourceSettings);
        workspace.AddFile(Path.Combine("project", "Source Agent", ".mcs", "conn.json"), "{}");
        store.Add(project, WorkflowFixtures.Record(SourceWorkflowRecordId, "Source Flow", "env-1", workflowId.ToString()));
        store.Add(project, ManagedAgentFixtures.Agent("agent-src", "Source Agent") with { AgentLocation = "Source Agent" });
        using var scope = new CurrentDirectoryScope(project);
        var copies = new FakeCopyPort();

        var result = await Create(store, copies).CopyAsync(LocalWorkflowRequest(), CancellationToken.None);

        var staged = Assert.Single(copies.StagedEntries);
        Assert.Equal(workflowId, Assert.Single(copies.Prepared).SourceWorkflowId);
        Assert.Equal(AgentTypes.McsWorkflow, result.AgentType);
        Assert.Equal(Path.Combine(workspace.Root, "Copy Flow"), result.ProjectDirectory);
        Assert.Equal(2, store.Read(project).ManagedAgents.Count);
        Assert.DoesNotContain(staged, entry => entry.Split(Path.DirectorySeparatorChar).Contains("Source Agent", StringComparer.Ordinal));
        Assert.DoesNotContain(staged, entry => entry.Split(Path.DirectorySeparatorChar).Contains(".mcs", StringComparer.Ordinal));
    }

    [Fact]
    public async Task Copy_OfAWorkflowAtTheRootOfAProject_StagesOnlyTheRootFilesAndTheWorkflowProjectFolders()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        var project = Path.Combine(workspace.Root, "project");
        var store = ManagedAgentFixtures.Store();
        var workflowId = WorkflowFixtures.Write(project, "Source Flow");
        workspace.AddFile(Path.Combine("project", "workflow.sync.yaml"), "workflowProjectVersion: 1\n");
        workspace.AddFile(Path.Combine("project", "README.md"), "# Source Flow\n");
        workspace.AddFile(Path.Combine("project", ".git"), "gitdir: ../.git/modules/project\n");
        workspace.AddFile(Path.Combine("project", "infrastructure", "connections", "new_teams.sync.yaml"), "connectionReferenceLogicalName: new_teams\n");
        workspace.AddFile(Path.Combine("project", ".mcs", "botdefinition.json"), "{}");
        workspace.AddFile(Path.Combine("project", "Source Agent", AgentConfigLocator.ProjectSettingsFileName), SourceSettings);
        workspace.AddFile(Path.Combine("project", "Source Agent", ".mcs", "conn.json"), "{}");
        workspace.AddFile(Path.Combine("project", "notes", "plan.txt"), "Unrelated to the workflow.\n");
        store.Add(project, WorkflowFixtures.Record(SourceWorkflowRecordId, "Source Flow", "env-1", workflowId.ToString()));
        store.Add(project, ManagedAgentFixtures.Agent("agent-src", "Source Agent") with { AgentLocation = "Source Agent" });
        using var scope = new CurrentDirectoryScope(project);
        var copies = new FakeCopyPort();

        await Create(store, copies).CopyAsync(LocalWorkflowRequest(), CancellationToken.None);

        var flowFolder = Path.Combine("workflows", $"SourceFlow-{workflowId}");
        Assert.Equal(
            [
                "README.md",
                "infrastructure",
                Path.Combine("infrastructure", "connections"),
                Path.Combine("infrastructure", "connections", "new_teams.sync.yaml"),
                "workflow.sync.yaml",
                "workflows",
                flowFolder,
                Path.Combine(flowFolder, "metadata.yml"),
                Path.Combine(flowFolder, "workflow.json"),
            ],
            [.. Assert.Single(copies.StagedEntries).Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task Copy_WithAProjectThatDoesNotExist_IsRefusedWithoutCopyingTheProjectAboveIt()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();
        var missing = Path.Combine(workspace.Root, "Missing Agent");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, copies).CopyAsync(LocalRequest() with { Project = missing }, CancellationToken.None));

        Assert.Equal("config-not-found", failure.ErrorKind);
        Assert.Contains(missing, failure.Message, StringComparison.Ordinal);
        Assert.Empty(copies.Prepared);
        AssertNothingLeftBehind(workspace.Root, store);
    }

    [Fact]
    public async Task Copy_OfAnAgentThatFillsItsProject_RegistersTheCopyInASiblingProject()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        var sourceProject = Path.Combine(workspace.Root, "source-project");
        var store = ManagedAgentFixtures.Store();
        workspace.AddFile(Path.Combine("source-project", AgentConfigLocator.ProjectSettingsFileName), SourceSettings);
        store.Add(sourceProject, ManagedAgentFixtures.Agent("agent-src", "Source Agent"));
        using var scope = new CurrentDirectoryScope(sourceProject);

        var result = await Create(store, new FakeCopyPort()).CopyAsync(LocalRequest(), CancellationToken.None);

        Assert.Equal(Path.Combine(workspace.Root, "Copy Agent"), result.ProjectDirectory);
        Assert.Single(store.Read(sourceProject).ManagedAgents);
    }

    [Fact]
    public async Task Copy_OfACloudSource_StagesItAndReadsItsCloudDefinitionWithoutKeepingItsBinding()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var copies = new FakeCopyPort();
        var workspaces = CloningWithCloudCache();

        var result = await Create(store, copies, workspaces, CloudCatalog()).CopyAsync(CloudRequest(), CancellationToken.None);

        Assert.Equal(SourceAgentId, workspaces.LastCloneCommand!.AgentId);
        Assert.False(workspaces.LastCloneCommand.IncludeLinkedComponentCollections);
        Assert.False(Directory.Exists(Path.Combine(result.WorkspaceDirectory, ".mcs")));
        Assert.Equal("{}", Assert.Single(copies.SourceDefinitions));
        Assert.False(Directory.Exists(CopyProjectState.AgentDirectory(workspace.Root, result.AgentId)));
        Assert.Equal(0, workspaces.PushCalls);
    }

    [Fact]
    public async Task Copy_OfACloudSource_RecordsItsProvenance()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var copies = new FakeCopyPort();

        var result = await Create(store, copies, CloningWithCloudCache(), CloudCatalog()).CopyAsync(CloudRequest(), CancellationToken.None);

        Assert.Equal("con_source", Assert.Single(copies.Prepared).SourceSchemaName);
        Assert.Equal(CopilotCopySourceKinds.Cloud, result.Source);
        Assert.Equal(SourceAgentId, result.SourceMcsAgentId);
        Assert.Null(store.Read(workspace.Root).ManagedAgents.Single().McsAgentId);
    }

    [Fact]
    public async Task Copy_OfACloudAgent_MatchesItsIdWithoutSurroundingSpace()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = CloudCatalog();

        var result = await Create(ManagedAgentFixtures.Store(), new FakeCopyPort(), CloningWithCloudCache(), catalog).CopyAsync(CloudRequest() with { CloneAgentId = $"  {SourceAgentId} " }, CancellationToken.None);

        Assert.Equal(SourceAgentId, result.SourceMcsAgentId);
        Assert.Equal(0, catalog.GetAgentCalls);
    }

    [Fact]
    public async Task Copy_OfACloudAgentTheListLeavesOut_ReadsItById()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();
        var catalog = CloudCatalog();

        var result = await Create(ManagedAgentFixtures.Store(), copies, CloningWithCloudCache(), catalog).CopyAsync(CloudRequest() with { CloneAgentId = OtherAgentId }, CancellationToken.None);

        Assert.Equal(OtherAgentId, result.SourceMcsAgentId);
        Assert.Equal(catalog.SchemaNameReturns, Assert.Single(copies.Prepared).SourceSchemaName);
        Assert.Equal(1, catalog.GetAgentCalls);
    }

    [Fact]
    public async Task Copy_OutsideAnyProject_IsRefusedWithoutLookingInTheCloud()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = CloningWithCloudCache();
        var catalog = CloudCatalog();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopyPort(), workspaces, catalog).CopyAsync(LocalRequest(), CancellationToken.None));

        Assert.Equal("config-not-found", failure.ErrorKind);
        Assert.Empty(catalog.RequestedMaximumCounts);
        Assert.Equal(0, catalog.GetAgentCalls);
        Assert.Equal(0, workspaces.CloneCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Root));
    }

    [Fact]
    public async Task Copy_WithAnInvalidSchemaNameOutsideAnyProject_IsRefusedForTheSchemaNameBeforeReadingAnything()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();
        var catalog = new FakeCopilotCatalogPort();
        var workflows = new FakeWorkflowStorePort();
        var (resolver, client) = ManagedAgentFixtures.TrackedResolver();
        var before = client.ListCalls;

        Assert.Equal("The schema name 'con copy' contains unsupported characters. Use letters, digits, and the characters _ - . { } !", (await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), copies, catalog: catalog, resolver: resolver, workflows: workflows).CopyAsync(LocalRequest() with { SchemaName = "con copy" }, CancellationToken.None))).Message);
        Assert.Equal(before, client.ListCalls);
        Assert.Empty(catalog.RequestedMaximumCounts);
        Assert.Empty(workflows.Calls);
        Assert.Empty(copies.Prepared);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Root));
    }

    [Fact]
    public async Task Copy_OfACloudAgentGivenAManagedRecordId_PointsAtTheLocalRecord()
    {
        var (workspace, store, sourceWorkspace) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = CloningWithCloudCache();

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store, new FakeCopyPort(), workspaces, CloudCatalog()).CopyAsync(CloudRequest() with { CloneAgentId = "agent-src" }, CancellationToken.None));

        Assert.Contains("--mcs-clone-agent-id agent-src is the managed-agent record 'Source Agent'", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"Copy that record without --mcs-clone-agent-id, passing its folder {sourceWorkspace} to --project", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"pass its cloud agent id {SourceAgentId} to --mcs-clone-agent-id", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, workspaces.CloneCalls);
    }

    [Fact]
    public async Task Copy_OfACloudAgentWithAProject_RegistersTheCopyInThatProject()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var destination = Path.Combine(workspace.Root, "destination");

        var result = await Create(store, new FakeCopyPort(), CloningWithCloudCache(), CloudCatalog()).CopyAsync(CloudRequest() with { Project = destination }, CancellationToken.None);

        Assert.Equal(destination, result.ProjectDirectory);
        Assert.Equal(Path.Combine(destination, "Copy Agent"), result.WorkspaceDirectory);
        Assert.Single(store.Read(destination).ManagedAgents);
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "Copy Agent")));
    }

    [Theory]
    [InlineData("Copy Agent")]
    [InlineData("COPY AGENT")]
    public async Task Copy_OfACloudAgentUnderANameAnotherAgentInTheEnvironmentHas_IsRefusedBeforeAnythingIsWritten(string takenName)
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = CloningWithCloudCache();
        var copies = new FakeCopyPort();
        var catalog = CloudCatalog(new CopilotAgentSummary { AgentId = OtherAgentId, SchemaName = "con_other", DisplayName = takenName });

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), copies, workspaces, catalog).CopyAsync(CloudRequest(), CancellationToken.None));

        Assert.Equal($"--agent-name 'Copy Agent' is taken: environment env-1 already has an agent named '{takenName}' (con_other). Choose another --agent-name.", failure.Message);
        Assert.Equal(0, workspaces.CloneCalls);
        Assert.Empty(copies.Prepared);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Root));
    }

    [Fact]
    public async Task Copy_OfALocalSourceUnderANameAnotherAgentInTheEnvironmentHas_IsRefusedAndLeavesNothingBehind()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();
        var catalog = CloudCatalog(new CopilotAgentSummary { AgentId = OtherAgentId, SchemaName = "con_other", DisplayName = "Copy Agent" });

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store, copies, catalog: catalog).CopyAsync(LocalRequest(), CancellationToken.None));

        Assert.Equal("--agent-name 'Copy Agent' is taken: environment env-1 already has an agent named 'Copy Agent' (con_other). Choose another --agent-name.", failure.Message);
        Assert.Empty(copies.Prepared);
        AssertNothingLeftBehind(workspace.Root, store);
    }

    [Fact]
    public async Task Copy_OfACloudAgentUnderItsOwnName_IsRefusedAsTheSourcesOwnName()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopyPort(), CloningWithCloudCache(), CloudCatalog()).CopyAsync(CloudRequest() with { Name = "source agent" }, CancellationToken.None));

        Assert.Equal("--agent-name 'source agent' is the source agent's own name. A copy needs a name of its own.", failure.Message);
    }

    [Fact]
    public async Task Copy_ReportsThePreparationsNoticesAsWarnings()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort { PreparedNotices = [new CopilotCopyFinding("workflow-draft", "The flow Weather is copied as a draft.", "workflows/Weather/metadata.yml"), new CopilotCopyFinding("knowledge-unverified", "The knowledge source Handbook was not checked.")] };

        var result = await Create(store, copies).CopyAsync(LocalRequest(), CancellationToken.None);

        Assert.Equal(["The flow Weather is copied as a draft. (workflows/Weather/metadata.yml)", "The knowledge source Handbook was not checked."], result.Warnings);
    }

    [Fact]
    public async Task Copy_OfAnAgent_ReportsTheAgentTypeAndNoWorkflowFields()
    {
        var (workspace, store, _) = LocalSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        var result = await Create(store, copies).CopyAsync(LocalRequest(), CancellationToken.None);

        Assert.Equal(AgentTypes.McsAgent, result.AgentType);
        Assert.Equal(AgentTypes.McsAgent, Assert.Single(copies.Prepared).AgentType);
        Assert.Null(Assert.Single(copies.Prepared).SourceWorkflowId);
        Assert.Null(result.McsWorkflowId);
        Assert.Null(result.WorkflowName);
        Assert.Null(result.WorkflowFolder);
        Assert.Null(result.WorkflowState);
        Assert.Null(result.SourceMcsWorkflowId);
    }

    [Fact]
    public async Task Copy_OfALocalWorkflow_PreparesAWorkflowAndRegistersAnMcsWorkflowRecord()
    {
        var (workspace, store, _) = LocalWorkflowSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        var result = await Create(store, copies).CopyAsync(LocalWorkflowRequest(), CancellationToken.None);

        var prepared = Assert.Single(copies.Prepared);
        Assert.Equal(AgentTypes.McsWorkflow, prepared.AgentType);
        Assert.Equal(Guid.Parse(SourceWorkflowId), prepared.SourceWorkflowId);
        Assert.Null(prepared.SchemaName);
        Assert.Null(prepared.SourceSchemaName);
        Assert.Null(prepared.SourceDefinitionPath);
        Assert.Equal("Copy Flow", prepared.DisplayName);
        Assert.Equal("env-1", prepared.Environment?.EnvironmentId);
        Assert.False(prepared.CapturedInTargetEnvironment);
        Assert.Empty(copies.Proposals);
        var record = store.Read(workspace.Root).ManagedAgents.Single(agent => agent.AgentId == result.AgentId);
        Assert.Equal(AgentTypes.McsWorkflow, record.AgentType);
        Assert.Equal(copies.CopiedWorkflowId.ToString(), record.McsWorkflowId);
        Assert.Equal("Copy Flow", record.DisplayName);
        Assert.Equal("Copy Flow", record.AgentLocation);
        Assert.Null(record.McsSchemaName);
        Assert.Null(record.McsAgentId);
        Assert.Equal("env-1", record.EnvironmentId);
        Assert.Equal(EnvironmentFixtures.ContosoTenant, record.TenantId);
        Assert.Empty(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Copy_OfALocalWorkflow_StagesWithoutGitConfigOrCloudCacheAndCopiesNothingAside()
    {
        var (workspace, store, _) = LocalWorkflowSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        workspace.AddFile(Path.Combine("Source Flow", "prompts", "Summarize", "metadata.yml"), "name: Summarize\n");
        workspace.AddFile(Path.Combine("Source Flow", "connectors", "Weather", "metadata.yml"), "{}");
        workspace.AddFile(Path.Combine("Source Flow", "docs", "notes.md"), "# Notes\n");
        IReadOnlyList<string>? copyState = null;
        var copies = new FakeCopyPort { OnPrepare = () => copyState = CopyStateFolders(workspace.Root) };

        var result = await Create(store, copies).CopyAsync(LocalWorkflowRequest(), CancellationToken.None);

        var sourceFolder = Path.Combine("workflows", $"SourceFlow-{SourceWorkflowId}");
        Assert.Equal(
            [
                "connectors",
                Path.Combine("connectors", "Weather"),
                Path.Combine("connectors", "Weather", "metadata.yml"),
                "docs",
                Path.Combine("docs", "notes.md"),
                "prompts",
                Path.Combine("prompts", "Summarize"),
                Path.Combine("prompts", "Summarize", "metadata.yml"),
                "workflows",
                sourceFolder,
                Path.Combine(sourceFolder, "metadata.yml"),
                Path.Combine(sourceFolder, "workflow.json"),
            ],
            [.. Assert.Single(copies.StagedEntries).Order(StringComparer.Ordinal)]);
        Assert.Equal(["staging"], copyState);
        Assert.Null(Assert.Single(copies.SourceDefinitions));
        Assert.False(Directory.Exists(Path.Combine(result.WorkspaceDirectory, ".git")));
        Assert.False(Directory.Exists(Path.Combine(result.WorkspaceDirectory, ".config")));
        Assert.False(Directory.Exists(Path.Combine(result.WorkspaceDirectory, ".mcs")));
        Assert.False(Directory.Exists(CopyProjectState.AgentDirectory(workspace.Root, result.AgentId)));
    }

    [Fact]
    public async Task Copy_OfALocalWorkflow_LeavesTheSourceUnchanged()
    {
        var (workspace, store, sourceWorkspace) = LocalWorkflowSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var before = Snapshot(sourceWorkspace);
        var sourceRecord = store.Read(workspace.Root).ManagedAgents[0];

        await Create(store, new FakeCopyPort()).CopyAsync(LocalWorkflowRequest(), CancellationToken.None);

        Assert.Equal(before, Snapshot(sourceWorkspace));
        Assert.Equal(sourceRecord, store.Read(workspace.Root).ManagedAgents.Single(agent => agent.AgentId == SourceWorkflowRecordId));
    }

    [Fact]
    public async Task Copy_OfAWorkflow_ReportsTheWorkflowFieldsAndPrintsTheWorkflowSummary()
    {
        var (workspace, store, _) = LocalWorkflowSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort { PreparedNotices = [new CopilotCopyFinding("workflow-draft", "The copy is created as a draft.")] };

        var result = await Create(store, copies).CopyAsync(LocalWorkflowRequest(), CancellationToken.None);

        var workflowId = copies.CopiedWorkflowId.ToString();
        Assert.Equal("agent-copied", result.Status);
        Assert.Equal(AgentTypes.McsWorkflow, result.AgentType);
        Assert.Equal("Copy Flow", result.DisplayName);
        Assert.Null(result.SchemaName);
        Assert.Null(result.SourceSchemaName);
        Assert.Equal(workflowId, result.McsWorkflowId);
        Assert.Equal("Copy Flow", result.WorkflowName);
        Assert.Equal(Path.Combine(workspace.Root, "Copy Flow", "workflows", $"CopyFlow-{workflowId}"), result.WorkflowFolder);
        Assert.Equal(WorkflowStateProjection.NotDeployed, result.WorkflowState);
        Assert.Equal(CopilotCopySourceKinds.Local, result.Source);
        Assert.Equal("Source Flow", result.SourceDisplayName);
        Assert.Equal(SourceWorkflowId, result.SourceMcsWorkflowId);
        Assert.Null(result.SourceMcsAgentId);
        Assert.Equal(workspace.Root, result.ProjectDirectory);
        Assert.Equal(Path.Combine(workspace.Root, "Copy Flow"), result.WorkspaceDirectory);
        Assert.Equal(["The copy is created as a draft."], result.Warnings);
        Assert.Equal(["Copied workflow 'Source Flow' (local) into 'Copy Flow'. Nothing was sent to Copilot Studio.", $"  agent id      {result.AgentId}", $"  workflow id   {workflowId}", "  environment   env-1", $"  workspace     {result.WorkspaceDirectory}", "  warning       The copy is created as a draft."], result.ToTextLines());
    }

    [Fact]
    public async Task Copy_OfALocalWorkflowWithASchemaName_IsRefusedBeforeAnyCloudRead()
    {
        var (workspace, store, _) = LocalWorkflowSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();
        var workflows = new FakeWorkflowStorePort();
        var (resolver, client) = ManagedAgentFixtures.TrackedResolver();
        var before = client.ListCalls;

        Assert.Equal("--schema-name names an agent's schema; a workflow has none. Remove --schema-name.", (await Assert.ThrowsAsync<UsageException>(() => Create(store, copies, resolver: resolver, workflows: workflows).CopyAsync(LocalWorkflowRequest() with { SchemaName = "con_mine" }, CancellationToken.None))).Message);
        Assert.Equal(before, client.ListCalls);
        Assert.Empty(workflows.Calls);
        Assert.Empty(copies.Prepared);
        AssertNothingLeftBehind(workspace.Root, store, copyFolder: "Copy Flow");
    }

    [Fact]
    public async Task Copy_OfALocalWorkflowAskedForAnotherEnvironment_NamesTheWorkflowOption()
    {
        var (workspace, store, _) = LocalWorkflowSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        Assert.Equal("--environment-id env-2 is not the environment the local source belongs to (env-1). Remove --environment-id, or copy the workflow as it is in that environment with --mcs-clone-workflow-id.", (await Assert.ThrowsAsync<UsageException>(() => Create(store, copies).CopyAsync(LocalWorkflowRequest() with { EnvironmentId = "env-2" }, CancellationToken.None))).Message);
        Assert.Empty(copies.Prepared);
        AssertNothingLeftBehind(workspace.Root, store, copyFolder: "Copy Flow");
    }

    [Fact]
    public async Task Copy_OfALocalWorkflowRecordWithoutAWorkflowId_IsRefused()
    {
        var (workspace, store, _) = LocalWorkflowSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Update(workspace.Root, store.Read(workspace.Root).ManagedAgents[0] with { McsWorkflowId = null });
        var copies = new FakeCopyPort();

        Assert.Equal("workflow-id-missing", (await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, copies).CopyAsync(LocalWorkflowRequest(), CancellationToken.None))).ErrorKind);
        Assert.Empty(copies.Prepared);
        AssertNothingLeftBehind(workspace.Root, store, copyFolder: "Copy Flow");
    }

    [Fact]
    public async Task Copy_OfAWorkflowWithANameOfAHundredCharacters_RegistersIt()
    {
        var (workspace, store, _) = LocalWorkflowSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();
        var newName = "Copy Flow".PadRight(100, 'x');

        var result = await Create(store, copies).CopyAsync(LocalWorkflowRequest() with { Name = newName }, CancellationToken.None);

        Assert.Equal(newName, result.DisplayName);
        Assert.Equal(newName, Assert.Single(copies.Prepared).DisplayName);
        Assert.Equal(newName, store.Read(workspace.Root).ManagedAgents.Single(agent => agent.AgentId == result.AgentId).DisplayName);
    }

    public static TheoryData<string> NamesTheWorkflowRuleRefuses => new() { "Copy Flow".PadRight(101, 'x'), "Copy\tFlow" };

    [Theory]
    [MemberData(nameof(NamesTheWorkflowRuleRefuses))]
    public async Task Copy_OfAWorkflowWithANameTheWorkflowRuleRefuses_IsRefusedBeforeAnyCloudRead(string newName)
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workflows = CloudWorkflows();
        var (resolver, client) = ManagedAgentFixtures.TrackedResolver();
        var before = client.ListCalls;

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopyPort(), resolver: resolver, workflows: workflows).CopyAsync(CloudWorkflowRequest() with { Name = newName }, CancellationToken.None));

        Assert.Equal("workflow-name-invalid", failure.ErrorKind);
        Assert.Equal(new FakeWorkflowScaffoldPort().ValidateName(newName), failure.Message);
        Assert.Equal(before, client.ListCalls);
        Assert.Empty(workflows.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Root));
    }

    [Fact]
    public async Task Copy_OfALocalWorkflow_ReadsNothingInTheCloudBeforeStaging()
    {
        var (workspace, store, _) = LocalWorkflowSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workflows = new FakeWorkflowStorePort();
        var catalog = new FakeCopilotCatalogPort();
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var (resolver, client) = ManagedAgentFixtures.TrackedResolver();
        var before = client.ListCalls;
        var environmentReads = -1;

        await Create(store, new FakeCopyPort { OnPrepare = () => environmentReads = client.ListCalls - before }, workspaces, catalog, resolver: resolver, workflows: workflows).CopyAsync(LocalWorkflowRequest(), CancellationToken.None);

        Assert.Equal(1, environmentReads);
        Assert.Equal([$"read-local:{SourceWorkflowId}"], workflows.Calls);
        Assert.Empty(catalog.RequestedMaximumCounts);
        Assert.Equal(0, catalog.GetAgentCalls);
        Assert.Equal(0, workspaces.CloneCalls);
    }

    [Fact]
    public async Task Copy_OfACloudWorkflow_DownloadsItOnceIntoStagingAndKeepsNoCloudCache()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workflows = CloudWorkflows();
        IReadOnlyList<string>? copyState = null;
        var copies = new FakeCopyPort { OnPrepare = () => copyState = CopyStateFolders(workspace.Root) };

        var result = await Create(ManagedAgentFixtures.Store(), copies, workflows: workflows).CopyAsync(CloudWorkflowRequest(), CancellationToken.None);

        var prepared = Assert.Single(copies.Prepared);
        var staged = Assert.Single(copies.StagedEntries);
        Assert.Equal([$"download:{SourceWorkflowId}"], workflows.Calls);
        Assert.StartsWith(CopyProjectState.Root(workspace.Root), prepared.WorkspaceDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine("workflows", $"SourceFlow-{SourceWorkflowId}", "workflow.json"), staged);
        Assert.DoesNotContain(staged, entry => entry.StartsWith(".mcs", StringComparison.Ordinal));
        Assert.Equal(["source", "staging"], copyState);
        Assert.Null(prepared.SourceDefinitionPath);
        Assert.False(Directory.Exists(Path.Combine(result.WorkspaceDirectory, ".mcs")));
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, ".mcs")));
        Assert.False(Directory.Exists(CopyProjectState.AgentDirectory(workspace.Root, result.AgentId)));
    }

    [Fact]
    public async Task Copy_OfACloudWorkflow_RecordsItsProvenanceAndTheDownloadsWarnings()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var workflows = CloudWorkflows("'Source Flow' also has a draft in env-1 that was saved in the designer but not published, so only the published version was written.");
        var copies = new FakeCopyPort { PreparedNotices = [new CopilotCopyFinding("workflow-draft", "The copy is created as a draft.")] };

        var result = await Create(store, copies, workflows: workflows).CopyAsync(CloudWorkflowRequest(), CancellationToken.None);

        Assert.Equal(CopilotCopySourceKinds.Cloud, result.Source);
        Assert.Equal(SourceWorkflowId, result.SourceMcsWorkflowId);
        Assert.Equal("Source Flow", result.SourceDisplayName);
        Assert.Null(result.SourceMcsAgentId);
        Assert.Equal([.. workflows.WarningsOnDownload, "The copy is created as a draft."], result.Warnings);
        Assert.True(Assert.Single(copies.Prepared).CapturedInTargetEnvironment);
        var record = Assert.Single(store.Read(workspace.Root).ManagedAgents);
        Assert.Equal(copies.CopiedWorkflowId.ToString(), record.McsWorkflowId);
        Assert.Equal("Approves expenses", record.Description);
    }

    [Theory]
    [InlineData("not-a-workflow")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Copy_OfACloudWorkflowWithAnIdThatIsNotAGuid_IsRefusedBeforeReadingAnything(string cloneWorkflowId)
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workflows = CloudWorkflows();
        var (resolver, client) = ManagedAgentFixtures.TrackedResolver();
        var before = client.ListCalls;

        Assert.Equal($"--mcs-clone-workflow-id '{cloneWorkflowId}' is not a workflow id.", (await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopyPort(), resolver: resolver, workflows: workflows).CopyAsync(CloudWorkflowRequest() with { CloneWorkflowId = cloneWorkflowId }, CancellationToken.None))).Message);
        Assert.Equal(before, client.ListCalls);
        Assert.Empty(workflows.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Root));
    }

    [Fact]
    public async Task Copy_OfACloudWorkflowGivenAManagedRecordId_PointsAtTheLocalRecord()
    {
        var (workspace, store, sourceWorkspace) = LocalWorkflowSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workflows = CloudWorkflows();

        Assert.Equal($"--mcs-clone-workflow-id {SourceWorkflowRecordId} is the managed-agent record 'Source Flow' in {workspace.Root}, not a cloud workflow. Copy that record without --mcs-clone-workflow-id, passing its folder {sourceWorkspace} to --project, or pass its cloud workflow id {SourceWorkflowId} to --mcs-clone-workflow-id.", (await Assert.ThrowsAsync<UsageException>(() => Create(store, new FakeCopyPort(), workflows: workflows).CopyAsync(CloudWorkflowRequest() with { CloneWorkflowId = SourceWorkflowRecordId }, CancellationToken.None))).Message);
        Assert.Empty(workflows.Calls);
        AssertNothingLeftBehind(workspace.Root, store, copyFolder: "Copy Flow");
    }

    public static TheoryData<ManagedAgentException> DownloadRefusals => new()
    {
        ManagedAgentException.WorkflowSourceNotFound(SourceWorkflowId, "env-1"),
        ManagedAgentException.WorkflowManaged($"The workflow {SourceWorkflowId} belongs to a managed solution in env-1."),
    };

    [Theory]
    [MemberData(nameof(DownloadRefusals))]
    public async Task Copy_OfACloudWorkflowTheDownloadRefuses_LeavesOnlyTheProjectLock(ManagedAgentException refusal)
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var workflows = CloudWorkflows();
        workflows.DownloadThrows = refusal;
        var copies = new FakeCopyPort();

        Assert.Same(refusal, await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, copies, workflows: workflows).CopyAsync(CloudWorkflowRequest(), CancellationToken.None)));

        Assert.Equal([$"download:{SourceWorkflowId}"], workflows.Calls);
        Assert.Empty(copies.Prepared);
        AssertOnlyTheProjectLockIsLeft(workspace.Root, store, registeredAgents: 0, copyFolder: "Copy Flow");
    }

    [Fact]
    public async Task Copy_OfACloudWorkflowWithAProject_RegistersTheCopyInThatProject()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var destination = Path.Combine(workspace.Root, "destination");

        var result = await Create(store, new FakeCopyPort(), workflows: CloudWorkflows()).CopyAsync(CloudWorkflowRequest() with { Project = destination }, CancellationToken.None);

        Assert.Equal(destination, result.ProjectDirectory);
        Assert.Equal(Path.Combine(destination, "Copy Flow"), result.WorkspaceDirectory);
        Assert.Equal(AgentTypes.McsWorkflow, Assert.Single(store.Read(destination).ManagedAgents).AgentType);
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "Copy Flow")));
    }

    [Fact]
    public async Task Copy_OfAWorkflowWhenPreparationIsBlocked_LeavesOnlyTheProjectLockBehind()
    {
        var (workspace, store, _) = LocalWorkflowSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, new FakeCopyPort { PreparationBlockers = [new CopilotCopyFinding("identity-taken", "The environment already has a workflow named 'Copy Flow' (e5e5e5e5-1111-2222-3333-444444444444). Copy the workflow again under another name.")] }).CopyAsync(LocalWorkflowRequest(), CancellationToken.None));

        Assert.Equal("copy-not-prepared", failure.ErrorKind);
        Assert.Equal("The copy 'Copy Flow' could not be prepared: The environment already has a workflow named 'Copy Flow' (e5e5e5e5-1111-2222-3333-444444444444). Copy the workflow again under another name.", failure.Message);
        AssertOnlyTheProjectLockIsLeft(workspace.Root, store, copyFolder: "Copy Flow");
    }

    [Fact]
    public async Task Copy_RegistersARecordTheWorkflowCommandsResolve()
    {
        var (workspace, store, _) = LocalWorkflowSource();
        using var disposable = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var copies = new FakeCopyPort();

        var result = await Create(store, copies).CopyAsync(LocalWorkflowRequest(), CancellationToken.None);

        var target = new WorkflowProjectResolver(ManagedAgentFixtures.AgentResolver(store), new AgentConfigLocator(), ManagedAgentFixtures.Resolver(), new FakeWorkflowWorkspacePort()).Resolve(workspace.Root, null, "Copy Flow", "msagent agent deploy");

        Assert.Equal(result.AgentId, target.Agent.AgentId);
        Assert.Equal(result.WorkspaceDirectory, target.WorkspaceRoot);
        Assert.Equal(copies.CopiedWorkflowId, target.WorkflowId);
        Assert.Equal(result.WorkflowFolder, target.Folder?.FolderPath);
    }

    private sealed class CountingAgentRegistry(IManagedAgentRegistry inner) : IManagedAgentRegistry
    {
        public int Lists { get; private set; }

        public IReadOnlyList<ManagedAgentRecord> List(string projectDirectory)
        {
            Lists++;

            return inner.List(projectDirectory);
        }

        public ManagedAgentRecord Add(string projectDirectory, ManagedAgentRecord record) => inner.Add(projectDirectory, record);

        public ManagedAgentRecord Update(string projectDirectory, ManagedAgentRecord record) => inner.Update(projectDirectory, record);

        public int RemoveWithDeployments(string projectDirectory, string agentId, string expectedETag) => inner.RemoveWithDeployments(projectDirectory, agentId, expectedETag);
    }
}
