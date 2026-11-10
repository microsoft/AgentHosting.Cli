using CoreCluster = Microsoft.Agents.Hosting.Cli.Core.Environments.CoreServicesClusterCategory;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Agents.Hosting.Cli.Core.Environments;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncConnectionAdapterTokenTests
{
    private static CopilotEnvironmentContext Environment() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "11111111-2222-3333-4444-555555555555",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static string Workspace() => Path.Combine(Path.GetTempPath(), "ah-connection-token");

    private static SyncConnectionAdapter Create(ITokenProvider tokenProvider, CoreCluster cluster = CoreCluster.Prod) =>
        CopilotTestFixtures.ConnectionAdapter(tokenProvider: tokenProvider, cluster: cluster);

    [Theory]
    [InlineData(CloudInstance.UsNat, false)]
    [InlineData(CloudInstance.UsSec, false)]
    [InlineData(CloudInstance.UsNat, true)]
    [InlineData(CloudInstance.UsSec, true)]
    public async Task ConnectionListingRejectsUnsupportedClusterBeforeAcquisition(CloudInstance cloud, bool captured)
    {
        var target = PowerPlatformApiTargetSelection.TargetFor(cloud);
        var tokens = new ObservingTokens();
        var credentials = new ProfileSyncResourceTests.Credentials();
        using var harness = new OrderingHarness(tokens, () => captured ? CoreCluster.Prod : PowerAppsServiceTarget.ClusterFor(target), credentials);
        var environment = Environment();
        if (captured)
            environment = environment with { Context = new(new AuthProfile
            {
                ProfileId = "captured", AccountKey = "principal", Username = "", Cloud = cloud,
                TenantId = environment.TenantId,
            }, target) };

        await Assert.ThrowsAsync<EndpointTrustException>(() =>
            harness.Adapter.ListConnectionsAsync(environment, "shared_teams", default));

        Assert.Empty(tokens.Resources);
        Assert.Empty(credentials.Requests);
        Assert.Null(harness.Catalog.LastContext);
        Assert.Equal(0, harness.Transport.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyCatalogCapturesClusterOnceBeforeAcquisition(bool connectors)
    {
        var calls = 0;
        var selected = CoreCluster.Prod;
        var tokens = new ObservingTokens(() =>
        {
            Assert.Equal(1, calls);
            selected = CoreCluster.Gov;
        });
        using var harness = new OrderingHarness(tokens, () => { calls++; return selected; });
        var environment = Environment();

        if (connectors)
            await harness.Adapter.ListConnectorsAsync(environment, default);
        else
            await harness.Adapter.ListConnectionsAsync(environment, "shared_teams", default);

        Assert.Equal(1, calls);
        Assert.Equal("https://api.bap.microsoft.com", Assert.Single(tokens.Resources));
        Assert.Equal(environment.TenantId, tokens.Tenant);
        Assert.Equal(SyncClusterCategory.For(CoreCluster.Prod), harness.Catalog.LastContext!.ClusterCategory);
        Assert.Equal(environment.EnvironmentId, harness.Catalog.LastContext.EnvironmentId);
        Assert.Equal("legacy-token", harness.Catalog.LastContext.AccessToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedCatalogDoesNotUseTheLegacyCluster(bool connectors)
    {
        var tokens = new ObservingTokens();
        var credentials = new ProfileSyncResourceTests.Credentials();
        using var harness = new OrderingHarness(tokens, () => throw new InvalidOperationException("Legacy resolution is forbidden."), credentials);
        var target = PowerPlatformApiTargetSelection.TargetFor(CloudInstance.UsGov);
        var environment = Environment();
        var profile = new AuthProfile
        {
            ProfileId = "captured", AccountKey = "principal", Username = "", Cloud = target.Cloud,
            TenantId = environment.TenantId, ClientId = PowerPlatformApiPermissions.AgentHostCliApplicationId,
        };
        environment = environment with { Context = new(profile, target) };

        if (connectors)
            await harness.Adapter.ListConnectorsAsync(environment, default);
        else
            await harness.Adapter.ListConnectionsAsync(environment, "shared_teams", default);

        var acquired = Assert.Single(credentials.Requests);
        Assert.Same(profile, acquired.Profile);
        Assert.Equal(environment.TenantId, acquired.Tenant);
        Assert.Equal(["https://gov.service.powerapps.us//.default"], acquired.Scopes);
        Assert.Equal(SyncClusterCategory.For(CoreCluster.Gov), harness.Catalog.LastContext!.ClusterCategory);
        Assert.Empty(tokens.Resources);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DataverseReferenceOperationsDoNotResolvePowerApps(bool create)
    {
        var tokens = new ObservingTokens(() => throw new InvalidOperationException("PowerApps acquisition is forbidden."));
        using var harness = new OrderingHarness(tokens, () => throw new EndpointTrustException("PowerApps resolution is forbidden."));
        var workspace = Path.Combine(System.Environment.CurrentDirectory, "unused-powerapps-ordering-workspace");

        if (create)
            await harness.Adapter.CreateConnectionReferenceAsync(Environment(), workspace, "shared_teams", default);
        else
            await harness.Adapter.RemoveConnectionReferenceAsync(Environment(), workspace, "cr_teams", false, default);

        Assert.Empty(tokens.Resources);
        Assert.Null(harness.Catalog.LastContext);
        Assert.Equal(0, harness.Transport.Requests);
    }

    private sealed class ObservingTokens(Action? onAcquire = null) : ITokenProvider
    {
        internal List<string> Resources { get; } = [];
        internal string? Tenant { get; private set; }
        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(string resource, string? tenant, CancellationToken ct)
        {
            Resources.Add(resource);
            Tenant = tenant;
            onAcquire?.Invoke();
            return Task.FromResult(new TokenAcquisitionResult
            {
                AccessToken = "legacy-token", TenantId = tenant, ExpiresOn = DateTimeOffset.MaxValue,
            });
        }
        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(IReadOnlyList<string> scopes, string? tenant, CancellationToken ct) =>
            throw new InvalidOperationException("The legacy resource overload must be preserved.");
    }

    private sealed class OrderingHarness : IDisposable
    {
        private readonly AgentHostDataverseHttpClientAccessor _accessor;
        internal RecordingCatalog Catalog { get; } = new();
        internal RecordingTransport Transport { get; } = new();
        internal SyncConnectionAdapter Adapter { get; }
        internal OrderingHarness(ITokenProvider tokens, Func<CoreCluster> cluster, IProfileCredentialProvider? credentials = null)
        {
            _accessor = new(new StubSyncAuthProvider(), () => Transport);
            var dataverse = new RecordingConnectionReferenceClient();
            Adapter = new(new RecordingConnectionManagementService(), new RecordingWorkflowActivationService(),
                new RecordingWorkspaceSynchronizer(), dataverse, Catalog, tokens, _accessor,
                new WorkspaceFileAccessorFactory(), new DataverseWebApiClient(dataverse, _accessor), cluster, credentials);
        }
        public void Dispose()
        {
            _accessor.Dispose();
            Transport.Dispose();
        }
    }

    private sealed class RecordingCatalog : IConnectionCatalogClient
    {
        internal PowerAppsContext? LastContext { get; private set; }
        public Task<IReadOnlyList<ConnectorInfo>> ListConnectorsAsync(PowerAppsContext context, CancellationToken ct)
        {
            LastContext = context;
            return Task.FromResult<IReadOnlyList<ConnectorInfo>>([]);
        }
        public Task<IReadOnlyList<ConnectionInstance>> ListConnectionsAsync(PowerAppsContext context, string connector, CancellationToken ct)
        {
            LastContext = context;
            return Task.FromResult<IReadOnlyList<ConnectionInstance>>([]);
        }
    }

    private sealed class RecordingTransport : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class ForeignTenantTokenProvider : ITokenProvider
    {
        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(string resource, string? targetTenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TokenAcquisitionResult { AccessToken = "token-1", TenantId = "99999999-9999-9999-9999-999999999999", ExpiresOn = DateTimeOffset.UtcNow.AddHours(1) });

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TokenAcquisitionResult { AccessToken = "token-1", TenantId = "99999999-9999-9999-9999-999999999999", ExpiresOn = DateTimeOffset.UtcNow.AddHours(1) });
    }

    private sealed class InteractionRequiredTokenProvider : ITokenProvider
    {
        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(string resource, string? targetTenantId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("interactive sign-in is required");

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("interactive sign-in is required");
    }

    [Fact]
    public async Task RemoveConnectionReference_AcquiresNoPowerAppsToken()
    {
        var tokenProvider = new StubTokenProvider();

        await Create(tokenProvider).RemoveConnectionReferenceAsync(Environment(), Workspace(), "cr_teams", confirmed: false, CancellationToken.None);

        Assert.Empty(tokenProvider.RequestedResources);
    }

    [Fact]
    public async Task RemoveConnectionReference_ProceedsWhenSilentAcquisitionWouldNeedInteraction()
    {
        var removal = await Create(new InteractionRequiredTokenProvider())
            .RemoveConnectionReferenceAsync(Environment(), Workspace(), "cr_teams", confirmed: false, CancellationToken.None);

        Assert.NotNull(removal);
    }

    [Fact]
    public async Task CreateConnectionReference_AcquiresNoPowerAppsToken()
    {
        var tokenProvider = new StubTokenProvider();

        await Create(tokenProvider).CreateConnectionReferenceAsync(Environment(), Workspace(), "shared_teams", CancellationToken.None);

        Assert.Empty(tokenProvider.RequestedResources);
    }

    [Fact]
    public async Task CreateConnectionReference_ProceedsWhenSilentAcquisitionWouldNeedInteraction()
    {
        var logicalName = await Create(new InteractionRequiredTokenProvider())
            .CreateConnectionReferenceAsync(Environment(), Workspace(), "shared_teams", CancellationToken.None);

        Assert.NotNull(logicalName);
    }

    [Fact]
    public async Task ReadDeclaredConnectors_AcquiresNoPowerAppsToken()
    {
        var tokenProvider = new StubTokenProvider();

        await Create(tokenProvider).ReadDeclaredConnectorsAsync(Environment(), Workspace(), CancellationToken.None);

        Assert.Empty(tokenProvider.RequestedResources);
    }

    [Fact]
    public async Task ReadConnections_StillAcquiresThePowerAppsTokenItsCatalogLookupNeeds()
    {
        var tokenProvider = new StubTokenProvider();

        await Create(tokenProvider).ReadConnectionsAsync(Environment(), Workspace(), CancellationToken.None);

        Assert.Single(tokenProvider.RequestedResources);
    }

    [Fact]
    public async Task ListConnections_RefusesATokenTheLibraryAcquiredForAnotherTenant()
    {
        var error = await Assert.ThrowsAsync<TokenClaimException>(() =>
            Create(new ForeignTenantTokenProvider()).ListConnectionsAsync(Environment(), "shared_teams", CancellationToken.None));

        Assert.Equal(TokenClaimFailure.WrongTenant, error.Failure);
    }

    [Fact]
    public async Task ListConnectors_RefusesATokenTheLibraryAcquiredForAnotherTenant()
    {
        var error = await Assert.ThrowsAsync<TokenClaimException>(() =>
            Create(new ForeignTenantTokenProvider()).ListConnectorsAsync(Environment(), CancellationToken.None));

        Assert.Equal(TokenClaimFailure.WrongTenant, error.Failure);
    }
}
