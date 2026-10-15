using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

[Collection("CurrentDirectory")]
public sealed class ManagedAgentDeleteServiceTests
{
    private static ManagedAgentDeleteService Create(JsonAgentConfigStore store, FakeCopilotCatalogPort catalog, FakeCopilotWorkspacePort workspaces, ScriptedConfirmationPrompt prompt) =>
        new(
            ManagedAgentFixtures.AgentResolver(store),
            store,
            CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem()),
            ManagedAgentFixtures.Resolver(),
            catalog,
            ManagedAgentFixtures.Identities(),
            prompt);

    private static FakeCopilotWorkspacePort Disconnected() => new() { WorkspaceExists = false };

    [Fact]
    public async Task Delete_RemovesTheCloudAgentThenEveryRecord()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "bot-1" };

        var result = await Create(store, catalog, Disconnected(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal("agent-deleted", result.Status);
        Assert.Equal(1, catalog.DeleteCalls);
        Assert.Equal(1, result.RemovedAgentDeployments);
        Assert.Empty(store.Read(workspace.Root).ManagedAgents);
        Assert.Empty(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Delete_WhenAlreadyCancelled_DeletesNothingEvenUnderNonInteractive()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "bot-1" };
        using var cancelled = new CancellationTokenSource();

        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(store, catalog, Disconnected(), new ScriptedConfirmationPrompt { Throws = true }).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1", NonInteractive = true },
            cancelled.Token));

        Assert.Equal(0, catalog.DeleteCalls);
        Assert.Single(store.Read(workspace.Root).ManagedAgents);
    }

    [Fact]
    public async Task Delete_UnderNonInteractive_DeletesWithoutPrompting()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "bot-1" };
        var prompt = new ScriptedConfirmationPrompt { Throws = true };

        var result = await Create(store, catalog, Disconnected(), prompt).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1", NonInteractive = true },
            CancellationToken.None);

        Assert.Equal(0, prompt.Calls);
        Assert.Equal("agent-deleted", result.Status);
        Assert.Empty(store.Read(workspace.Root).ManagedAgents);
    }

    [Fact]
    public async Task Delete_WhenDeclined_DeletesNothing()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "bot-1" };

        var result = await Create(store, catalog, Disconnected(), new ScriptedConfirmationPrompt { Answer = false }).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal("cancelled", result.Status);
        Assert.Equal(0, catalog.DeleteCalls);
        Assert.Single(store.Read(workspace.Root).ManagedAgents);
    }

    [Fact]
    public async Task Delete_TargetsTheAgentTheWorkspaceIsBoundTo()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var catalog = new FakeCopilotCatalogPort();

        var result = await Create(store, catalog, new FakeCopilotWorkspacePort(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new ManagedAgentDeleteRequest(),
            CancellationToken.None);

        Assert.Equal("env-1", result.EnvironmentId);
        Assert.Equal(1, catalog.DeleteCalls);
        Assert.Empty(catalog.ResolvedValues);
    }

    [Fact]
    public async Task Delete_WithAnExplicitSelector_StillTargetsTheWorkspaceBinding()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "agent-1" };

        await Create(store, catalog, new FakeCopilotWorkspacePort(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Empty(catalog.ResolvedValues);
        Assert.Equal(1, catalog.DeleteCalls);
    }

    [Fact]
    public async Task Delete_WhenTheWorkspaceSchemaIsUnknown_DeletesNothing()
    {
        using var workspace = new TemporaryWorkspace(schemaName: "");
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var catalog = new FakeCopilotCatalogPort();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => Create(store, catalog, new FakeCopilotWorkspacePort(), new ScriptedConfirmationPrompt()).DeleteAsync(
                new ManagedAgentDeleteRequest(),
                CancellationToken.None));

        Assert.Equal("workspace-schema-unknown", failure.ErrorKind);
        Assert.Equal(0, catalog.DeleteCalls);
        Assert.Single(store.List(workspace.Root));
    }

    [Fact]
    public async Task Delete_WhenTheRecordCarriesTheSchema_StillProceedsWithoutAProjectSchema()
    {
        using var workspace = new TemporaryWorkspace(schemaName: "");
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { McsSchemaName = "con_recorded" });
        var catalog = new FakeCopilotCatalogPort { SchemaNameReturns = "con_recorded" };

        await Create(store, catalog, new FakeCopilotWorkspacePort(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new ManagedAgentDeleteRequest(),
            CancellationToken.None);

        Assert.Equal(1, catalog.DeleteCalls);
    }

    [Fact]
    public async Task Delete_WhenTheWorkspaceBindingNamesAnotherCloudAgent_DeletesNothing()
    {
        using var workspace = new TemporaryWorkspace(schemaName: "con_expected");
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var catalog = new FakeCopilotCatalogPort { SchemaNameReturns = "con_somebody_elses_agent" };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => Create(store, catalog, new FakeCopilotWorkspacePort(), new ScriptedConfirmationPrompt()).DeleteAsync(
                new ManagedAgentDeleteRequest(),
                CancellationToken.None));

        Assert.Equal("workspace-identity-mismatch", failure.ErrorKind);
        Assert.Equal(0, catalog.DeleteCalls);
    }

    [Fact]
    public async Task Delete_WhenTheWorkspaceBindingNamesTheRecordsAgent_Proceeds()
    {
        using var workspace = new TemporaryWorkspace(schemaName: "con_expected");
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var catalog = new FakeCopilotCatalogPort { SchemaNameReturns = "CON_EXPECTED" };

        await Create(store, catalog, new FakeCopilotWorkspacePort(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new ManagedAgentDeleteRequest(),
            CancellationToken.None);

        Assert.Equal(1, catalog.DeleteCalls);
    }

    [Fact]
    public async Task Delete_WhenTheProjectSchemaNamesAnotherAgentThanTheWorkspace_Fails()
    {        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "a-different-agent" };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => Create(store, catalog, new FakeCopilotWorkspacePort(), new ScriptedConfirmationPrompt()).DeleteAsync(
                new ManagedAgentDeleteRequest { AgentId = "agent-1", DeploymentName = "dev" },
                CancellationToken.None));

        Assert.Equal("workspace-identity-mismatch", failure.ErrorKind);
        Assert.Equal(0, catalog.DeleteCalls);
    }

    [Fact]
    public async Task Delete_WhenTheProjectSchemaNamesAnotherAgentInAnotherEnvironment_IsAllowed()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "a-different-agent" };
        var workspaces = new FakeCopilotWorkspacePort { EnvironmentIdReturns = "env-elsewhere" };

        await Create(store, catalog, workspaces, new ScriptedConfirmationPrompt()).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(1, catalog.DeleteCalls);
    }

    [Fact]
    public async Task Delete_WhenTheWorkspaceIsNoLongerOnDisk_RemovesTheRecordsAnyway()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentLocation = "Departed" });
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "agent-1" };

        var result = await Create(store, catalog, new FakeCopilotWorkspacePort(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal("agent-deleted", result.Status);
        Assert.True(result.McsAgentAlreadyAbsent);
        Assert.Equal(0, catalog.DeleteCalls);
        Assert.Empty(store.List(workspace.Root));
    }

    [Fact]
    public async Task Delete_WhenNothingExistsRemotely_MakesNoCloudCall()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = null };

        var result = await Create(store, catalog, Disconnected(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.True(result.McsAgentAlreadyAbsent);
        Assert.Equal(0, catalog.DeleteCalls);
        Assert.Empty(store.Read(workspace.Root).ManagedAgents);
    }

    [Fact]
    public async Task Delete_ReportsTheTargetsItLeavesRunning()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-dev", name: "dev"));
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-test", name: "test", stage: DeploymentStages.Test, environmentId: "env-test"));
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-prod", name: "prod", stage: DeploymentStages.Prod, environmentId: "env-prod"));

        var result = await Create(store, new FakeCopilotCatalogPort { ResolveReturns = "bot-1" }, Disconnected(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal(3, result.RemovedAgentDeployments);
        Assert.Equal(["test", "prod"], result.OrphanedTargets.Select(orphan => orphan.DeploymentName));
    }

    [Fact]
    public async Task Delete_LeavesLocalFilesUntouched()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var settingsPath = Path.Combine(workspace.Root, AgentConfigLocator.ProjectSettingsFileName);
        var before = File.ReadAllBytes(settingsPath);

        await Create(store, new FakeCopilotCatalogPort { ResolveReturns = "bot-1" }, Disconnected(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal(before, File.ReadAllBytes(settingsPath));
    }

    [Fact]
    public async Task Delete_ForANonMcsRecord_Fails()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentType = AgentTypes.CodeAgent });

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, new FakeCopilotCatalogPort(), Disconnected(), new ScriptedConfirmationPrompt())
            .DeleteAsync(new ManagedAgentDeleteRequest { AgentId = "agent-1" }, CancellationToken.None));

        Assert.Equal("not-an-mcs-agent", failure.ErrorKind);
    }

    [Fact]
    public async Task Delete_WithASecondRecordInTheProject_IsAccepted()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        store.Add(workspace.Root, ManagedAgentFixtures.Agent("agent-2", "Other Agent") with { AgentLocation = "Other Agent" });

        Assert.Equal(2, store.List(workspace.Root).Count);
        await Task.CompletedTask;
    }
}

