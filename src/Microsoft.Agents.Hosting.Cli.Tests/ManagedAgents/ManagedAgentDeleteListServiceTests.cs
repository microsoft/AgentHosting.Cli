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
public sealed class ManagedAgentDeleteServiceTests
{
    private static ManagedAgentDeleteService Create(JsonAgentConfigStore store, FakeCopilotCatalogPort catalog, FakeCopilotWorkspacePort workspaces, ScriptedConfirmationPrompt prompt, CopilotEnvironmentResolver? environments = null, FakeAgentResourcePort? resources = null, IManagedAgentRegistry? agents = null) =>
        new(
            ManagedAgentFixtures.AgentResolver(store),
            agents ?? store,
            CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem()),
            environments ?? ManagedAgentFixtures.Resolver(),
            catalog,
            ManagedAgentFixtures.Identities(),
            resources ?? new FakeAgentResourcePort(),
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
    public async Task Delete_OfARecordWithNoEnvironment_NamesTheRecord()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent(environmentId: string.Empty));
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "bot-1" };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, catalog, Disconnected(), new ScriptedConfirmationPrompt { Throws = true }).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None));

        Assert.Equal("agent-environment-missing", failure.ErrorKind);
        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Contains("'Contoso Agent' (agent-1)", failure.Message, StringComparison.Ordinal);
        Assert.Empty(catalog.ResolvedValues);
        Assert.Equal(0, catalog.DeleteCalls);
        Assert.Single(store.List(workspace.Root));
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

    [Fact]
    public async Task Delete_OfAnAgentDeployedElsewhere_DeletesOnlyTheTargetAndListsTheOrphansBeforeDeleting()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = DeployedToTest(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "bot-1" };
        var prompt = new ScriptedConfirmationPrompt();

        var result = await Create(store, catalog, Disconnected(), prompt).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal("agent-deleted", result.Status);
        Assert.Equal(["env-1"], catalog.DeletedIn);
        Assert.Equal("env-test", Assert.Single(result.OrphanedTargets).EnvironmentId);
        Assert.Contains($"  orphaned by this delete:{Environment.NewLine}    env-test (deployment \"test\")", prompt.LastDetail, StringComparison.Ordinal);
        Assert.Empty(store.List(workspace.Root));
        Assert.Empty(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Delete_WhenTheRecordsCannotBeRemovedAfterTheCloudDelete_NamesTheEnvironmentTheAgentWasDeletedFrom()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "bot-1" };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, catalog, Disconnected(), new ScriptedConfirmationPrompt(), agents: new RefusingAgentRegistry(store)).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None));

        Assert.Equal("records-survived-cloud-delete", failure.ErrorKind);
        Assert.StartsWith("The cloud agent in env-1 was deleted, but the records for 'agent-1' could not be removed: ", failure.Message, StringComparison.Ordinal);
        Assert.Equal(["env-1"], catalog.DeletedIn);
        Assert.Single(store.List(workspace.Root));
    }

    [Fact]
    public async Task Delete_WhenTheRecordsCannotBeRemovedAndNoCloudAgentWasDeleted_ReportsTheRecordFailure()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = null };

        await Assert.ThrowsAsync<IOException>(() => Create(store, catalog, Disconnected(), new ScriptedConfirmationPrompt(), agents: new RefusingAgentRegistry(store)).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None));

        Assert.Equal(0, catalog.DeleteCalls);
        Assert.Single(store.List(workspace.Root));
    }

    [Fact]
    public async Task Delete_WhenItsConnectedAgentIsGoneAndAnotherAgentCarriesItsSchemaName_DeletesNothing()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "bot-recreated", GetAgentThrows = CopilotStudioException.AgentNotFound("agent-1") };
        var prompt = new ScriptedConfirmationPrompt { Throws = true };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, catalog, new FakeCopilotWorkspacePort(), prompt).DeleteAsync(
            new ManagedAgentDeleteRequest(),
            CancellationToken.None));

        Assert.Equal("workspace-identity-mismatch", failure.ErrorKind);
        Assert.Equal(["con_agent"], catalog.ResolvedValues);
        Assert.Equal(0, catalog.DeleteCalls);
        Assert.Equal(0, prompt.Calls);
        Assert.Single(store.List(workspace.Root));
    }

    [Fact]
    public async Task Delete_WhenTheTargetIsAlreadyGone_ReportsNoOrphanInItsEnvironment()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var prompt = new ScriptedConfirmationPrompt();

        var result = await Create(store, new FakeCopilotCatalogPort { ResolveReturns = null }, Disconnected(), prompt).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal("agent-deleted", result.Status);
        Assert.True(result.McsAgentAlreadyAbsent);
        Assert.Empty(result.OrphanedTargets);
        Assert.Contains("cloud agent  none — this agent exists nowhere in Copilot Studio", prompt.LastDetail, StringComparison.Ordinal);
        Assert.Contains("  cloud agent  none — this agent existed nowhere in Copilot Studio", result.ToTextLines());
        Assert.Empty(store.List(workspace.Root));
    }

    [Fact]
    public async Task Delete_WithoutAllResources_ReadsAndDeletesNoResource()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var resources = new FakeAgentResourcePort { Listed = [new(CopilotCopyIdentityKinds.AIModel, PromptId)] };

        var result = await Create(store, new FakeCopilotCatalogPort { ResolveReturns = ManagedAgentFixtures.CopyAgentId }, Disconnected(), new ScriptedConfirmationPrompt(), resources: resources).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal("agent-deleted", result.Status);
        Assert.Equal(0, resources.ListCalls);
        Assert.Empty(resources.Searches);
        Assert.Empty(resources.Deleted);
        Assert.Empty(result.Resources);
    }

    [Fact]
    public async Task Delete_WithAllResources_ListsEveryResourceInTheConfirmationAndDeletesNothingWhenDeclined()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = ManagedAgentFixtures.CopyAgentId };
        var resources = new FakeAgentResourcePort { Listed = [new(CopilotCopyIdentityKinds.Workflow, ManagedAgentFixtures.CopyWorkflowId.ToUpperInvariant()), new(CopilotCopyIdentityKinds.AIModel, PromptId)] };
        var prompt = new ScriptedConfirmationPrompt { Answer = false };

        var result = await Create(store, catalog, Disconnected(), prompt, resources: resources).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1", AllResources = true },
            CancellationToken.None);

        Assert.Equal("cancelled", result.Status);
        Assert.Equal(1, prompt.Calls);
        Assert.Contains($"  resources:{Environment.NewLine}    aiModel {PromptId} in env-1{Environment.NewLine}    workflow {ManagedAgentFixtures.CopyWorkflowId} in env-1", prompt.LastDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, catalog.DeleteCalls);
        Assert.Empty(resources.Deleted);
        Assert.Single(store.List(workspace.Root));
        Assert.Single(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Delete_WithAllResourcesUnderNonInteractive_DeletesTheAgentAndEveryResourceWithoutAsking()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = ManagedAgentFixtures.CopyAgentId };
        var resources = new FakeAgentResourcePort { Listed = [new(CopilotCopyIdentityKinds.Workflow, ManagedAgentFixtures.CopyWorkflowId), new(CopilotCopyIdentityKinds.AIModel, PromptId)] };
        var prompt = new ScriptedConfirmationPrompt { Throws = true };

        var result = await Create(store, catalog, Disconnected(), prompt, resources: resources).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1", AllResources = true, NonInteractive = true },
            CancellationToken.None);

        Assert.Equal("agent-deleted", result.Status);
        Assert.Equal(0, prompt.Calls);
        Assert.Equal(["env-1"], catalog.DeletedIn);
        Assert.Equal([PromptId, ManagedAgentFixtures.CopyWorkflowId], resources.Deleted.Select(deleted => deleted.Resource.Identity));
        Assert.Empty(store.List(workspace.Root));
    }

    [Fact]
    public async Task Delete_WithAllResources_DeletesTheAgentThenEveryResourceItsWorkspaceHoldsInOrder()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = ManagedAgentFixtures.CopyAgentId };
        var resources = new FakeAgentResourcePort
        {
            Listed =
            [
                new(CopilotCopyIdentityKinds.Connector, ConnectorId),
                new(CopilotCopyIdentityKinds.ConnectionReference, "con_copyweather"),
                new(CopilotCopyIdentityKinds.Workflow, ManagedAgentFixtures.CopyWorkflowId),
                new(CopilotCopyIdentityKinds.ConnectionReference, "CON_COPYWEATHER"),
                new(CopilotCopyIdentityKinds.AIModel, PromptId),
            ],
            Missing = { ConnectorId },
        };
        var prompt = new ScriptedConfirmationPrompt();

        var result = await Create(store, catalog, Disconnected(), prompt, resources: resources).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1", AllResources = true },
            CancellationToken.None);

        var searched = Assert.Single(resources.Searches);
        Assert.Equal("agent-deleted", result.Status);
        Assert.Equal(1, resources.ListCalls);
        Assert.Equal(["env-1"], catalog.DeletedIn);
        Assert.Equal("env-1", searched.EnvironmentId);
        Assert.Equal([PromptId, ManagedAgentFixtures.CopyWorkflowId, "con_copyweather", ConnectorId], searched.Resources.Select(resource => resource.Identity));
        Assert.Equal([("env-1", PromptId), ("env-1", ManagedAgentFixtures.CopyWorkflowId), ("env-1", "con_copyweather")], resources.Deleted.Select(deleted => (deleted.EnvironmentId, deleted.Resource.Identity)));
        Assert.Equal(3, result.Resources.Count);
        Assert.Contains("connectionReference con_copyweather in env-1", prompt.LastDetail, StringComparison.Ordinal);
        Assert.Contains("  resources:", result.ToTextLines());
        Assert.Empty(store.List(workspace.Root));
    }

    [Fact]
    public async Task Delete_WithAllResourcesOfAWorkspaceHoldingNoResource_SearchesNoEnvironment()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = ManagedAgentFixtures.CopyAgentId };
        var resources = new FakeAgentResourcePort();

        var result = await Create(store, catalog, Disconnected(), new ScriptedConfirmationPrompt(), resources: resources).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1", AllResources = true },
            CancellationToken.None);

        Assert.Equal(["env-1"], catalog.DeletedIn);
        Assert.Equal(1, resources.ListCalls);
        Assert.Empty(resources.Searches);
        Assert.Empty(resources.Deleted);
        Assert.Empty(result.Resources);
    }

    [Fact]
    public async Task Delete_WithAllResourcesWhenNoCloudAgentCarriesTheSchemaName_StillDeletesTheResourcesItsWorkspaceHolds()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = null };
        var resources = new FakeAgentResourcePort { Listed = [new(CopilotCopyIdentityKinds.Workflow, ManagedAgentFixtures.CopyWorkflowId)] };

        var result = await Create(store, catalog, Disconnected(), new ScriptedConfirmationPrompt(), resources: resources).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1", AllResources = true },
            CancellationToken.None);

        Assert.True(result.McsAgentAlreadyAbsent);
        Assert.Equal(0, catalog.DeleteCalls);
        Assert.Equal([(CopilotCopyIdentityKinds.Workflow, ManagedAgentFixtures.CopyWorkflowId)], resources.Deleted.Select(deleted => (deleted.Resource.Kind, deleted.Resource.Identity)));
        Assert.Empty(store.List(workspace.Root));
    }

    [Fact]
    public async Task Delete_WithAllResourcesWhenTheAgentDeleteFails_DeletesNoResourceAndKeepsTheRecords()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = ManagedAgentFixtures.CopyAgentId, DeleteThrows = new InvalidOperationException("the service is unavailable") };
        var resources = new FakeAgentResourcePort { Listed = [new(CopilotCopyIdentityKinds.Workflow, ManagedAgentFixtures.CopyWorkflowId)] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(store, catalog, Disconnected(), new ScriptedConfirmationPrompt(), resources: resources).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1", AllResources = true },
            CancellationToken.None));

        Assert.Single(resources.Searches);
        Assert.Empty(resources.Deleted);
        Assert.Single(store.List(workspace.Root));
    }

    [Fact]
    public async Task Delete_WithAllResourcesWhenAResourceDeleteFails_DeletesTheRestAndKeepsTheRecords()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "bot-1" };
        var resources = new FakeAgentResourcePort
        {
            Listed = [new(CopilotCopyIdentityKinds.Workflow, ManagedAgentFixtures.CopyWorkflowId), new(CopilotCopyIdentityKinds.AIModel, PromptId)],
            DeleteThrowsFor = { [PromptId] = new InvalidOperationException("the prompt is locked") },
        };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, catalog, Disconnected(), new ScriptedConfirmationPrompt(), resources: resources).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1", AllResources = true },
            CancellationToken.None));

        Assert.Equal("resource-delete-incomplete", failure.ErrorKind);
        Assert.Equal(ExitCodes.Failure, failure.ExitCode);
        Assert.Contains($"with 1 of its resources, but 1 could not be deleted: aiModel {PromptId} in env-1 (InvalidOperationException: the prompt is locked)", failure.Message, StringComparison.Ordinal);
        Assert.Equal(["env-1"], catalog.DeletedIn);
        Assert.Equal(ManagedAgentFixtures.CopyWorkflowId, Assert.Single(resources.Deleted).Resource.Identity);
        Assert.Single(store.List(workspace.Root));
    }

    [Fact]
    public async Task Delete_WithAllResourcesWhenTheWorkspaceIsGone_DeletesNothing()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentLocation = "Departed" });
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "bot-1" };
        var resources = new FakeAgentResourcePort();

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store, catalog, Disconnected(), new ScriptedConfirmationPrompt { Throws = true }, resources: resources).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1", AllResources = true },
            CancellationToken.None));

        Assert.Contains("no longer on disk", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, resources.ListCalls);
        Assert.Empty(catalog.ResolvedValues);
        Assert.Equal(0, catalog.DeleteCalls);
        Assert.Single(store.List(workspace.Root));
    }

    [Fact]
    public async Task Delete_WithAllResourcesOfAnAgentDeployedElsewhere_SearchesAndDeletesOnlyInTheTargetEnvironment()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = DeployedToTest(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "bot-1" };
        var resources = new FakeAgentResourcePort { Listed = [new(CopilotCopyIdentityKinds.ConnectionReference, "con_weather"), new(CopilotCopyIdentityKinds.Workflow, ManagedAgentFixtures.CopyWorkflowId)] };

        var result = await Create(store, catalog, Disconnected(), new ScriptedConfirmationPrompt(), resources: resources).DeleteAsync(
            new ManagedAgentDeleteRequest { AgentId = "agent-1", AllResources = true },
            CancellationToken.None);

        Assert.Equal(["env-1"], catalog.DeletedIn);
        Assert.Equal(["env-1"], resources.Searches.Select(search => search.EnvironmentId));
        Assert.Equal([("env-1", ManagedAgentFixtures.CopyWorkflowId), ("env-1", "con_weather")], resources.Deleted.Select(deleted => (deleted.EnvironmentId, deleted.Resource.Identity)));
        Assert.Equal(2, result.Resources.Count);
        Assert.Equal("env-test", Assert.Single(result.OrphanedTargets).EnvironmentId);
        Assert.Empty(store.List(workspace.Root));
    }

    private const string PromptId = "c0c0c0c0-1111-2222-3333-444444444444";

    private const string ConnectorId = "d0d0d0d0-1111-2222-3333-444444444444";

    private static JsonAgentConfigStore DeployedToTest(string projectDirectory)
    {
        var store = ManagedAgentFixtures.Store();
        store.Add(projectDirectory, ManagedAgentFixtures.Agent());
        store.Add(projectDirectory, ManagedAgentFixtures.Deployment("slot-dev", name: "dev"));
        store.Add(projectDirectory, ManagedAgentFixtures.Deployment("slot-test", name: "test", stage: DeploymentStages.Test, environmentId: "env-test"));

        return store;
    }

    private static (TemporaryWorkspace Workspace, JsonAgentConfigStore Store) Registered()
    {
        var workspace = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());

        return (workspace, store);
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
    public async Task List_TenantScope_ReadsUpToFourEnvironmentsAtOnceAndKeepsTheirOrder()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var environmentIds = Enumerable.Range(1, 6).Select(number => $"env-{number}").ToArray();
        var catalog = Catalog();

        foreach (var environmentId in environmentIds)
        {
            catalog.AgentsByEnvironment[environmentId] = [.. Catalog($"agent_{environmentId}").Agents];
            catalog.ListDelays[environmentId] = TimeSpan.FromMilliseconds(environmentId == "env-1" ? 200 : 20);
        }

        var result = await Create(ManagedAgentFixtures.Store(), catalog, [.. environmentIds.Select(environmentId => new PowerPlatformEnvironmentSpec(environmentId, CopilotTestFixtures.DataverseOrganization))])
            .ListAsync(new ManagedAgentListRequest { Tenant = true, Limit = 50 }, CancellationToken.None);

        Assert.Equal(4, catalog.MaximumConcurrentLists);
        Assert.Equal(environmentIds, result.Agents.Select(agent => agent.EnvironmentId));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task List_TenantScope_StopsAtTheLimitInEnvironmentOrderAndReportsOnlyTheFailuresBeforeIt()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = Catalog();
        catalog.AgentsByEnvironment["env-1"] = [.. Catalog("a_first", "b_first").Agents];
        catalog.AgentsByEnvironment["env-3"] = [.. Catalog("a_third", "b_third").Agents];
        catalog.ListDelays["env-1"] = TimeSpan.FromMilliseconds(100);
        catalog.ListThrowsFor["env-2"] = new InvalidOperationException("env-2 is unavailable");
        catalog.ListThrowsFor["env-4"] = new InvalidOperationException("env-4 is unavailable");

        var result = await Create(
            ManagedAgentFixtures.Store(),
            catalog,
            new PowerPlatformEnvironmentSpec("env-1", CopilotTestFixtures.DataverseOrganization),
            new PowerPlatformEnvironmentSpec("env-2", CopilotTestFixtures.DataverseOrganization),
            new PowerPlatformEnvironmentSpec("env-3", CopilotTestFixtures.DataverseOrganization),
            new PowerPlatformEnvironmentSpec("env-4", CopilotTestFixtures.DataverseOrganization))
            .ListAsync(new ManagedAgentListRequest { Tenant = true, Limit = 3 }, CancellationToken.None);

        Assert.Equal(["a_first", "b_first", "a_third"], result.Agents.Select(agent => agent.SchemaName));
        Assert.True(result.Truncated);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("env-2", diagnostic.EnvironmentId);
        Assert.Equal("env-2 is unavailable", diagnostic.Message);
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
