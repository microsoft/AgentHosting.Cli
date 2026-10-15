using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentServiceTests
{
    private const string AgentId = "44444444-4444-4444-4444-444444444444";
    private const string AppId = "55555555-5555-5555-5555-555555555555";
    private const string ProvisionedId = "11111111-1111-1111-1111-111111111111";
    private const string SecondId = "33333333-3333-3333-3333-333333333333";
    private const string UnknownId = "99999999-9999-9999-9999-999999999999";

    private static DeploymentService Service(
        out string directory,
        FakeSubscriptionResolver? subscriptionResolver = null,
        RecordingBotNameProbe? nameProbe = null,
        CapturingBotProvisioningPort? bots = null,
        FakeResourceGroupResolver? resourceGroupResolver = null)
    {
        directory = Path.Combine(Path.GetTempPath(), "ah-deployment-service-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var botProvisioning = bots ?? new CapturingBotProvisioningPort();
        var botNameAvailability = nameProbe ?? new RecordingBotNameProbe();
        var subscriptions = subscriptionResolver ?? new FakeSubscriptionResolver();
        var resourceGroups = resourceGroupResolver
            ?? new FakeResourceGroupResolver
            {
                Result = new AzureResourceGroupInfo { Name = "rg-1", Location = "global" },
            };
        return new DeploymentService(
            ProjectStore(directory),
            new CompositeDeploymentTransport(
                botProvisioning,
                botNameAvailability,
                subscriptions,
                resourceGroups));
    }

    private static PinnedProjectStore ProjectStore(string directory)
    {
        var locator = new AgentConfigLocator();
        return new PinnedProjectStore(new ProjectDeploymentStore(new JsonAgentConfigStore(locator, new ServiceTestClock()), locator), directory);
    }

    private sealed class PinnedProjectStore : IDeploymentStore
    {
        private readonly IDeploymentStore _inner;
        private readonly string _project;

        public PinnedProjectStore(IDeploymentStore inner, string project)
        {
            _inner = inner;
            _project = project;
        }

        public IReadOnlyList<CodeAgentDeployment> List(string? project) => _inner.List(_project);

        public void Upsert(string? project, CodeAgentDeployment deployment) => _inner.Upsert(_project, deployment);

        public void Remove(string? project, string deploymentId) => _inner.Remove(_project, deploymentId);
    }

    private sealed class ServiceTestClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static DeploymentService NewService(
        IDeploymentStore store,
        CapturingBotProvisioningPort botProvisioning) =>
        new(
            store,
            new CompositeDeploymentTransport(
                botProvisioning,
                resourceGroupResolver: new FakeResourceGroupResolver
                {
                    Result = new AzureResourceGroupInfo { Name = "rg-1", Location = "global" },
                }));

    private static DeploymentCreateRequest ValidCreate(
        string name = "dev1",
        string? type = "Dev",
        string? resourceGroup = "rg-1",
        string? entraId = AppId,
        string? entraIdType = "AppReg",
        string? subscription = null,
        string? tenant = null) => new()
        {
            AgentId = AgentId,
            DeploymentName = name,
            DeploymentType = type!,
            EntraId = entraId!,
            EntraIdType = entraIdType!,
            ResourceGroup = resourceGroup,
            SubscriptionId = subscription,
            TenantId = tenant,
        };

    private static CodeAgentDeployment ProvisionedRecord(
        string id = ProvisionedId,
        string agentId = "44444444-4444-4444-4444-444444444444",
        string name = "dev") => new()
    {
        DeploymentId = id,
        AgentId = agentId,
        DeploymentName = name,
        DeploymentType = DeploymentType.Dev,
        Bot = new DeploymentBotResource
        {
            ResourceId = $"/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/{name}",
            Name = name,
            TenantId = "tenant-1",
            Sku = "F0",
            Location = "global",
            DisplayName = name,
            EntraId = "app-1",
            EntraIdType = "AppReg",
            Endpoint = "https://old/api/messages",
        },
    };

    private static DeploymentBotResource LiveBot(string name = "dev", string appId = "app-1", string endpoint = "https://gw/api/messages") => new()
    {
        Name = name,
        EntraId = appId,
        EntraIdType = "AppReg",
        ResourceId = $"/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/{name}",
        TenantId = "tenant-1",
        Sku = "S1",
        Location = "westus",
        DisplayName = $"{name} display",
        Description = "from ARM",
        Endpoint = endpoint,
    };

    [Fact]
    public async Task CreateAsync_MissingAgent_ThrowsUsage()
    {
        var service = Service(out _);
        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            service.CreateAsync(ValidCreate() with { AgentId = "" }, CancellationToken.None));
        Assert.Contains("--agent-id", ex.Message);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("dev bot")]
    [InlineData("dev.bot")]
    public async Task CreateAsync_InvalidName_ThrowsUsage(string name)
    {
        var service = Service(out _);
        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            service.CreateAsync(ValidCreate(name: name), CancellationToken.None));
        Assert.Contains("between 4 and 42", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_MissingEntraId_ThrowsUsageBeforeArm()
    {
        var bots = new CapturingBotProvisioningPort();
        var service = Service(out _, bots: bots);

        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            service.CreateAsync(ValidCreate(entraId: " "), CancellationToken.None));

        Assert.Contains("--entra-id cannot be empty", ex.Message);
        Assert.Empty(bots.CreateRequests);
    }

    [Fact]
    public async Task CreateAsync_UnsupportedEntraIdType_ThrowsUsageBeforeArm()
    {
        var bots = new CapturingBotProvisioningPort();
        var service = Service(out _, bots: bots);

        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            service.CreateAsync(ValidCreate(entraIdType: "Blueprint"), CancellationToken.None));

        Assert.Contains("--entra-id-type is not supported", ex.Message);
        Assert.Empty(bots.CreateRequests);
    }

    [Fact]
    public async Task CreateAsync_HappyPath_ChecksNameCreatesBotAndPersistsRecord()
    {
        var subscription = new FakeSubscriptionResolver { Result = new AzureSubscriptionInfo { SubscriptionId = "sub-1", TenantId = "tenant-1" } };
        var rg = new FakeResourceGroupResolver { Result = new AzureResourceGroupInfo { Name = "rg-1", Location = "global" } };
        var probe = new RecordingBotNameProbe();
        var bots = new CapturingBotProvisioningPort();
        var service = Service(out var dir, subscription, probe, bots, rg);

        var result = await service.CreateAsync(ValidCreate(), CancellationToken.None);

        Assert.Equal(DeploymentOperationStatus.Created, result.Status);
        Assert.Equal(DeterministicDeploymentId.Compute(AgentId, "dev1").ToString(), result.Payload!.DeploymentId);
        Assert.Equal("dev1", Assert.Single(probe.CheckedNames));
        var create = Assert.Single(bots.CreateRequests);
        Assert.Equal("sub-1", create.SubscriptionId);
        Assert.Equal("rg-1", create.ResourceGroup);
        Assert.Equal("tenant-1", create.TenantId);
        Assert.Equal(AppId, create.EntraId);

        var stored = Assert.Single(ProjectStore(dir).List(null));
        Assert.Equal(result.Payload.DeploymentId, stored.DeploymentId);
        Assert.Equal(AppId, stored.Bot.EntraId);
        Assert.Equal("AppReg", stored.Bot.EntraIdType);
        Assert.Equal("F0", stored.Bot.Sku);
        Assert.Equal("global", stored.Bot.Location);
        Assert.Contains("/botServices/dev1", stored.Bot.ResourceId);
    }

    [Fact]
    public async Task CreateAsync_NameUnavailable_ReadsAndAdoptsExistingBot()
    {
        var probe = new RecordingBotNameProbe { IsAvailable = false, UnavailableMessage = "already taken" };
        var bots = new CapturingBotProvisioningPort { ExistingBot = LiveBot("dev1", AppId, "https://live/api/messages") };
        var service = Service(out var dir, nameProbe: probe, bots: bots);

        var result = await service.CreateAsync(ValidCreate(), CancellationToken.None);

        Assert.Equal(DeploymentOperationStatus.Existed, result.Status);
        Assert.Empty(bots.CreateRequests);
        Assert.Equal("https://live/api/messages", result.Payload!.Bot.Endpoint);
        Assert.Equal("https://live/api/messages", Assert.Single(ProjectStore(dir).List(null)).Bot.Endpoint);
    }

    [Fact]
    public async Task CreateAsync_ExistingRecord_ReusesPinnedCoordinates_WithoutResolvingLiveInventory()
    {
        // An identical create retry when a record already exists must reconcile against the record's
        // pinned subscription/resource-group/tenant instead of resolving from live Azure inventory — so
        // the retry stays unambiguous even after the account gains extra subscriptions/resource groups.
        var deploymentId = DeterministicDeploymentId.Compute(AgentId, "dev1").ToString();
        var store = new FakeStore(ProvisionedRecord(id: deploymentId, agentId: AgentId, name: "dev1"));

        var subscription = new FakeSubscriptionResolver
        {
            Throw = new ProvisioningException(ProvisioningErrorKind.AmbiguousContext, "ambiguous", "pass --subscription"),
        };
        var rg = new FakeResourceGroupResolver
        {
            Throw = new ProvisioningException(ProvisioningErrorKind.AmbiguousContext, "ambiguous", "pass --resource-group"),
        };
        var probe = new RecordingBotNameProbe { IsAvailable = false, UnavailableMessage = "already taken" };
        var bots = new CapturingBotProvisioningPort { ExistingBot = LiveBot("dev1", AppId, "https://live/api/messages") };

        var service = new DeploymentService(
            store,
            new CompositeDeploymentTransport(bots, probe, subscription, rg));

        var result = await service.CreateAsync(ValidCreate(name: "dev1"), CancellationToken.None);

        Assert.Equal(DeploymentOperationStatus.Existed, result.Status);
        // Live inventory resolution is skipped entirely.
        Assert.False(subscription.IsCalled);
        Assert.False(rg.IsCalled);
        Assert.Empty(bots.CreateRequests);
        // The GET targeted exactly the coordinates pinned in the record.
        Assert.Equal(
            "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev1",
            bots.LastBotResourceId);
        Assert.Equal("tenant-1", bots.LastTenantId);
    }

    [Fact]
    public async Task CreateAsync_NameUnavailable_ReadFailureThrowsDeploymentReadFailed()
    {
        var probe = new RecordingBotNameProbe { IsAvailable = false };
        var bots = new CapturingBotProvisioningPort
        {
            GetBotThrows = new TargetRequestException("ARM failed", ExitCodes.Failure, TargetErrorKind.RequestFailed, 500),
        };
        var service = Service(out _, nameProbe: probe, bots: bots);

        var ex = await Assert.ThrowsAsync<DeploymentException>(() => service.CreateAsync(ValidCreate(), CancellationToken.None));
        Assert.Equal("deployment-read-failed", ex.ErrorKind);
        Assert.Empty(bots.CreateRequests);
    }

    [Fact]
    public async Task CreateAsync_OmittedResourceGroup_UsesResolvedResourceGroup()
    {
        var rg = new FakeResourceGroupResolver { Result = new AzureResourceGroupInfo { Name = "rg-resolved", Location = "global" } };
        var bots = new CapturingBotProvisioningPort();
        var service = Service(out _, bots: bots, resourceGroupResolver: rg);

        await service.CreateAsync(ValidCreate(resourceGroup: null), CancellationToken.None);

        Assert.Equal("rg-resolved", Assert.Single(bots.CreateRequests).ResourceGroup);
        // An omitted --resource-group leaves the preference null, so the resolver auto-resolves.
        Assert.Null(rg.RequestedPreferred);
    }

    [Fact]
    public async Task CreateAsync_ResourceGroupResolverFailurePropagatesBeforeArm()
    {
        var rg = new FakeResourceGroupResolver
        {
            Throw = new ProvisioningException(
                ProvisioningErrorKind.MissingContext,
                "No resource groups were found.",
                "Create a resource group."),
        };
        var bots = new CapturingBotProvisioningPort();
        var service = Service(out _, bots: bots, resourceGroupResolver: rg);

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() => service.CreateAsync(ValidCreate(resourceGroup: null), CancellationToken.None));
        Assert.Equal(ProvisioningErrorKind.MissingContext, ex.Kind);
        Assert.Empty(bots.CreateRequests);
    }

    [Fact]
    public async Task CreateAsync_Default_ClearsPreviousDefaultForSameAgent()
    {
        var service = Service(out var dir);

        await service.CreateAsync(ValidCreate(name: "dev1") with { IsDefault = true }, CancellationToken.None);
        await service.CreateAsync(ValidCreate(name: "dev2") with { IsDefault = true }, CancellationToken.None);

        var stored = ProjectStore(dir).List(null);
        Assert.False(stored.Single(d => d.DeploymentName == "dev1").IsDefault);
        Assert.True(stored.Single(d => d.DeploymentName == "dev2").IsDefault);
    }

    [Fact]
    public async Task CreateAsync_SurfacesRawArmResponse()
    {
        using var doc = JsonDocument.Parse("""{"id":"/bot","properties":{"msaAppType":"SingleTenant"}}""");
        var bots = new CapturingBotProvisioningPort { RawArmResponse = doc.RootElement.Clone() };
        var service = Service(out _, bots: bots);

        var result = await service.CreateAsync(ValidCreate(), CancellationToken.None);

        Assert.Equal("/bot", result.ArmResponse!.Value.GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetAsync_ReconcilesRecordWithLiveBotRead()
    {
        var store = new FakeStore(ProvisionedRecord());
        var bots = new CapturingBotProvisioningPort { ExistingBot = LiveBot() };
        var service = NewService(store, bots);

        var result = await service.GetAsync(new DeploymentShowRequest { DeploymentId = ProvisionedId }, default);

        Assert.Equal(DeploymentOperationStatus.Retrieved, result.Status);
        Assert.Equal("https://gw/api/messages", result.Payload!.Bot.Endpoint);
        Assert.Equal("app-1", result.Payload.Bot.EntraId);
        Assert.Equal(ProvisionedRecord().Bot.ResourceId, bots.LastBotResourceId);
        Assert.Equal("tenant-1", bots.LastTenantId);
        Assert.Equal("https://gw/api/messages", store.Records.Single().Bot.Endpoint);
    }

    [Fact]
    public async Task GetAsync_BotMissingLive_DeletesLocalRecordAndThrowsNotFound()
    {
        var store = new FakeStore(ProvisionedRecord());
        var service = NewService(store, new CapturingBotProvisioningPort());

        var ex = await Assert.ThrowsAsync<DeploymentException>(() =>
            service.GetAsync(new DeploymentShowRequest { DeploymentId = ProvisionedId }, default));

        Assert.Equal("deployment-not-found", ex.ErrorKind);
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task GetAsync_ByAgentAndName_NoLocalRecord_ImportsBotFromAzure()
    {
        const string name = "importbot";
        var store = new FakeStore();
        var bots = new CapturingBotProvisioningPort { ExistingBot = LiveBot(name, "imported-app", "https://imported/api/messages") };
        var service = NewService(store, bots);

        var result = await service.GetAsync(new DeploymentShowRequest { AgentId = AgentId, DeploymentName = name, ResourceGroup = "rg-1" }, default);

        var expectedId = DeterministicDeploymentId.Compute(AgentId, name).ToString();
        Assert.Equal(expectedId, result.Payload!.DeploymentId);
        Assert.Equal("imported-app", store.Records.Single().Bot.EntraId);
        Assert.Equal($"/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/{name}", bots.LastBotResourceId);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesBotAndPersistsRecord()
    {
        var store = new FakeStore(ProvisionedRecord());
        var bots = new CapturingBotProvisioningPort();
        var service = NewService(store, bots);

        var result = await service.UpdateAsync(
            new DeploymentUpdateRequest { DeploymentId = ProvisionedId, Endpoint = "https://new/api/messages", DisplayName = "New Name", DeploymentType = "Prod", IsDefault = true },
            default);

        Assert.Equal(DeploymentOperationStatus.Updated, result.Status);
        var update = Assert.Single(bots.UpdateRequests);
        Assert.Equal("sub-1", update.SubscriptionId);
        Assert.Equal("rg-1", update.ResourceGroup);
        Assert.Equal("tenant-1", update.TenantId);
        Assert.Equal("https://new/api/messages", update.Endpoint);
        Assert.True(result.Payload!.IsDefault);
        Assert.Equal(DeploymentType.Prod, result.Payload.DeploymentType);
        Assert.Equal("https://new/api/messages", store.Records.Single().Bot.Endpoint);
    }

    [Fact]
    public async Task UpdateAsync_NoFields_ThrowsUsageBeforeArm()
    {
        var bots = new CapturingBotProvisioningPort();
        var service = NewService(new FakeStore(ProvisionedRecord()), bots);

        await Assert.ThrowsAsync<UsageException>(() => service.UpdateAsync(new DeploymentUpdateRequest { DeploymentId = ProvisionedId }, default));
        Assert.Empty(bots.UpdateRequests);
    }

    [Fact]
    public async Task UpdateAsync_LocalOnlyFields_UpdatesRecordWithoutArm()
    {
        var store = new FakeStore(ProvisionedRecord());
        var bots = new CapturingBotProvisioningPort();
        var service = NewService(store, bots);

        var result = await service.UpdateAsync(
            new DeploymentUpdateRequest { DeploymentId = ProvisionedId, DeploymentType = "Prod", IsDefault = true },
            default);

        Assert.Equal(DeploymentOperationStatus.Updated, result.Status);
        Assert.Empty(bots.UpdateRequests);
        var stored = store.Records.Single();
        Assert.Equal(DeploymentType.Prod, stored.DeploymentType);
        Assert.True(stored.IsDefault);
        Assert.Equal("https://old/api/messages", stored.Bot.Endpoint);
    }

    [Fact]
    public async Task UpdateAsync_BotMissingLive_DeletesLocalRecordAndThrowsNotFound()
    {
        var store = new FakeStore(ProvisionedRecord());
        var bots = new CapturingBotProvisioningPort
        {
            UpdateBotThrows = new TargetRequestException(
                "missing",
                ExitCodes.Usage,
                TargetErrorKind.RequestFailed,
                404,
                "missing"),
        };
        var service = NewService(store, bots);

        var ex = await Assert.ThrowsAsync<DeploymentException>(() =>
            service.UpdateAsync(new DeploymentUpdateRequest { DeploymentId = ProvisionedId, Endpoint = "https://new/api/messages" }, default));

        Assert.Equal("deployment-not-found", ex.ErrorKind);
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task DeleteAsync_DeletesBotAndRemovesLocalRecord()
    {
        var store = new FakeStore(ProvisionedRecord(), ProvisionedRecord(SecondId, name: "dev2"));
        var bots = new CapturingBotProvisioningPort();
        var service = NewService(store, bots);

        var result = await service.DeleteAsync(new DeploymentRequest { DeploymentId = ProvisionedId }, default);

        Assert.Equal(DeploymentOperationStatus.Deleted, result.Status);
        Assert.Null(result.Payload);
        Assert.Equal(ProvisionedRecord().Bot.ResourceId, Assert.Single(bots.Deleted));
        Assert.Equal(SecondId, Assert.Single(store.Records).DeploymentId);
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_IsIdempotent()
    {
        var service = NewService(new FakeStore(), new CapturingBotProvisioningPort());
        var report = await service.DeleteAsync(new DeploymentRequest { DeploymentId = UnknownId }, default);
        Assert.Equal(DeploymentOperationStatus.Deleted, report.Status);
    }

    private sealed class FakeStore : IDeploymentStore
    {
        public FakeStore(params CodeAgentDeployment[] seed) => Records = seed.ToList();
        public List<CodeAgentDeployment> Records { get; private set; }
        public IReadOnlyList<CodeAgentDeployment>? Saved { get; private set; }

        public IReadOnlyList<CodeAgentDeployment> List(string? project) => Records.ToList();

        public void Upsert(string? project, CodeAgentDeployment deployment)
        {
            Records.RemoveAll(record => string.Equals(record.DeploymentId, deployment.DeploymentId, StringComparison.OrdinalIgnoreCase));
            Records.Add(deployment);
            Saved = Records.ToList();
        }

        public void Remove(string? project, string deploymentId)
        {
            Records.RemoveAll(record => string.Equals(record.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase));
            Saved = Records.ToList();
        }
    }

}