[Collection("CurrentDirectory")]
public sealed class ManagedAgentListServiceTests
{
    private static ManagedAgentListService Create(JsonAgentConfigStore store, FakeCopilotCatalogPort catalog, params PowerPlatformEnvironmentSpec[] environments) =>
        CreateTracked(store, catalog, environments).Service;

    private static (ManagedAgentListService Service, FakeEnvironmentClient Client) CreateTracked(JsonAgentConfigStore store, FakeCopilotCatalogPort catalog, params PowerPlatformEnvironmentSpec[] environments) =>
        CreateTracked(store, catalog, new FakeWorkflowStorePort(), environments);

    private static (ManagedAgentListService Service, FakeEnvironmentClient Client) CreateTracked(JsonAgentConfigStore store, FakeCopilotCatalogPort catalog, FakeWorkflowStorePort workflows, params PowerPlatformEnvironmentSpec[] environments)
    {
        var specs = environments.Length == 0 ? [new PowerPlatformEnvironmentSpec("env-1", CopilotTestFixtures.DataverseOrganization)] : environments;
        var built = specs.Select(spec => EnvironmentFixtures.Environment(spec.EnvironmentId, instanceUrl: spec.InstanceUrl)).ToArray();
        var (resolver, client) = ManagedAgentFixtures.TrackedResolver(built);

        var service = new ManagedAgentListService(
            resolver,
            catalog,
            workflows,
            ManagedAgentFixtures.AgentResolver(store),
            new AgentConfigLocator(),
            ManagedAgentFixtures.Identities(),
            store,
            store);

        return (service, client);
    }

