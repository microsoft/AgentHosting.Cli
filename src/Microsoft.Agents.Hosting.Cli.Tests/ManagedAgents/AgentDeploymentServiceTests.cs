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
public sealed class AgentDeploymentCreateServiceTests
{
    private static AgentDeploymentCreateService Create(JsonAgentConfigStore store, CopilotEnvironmentResolver? resolver = null) =>
        new(
            ManagedAgentFixtures.AgentResolver(store),
            store,
            resolver ?? ManagedAgentFixtures.Resolver(),
            ManagedAgentFixtures.Identities(),
            ManagedAgentFixtures.Principals());

    [Fact]
    public void Create_TakesNoCloudAgentCatalogDependency()
    {
        var parameters = typeof(AgentDeploymentCreateService).GetConstructors().Single().GetParameters();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(ICopilotCatalogPort));
    }

    [Fact]
    public async Task Create_WritesADevelopmentSlotInTheHomeEnvironment()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var result = await Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "dev", DeploymentName = "develop" },
            CancellationToken.None);

        Assert.Equal("env-1", result.EnvironmentId);
        Assert.Equal("env-1", result.TargetEnvironmentId);
        Assert.Equal("con_agent", result.SchemaName);
    }

    [Fact]
    public async Task Create_ForARecordWithNoEnvironment_NamesTheRecord()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent(environmentId: string.Empty));
        var (resolver, client) = ManagedAgentFixtures.TrackedResolver();
        var before = client.ListCalls;

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, resolver).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "dev", DeploymentName = "develop" },
            CancellationToken.None));

        Assert.Equal("agent-environment-missing", failure.ErrorKind);
        Assert.Contains("'Contoso Agent' (agent-1)", failure.Message, StringComparison.Ordinal);
        Assert.Equal(before, client.ListCalls);
        Assert.Empty(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Create_LeavesTheManagedAgentUntouched()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var before = store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        await Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "dev", DeploymentName = "develop" },
            CancellationToken.None);

        Assert.Equal(before, store.Read(workspace.Root).ManagedAgents[0]);
    }

    [Fact]
    public async Task Create_AcceptsAnEnvironmentOnADevelopmentSlot()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var resolver = ManagedAgentFixtures.Resolver(
            EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization),
            EnvironmentFixtures.Environment("env-2", instanceUrl: CopilotTestFixtures.DataverseOrganization));

        var result = await Create(store, resolver).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "dev", DeploymentName = "develop", EnvironmentId = "env-2" },
            CancellationToken.None);

        Assert.Equal("env-2", result.TargetEnvironmentId);
        Assert.Equal("env-2", result.EnvironmentId);
    }

    [Fact]
    public async Task Create_ForADevelopmentSlotInTheHomeEnvironment_StoresIt()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var result = await Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "dev", DeploymentName = "develop", EnvironmentId = "env-1" },
            CancellationToken.None);

        Assert.Equal("env-1", result.EnvironmentId);
        Assert.Equal("env-1", result.TargetEnvironmentId);
    }

    [Fact]
    public async Task Create_RequiresAnEnvironmentOnAProductionSlot()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        await Assert.ThrowsAsync<UsageException>(() => Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "prod", DeploymentName = "prod" },
            CancellationToken.None));
    }

    [Theory]
    [InlineData("a/bcd")]
    [InlineData("has space")]
    [InlineData("abc.d")]
    [InlineData("ab")]
    [InlineData("a234567890123456789012345678901234567890123")]
    public async Task Create_RejectsANameOutsideTheRuleBeforeWritingAnything(string deploymentName)
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, WorkflowFixtures.Record());

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "wf-1", DeploymentType = "dev", DeploymentName = deploymentName },
            CancellationToken.None));

        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Equal("--deployment-name must be between 4 and 42 characters in length and can only contain the characters -, a-z, A-Z, 0-9, and _.", failure.Message);
        Assert.Empty(store.Read(workspace.Root).AgentDeployments);
    }

    [Theory]
    [InlineData("agent-1")]
    [InlineData("wf-1")]
    public async Task Create_RejectsAThreeCharacterNameForAnMcsAgentOrWorkflow(string agentId)
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, WorkflowFixtures.Record(agentLocation: "flows"));

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = agentId, DeploymentType = "dev", DeploymentName = "dev" },
            CancellationToken.None));

        Assert.Equal("--deployment-name must be between 4 and 42 characters in length and can only contain the characters -, a-z, A-Z, 0-9, and _.", failure.Message);
        Assert.Empty(store.Read(workspace.Root).AgentDeployments);
    }

    [Theory]
    [InlineData("agent-1")]
    [InlineData("wf-1")]
    public async Task Create_AcceptsATrimmedFourCharacterNameForAnMcsAgentOrWorkflow(string agentId)
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, WorkflowFixtures.Record(agentLocation: "flows"));

        var result = await Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = agentId, DeploymentType = "dev", DeploymentName = " abcd " },
            CancellationToken.None);

        Assert.Equal("abcd", result.DeploymentName);
        Assert.Equal("abcd", store.Read(workspace.Root).AgentDeployments.Single().DeploymentName);
    }

    [Fact]
    public async Task Create_RejectsAnUnknownStage()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        await Assert.ThrowsAsync<UsageException>(() => Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "staging", DeploymentName = "staging" },
            CancellationToken.None));
    }

    [Fact]
    public async Task Create_WhenTheSchemaNameIsAlreadyInTheTarget_StillWritesTheRow()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        await Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "dev", DeploymentName = "develop" },
            CancellationToken.None);

        Assert.Single(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Create_ForAProjectBoundToAnotherEnvironment_StillWritesTheRow()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var result = await Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "dev", DeploymentName = "develop" },
            CancellationToken.None);

        Assert.Equal("con_agent", result.SchemaName);
        Assert.Single(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Create_ReadsTheSchemaNameFromTheProject()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var result = await Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "dev", DeploymentName = "develop" },
            CancellationToken.None);

        Assert.Equal("con_agent", result.SchemaName);
    }

    [Fact]
    public async Task Create_WithoutAProject_Fails()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        File.Delete(Path.Combine(workspace.Root, AgentConfigLocator.ProjectSettingsFileName));

        await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "dev", DeploymentName = "develop" },
            CancellationToken.None));
    }

    [Fact]
    public async Task Create_RejectsASecondSlotForTheSameStage()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var service = Create(store);

        await service.CreateAsync(new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "dev", DeploymentName = "develop" }, CancellationToken.None);

        await Assert.ThrowsAsync<ManagedAgentException>(() => service.CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "dev", DeploymentName = "dev-two" },
            CancellationToken.None));
    }

    [Fact]
    public async Task Create_ForProdWithAnAgentAlreadyInTheTarget_IsAccepted()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var resolver = ManagedAgentFixtures.Resolver(
            EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization),
            EnvironmentFixtures.Environment("env-2", instanceUrl: CopilotTestFixtures.DataverseOrganization));

        var result = await Create(store, resolver).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "prod", DeploymentName = "prod", EnvironmentId = "env-2" },
            CancellationToken.None);

        Assert.Equal("env-2", result.TargetEnvironmentId);
        Assert.Single(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Create_RecordsWhoCreatedTheDeployment()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        await Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "dev", DeploymentName = "develop" },
            CancellationToken.None);

        var deployment = store.Read(workspace.Root).AgentDeployments.Single();

        Assert.NotNull(deployment.CreatedBy);
        Assert.Equal(PrincipalObjectTypes.User, deployment.CreatedBy!.ObjectType);
    }

    [Fact]
    public async Task Create_ForTheFirstSlotIntoAnEnvironmentAlreadyHoldingTheAgent_IsAccepted()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var resolver = ManagedAgentFixtures.Resolver(
            EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization),
            EnvironmentFixtures.Environment("env-2", instanceUrl: CopilotTestFixtures.DataverseOrganization));

        await Create(store, resolver).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "test", DeploymentName = "test", EnvironmentId = "env-2" },
            CancellationToken.None);

        var stored = store.Read(workspace.Root).AgentDeployments.Single();

        Assert.Equal("test", stored.DeploymentType);
        Assert.Equal("env-2", stored.EnvironmentId);
        Assert.Equal("env-1", stored.HomeEnvironmentId);
    }

    [Fact]
    public async Task Create_ForProdInAnEnvironmentTheAgentAlreadyOwns_IsNotTreatedAsUnmanaged()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-1", name: "dev"));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-1", DeploymentType = "prod", DeploymentName = "prod", EnvironmentId = "env-1" },
            CancellationToken.None));

        Assert.Equal("deployment-already-exists", failure.ErrorKind);
    }

    [Fact]
    public async Task Create_WithoutAnAgentId_UsesTheProjectsAgent()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var result = await Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { DeploymentType = "dev", DeploymentName = "develop" },
            CancellationToken.None);

        Assert.Equal("agent-1", result.AgentId);
    }

    [Fact]
    public async Task Create_WithAnAgentIdTheProjectDoesNotHold_Fails()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentId = "agent-9", DeploymentType = "dev", DeploymentName = "develop" },
            CancellationToken.None));

        Assert.Equal("agent-id-conflict", failure.ErrorKind);
    }

    [Fact]
    public async Task Create_WithAnAgentName_UsesThatAgent()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        workspace.AddFile(Path.Combine("second", AgentConfigLocator.ProjectSettingsFileName), "displayName: \"Second\"\nschemaName: \"con_second\"\n");
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Agent(agentId: "agent-2", displayName: "Second") with { AgentLocation = "second" });

        var result = await Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentName = "Second", DeploymentType = "dev", DeploymentName = "develop" },
            CancellationToken.None);

        Assert.Equal("agent-2", result.AgentId);
        Assert.Equal("con_second", result.SchemaName);
    }

    [Fact]
    public async Task Create_WithAnAgentNameTheProjectDoesNotHold_Fails()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store).CreateAsync(
            new AgentDeploymentCreateRequest { AgentName = "Nobody", DeploymentType = "dev", DeploymentName = "develop" },
            CancellationToken.None));

        Assert.Equal("agent-not-found", failure.ErrorKind);
    }

    [Fact]
    public async Task Create_WithNoAgentRegistered_Fails()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<ManagedAgentException>(() => Create(ManagedAgentFixtures.Store()).CreateAsync(
            new AgentDeploymentCreateRequest { DeploymentType = "dev", DeploymentName = "develop" },
            CancellationToken.None));
    }
}

