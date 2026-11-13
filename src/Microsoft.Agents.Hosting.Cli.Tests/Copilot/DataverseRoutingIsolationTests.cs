using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class DataverseRoutingIsolationTests
{
    private static CopilotEnvironmentContext Environment(string name) => new()
    {
        EnvironmentId = name, TenantId = "tenant-" + name,
        DataverseOrganization = new Uri($"https://{name}.crm.dynamics.com/"),
    };

    [Fact]
    public async Task CreatedClientKeepsItsTenantAfterAnotherClientIsCreated()
    {
        var tokens = new RecordingTokenProvider();
        using var accessor = new AgentHostDataverseHttpClientAccessor(new AgentHostSyncAuthProvider(tokens),
            () => new ScriptedHttpMessageHandler());
        var sdk = new RoutingClient();
        DataverseTargeting.Connect((ISyncDataverseClient)sdk, accessor, Environment("a"));
        using var a = accessor.CreateClient();
        DataverseTargeting.Connect((ISyncDataverseClient)sdk, accessor, Environment("b"));
        using var b = accessor.CreateClient();

        using var responseA = await a.GetAsync(Environment("a").DataverseOrganization);
        using var responseB = await b.GetAsync(Environment("b").DataverseOrganization);

        Assert.Equal(["tenant-a", "tenant-b"], tokens.RequestedTargetTenants);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdapterRestoresCallerScopeAfterSuccessOrFailure(bool fail)
    {
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(),
            () => new ScriptedHttpMessageHandler());
        var sdk = new RoutingClient { Fail = fail };
        DataverseOperationScope.Set(Environment("a").DataverseOrganization, "tenant-a");
        var adapter = new DataverseCatalogAdapter(sdk, accessor);

        try
        {
            await adapter.ResolveAgentIdAsync(Environment("b"), "schema", default);
        }
        catch (InvalidOperationException) when (fail)
        {
        }

        Assert.Equal(Environment("a").DataverseOrganization, DataverseOperationScope.Organization);
        Assert.Equal("tenant-a", DataverseOperationScope.TenantId);
    }

    [Fact]
    public async Task RawWebApiDoesNotRetargetSharedSdkClient()
    {
        var sdk = new RoutingClient();
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(),
            () => new ScriptedHttpMessageHandler());

        using var response = await new DataverseWebApiClient(sdk, accessor)
            .SendAsync(Environment("b"), HttpMethod.Get, "api/data/v9.2/bots", default);

        Assert.Null(sdk.Url);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AliasedWorkflowOperationWaitsCancellablyWithoutRetargetingCatalog(bool fail)
    {
        var sdk = new RoutingClient { Pause = true, Fail = fail };
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(),
            () => new ScriptedHttpMessageHandler());
        var catalog = new DataverseCatalogAdapter(sdk, accessor);
        var workflow = new WorkflowDataverseClient(sdk, new StandaloneWorkflowSynchronizer(sdk), accessor);
        using var canceled = new CancellationTokenSource();
        var a = catalog.ResolveAgentIdAsync(Environment("a"), "schema", default);
        await sdk.Entered.Task;
        try
        {
            var b = workflow.GetAsync(Environment("b"), Guid.NewGuid(), canceled.Token);
            Assert.False(b.IsCompleted);
            Assert.Equal(Environment("a").DataverseOrganization.ToString(), sdk.Url);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
            Assert.Equal(0, sdk.WorkflowCalls);
        }
        finally
        {
            sdk.Release.TrySetResult();
            if (fail)
                await Assert.ThrowsAsync<InvalidOperationException>(() => a);
            else
                await a;
        }

        Assert.Null(await workflow.GetAsync(Environment("b"), Guid.NewGuid(), default));
        Assert.Equal(1, sdk.WorkflowCalls);
        Assert.Equal(Environment("b").DataverseOrganization.ToString(), sdk.Url);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("exception")]
    [InlineData("cancellation")]
    public async Task NestedScopeRestoresExactPriorContextAcrossAwaitAndUnwind(string outcome)
    {
        var outer = new DataverseOperationScope.DataverseTarget(Environment("a").DataverseOrganization, "tenant-a", null);
        var inner = new DataverseOperationScope.DataverseTarget(Environment("b").DataverseOrganization, "tenant-b", null);
        using var outerScope = DataverseOperationScope.Push(outer);

        try
        {
            using var innerScope = DataverseOperationScope.Push(inner);
            using (DataverseOperationScope.Push(new(Environment("c").DataverseOrganization, "tenant-c", null)))
                await Task.Yield();
            Assert.Same(inner, DataverseOperationScope.Capture());
            if (outcome == "exception")
                throw new InvalidOperationException("scripted");
            if (outcome == "cancellation")
                throw new OperationCanceledException();
        }
        catch (Exception failure) when (failure is InvalidOperationException or OperationCanceledException)
        {
        }

        Assert.Same(outer, DataverseOperationScope.Capture());
    }

    [Fact]
    public async Task OwnerCancellationReleasesAliasBoundaryAndRestoresCallerScope()
    {
        var sdk = new RoutingClient { Pause = true };
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => new ScriptedHttpMessageHandler());
        var catalog = new DataverseCatalogAdapter(sdk, accessor);
        var workflow = new WorkflowDataverseClient(sdk, new StandaloneWorkflowSynchronizer(sdk), accessor);
        using var cancelOwner = new CancellationTokenSource();
        DataverseOperationScope.Set(Environment("c").DataverseOrganization, "tenant-c");

        var a = catalog.ResolveAgentIdAsync(Environment("a"), "schema", cancelOwner.Token);
        await sdk.Entered.Task;
        var b = workflow.GetAsync(Environment("b"), Guid.NewGuid(), default);
        Assert.False(b.IsCompleted);
        cancelOwner.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.Null(await b);
        Assert.Equal("tenant-c", DataverseOperationScope.TenantId);
    }

    [Fact]
    public async Task InheritedChildCannotBypassGateEvenWhenItsTargetMatchesOwner()
    {
        var sdk = new RoutingClient { Pause = true };
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => new ScriptedHttpMessageHandler());
        var catalog = new DataverseCatalogAdapter(sdk, accessor);
        var workflow = new WorkflowDataverseClient(sdk, new StandaloneWorkflowSynchronizer(sdk), accessor);
        Task<WorkflowDocument?>? child = null;
        sdk.OnEnter = () => child = workflow.GetAsync(Environment("a"), Guid.NewGuid(), default);
        var a = catalog.ResolveAgentIdAsync(Environment("a"), "schema", default);
        await sdk.Entered.Task;
        try
        {
            Assert.NotNull(child);
            Assert.False(child.IsCompleted);
            Assert.Equal(0, sdk.WorkflowCalls);
        }
        finally
        {
            sdk.Release.TrySetResult();
            await a;
        }
        Assert.Null(await child);
        Assert.Equal(1, sdk.WorkflowCalls);
    }

    [Fact]
    public async Task SdkCallbackAfterAwaitSeesCapturedProfileAndMayUseNestedRawHttp()
    {
        var profile = new AuthProfile
        {
            ProfileId = "captured", AccountKey = "home", Username = "synthetic",
            ClientId = Microsoft.Agents.Hosting.Cli.Core.Auth.PowerPlatformApiPermissions.AgentHostCliApplicationId,
        };
        var environment = Environment("a") with
        {
            Context = new(profile, Microsoft.Agents.Hosting.Cli.Core.Environments.PowerPlatformApiTargetConfig.Public),
        };
        var sdk = new RoutingClient { Pause = true };
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => new ScriptedHttpMessageHandler());
        var webApi = new DataverseWebApiClient(sdk, accessor);
        sdk.AfterAwait = async () =>
        {
            Assert.Same(environment.Context, DataverseOperationScope.Context);
            Assert.Equal(environment.TenantId, DataverseOperationScope.TenantId);
            using var response = await webApi.SendAsync(environment, HttpMethod.Get, "api/data/v9.2/bots", default);
            Assert.Same(environment.Context, DataverseOperationScope.Context);
        };
        var operation = new DataverseCatalogAdapter(sdk, accessor).ResolveAgentIdAsync(environment, "schema", default);
        await sdk.Entered.Task;
        DataverseOperationScope.Set(Environment("b").DataverseOrganization, "tenant-b");
        sdk.Release.TrySetResult();
        await operation;
        Assert.Equal("tenant-b", DataverseOperationScope.TenantId);
    }

    private sealed class RoutingClient : SyncDataverseClient, ISyncDataverseClient, IStandaloneWorkflowDataverseClient
    {
        public RoutingClient() : base(new StubDataverseHttpClientAccessor(), "tests") { }
        public string? Url { get; private set; }
        public bool Pause { get; init; }
        public bool Fail { get; init; }
        public int WorkflowCalls { get; private set; }
        public Action? OnEnter { get; set; }
        public Func<Task>? AfterAwait { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public new void SetDataverseUrl(string url) => Url = url;

        public override async Task<Guid> GetAgentIdBySchemaNameAsync(string schemaName, CancellationToken cancellationToken)
        {
            OnEnter?.Invoke();
            Entered.TrySetResult();
            if (Pause)
                await Release.Task.WaitAsync(cancellationToken);
            if (AfterAwait is not null)
                await AfterAwait();
            if (Fail)
                throw new InvalidOperationException("scripted failure");
            return Guid.Empty;
        }

        public override Task<WorkflowMetadata?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken)
        {
            WorkflowCalls++;
            return Task.FromResult<WorkflowMetadata?>(null);
        }
    }
}