    private static FakeCopilotCatalogPort Catalog(params string[] schemaNames)
    {
        var catalog = new FakeCopilotCatalogPort();
        catalog.Agents.AddRange(schemaNames.Select(schemaName => new CopilotAgentSummary
        {
            AgentId = $"bot-{schemaName}",
            SchemaName = schemaName,
            DisplayName = schemaName,
            State = "Provisioned",
            LastChangedOn = ManagedAgentFixtures.Now,
        }));

        return catalog;
    }

    [Fact]
    public async Task List_ReadsTheActiveEnvironmentByDefault()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(ManagedAgentFixtures.Store(), Catalog("con_agent")).ListAsync(new ManagedAgentListRequest(), CancellationToken.None);

        Assert.Equal("environment", result.Scope);
        Assert.Equal("env-1", result.EnvironmentId);
        Assert.Single(result.Agents);
        Assert.Equal(ManagedAgentFixtures.Now, result.Agents[0].LastChangedOn);
    }

    [Fact]
    public async Task List_AnnotatesTheRowThatMatchesTheLocalRecord()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());

        var result = await Create(store, Catalog("con_agent", "other_agent")).ListAsync(new ManagedAgentListRequest(), CancellationToken.None);

        var annotated = result.Agents.Single(agent => agent.SchemaName == "con_agent");
        var unannotated = result.Agents.Single(agent => agent.SchemaName == "other_agent");

        Assert.Equal("agent-1", annotated.AgentId);
        Assert.Equal(1, annotated.DeploymentSlotCount);
        Assert.Null(unannotated.AgentId);
        Assert.Null(unannotated.DeploymentSlotCount);
    }

    [Fact]
    public async Task List_OutsideAProjectDirectory_LeavesEveryRowUnannotated()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(ManagedAgentFixtures.Store(), Catalog("con_agent")).ListAsync(new ManagedAgentListRequest(), CancellationToken.None);

        Assert.All(result.Agents, agent => Assert.Null(agent.AgentId));
    }

    [Fact]
    public async Task List_TenantScope_SkipsEnvironmentsWithoutADataverseUrl()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = Catalog("con_agent");

        var result = await Create(
            ManagedAgentFixtures.Store(),
            catalog,
            new PowerPlatformEnvironmentSpec("env-1", CopilotTestFixtures.DataverseOrganization),
            new PowerPlatformEnvironmentSpec("env-2", null))
            .ListAsync(new ManagedAgentListRequest { Tenant = true }, CancellationToken.None);

        Assert.Equal("tenant", result.Scope);
        Assert.Null(result.EnvironmentId);
        Assert.Single(result.Diagnostics);
        Assert.Equal("env-2", result.Diagnostics[0].EnvironmentId);
        Assert.Single(result.Agents);
    }

    [Fact]
    public async Task List_TenantScope_StopsAtTheLimit()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(ManagedAgentFixtures.Store(), Catalog("a_one", "b_two", "c_three"))
            .ListAsync(new ManagedAgentListRequest { Tenant = true, Limit = 2 }, CancellationToken.None);

        Assert.Equal(2, result.Agents.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task List_AnnotatesOnlyTheEnvironmentTheRecordBelongsTo()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var catalog = Catalog("con_agent");

        var result = await Create(
            store,
            catalog,
            new PowerPlatformEnvironmentSpec("env-1", CopilotTestFixtures.DataverseOrganization),
            new PowerPlatformEnvironmentSpec("env-elsewhere", CopilotTestFixtures.DataverseOrganization))
            .ListAsync(new ManagedAgentListRequest { Tenant = true, Limit = 50 }, CancellationToken.None);

        Assert.Equal(2, result.Agents.Count);
        Assert.Single(result.Agents, agent => agent.AgentId is not null);
        Assert.Equal("env-1", Assert.Single(result.Agents, agent => agent.AgentId is not null).EnvironmentId);
    }

    [Fact]
    public async Task List_AnnotatesAnEnvironmentReachedThroughADeployment()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-test", "agent-1", "test", DeploymentStages.Test, "env-promoted"));

        var result = await Create(
            store,
            Catalog("con_agent"),
            new PowerPlatformEnvironmentSpec("env-promoted", CopilotTestFixtures.DataverseOrganization))
            .ListAsync(new ManagedAgentListRequest { Tenant = true, Limit = 50 }, CancellationToken.None);

        Assert.Equal("agent-1", Assert.Single(result.Agents).AgentId);
    }

    [Fact]
    public async Task List_TenantScope_DownloadsTheEnvironmentInventoryOnce()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var (service, client) = CreateTracked(
            ManagedAgentFixtures.Store(),
            Catalog("con_agent"),
            new PowerPlatformEnvironmentSpec("env-1", CopilotTestFixtures.DataverseOrganization),
            new PowerPlatformEnvironmentSpec("env-2", CopilotTestFixtures.DataverseOrganization),
            new PowerPlatformEnvironmentSpec("env-3", CopilotTestFixtures.DataverseOrganization),
            new PowerPlatformEnvironmentSpec("env-4", CopilotTestFixtures.DataverseOrganization));

        var before = client.ListCalls;

        await service.ListAsync(new ManagedAgentListRequest { Tenant = true, Limit = 50 }, CancellationToken.None);

        Assert.Equal(1, client.ListCalls - before);
    }

    [Fact]
    public async Task List_TenantScope_ReportsAnEnvironmentItCouldNotRead()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(
            ManagedAgentFixtures.Store(),
            Catalog("con_agent"),
            new PowerPlatformEnvironmentSpec("env-1", CopilotTestFixtures.DataverseOrganization),
            new PowerPlatformEnvironmentSpec("env-2", null))
            .ListAsync(new ManagedAgentListRequest { Tenant = true }, CancellationToken.None);

        Assert.Equal("env-2", Assert.Single(result.Diagnostics).EnvironmentId);
        Assert.NotEmpty(Assert.Single(result.Diagnostics).Message);
    }

    [Fact]
    public async Task List_RequestsOneRowBeyondTheLimitSoTruncationStaysDetectable()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = Catalog("a_one", "b_two", "c_three");

        await Create(ManagedAgentFixtures.Store(), catalog)
            .ListAsync(new ManagedAgentListRequest { Limit = 2 }, CancellationToken.None);

        Assert.Equal(3, Assert.Single(catalog.RequestedMaximumCounts));
    }

    [Fact]
    public async Task List_RejectsTenantAndEnvironmentTogether()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), Catalog())
            .ListAsync(new ManagedAgentListRequest { Tenant = true, EnvironmentId = "env-1" }, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task List_RejectsANonPositiveLimit(int limit)
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), Catalog())
            .ListAsync(new ManagedAgentListRequest { Limit = limit }, CancellationToken.None));
    }

    [Fact]
    public async Task List_ReportsTheCloudAgentIdAndTenant()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(ManagedAgentFixtures.Store(), Catalog("con_agent")).ListAsync(new ManagedAgentListRequest(), CancellationToken.None);

        Assert.Equal("bot-con_agent", result.Agents[0].McsAgentId);
        Assert.Equal(EnvironmentFixtures.ContosoTenant, result.Agents[0].TenantId);
    }

    [Fact]
    public async Task List_ReportsEveryRowAsAnMcsAgent()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(ManagedAgentFixtures.Store(), Catalog("con_agent")).ListAsync(new ManagedAgentListRequest(), CancellationToken.None);

        Assert.All(result.Agents, agent => Assert.Equal(AgentTypes.McsAgent, agent.AgentType));
    }
}

internal sealed record PowerPlatformEnvironmentSpec(string EnvironmentId, string? InstanceUrl);