[Collection("CurrentDirectory")]
public sealed class AgentDeploymentListServiceTests
{
    [Fact]
    public void List_ReturnsSlotsInPromotionOrder()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-prod", name: "prod", stage: DeploymentStages.Prod, environmentId: "env-prod"));
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-dev", name: "dev"));
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-test", name: "test", stage: DeploymentStages.Test, environmentId: "env-test"));

        var result = new AgentDeploymentListService(ManagedAgentFixtures.AgentResolver(store)).List(new AgentDeploymentListRequest { AgentId = "agent-1" });

        Assert.Equal(["dev", "test", "prod"], result.Slots.Select(slot => slot.DeploymentType));
    }

    [Fact]
    public void List_ForAnAgentWithNoSlots_ReturnsAnEmptyArray()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        Assert.Empty(new AgentDeploymentListService(ManagedAgentFixtures.AgentResolver(store)).List(new AgentDeploymentListRequest { AgentId = "agent-1" }).Slots);
    }

    [Fact]
    public void List_ReturnsOnlyTheRequestedAgentsSlots()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-1", "agent-1", "dev"));
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", "agent-1", "test", DeploymentStages.Test, "env-test"));

        var result = new AgentDeploymentListService(ManagedAgentFixtures.AgentResolver(store)).List(new AgentDeploymentListRequest { AgentId = "agent-1" });

        Assert.Equal(["slot-1", "slot-2"], result.Slots.Select(slot => slot.DeploymentId));
    }

    [Fact]
    public void List_WithNoConfiguration_Fails()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = Assert.Throws<ManagedAgentException>(() => new AgentDeploymentListService(ManagedAgentFixtures.AgentResolver(ManagedAgentFixtures.Store()))
            .List(new AgentDeploymentListRequest { AgentId = "agent-1" }));

        Assert.Equal("config-not-found", failure.ErrorKind);
    }

    [Fact]
    public void List_WithAnUnknownAgent_Fails()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var failure = Assert.Throws<ManagedAgentException>(() => new AgentDeploymentListService(ManagedAgentFixtures.AgentResolver(store))
            .List(new AgentDeploymentListRequest { AgentId = "nope" }));

        Assert.Equal("agent-id-conflict", failure.ErrorKind);
    }

    [Fact]
    public void List_WithoutAnAgentId_ListsEveryAgentsDeployments()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(deploymentId: "slot-1", name: "dev"));

        var listed = new AgentDeploymentListService(ManagedAgentFixtures.AgentResolver(store)).List(new AgentDeploymentListRequest());

        Assert.Null(listed.AgentId);
        Assert.Equal("agent-1", Assert.Single(listed.Slots).AgentId);
        Assert.Equal("Contoso Agent", Assert.Single(listed.Slots).AgentName);
    }
}

