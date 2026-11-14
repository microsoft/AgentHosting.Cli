using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

[Collection("CurrentDirectory")]
public sealed class MultiAgentProjectTests
{
    private static readonly AgentConfigLocator Locator = new();

    private static ManagedAgentDeployService CreateDeploy(JsonAgentConfigStore store, FakeCopilotWorkspacePort workspaces, string boundSchemaName = "con_agent")
    {
        var environments = ManagedAgentFixtures.Resolver();
        var locator = CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem());
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "agent-1", SchemaNameReturns = boundSchemaName };

        return new ManagedAgentDeployService(
            ManagedAgentFixtures.AgentResolver(store),
            store,
            store,
            environments,
            locator,
            new CopilotWorkspaceService(workspaces, locator),
            ManagedAgentFixtures.Identities(),
            new RecordingAttachPort { OnAttach = () => workspaces.WorkspaceExists = true },
            catalog,
            new CopilotCatalogService(catalog, environments, locator, new FixedClock(ManagedAgentFixtures.Now), (_, _) => Task.CompletedTask),
            ManagedAgentFixtures.Principals(),
            ManagedAgentFixtures.ActivationPass(),
            ManagedAgentFixtures.AgentNames(),
            new FixedClock(ManagedAgentFixtures.Now));
    }

    private static void WriteWorkspace(TemporaryWorkspace workspace, string folder, string schemaName) =>
        workspace.AddFile(Path.Combine(folder, AgentConfigLocator.ProjectSettingsFileName), $"displayName: \"{folder}\"\nschemaName: \"{schemaName}\"\n");

    private static (TemporaryWorkspace Workspace, JsonAgentConfigStore Store) TwoAgents()
    {
        var workspace = new TemporaryWorkspace(withProjectFile: false);
        WriteWorkspace(workspace, "Agent One", "con_one");
        WriteWorkspace(workspace, "Agent Two", "con_two");

        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent("agent-1", "Agent One") with { AgentLocation = "Agent One" });
        store.Add(workspace.Root, ManagedAgentFixtures.Agent("agent-2", "Agent Two") with { AgentLocation = "Agent Two" });

        return (workspace, store);
    }

    [Fact]
    public void TwoAgentsInOneConfiguration_AreStored()
    {
        var (workspace, store) = TwoAgents();
        using var _ = workspace;

        Assert.Equal(["agent-1", "agent-2"], store.List(workspace.Root).Select(agent => agent.AgentId));
    }

    [Fact]
    public void EachAgentResolvesItsOwnWorkspace()
    {
        var (workspace, store) = TwoAgents();
        using var _ = workspace;
        var agents = store.List(workspace.Root);

        Assert.Equal(Path.Combine(workspace.Root, "Agent One"), Locator.AgentWorkspaceDirectory(workspace.Root, agents[0].AgentLocation));
        Assert.Equal(Path.Combine(workspace.Root, "Agent Two"), Locator.AgentWorkspaceDirectory(workspace.Root, agents[1].AgentLocation));
    }

    [Fact]
    public void TwoAgentsWhoseLocationsAreAliasesOfOneDirectory_AreRejected()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        WriteWorkspace(workspace, "Agent One", "con_one");
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentLocation = "Agent One" });

        var failure = Assert.Throws<ManagedAgentException>(() => store.Add(
            workspace.Root,
            ManagedAgentFixtures.Agent() with { AgentId = "agent-2", DisplayName = "Agent Two", AgentLocation = "Agent One/." }));

        Assert.Equal("config-invalid", failure.ErrorKind);
        Assert.Contains("agent location", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAgentWhoseLocationHoldsNoProject_FailsOnlyWhenItIsUsed()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        WriteWorkspace(workspace, "Agent One", "con_one");
        var store = ManagedAgentFixtures.Store();
        var stored = store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentLocation = "Absent" });

        var failure = Assert.Throws<ManagedAgentException>(() => ManagedAgentFixtures.AgentResolver(store).ResolveAgentWorkspaceDirectory(workspace.Root, stored));

        Assert.Equal("workspace-missing", failure.ErrorKind);
    }

    [Fact]
    public void AnAgentLocationIsAlwaysRecorded()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        WriteWorkspace(workspace, "Agent One", "con_one");
        var store = ManagedAgentFixtures.Store();

        var stored = store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentLocation = "Agent One" });

        Assert.Equal("Agent One", stored.AgentLocation);
        Assert.Equal(Path.Combine(workspace.Root, "Agent One"), Locator.AgentWorkspaceDirectory(workspace.Root, stored.AgentLocation));
    }

    [Fact]
    public void AFlatProjectRecordsTheRootLocation()
    {
        using var workspace = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();

        var stored = store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        Assert.Equal(AgentConfigLocator.CurrentDirectoryLocation, stored.AgentLocation);
        Assert.Equal(workspace.Root, Locator.AgentWorkspaceDirectory(workspace.Root, stored.AgentLocation));
    }

    [Fact]
    public async Task DeployResolvesTheNamedAgentsWorkspace()
    {
        var (workspace, store) = TwoAgents();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-2", agentId: "agent-2", name: "dev"));
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        var result = await CreateDeploy(store, workspaces, boundSchemaName: "con_two").DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-2", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("con_two", result.SchemaName);
        Assert.Equal([Path.Combine(workspace.Root, "Agent Two")], workspaces.PushedDirectories);
    }

    [Fact]
    public async Task DeployWhoseBindingPointsAtAnotherAgent_IsRejected()
    {
        var (workspace, store) = TwoAgents();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-2", agentId: "agent-2", name: "dev"));
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => CreateDeploy(store, workspaces, boundSchemaName: "con_somebody_else").DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-2", DeploymentName = "dev" },
            CancellationToken.None));

        Assert.Equal("workspace-identity-mismatch", failure.ErrorKind);
        Assert.Empty(workspaces.PushedDirectories);
    }

    [Fact]
    public async Task DeployWithoutASelectorInAMultiAgentProject_IsAmbiguous()
    {
        var (workspace, store) = TwoAgents();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-1", agentId: "agent-1", name: "dev"));
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-2", agentId: "agent-2", name: "dev"));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => CreateDeploy(store, new FakeCopilotWorkspacePort { WorkspaceExists = true }).DeployAsync(
            new ManagedAgentDeployRequest { DeploymentName = "dev" },
            CancellationToken.None));

        Assert.Equal("deployment-ambiguous", failure.ErrorKind);
        Assert.Contains("Agent One", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Agent Two", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployWithOnlyADeploymentName_FindsItsOwningAgent()
    {
        var (workspace, store) = TwoAgents();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-2", agentId: "agent-2", name: "second"));
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        var result = await CreateDeploy(store, workspaces, boundSchemaName: "con_two").DeployAsync(
            new ManagedAgentDeployRequest { DeploymentName = "second" },
            CancellationToken.None);

        Assert.Equal("agent-2", result.AgentId);
    }

    [Fact]
    public void TwoAgentsMayShareADeploymentEnvironment()
    {
        var (workspace, store) = TwoAgents();
        using var _ = workspace;
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-1", agentId: "agent-1", name: "dev-one"));

        var stored = store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-2", agentId: "agent-2", name: "dev-two"));

        Assert.Equal("slot-2", stored.DeploymentId);
    }

    [Fact]
    public void TwoAgentsMayShareADeploymentName()
    {
        var (workspace, store) = TwoAgents();
        using var _ = workspace;
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-1", agentId: "agent-1", name: "shared"));

        var stored = store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-2", agentId: "agent-2", name: "SHARED"));

        Assert.Equal("slot-2", stored.DeploymentId);
    }

    [Fact]
    public void TwoAgentsMayShareADeploymentNameAcrossEnvironments()
    {
        var (workspace, store) = TwoAgents();
        using var _ = workspace;
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-1", agentId: "agent-1", name: "shared"));

        var stored = store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-2", agentId: "agent-2", name: "shared", stage: DeploymentStages.Test, environmentId: "env-test"));

        Assert.Equal("slot-2", stored.DeploymentId);
    }

    [Fact]
    public async Task InitAdoptsTheOnlyUnregisteredWorkspace()
    {
        var (workspace, store) = TwoAgents();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        WriteWorkspace(workspace, "Agent Three", "con_three");

        var result = await CreateInit(store).InitAsync(new ManagedAgentInitRequest { Name = "Adopted" }, CancellationToken.None);

        Assert.Equal("Adopted", result.DisplayName);
        Assert.Equal("Agent Three", store.List(workspace.Root).Single(agent => agent.AgentId == result.AgentId).AgentLocation);
    }

    [Fact]
    public async Task InitWithTwoUnregisteredWorkspaces_IsAmbiguous()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        WriteWorkspace(workspace, "Agent One", "con_one");
        WriteWorkspace(workspace, "Agent Two", "con_two");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => CreateInit(ManagedAgentFixtures.Store()).InitAsync(
            new ManagedAgentInitRequest { Name = "Adopted" },
            CancellationToken.None));

        Assert.Equal("workspace-ambiguous", failure.ErrorKind);
    }

    [Fact]
    public async Task InitWithEveryWorkspaceRegistered_Fails()
    {
        var (workspace, store) = TwoAgents();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => CreateInit(store).InitAsync(
            new ManagedAgentInitRequest { Name = "Adopted" },
            CancellationToken.None));

        Assert.Equal("already-registered", failure.ErrorKind);
    }

    [Fact]
    public async Task InitOnAFlatProject_RecordsTheRootLocation()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();

        var result = await CreateInit(store).InitAsync(new ManagedAgentInitRequest { Name = "Adopted" }, CancellationToken.None);

        Assert.Equal(AgentConfigLocator.CurrentDirectoryLocation, store.List(workspace.Root).Single(agent => agent.AgentId == result.AgentId).AgentLocation);
    }

    private static ManagedAgentInitService CreateInit(JsonAgentConfigStore store)
    {
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };

        return new ManagedAgentInitService(
            store,
            new AgentConfigLocator(),
            CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem()),
            ManagedAgentFixtures.Resolver(),
            ManagedAgentFixtures.Identities());
    }
}
