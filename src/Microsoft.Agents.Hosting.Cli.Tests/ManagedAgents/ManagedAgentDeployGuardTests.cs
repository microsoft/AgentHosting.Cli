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
public sealed class ManagedAgentDeployGuardTests
{
    private static ManagedAgentDeployService Create(JsonAgentConfigStore store, FakeCopilotWorkspacePort workspaces, FakeCopilotCatalogPort catalog, CopilotEnvironmentResolver? resolver = null, RecordingAttachPort? attach = null)
    {
        var environments = resolver ?? ManagedAgentFixtures.Resolver();
        var locator = CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem());

        return new ManagedAgentDeployService(
            ManagedAgentFixtures.AgentResolver(store),
            store,
            store,
            environments,
            locator,
            new CopilotWorkspaceService(workspaces, locator),
            ManagedAgentFixtures.Identities(),
            attach ?? new RecordingAttachPort
            {
                OnAttachCommand = command =>
                {
                    workspaces.WorkspaceExists = true;
                    workspaces.EnvironmentIdByDirectory[Path.GetFileName(command.ProjectDirectory)] = command.Environment.EnvironmentId;
                },
            },
            catalog,
            new CopilotCatalogService(catalog, environments, locator, new FixedClock(ManagedAgentFixtures.Now), (_, _) => Task.CompletedTask),
            ManagedAgentFixtures.Principals(),
            ManagedAgentFixtures.ActivationPass(),
            new FixedClock(ManagedAgentFixtures.Now));
    }

    private static CopilotEnvironmentResolver TwoEnvironments() => ManagedAgentFixtures.Resolver(
        EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization),
        EnvironmentFixtures.Environment("env-test", instanceUrl: CopilotTestFixtures.DataverseOrganization));
    private static (TemporaryWorkspace Workspace, JsonAgentConfigStore Store) Registered()
    {
        var workspace = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();

        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(name: DeploymentStages.Dev, stage: DeploymentStages.Dev));

        return (workspace, store);
    }

    private static ManagedAgentDeployRequest Request(string? schemaName = null) => new()
    {
        AgentId = "agent-1",
        DeploymentName = DeploymentStages.Dev,
        SchemaName = schemaName,
    };

    [Fact]
    public async Task Deploy_WhenTheCatalogFailsTransiently_DoesNotReattach()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var catalog = new FakeCopilotCatalogPort { GetAgentThrows = CopilotStudioException.ServiceError("GET bots", "HTTP 401 Unauthorized.") };

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(store, workspaces, catalog).DeployAsync(Request(), CancellationToken.None));

        Assert.Equal(0, workspaces.PushCalls);
    }

    [Fact]
    public async Task Deploy_WhenTheCatalogFailsTransiently_NeverOverwrites()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var catalog = new FakeCopilotCatalogPort { GetAgentThrows = CopilotStudioException.ServiceError("GET bots", "HTTP 503 Service Unavailable.") };

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(store, workspaces, catalog).DeployAsync(Request(), CancellationToken.None));

        Assert.Null(workspaces.LastPushOverwrite);
    }

    [Fact]
    public async Task Deploy_WhenTheAgentIsGenuinelyMissing_Reattaches()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var catalog = new FakeCopilotCatalogPort { GetAgentThrows = CopilotStudioException.AgentNotFound("agent-1") };

        var result = await Create(store, workspaces, catalog).DeployAsync(Request(), CancellationToken.None);

        Assert.Equal("reattach", result.Action);
    }

    [Fact]
    public async Task Deploy_WithASchemaNameNamingTheBoundAgent_Pushes()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        var result = await Create(store, workspaces, new FakeCopilotCatalogPort()).DeployAsync(Request("con_agent"), CancellationToken.None);

        Assert.Equal("push", result.Action);
    }

    [Fact]
    public async Task Deploy_WithASchemaNameNamingAnotherAgent_Fails()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => Create(store, workspaces, new FakeCopilotCatalogPort()).DeployAsync(Request("con_somethingelse"), CancellationToken.None));

        Assert.Contains("con_somethingelse", failure.Message, StringComparison.Ordinal);
        Assert.Contains("con_agent", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deploy_WithASchemaNameNamingAnotherAgent_NeverPushes()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        await Assert.ThrowsAsync<ManagedAgentException>(
            () => Create(store, workspaces, new FakeCopilotCatalogPort()).DeployAsync(Request("con_somethingelse"), CancellationToken.None));

        Assert.Equal(0, workspaces.PushCalls);
    }

    [Fact]
    public async Task Deploy_WithNoSchemaNameOverride_ReadsNoAgentToCompareAgainst()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort();

        await Create(store, new FakeCopilotWorkspacePort { WorkspaceExists = true }, catalog).DeployAsync(Request(), CancellationToken.None);

        Assert.Equal(1, catalog.GetAgentCalls);
    }

    [Theory]
    [InlineData("https://evil.example.com/")]
    [InlineData("http://contoso.crm.dynamics.com/")]
    [InlineData("https://contoso.crm.dynamics.com:8443/")]
    public async Task Deploy_WhenTheWorkspaceRecordsAnotherOrigin_Fails(string organization)
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true, DataverseOrganizationReturns = organization };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(store, workspaces, new FakeCopilotCatalogPort()).DeployAsync(Request(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.UntrustedDataverseOrigin, failure.ErrorKind);
        Assert.Equal(0, workspaces.PushCalls);
    }

    [Fact]
    public async Task Deploy_WhenTheWorkspaceRecordsTheEnvironmentsOrigin_Pushes()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true, DataverseOrganizationReturns = CopilotTestFixtures.DataverseOrganization };

        var result = await Create(store, workspaces, new FakeCopilotCatalogPort()).DeployAsync(Request(), CancellationToken.None);

        Assert.Equal("push", result.Action);
    }

    [Fact]
    public async Task Deploy_ToAnotherEnvironmentWhereTheAgentAlreadyExists_Retargets()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-test", "agent-1", "test", DeploymentStages.Test, "env-test"));
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true, DataverseOrganizationReturns = CopilotTestFixtures.DataverseOrganization };
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_topic"));

        var result = await Create(store, workspaces, new FakeCopilotCatalogPort(), TwoEnvironments()).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "test" },
            CancellationToken.None);

        Assert.Equal("retarget", result.Action);
        Assert.Equal("env-test", result.TargetEnvironmentId);
    }

    [Fact]
    public async Task Deploy_ToAnotherEnvironment_PushesAsAnOverwrite()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-test", "agent-1", "test", DeploymentStages.Test, "env-test"));
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true, DataverseOrganizationReturns = CopilotTestFixtures.DataverseOrganization };
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_topic"));

        await Create(store, workspaces, new FakeCopilotCatalogPort(), TwoEnvironments()).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "test" },
            CancellationToken.None);

        Assert.True(workspaces.LastPushOverwrite);
    }

    [Fact]
    public async Task Deploy_WhenTheServiceDeclinesAWorkflow_StillSucceedsAndWarns()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true, DataverseOrganizationReturns = CopilotTestFixtures.DataverseOrganization };
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_topic"));
        workspaces.PushWarnings.Add("Workflow 'Escalate' was not activated: this is not a valid connection");

        var result = await Create(store, workspaces, new FakeCopilotCatalogPort()).DeployAsync(Request(), CancellationToken.None);

        Assert.Equal("push", result.Action);
        Assert.Contains(result.Warnings, warning => warning.Contains("Escalate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_ReportsEachWarningOnce()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true, DataverseOrganizationReturns = CopilotTestFixtures.DataverseOrganization };
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_topic"));
        workspaces.PushWarnings.Add("Workflow 'Escalate' was not activated: this is not a valid connection");
        workspaces.PushWarnings.Add("Workflow 'Escalate' was not activated: this is not a valid connection");

        var result = await Create(store, workspaces, new FakeCopilotCatalogPort()).DeployAsync(Request(), CancellationToken.None);

        Assert.Single(result.Warnings, warning => warning.Contains("Escalate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_WithPublishWhenAnExistingAgentWasAdopted_StillPublishes()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var attach = new RecordingAttachPort { AgentCreated = false, OnAttach = () => workspaces.WorkspaceExists = true };
        attach.AttachNotices.Add("An agent already carried the schema name con_agent in env-1; it was adopted rather than duplicated.");
        store.Update(workspace.Root, store.List(workspace.Root).Single() with { McsAgentId = "11111111-1111-1111-1111-111111111111" });
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "11111111-1111-1111-1111-111111111111" };
        catalog.PublishedOnSequence.Enqueue(null);
        catalog.PublishedOnSequence.Enqueue(ManagedAgentFixtures.Now);

        var result = await Create(store, workspaces, catalog, attach: attach).DeployAsync(
            Request() with { Publish = true },
            CancellationToken.None);

        Assert.True(result.Published);
        Assert.Contains(result.Warnings, warning => warning.Contains("adopted rather than duplicated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_WithPublishWhenAttachRefusedAWorkflow_WithholdsThePublish()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var attach = new RecordingAttachPort { OnAttach = () => workspaces.WorkspaceExists = true };
        attach.AttachWarnings.Add("Workflow 'Escalate' is not running: the service left it disabled.");
        store.Update(workspace.Root, store.List(workspace.Root).Single() with { McsAgentId = "11111111-1111-1111-1111-111111111111" });
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "11111111-1111-1111-1111-111111111111" };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, workspaces, catalog, attach: attach).DeployAsync(
            Request() with { Publish = true },
            CancellationToken.None));

        Assert.Equal("publish-withheld-incomplete-push", failure.ErrorKind);
        Assert.Equal(0, catalog.PublishCalls);
    }

    [Fact]
    public async Task Deploy_WithPublishWhenTheServiceDeclinedAWorkflow_WithholdsThePublish()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true, DataverseOrganizationReturns = CopilotTestFixtures.DataverseOrganization };
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_topic"));
        workspaces.PushWarnings.Add("Workflow 'Escalate' was not activated: this is not a valid connection");
        var catalog = new FakeCopilotCatalogPort();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, workspaces, catalog).DeployAsync(
            Request() with { Publish = true },
            CancellationToken.None));

        Assert.Equal("publish-withheld-incomplete-push", failure.ErrorKind);
        Assert.Contains("Escalate", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, catalog.PublishCalls);
    }

    [Fact]
    public async Task Deploy_WithPublishWhenTheServiceDeclinedAWorkflow_StillRecordsThePush()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true, DataverseOrganizationReturns = CopilotTestFixtures.DataverseOrganization };
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_topic"));
        workspaces.PushWarnings.Add("Workflow 'Escalate' was not activated: this is not a valid connection");

        await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, workspaces, new FakeCopilotCatalogPort()).DeployAsync(
            Request() with { Publish = true },
            CancellationToken.None));

        Assert.NotNull(store.Read(workspace.Root).AgentDeployments.Single().LastDeployedTime);
    }

    [Fact]
    public async Task Deploy_ReportsConnectorsAndFlowsPushedAlongsideComponents()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true, DataverseOrganizationReturns = CopilotTestFixtures.DataverseOrganization, AuxiliaryChangeCountReturns = 2 };

        var result = await Create(store, workspaces, new FakeCopilotCatalogPort()).DeployAsync(Request(), CancellationToken.None);

        Assert.Equal(2, result.AuxiliaryChangeCount);
        Assert.Equal(0, result.PushedComponentCount);
    }

    [Fact]
    public async Task Deploy_RecordsTheCloudIdentityOnTheAgent()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true, DataverseOrganizationReturns = CopilotTestFixtures.DataverseOrganization };
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_topic"));

        await Create(store, workspaces, new FakeCopilotCatalogPort()).DeployAsync(Request(), CancellationToken.None);

        var agent = store.List(workspace.Root).Single();
        Assert.Equal("agent-1", agent.McsAgentId);
        Assert.Equal("con_agent", agent.McsSchemaName);
    }

    [Fact]
    public async Task Deploy_WhenTheRegistryWriteFails_ReportsTheCloudTargetItAlreadyChanged()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true, DataverseOrganizationReturns = CopilotTestFixtures.DataverseOrganization };
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_topic"));
        var deployments = new RefusingDeploymentRegistry(store);

        var service = new ManagedAgentDeployService(
            ManagedAgentFixtures.AgentResolver(store),
            store,
            deployments,
            ManagedAgentFixtures.Resolver(),
            CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem()),
            new CopilotWorkspaceService(workspaces, CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem())),
            ManagedAgentFixtures.Identities(),
            new RecordingAttachPort(),
            new FakeCopilotCatalogPort(),
            new CopilotCatalogService(new FakeCopilotCatalogPort(), ManagedAgentFixtures.Resolver(), CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem()), new FixedClock(ManagedAgentFixtures.Now), (_, _) => Task.CompletedTask),
            ManagedAgentFixtures.Principals(),
            ManagedAgentFixtures.ActivationPass(),
            new FixedClock(ManagedAgentFixtures.Now));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => service.DeployAsync(Request(), CancellationToken.None));

        Assert.Equal("deploy-recorded-nowhere", failure.ErrorKind);
        Assert.Equal(1, workspaces.PushCalls);
        Assert.Contains("agent-1", failure.Message, StringComparison.Ordinal);
    }

    private const string ExistingAgent = "11111111-1111-1111-1111-111111111111";

    /// <summary>A project whose slot has already deployed, under the schema name it recorded then.</summary>
    private static (TemporaryWorkspace Workspace, JsonAgentConfigStore Store) Deployed(string recordedSchemaName = "con_agent")
    {
        var workspace = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();

        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { McsSchemaName = recordedSchemaName });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(name: DeploymentStages.Dev, stage: DeploymentStages.Dev, lastDeployedTime: ManagedAgentFixtures.Now));

        return (workspace, store);
    }

    private static FakeCopilotCatalogPort Occupied(bool managed = false) =>
        new() { ResolveReturns = ExistingAgent, IsManagedReturns = managed };

    [Fact]
    public async Task Deploy_WhenAnUnrelatedAgentCarriesTheSchemaName_Fails()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => Create(store, workspaces, Occupied()).DeployAsync(Request(), CancellationToken.None));

        Assert.Equal("unverified-agent-in-target", failure.ErrorKind);
        Assert.Contains(ExistingAgent, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deploy_WhenAnUnrelatedAgentCarriesTheSchemaName_AdoptsNothingAndPushesNothing()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var attach = new RecordingAttachPort { AgentCreated = false, OnAttach = () => workspaces.WorkspaceExists = true };

        await Assert.ThrowsAsync<ManagedAgentException>(
            () => Create(store, workspaces, Occupied(), attach: attach).DeployAsync(Request(), CancellationToken.None));

        Assert.Equal(0, attach.Calls);
        Assert.Equal(0, workspaces.PushCalls);
    }

    [Fact]
    public async Task Deploy_AProductionSlot_WhenAnUnrelatedAgentCarriesTheSchemaName_NeverOverwritesIt()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(name: "prod", stage: DeploymentStages.Prod));
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, workspaces, Occupied()).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "prod" },
            CancellationToken.None));

        Assert.Equal("unverified-agent-in-target", failure.ErrorKind);
        Assert.Null(workspaces.LastPushOverwrite);
    }

    [Fact]
    public async Task Deploy_WithOverwrite_AdoptsTheAgentCarryingTheSchemaName()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var attach = new RecordingAttachPort { AgentCreated = false, OnAttach = () => workspaces.WorkspaceExists = true };

        var result = await Create(store, workspaces, Occupied(), attach: attach).DeployAsync(
            Request() with { Overwrite = true },
            CancellationToken.None);

        Assert.Equal("reattach", result.Action);
        Assert.False(result.TargetAgentCreated);
    }

    [Fact]
    public async Task Deploy_WhenItAdoptsATarget_PinsTheAttachToTheAgentItChecked()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var attach = new RecordingAttachPort { AgentCreated = false, OnAttach = () => workspaces.WorkspaceExists = true };

        await Create(store, workspaces, Occupied(), attach: attach).DeployAsync(
            Request() with { Overwrite = true },
            CancellationToken.None);

        Assert.Equal(ExistingAgent, attach.LastCommand!.ExpectedAgentId);
    }

    [Fact]
    public async Task Deploy_WhenTheRecordAlreadyNamesTheAgent_AdoptsItWithoutOverwrite()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Update(workspace.Root, store.List(workspace.Root).Single() with { McsAgentId = ExistingAgent });
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var attach = new RecordingAttachPort { AgentCreated = false, OnAttach = () => workspaces.WorkspaceExists = true };

        var result = await Create(store, workspaces, Occupied(), attach: attach).DeployAsync(Request(), CancellationToken.None);

        Assert.Equal("reattach", result.Action);
        Assert.Equal(ExistingAgent, attach.LastCommand!.ExpectedAgentId);
    }

    [Fact]
    public async Task Deploy_WhenTheSlotHasDeployedBefore_AdoptsItsAgentWithoutOverwrite()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var attach = new RecordingAttachPort { AgentCreated = false, OnAttach = () => workspaces.WorkspaceExists = true };

        var result = await Create(store, workspaces, Occupied(), attach: attach).DeployAsync(Request(), CancellationToken.None);

        Assert.Equal("reattach", result.Action);
        Assert.Equal(1, workspaces.PushCalls);
    }

    [Fact]
    public async Task Deploy_WhenASchemaNameOverridePointsAnEstablishedSlotElsewhere_Fails()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => Create(store, workspaces, Occupied()).DeployAsync(Request("con_somebodyelse"), CancellationToken.None));

        Assert.Equal("unverified-agent-in-target", failure.ErrorKind);
    }

    [Fact]
    public async Task Deploy_WhenTheTargetCameFromAManagedSolution_Fails()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => Create(store, workspaces, Occupied(managed: true)).DeployAsync(Request(), CancellationToken.None));

        Assert.Equal("target-agent-read-only", failure.ErrorKind);
        Assert.Equal(0, workspaces.PushCalls);
    }

    [Fact]
    public async Task Deploy_WhenTheTargetCameFromAManagedSolution_IsRefusedEvenWithOverwrite()
    {
        var (workspace, store) = Deployed();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, workspaces, Occupied(managed: true)).DeployAsync(
            Request() with { Overwrite = true },
            CancellationToken.None));

        Assert.Equal("target-agent-read-only", failure.ErrorKind);
    }

    [Fact]
    public async Task Deploy_WhenTheBoundAgentCameFromAManagedSolution_Fails()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true, DataverseOrganizationReturns = CopilotTestFixtures.DataverseOrganization };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => Create(store, workspaces, new FakeCopilotCatalogPort { IsManagedReturns = true }).DeployAsync(Request(), CancellationToken.None));

        Assert.Equal("target-agent-read-only", failure.ErrorKind);
        Assert.Equal(0, workspaces.PushCalls);
    }

    [Fact]
    public async Task Deploy_WhenNothingCarriesTheSchemaName_CreatesWithoutAnyAcknowledgement()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var attach = new RecordingAttachPort { OnAttach = () => workspaces.WorkspaceExists = true };

        var result = await Create(store, workspaces, new FakeCopilotCatalogPort(), attach: attach).DeployAsync(Request(), CancellationToken.None);

        Assert.True(result.TargetAgentCreated);
        Assert.Null(attach.LastCommand!.ExpectedAgentId);
    }
}

internal sealed class RefusingDeploymentRegistry : IAgentDeploymentRegistry
{
    private readonly IAgentDeploymentRegistry _inner;

    public RefusingDeploymentRegistry(IAgentDeploymentRegistry inner) => _inner = inner;

    public AgentDeploymentRecord Add(string projectDirectory, AgentDeploymentRecord record) => _inner.Add(projectDirectory, record);

    public IReadOnlyList<AgentDeploymentRecord> List(string projectDirectory) => _inner.List(projectDirectory);

    public AgentDeploymentRecord Update(string projectDirectory, AgentDeploymentRecord record) =>
        throw new IOException("the configuration file is locked by another process");

    public void Remove(string projectDirectory, string deploymentId, string expectedETag) => _inner.Remove(projectDirectory, deploymentId, expectedETag);
}
