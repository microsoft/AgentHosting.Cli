using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

[Collection("CurrentDirectory")]
public sealed class ManagedAgentComponentCollectionTests
{
    private const string AgentFolderName = "Contoso Agent";

    private const string CollectionFolderName = "Shared Tools";

    private static ManagedAgentDeployService CreateDeploy(JsonAgentConfigStore store, FakeCopilotWorkspacePort workspaces)
    {
        var environments = ManagedAgentFixtures.Resolver();
        var locator = CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem());
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "agent-1" };

        return new ManagedAgentDeployService(
            ManagedAgentFixtures.AgentResolver(store),
            store,
            store,
            environments,
            locator,
            new CopilotWorkspaceService(workspaces, locator),
            new SyncComponentCollectionAdapter(),
            ManagedAgentFixtures.Identities(),
            new RecordingAttachPort { OnAttach = () => workspaces.WorkspaceExists = true },
            catalog,
            new CopilotCatalogService(catalog, new FakeCopilotPublishPort(), environments, locator),
            ManagedAgentFixtures.Principals(),
            ManagedAgentFixtures.ActivationReporter(),
            new FixedClock(ManagedAgentFixtures.Now));
    }

    private static ManagedAgentPullService CreatePull(JsonAgentConfigStore store, FakeCopilotWorkspacePort workspaces)
    {
        var locator = CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem());

        return new ManagedAgentPullService(
            ManagedAgentFixtures.AgentResolver(store),
            locator,
            new CopilotWorkspaceService(workspaces, locator),
            new SyncComponentCollectionAdapter(),
            ManagedAgentFixtures.Resolver());
    }

    private static (TemporaryWorkspace Workspace, JsonAgentConfigStore Store) NestedProject()
    {
        var workspace = new TemporaryWorkspace(withProjectFile: false);
        workspace.AddFile(Path.Combine(AgentFolderName, WorkspaceFileNames.Settings), "displayName: \"Contoso Agent\"\nschemaName: \"con_agent\"\n");
        workspace.AddFile(Path.Combine(AgentFolderName, WorkspaceFileNames.References), $"componentCollections:\n  - schemaName:\n    directory: ../{CollectionFolderName}/\n");
        workspace.AddFile(Path.Combine(CollectionFolderName, WorkspaceFileNames.Collection), "schemaName: con_sharedtools\n");

        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentLocation = AgentFolderName });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());

        return (workspace, store);
    }

    [Fact]
    public async Task Deploy_PushesTheCollectionBeforeTheAgent()
    {
        var (workspace, store) = NestedProject();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        await CreateDeploy(store, workspaces).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(
            [Path.Combine(workspace.Root, CollectionFolderName), Path.Combine(workspace.Root, AgentFolderName)],
            workspaces.PushedDirectories);
    }

    [Fact]
    public async Task Deploy_WithACollectionInAnotherEnvironment_WarnsAndSkipsIt()
    {
        var (workspace, store) = NestedProject();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        workspaces.EnvironmentIdByDirectory[CollectionFolderName] = "env-other";

        var result = await CreateDeploy(store, workspaces).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal([Path.Combine(workspace.Root, AgentFolderName)], workspaces.PushedDirectories);
        Assert.Contains(result.Warnings, warning => warning.Contains(CollectionFolderName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_InAFlatProject_PushesOnlyTheProject()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        await CreateDeploy(store, workspaces).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal([workspace.Root], workspaces.PushedDirectories);
    }

    [Fact]
    public async Task Pull_TakesTheCollectionAndTheAgent()
    {
        var (workspace, store) = NestedProject();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        await CreatePull(store, workspaces).PullAsync(new ManagedAgentPullRequest(), CancellationToken.None);

        Assert.Equal(
            [Path.Combine(workspace.Root, CollectionFolderName), Path.Combine(workspace.Root, AgentFolderName)],
            workspaces.PulledDirectories);
    }

    [Fact]
    public async Task Pull_WithACollectionInAnotherEnvironment_LeavesItAlone()
    {
        var (workspace, store) = NestedProject();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        workspaces.EnvironmentIdByDirectory[CollectionFolderName] = "env-other";

        await CreatePull(store, workspaces).PullAsync(new ManagedAgentPullRequest(), CancellationToken.None);

        Assert.Equal([Path.Combine(workspace.Root, AgentFolderName)], workspaces.PulledDirectories);
    }

    [Fact]
    public async Task Deploy_ReadsTheSchemaNameFromTheNestedAgentWorkspace()
    {
        var (workspace, store) = NestedProject();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        var result = await CreateDeploy(store, workspaces).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("con_agent", result.SchemaName);
    }
}