[Collection("CurrentDirectory")]
public sealed class AgentDeploymentShowServiceTests
{
    private static AgentDeploymentShowService Create(JsonAgentConfigStore store) =>
        new(ManagedAgentFixtures.AgentResolver(store));

    [Fact]
    public void Show_SelectsByAgentName()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-dev", name: "dev"));

        var result = Create(store).Show(new AgentDeploymentShowRequest { AgentName = "Contoso Agent", DeploymentName = "dev" });

        Assert.Equal("slot-dev", result.DeploymentId);
    }

    [Fact]
    public void Show_ByAgentName_MatchesTheSameRowAsByAgentId()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-dev", name: "dev"));

        var byName = Create(store).Show(new AgentDeploymentShowRequest { AgentName = "Contoso Agent", DeploymentName = "dev" });
        var byId = Create(store).Show(new AgentDeploymentShowRequest { AgentId = "agent-1", DeploymentName = "dev" });

        Assert.Equal(byId.DeploymentId, byName.DeploymentId);
        Assert.Equal(byId.AgentId, byName.AgentId);
    }

    [Fact]
    public void Show_WithOnlyAnAgentName_FindsItsSingleDeployment()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-dev", name: "dev"));

        Assert.Equal("slot-dev", Create(store).Show(new AgentDeploymentShowRequest { AgentName = "Contoso Agent" }).DeploymentId);
    }

    [Fact]
    public void Show_WithANameTwoAgentsShare_IsResolvedByTheAgentName()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentId = "agent-2", DisplayName = "Second", AgentLocation = "Second" });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-1", name: "dev"));
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", agentId: "agent-2", name: "dev"));

        var result = Create(store).Show(new AgentDeploymentShowRequest { AgentName = "Second", DeploymentName = "dev" });

        Assert.Equal("slot-2", result.DeploymentId);
        Assert.Equal("agent-2", result.AgentId);
    }

    [Fact]
    public void Show_WithAnUnknownAgentName_Fails()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-dev", name: "dev"));

        Assert.Throws<ManagedAgentException>(() => Create(store).Show(new AgentDeploymentShowRequest { AgentName = "Nope", DeploymentName = "dev" }));
    }

    [Fact]
    public void Show_SelectsByAgentIdOverAgentName()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentId = "agent-2", DisplayName = "Second", AgentLocation = "Second" });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-1", name: "dev"));
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", agentId: "agent-2", name: "dev"));

        var result = Create(store).Show(new AgentDeploymentShowRequest { AgentId = "agent-1", AgentName = "Second", DeploymentName = "dev" });

        Assert.Equal("slot-1", result.DeploymentId);
    }
}

