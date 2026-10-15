using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

[Collection("CurrentDirectory")]
public sealed class ManagedAgentShowServiceTests
{
    private static ManagedAgentShowService Create(JsonAgentConfigStore store) =>
        new(store, store, new AgentConfigLocator());

    [Fact]
    public async Task Show_WithNoSelector_ShowsEveryRecord()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var result = await Create(store).ShowAsync(new ManagedAgentShowRequest(), CancellationToken.None);

        Assert.Equal("agent-1", Assert.Single(result.Agents).AgentId);
        Assert.Equal(workspace.Root, result.ProjectDirectory);
    }

    [Fact]
    public async Task Show_WithAnAgentId_ShowsThatRecord()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var result = await Create(store).ShowAsync(new ManagedAgentShowRequest { AgentId = "agent-1" }, CancellationToken.None);

        Assert.Equal("agent-1", Assert.Single(result.Agents).AgentId);
    }

    [Fact]
    public async Task Show_CarriesTheDeploymentsBelongingToTheAgent()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-1", name: "dev"));
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-2", name: "test", stage: DeploymentStages.Test, environmentId: "env-test"));

        var result = await Create(store).ShowAsync(new ManagedAgentShowRequest(), CancellationToken.None);

        Assert.Equal(["slot-1", "slot-2"], result.Deployments.Select(deployment => deployment.DeploymentId));
        Assert.All(result.Deployments, deployment => Assert.Equal("agent-1", deployment.AgentId));
    }

    [Fact]
    public async Task Show_CarriesTheDeploymentRowsVerbatim()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var stored = store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-2", name: "test", stage: DeploymentStages.Test, environmentId: "env-test"));

        var result = await Create(store).ShowAsync(new ManagedAgentShowRequest(), CancellationToken.None);

        var shown = Assert.Single(result.Deployments);
        Assert.Equivalent(stored, shown, strict: true);
    }

    [Fact]
    public async Task Show_WithASelector_CarriesThatAgentsDeployments()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-1", name: "dev"));

        var result = await Create(store).ShowAsync(new ManagedAgentShowRequest { AgentId = "agent-1" }, CancellationToken.None);

        Assert.Equal("slot-1", Assert.Single(result.Deployments).DeploymentId);
    }

    [Fact]
    public async Task Show_WithoutDeployments_CarriesNone()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var result = await Create(store).ShowAsync(new ManagedAgentShowRequest(), CancellationToken.None);

        Assert.Empty(result.Deployments);
    }

    [Fact]
    public async Task Show_WithAnAgentIdTheProjectDoesNotHold_ReportsAConflict()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => Create(store).ShowAsync(new ManagedAgentShowRequest { AgentId = "agent-9" }, CancellationToken.None));

        Assert.Equal("agent-not-found", failure.ErrorKind);
    }

    [Fact]
    public async Task Show_WithAnUnknownAgentId_Fails()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => Create(store).ShowAsync(new ManagedAgentShowRequest { AgentId = "agent-missing" }, CancellationToken.None));

        Assert.Equal("agent-not-found", failure.ErrorKind);
        Assert.Equal("Run `msagent agent show` to list the agents this project holds, or register this project with `msagent agent init`.", failure.Remediation);
        Assert.DoesNotContain("agent.config.json", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_WithAnAgentName_ShowsThatRecord()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var result = await Create(store).ShowAsync(new ManagedAgentShowRequest { AgentName = "Contoso Agent" }, CancellationToken.None);

        Assert.Equal("Contoso Agent", Assert.Single(result.Agents).DisplayName);
    }

    [Fact]
    public async Task Show_PrefersTheIdentifierOverTheName()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var result = await Create(store).ShowAsync(
            new ManagedAgentShowRequest { AgentId = "agent-1", AgentName = "Something Else" },
            CancellationToken.None);

        Assert.Equal("agent-1", Assert.Single(result.Agents).AgentId);
    }

    [Fact]
    public async Task Show_CarriesTheWholeRecord()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var agent = Assert.Single((await Create(store).ShowAsync(new ManagedAgentShowRequest(), CancellationToken.None)).Agents);

        Assert.Equal(AgentTypes.McsAgent, agent.AgentType);
        Assert.Equal(EnvironmentFixtures.ContosoTenant, agent.TenantId);
        Assert.Equal("env-1", agent.EnvironmentId);
        Assert.Equal(EntityStates.Saved, agent.AgentState);
        Assert.NotNull(agent.EntityTag);
    }

    [Fact]
    public async Task Show_FromInsideAWorkspaceFolder_FindsTheEnclosingProject()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentLocation = "tmpl 3" });
        var nested = Directory.CreateDirectory(Path.Combine(workspace.Root, "tmpl 3")).FullName;
        using var scope = new CurrentDirectoryScope(nested);

        var result = await Create(store).ShowAsync(new ManagedAgentShowRequest(), CancellationToken.None);

        Assert.Equal(workspace.Root, result.ProjectDirectory);
        Assert.Single(result.Agents);
    }

    [Fact]
    public async Task Show_CarriesTheMcsIdentityOnceItIsKnown()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { McsAgentId = "cloud-1", McsSchemaName = "con_agent" });

        var agent = Assert.Single((await Create(store).ShowAsync(new ManagedAgentShowRequest(), CancellationToken.None)).Agents);

        Assert.Equal("cloud-1", agent.McsAgentId);
        Assert.Equal("con_agent", agent.McsSchemaName);
    }

    [Fact]
    public async Task Show_LeavesTheMcsIdentityUnsetUntilItIsKnown()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var agent = Assert.Single((await Create(store).ShowAsync(new ManagedAgentShowRequest(), CancellationToken.None)).Agents);

        Assert.Null(agent.McsAgentId);
        Assert.Null(agent.McsSchemaName);
    }

    [Fact]
    public async Task Show_CarriesThePrincipalsAsRecordedRatherThanFlattened()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with
        {
            Owner = new PrincipalReference { ObjectId = "674b4cab-fb0c-466a-a81d-5a94243993b7", ObjectType = PrincipalObjectTypes.User },
        });

        var agent = Assert.Single((await Create(store).ShowAsync(new ManagedAgentShowRequest(), CancellationToken.None)).Agents);

        Assert.Equal("674b4cab-fb0c-466a-a81d-5a94243993b7", agent.Owner?.ObjectId);
        Assert.Equal(PrincipalObjectTypes.User, agent.Owner?.ObjectType);
    }

    [Fact]
    public async Task Show_ReadsTheProjectItIsPointedAt()
    {
        using var elsewhere = new TemporaryWorkspace();
        using var here = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(here.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(elsewhere.Root, ManagedAgentFixtures.Agent());

        var result = await Create(store).ShowAsync(new ManagedAgentShowRequest { Project = elsewhere.Root }, CancellationToken.None);

        Assert.Single(result.Agents);
        Assert.Equal(elsewhere.Root, result.ProjectDirectory);
    }

    [Fact]
    public async Task Show_WithoutAConfiguration_Fails()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => Create(ManagedAgentFixtures.Store()).ShowAsync(new ManagedAgentShowRequest(), CancellationToken.None));

        Assert.Equal("project-not-found", failure.ErrorKind);
    }
}