[Collection("CurrentDirectory")]
public sealed class AgentDeploymentUpdateServiceTests
{
    private static AgentDeploymentUpdateService Create(JsonAgentConfigStore store) =>
        new(ManagedAgentFixtures.AgentResolver(store), store, new AgentConfigLocator());

    private static (TemporaryWorkspace Workspace, JsonAgentConfigStore Store) Registered()
    {
        var workspace = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());

        return (workspace, store);
    }

    [Fact]
    public async Task Update_RenamesTheDeployment()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(store).UpdateAsync(
            new AgentDeploymentUpdateRequest { AgentId = "agent-1", DeploymentName = "dev", NewName = "Shared-development" },
            CancellationToken.None);

        Assert.Equal("dev", result.PreviousDeploymentName);
        Assert.Equal("Shared-development", result.DeploymentName);
        Assert.Equal("Shared-development", store.Read(workspace.Root).AgentDeployments.Single().DeploymentName);
    }

    [Theory]
    [InlineData("a/bcd")]
    [InlineData("has space")]
    [InlineData("abc.d")]
    [InlineData("abc")]
    [InlineData("DEV")]
    [InlineData("a234567890123456789012345678901234567890123")]
    public async Task Update_ToANameOutsideTheRule_IsAUsageError(string newName)
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store).UpdateAsync(
            new AgentDeploymentUpdateRequest { AgentId = "agent-1", DeploymentName = "dev", NewName = newName },
            CancellationToken.None));

        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Equal("--new-name must be between 4 and 42 characters in length and can only contain the characters -, a-z, A-Z, 0-9, and _.", failure.Message);
        Assert.Equal("dev", store.Read(workspace.Root).AgentDeployments.Single().DeploymentName);
    }

    [Fact]
    public async Task Update_ToAThreeCharacterName_IsAUsageErrorForAWorkflow()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, WorkflowFixtures.Record());
        store.Add(workspace.Root, WorkflowFixtures.Deployment());

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store).UpdateAsync(
            new AgentDeploymentUpdateRequest { AgentId = "wf-1", DeploymentName = "devwf", NewName = "abc" },
            CancellationToken.None));

        Assert.Equal("--new-name must be between 4 and 42 characters in length and can only contain the characters -, a-z, A-Z, 0-9, and _.", failure.Message);
        Assert.Equal("devwf", store.Read(workspace.Root).AgentDeployments.Single().DeploymentName);
    }

    [Fact]
    public async Task Update_RenamesAWorkflowSlotAnEarlierCloneNamedDev()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, WorkflowFixtures.Record());
        store.Add(workspace.Root, WorkflowFixtures.Deployment(deploymentName: "dev"));

        var result = await Create(store).UpdateAsync(
            new AgentDeploymentUpdateRequest { AgentId = "wf-1", DeploymentName = "dev", NewName = "develop" },
            CancellationToken.None);

        Assert.Equal("dev", result.PreviousDeploymentName);
        Assert.Equal("develop", store.Read(workspace.Root).AgentDeployments.Single().DeploymentName);
    }

    [Fact]
    public async Task Update_StillResolvesASlotWhoseNameTheRuleNowRejects()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment(name: "x y"));

        var result = await Create(store).UpdateAsync(
            new AgentDeploymentUpdateRequest { AgentId = "agent-1", DeploymentName = "x y", NewName = "x y" },
            CancellationToken.None);

        Assert.Equal("x y", result.DeploymentName);
    }

    [Fact]
    public async Task Update_LeavesTheStageAndEnvironmentAlone()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var before = store.Read(workspace.Root).AgentDeployments.Single();

        var result = await Create(store).UpdateAsync(
            new AgentDeploymentUpdateRequest { AgentId = "agent-1", DeploymentId = "slot-1", NewName = "Renamed" },
            CancellationToken.None);

        Assert.Equal(before.DeploymentType, result.DeploymentType);
        Assert.Equal(before.EnvironmentId, result.EnvironmentId);
    }

    [Fact]
    public async Task Update_SelectsByIdOverName()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", name: "test", stage: DeploymentStages.Test, environmentId: "env-test"));

        var result = await Create(store).UpdateAsync(
            new AgentDeploymentUpdateRequest { AgentId = "agent-1", DeploymentId = "slot-2", DeploymentName = "dev", NewName = "Renamed" },
            CancellationToken.None);

        Assert.Equal("slot-2", result.DeploymentId);
    }

    [Fact]
    public async Task Update_ToANameAnotherDeploymentHolds_IsRejected()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", name: "test", stage: DeploymentStages.Test, environmentId: "env-test"));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store).UpdateAsync(
            new AgentDeploymentUpdateRequest { AgentId = "agent-1", DeploymentId = "slot-1", NewName = "TEST" },
            CancellationToken.None));

        Assert.Equal("deployment-already-exists", failure.ErrorKind);
        Assert.Equal("dev", store.Read(workspace.Root).AgentDeployments.Single(deployment => deployment.DeploymentId == "slot-1").DeploymentName);
    }

    [Fact]
    public async Task Update_ToTheSameName_ChangesNothing()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var before = store.Read(workspace.Root).AgentDeployments.Single();

        var result = await Create(store).UpdateAsync(
            new AgentDeploymentUpdateRequest { AgentId = "agent-1", DeploymentName = "dev", NewName = "dev" },
            CancellationToken.None);

        Assert.Equal("dev", result.DeploymentName);
        Assert.Equal(before.EntityTag, store.Read(workspace.Root).AgentDeployments.Single().EntityTag);
    }

    [Fact]
    public async Task Update_WithoutANewName_IsAUsageError()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<UsageException>(() => Create(store).UpdateAsync(
            new AgentDeploymentUpdateRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None));
    }

    [Theory]
    [InlineData("https://tunnel.example.com")]
    [InlineData("null")]
    public async Task Update_WithAnEndpointUrl_IsAUsageError(string endpointUrl)
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store).UpdateAsync(
            new AgentDeploymentUpdateRequest { AgentId = "agent-1", DeploymentName = "dev", NewName = "Renamed", EndpointUrl = endpointUrl },
            CancellationToken.None));

        Assert.Contains("--endpoint-url", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_WithOnlyAnAgentSelector_RenamesItsSingleDeployment()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(store).UpdateAsync(
            new AgentDeploymentUpdateRequest { AgentId = "agent-1", NewName = "Renamed" },
            CancellationToken.None);

        Assert.Equal("slot-1", result.DeploymentId);
        Assert.Equal("Renamed", result.DeploymentName);
    }

    [Fact]
    public async Task Update_WithOnlyADeploymentName_FindsItsOwningAgent()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentId = "agent-2", DisplayName = "Second", AgentLocation = "Second" });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", agentId: "agent-2", name: "other"));

        var result = await Create(store).UpdateAsync(
            new AgentDeploymentUpdateRequest { DeploymentName = "other", NewName = "Renamed" },
            CancellationToken.None);

        Assert.Equal("agent-2", result.AgentId);
        Assert.Equal("slot-2", result.DeploymentId);
    }

    [Fact]
    public async Task Update_WithANameTwoAgentsShare_IsAmbiguous()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentId = "agent-2", DisplayName = "Second", AgentLocation = "Second" });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", agentId: "agent-2", name: "dev"));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store).UpdateAsync(
            new AgentDeploymentUpdateRequest { DeploymentName = "dev", NewName = "Renamed" },
            CancellationToken.None));

        Assert.Equal("deployment-ambiguous", failure.ErrorKind);
        Assert.Contains("narrow it with --agent-id or --agent-name", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void SlotAmbiguous_WithoutASelector_DoesNotAskForTheAgentThatIsAlreadyNamed()
    {
        var failure = ManagedAgentException.SlotAmbiguous(null, ["dev (slot-1)", "dev2 (slot-2)"]);

        Assert.Equal("Name the deployment with --deployment-id or --deployment-name.", failure.Remediation);
        Assert.DoesNotContain("--agent-id", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_WithAnUnknownDeployment_IsRejected()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store).UpdateAsync(
            new AgentDeploymentUpdateRequest { AgentId = "agent-1", DeploymentName = "absent", NewName = "Renamed" },
            CancellationToken.None));
    }
}

[Collection("CurrentDirectory")]
public sealed class AgentDeploymentDeleteServiceTests
{
    private static AgentDeploymentDeleteService Create(JsonAgentConfigStore store, FakeCopilotCatalogPort catalog, IConfirmationPrompt confirmations, CopilotEnvironmentResolver? environments = null) =>
        new(ManagedAgentFixtures.AgentResolver(store), store, store, environments ?? ManagedAgentFixtures.Resolver(), catalog, ManagedAgentFixtures.Identities(), confirmations, new AgentConfigLocator());

    private static (TemporaryWorkspace Workspace, JsonAgentConfigStore Store) Registered(string? mcsAgentId = null)
    {
        var workspace = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { McsSchemaName = "con_agent", McsAgentId = mcsAgentId });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());

        return (workspace, store);
    }

    [Fact]
    public async Task Delete_RemovesOnlyTheDeploymentRecord()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "cloud-1" };

        var result = await Create(store, catalog, new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("deployment-deleted", result.Status);
        Assert.Empty(store.Read(workspace.Root).AgentDeployments);
        Assert.Single(store.List(workspace.Root));
        Assert.Equal(0, catalog.DeleteCalls);
    }

    [Fact]
    public async Task Delete_WithoutTheFlag_NeverLooksUpTheCloudAgent()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "cloud-1" };

        await Create(store, catalog, new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Empty(catalog.ResolvedValues);
    }

    [Fact]
    public async Task Delete_WithTheFlagForAWorkflow_IsAUsageErrorThatPointsAtAgentDelete()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentType = AgentTypes.McsWorkflow, McsSchemaName = null, McsWorkflowId = Guid.NewGuid().ToString() });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment() with { AgentType = AgentTypes.McsWorkflow });
        var catalog = new FakeCopilotCatalogPort();

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store, catalog, new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1", DeploymentName = "dev", DeleteDeployedMcsAgent = true },
            CancellationToken.None));

        Assert.Contains("msagent agent delete", failure.Message, StringComparison.Ordinal);
        Assert.Single(store.Read(workspace.Root).AgentDeployments);
        Assert.Equal(0, catalog.DeleteCalls);
    }

    [Fact]
    public async Task Delete_WithoutTheFlagForAWorkflow_RemovesTheDeploymentRecord()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentType = AgentTypes.McsWorkflow, McsSchemaName = null, McsWorkflowId = Guid.NewGuid().ToString() });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment() with { AgentType = AgentTypes.McsWorkflow });

        var result = await Create(store, new FakeCopilotCatalogPort(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("deployment-deleted", result.Status);
        Assert.Empty(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Delete_WithTheFlag_DeletesTheCloudAgentFirst()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "cloud-1" };

        var result = await Create(store, catalog, new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1", DeploymentName = "dev", DeleteDeployedMcsAgent = true },
            CancellationToken.None);

        Assert.Equal("cloud-1", result.Deployments.Single().DeletedMcsAgentId);
        Assert.Equal(1, catalog.DeleteCalls);
        Assert.Empty(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Delete_WithTheFlagWhenTheCloudAgentIsAbsent_StillRemovesTheRecord()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = null };

        var result = await Create(store, catalog, new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1", DeploymentName = "dev", DeleteDeployedMcsAgent = true },
            CancellationToken.None);

        Assert.True(result.Deployments.Single().McsAgentAlreadyAbsent);
        Assert.Equal(0, catalog.DeleteCalls);
        Assert.Empty(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Delete_WithTheFlag_ClearsTheAgentsRecordedCloudId()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Update(workspace.Root, store.List(workspace.Root).Single() with { McsAgentId = "cloud-1" });
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "cloud-1" };

        await Create(store, catalog, new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1", DeploymentName = "dev", DeleteDeployedMcsAgent = true },
            CancellationToken.None);

        Assert.Null(store.List(workspace.Root).Single().McsAgentId);
        Assert.Equal("con_agent", store.List(workspace.Root).Single().McsSchemaName);
    }

    [Fact]
    public async Task Delete_WhenTheRecordNamesAnotherCloudAgent_KeepsIt()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Update(workspace.Root, store.List(workspace.Root).Single() with { McsAgentId = "cloud-elsewhere" });
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "cloud-1" };

        await Create(store, catalog, new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1", DeploymentName = "dev", DeleteDeployedMcsAgent = true },
            CancellationToken.None);

        Assert.Equal("cloud-elsewhere", store.List(workspace.Root).Single().McsAgentId);
    }

    [Fact]
    public async Task Delete_WithoutTheFlag_KeepsTheAgentsRecordedCloudId()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Update(workspace.Root, store.List(workspace.Root).Single() with { McsAgentId = "cloud-1" });

        await Create(store, new FakeCopilotCatalogPort { ResolveReturns = "cloud-1" }, new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("cloud-1", store.List(workspace.Root).Single().McsAgentId);
    }

    [Fact]
    public async Task Delete_WhenTheCloudDeleteFails_KeepsTheRecord()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "cloud-1", DeleteThrows = new InvalidOperationException("the service is unavailable") };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, catalog, new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1", DeploymentName = "dev", DeleteDeployedMcsAgent = true },
            CancellationToken.None));

        Assert.Equal("deployment-target-delete-failed", failure.ErrorKind);
        Assert.Single(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Delete_WhenConfirmationIsDeclined_DeletesNothing()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "cloud-1" };

        var result = await Create(store, catalog, new ScriptedConfirmationPrompt { Outcome = ConfirmationOutcome.Declined }).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1", DeploymentName = "dev", DeleteDeployedMcsAgent = true },
            CancellationToken.None);

        Assert.Equal("cancelled", result.Status);
        Assert.Single(store.Read(workspace.Root).AgentDeployments);
        Assert.Equal(0, catalog.DeleteCalls);
    }

    [Fact]
    public async Task Delete_WhenTheTypedNameDoesNotMatch_DeletesNothing()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(store, new FakeCopilotCatalogPort(), new ScriptedConfirmationPrompt { Outcome = ConfirmationOutcome.Mismatched }).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("name-mismatch", result.Status);
        Assert.Single(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Delete_NonInteractive_DeletesWithoutPrompting()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "cloud-1" };
        var prompt = new ScriptedConfirmationPrompt { Throws = true };

        var result = await Create(store, catalog, prompt).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1", DeploymentName = "dev", DeleteDeployedMcsAgent = true, NonInteractive = true },
            CancellationToken.None);

        Assert.Equal(0, prompt.Calls);
        Assert.Equal("deployment-deleted", result.Status);
        Assert.Equal(1, catalog.DeleteCalls);
        Assert.Empty(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Delete_WithoutAConfiguration_SaysSo()
    {
        using var project = new TemporaryProject();
        using var scope = new CurrentDirectoryScope(project.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { DeploymentName = "dev" },
            CancellationToken.None));

        Assert.Equal("config-not-found", failure.ErrorKind);
    }

    [Fact]
    public async Task Delete_WhenTheRecordCannotBeRemovedAfterTheCloudDelete_ReportsWhatIsAlreadyGone()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "cloud-1" };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => new AgentDeploymentDeleteService(
            ManagedAgentFixtures.AgentResolver(store),
            store,
            new UnremovableDeploymentRegistry(store),
            ManagedAgentFixtures.Resolver(),
            catalog,
            ManagedAgentFixtures.Identities(),
            new ScriptedConfirmationPrompt(),
            new AgentConfigLocator()).DeleteAsync(
                new AgentDeploymentDeleteRequest { AgentId = "agent-1", DeploymentName = "dev", DeleteDeployedMcsAgent = true },
                CancellationToken.None));

        Assert.Equal("records-survived-cloud-delete", failure.ErrorKind);
        Assert.Equal(1, catalog.DeleteCalls);
    }

    [Fact]
    public async Task Delete_KeepsTheAgentsOtherDeployments()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", name: "test", stage: DeploymentStages.Test, environmentId: "env-test"));

        await Create(store, new FakeCopilotCatalogPort(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1", DeploymentId = "slot-1" },
            CancellationToken.None);

        Assert.Equal(["slot-2"], store.Read(workspace.Root).AgentDeployments.Select(deployment => deployment.DeploymentId));
    }

    [Fact]
    public async Task Delete_WithOnlyAnAgentSelector_DeletesItsSingleDeployment()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(store, new FakeCopilotCatalogPort(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal("slot-1", result.Deployments.Single().DeploymentId);
        Assert.Empty(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Delete_WithOnlyAnAgentSelector_DeletesEveryDeploymentItOwns()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", name: "test", stage: DeploymentStages.Test, environmentId: "env-test"));
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-3", name: "prod", stage: DeploymentStages.Prod, environmentId: "env-prod"));

        var result = await Create(store, new FakeCopilotCatalogPort(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal(["dev", "test", "prod"], result.Deployments.Select(deployment => deployment.DeploymentName));
        Assert.Empty(store.Read(workspace.Root).AgentDeployments);
        Assert.Single(store.List(workspace.Root));
    }

    [Fact]
    public async Task Delete_WithOnlyAnAgentSelector_ConfirmsAgainstTheAgentName()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", name: "test", stage: DeploymentStages.Test, environmentId: "env-test"));
        var prompt = new ScriptedConfirmationPrompt();

        await Create(store, new FakeCopilotCatalogPort(), prompt).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Contains("all 2 deployments", prompt.LastDetail, StringComparison.Ordinal);
        Assert.Contains("Type Contoso Agent to confirm", prompt.LastDetail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("cloud-1")]
    public async Task Delete_WithOnlyAnAgentSelectorAndTheFlag_DeletesEachEnvironmentsCloudAgentAndForgetsTheRecordedOne(string? recordedMcsAgentId)
    {
        var (workspace, store) = Registered(recordedMcsAgentId);
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", name: "test", stage: DeploymentStages.Test, environmentId: "env-test"));
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "cloud-1" };
        var environments = ManagedAgentFixtures.Resolver(
            EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization),
            EnvironmentFixtures.Environment("env-test", instanceUrl: CopilotTestFixtures.DataverseOrganization));

        var result = await Create(store, catalog, new ScriptedConfirmationPrompt(), environments).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1", DeleteDeployedMcsAgent = true },
            CancellationToken.None);

        Assert.Equal("deployment-deleted", result.Status);
        Assert.Equal(2, catalog.DeleteCalls);
        Assert.All(result.Deployments, deployment => Assert.Equal("cloud-1", deployment.DeletedMcsAgentId));
        Assert.Empty(store.Read(workspace.Root).AgentDeployments);
        Assert.Null(Assert.Single(store.List(workspace.Root)).McsAgentId);
    }

    [Fact]
    public async Task Delete_WithOnlyAnAgentSelectorWhenDeclined_DeletesNothing()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", name: "test", stage: DeploymentStages.Test, environmentId: "env-test"));

        var result = await Create(store, new FakeCopilotCatalogPort(), new ScriptedConfirmationPrompt { Outcome = ConfirmationOutcome.Declined }).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal("cancelled", result.Status);
        Assert.Equal(2, store.Read(workspace.Root).AgentDeployments.Count);
    }

    [Fact]
    public async Task Delete_WithOnlyADeploymentName_FindsItsOwningAgent()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentId = "agent-2", DisplayName = "Second", AgentLocation = "Second" });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", agentId: "agent-2", name: "other"));

        var result = await Create(store, new FakeCopilotCatalogPort(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { DeploymentName = "other" },
            CancellationToken.None);

        Assert.Equal("agent-2", result.AgentId);
        Assert.Equal("slot-2", result.Deployments.Single().DeploymentId);
        Assert.Equal(["slot-1"], store.Read(workspace.Root).AgentDeployments.Select(deployment => deployment.DeploymentId));
    }

    [Fact]
    public async Task Delete_WithOnlyADeploymentId_FindsItsOwningAgent()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentId = "agent-2", DisplayName = "Second", AgentLocation = "Second" });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", agentId: "agent-2", name: "other"));

        var result = await Create(store, new FakeCopilotCatalogPort(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { DeploymentId = "slot-2" },
            CancellationToken.None);

        Assert.Equal("agent-2", result.AgentId);
    }

    [Fact]
    public async Task Delete_WithANameTwoAgentsShare_IsAmbiguous()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentId = "agent-2", DisplayName = "Second", AgentLocation = "Second" });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", agentId: "agent-2", name: "dev"));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, new FakeCopilotCatalogPort(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { DeploymentName = "dev" },
            CancellationToken.None));

        Assert.Equal("deployment-ambiguous", failure.ErrorKind);
        Assert.Equal(2, store.Read(workspace.Root).AgentDeployments.Count);
    }

    [Fact]
    public async Task Delete_WithANameTwoAgentsShare_IsResolvedByTheAgentSelector()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentId = "agent-2", DisplayName = "Second", AgentLocation = "Second" });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", agentId: "agent-2", name: "dev"));

        var result = await Create(store, new FakeCopilotCatalogPort(), new ScriptedConfirmationPrompt()).DeleteAsync(
            new AgentDeploymentDeleteRequest { AgentId = "agent-2", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("slot-2", result.Deployments.Single().DeploymentId);
    }
}

internal sealed class UnremovableDeploymentRegistry : IAgentDeploymentRegistry
{
    private readonly IAgentDeploymentRegistry _inner;

    public UnremovableDeploymentRegistry(IAgentDeploymentRegistry inner) => _inner = inner;

    public AgentDeploymentRecord Add(string projectDirectory, AgentDeploymentRecord record) => _inner.Add(projectDirectory, record);

    public IReadOnlyList<AgentDeploymentRecord> List(string projectDirectory) => _inner.List(projectDirectory);

    public AgentDeploymentRecord Update(string projectDirectory, AgentDeploymentRecord record) => _inner.Update(projectDirectory, record);

    public void Remove(string projectDirectory, string deploymentId, string expectedETag) =>
        throw new IOException("the configuration file is locked by another process");
}
