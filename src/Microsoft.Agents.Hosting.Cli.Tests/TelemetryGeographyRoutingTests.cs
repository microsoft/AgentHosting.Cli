using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Telemetry;
using Microsoft.PowerPlatform.Tooling.OneDS;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class TelemetryGeographyRoutingTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 9, 23, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("unitedstates", "Global")]
    [InlineData("europe", "Europe")]
    [InlineData("atlantis", "Europe")]
    [InlineData(null, "Europe")]
    public async Task ProductionRoutingUsesAuthoritativeGeographyAndFailsClosed(
        string? geography,
        string expected)
    {
        var geographyClient = new FixedGeographyClient(geography);
        var resolver = Resolver(geographyClient);

        TelemetryDestination destination =
            Assert.IsType<TelemetryDestination>(
                await resolver.ResolveAsync(
                    ResolvedTarget("environment-one"),
                    CancellationToken.None));

        Assert.Equal(expected, destination.Location.ToString());
        Assert.Single(geographyClient.Targets);
    }

    [Fact]
    public async Task GeographyFailureFallsBackToEurope()
    {
        var resolver = Resolver(new ThrowingGeographyClient());

        TelemetryDestination destination =
            Assert.IsType<TelemetryDestination>(
                await resolver.ResolveAsync(
                    ResolvedTarget("environment-one"),
                    CancellationToken.None));

        Assert.Equal(TelemetryLocation.Europe, destination.Location);
    }

    [Theory]
    [InlineData("unitedstates", "canada", "Global")]
    [InlineData("unitedstates", "europe", "Europe")]
    [InlineData("unitedstates", "atlantis", "Europe")]
    public async Task MultipleTargetsRouteGlobalOnlyWhenEveryTargetIsKnownNonEu(
        string first,
        string second,
        string expected)
    {
        var geography = new PerTargetGeographyClient(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["environment-one"] = first,
                ["environment-two"] = second,
            });
        var resolver = Resolver(geography);

        TelemetryDestination destination =
            Assert.IsType<TelemetryDestination>(
                await resolver.ResolveAsync(
                    TelemetryEnvironmentTarget.Resolved(
                        [
                            Target("environment-one"),
                            Target("environment-two"),
                        ]),
                    CancellationToken.None));

        Assert.Equal(expected, destination.Location.ToString());
    }

    [Fact]
    public async Task ExplicitTargetDifferentFromActiveUsesTheExplicitEnvironment()
    {
        var observer = new CommandTelemetryEnvironmentObserver();
        var capture = new TargetCaptureResolver();
        await using var session = Session(capture, observer);
        session.RecordAttempt(Path());
        using IDisposable scope = session.EnterObservationScope()!;
        var selection = new InMemoryEnvironmentSelectionStore();
        await selection.SetActiveAsync(
            Active("environment-active"),
            CancellationToken.None);
        EnvironmentSelectionService environments = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(
                Environment("environment-explicit")),
            selection,
            InMemoryAuthProfileStore.SignedIn(
                "operator@example.invalid",
                EnvironmentFixtures.ContosoTenant),
            observer);

        await environments.ResolveForCommandAsync(
            "environment-explicit",
            CancellationToken.None);
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        scope.Dispose();
        await session.DisposeAsync();

        ResolvedEnvironmentTarget target =
            Assert.Single(
                Assert.IsType<TelemetryEnvironmentTarget>(capture.LastTarget)
                    .Environments);
        Assert.Equal("environment-explicit", target.EnvironmentId);
    }

    [Fact]
    public async Task EnvironmentSelectObservesThePersistedResolvedEnvironment()
    {
        var observer = new CommandTelemetryEnvironmentObserver();
        var capture = new TargetCaptureResolver();
        await using var session = Session(capture, observer);
        session.RecordAttempt(Path());
        using IDisposable scope = session.EnterObservationScope()!;
        var selection = new InMemoryEnvironmentSelectionStore();
        EnvironmentSelectionService environments = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(Environment("environment-selected")),
            selection,
            InMemoryAuthProfileStore.SignedIn(
                "operator@example.invalid",
                EnvironmentFixtures.ContosoTenant),
            observer);

        await environments.SelectAsync(
            "environment-selected",
            CancellationToken.None);
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        scope.Dispose();
        await session.DisposeAsync();

        Assert.Equal("environment-selected", selection.Active?.EnvironmentId);
        Assert.Equal(
            "environment-selected",
            Assert.Single(capture.LastTarget!.Environments).EnvironmentId);
    }

    [Fact]
    public async Task WorkspaceDeploymentResolutionObservesWorkspaceEnvironment()
    {
        var observer = new CommandTelemetryEnvironmentObserver();
        var capture = new TargetCaptureResolver();
        await using var session = Session(capture, observer);
        session.RecordAttempt(Path());
        using IDisposable scope = session.EnterObservationScope()!;
        EnvironmentSelectionService environments = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(
                Environment(
                    "environment-deployment",
                    instanceUrl: "https://contoso.crm.dynamics.com/")),
            new InMemoryEnvironmentSelectionStore(),
            InMemoryAuthProfileStore.SignedIn(
                "operator@example.invalid",
                EnvironmentFixtures.ContosoTenant),
            observer);
        var workspaceResolver = new CopilotEnvironmentResolver(
            environments,
            PowerPlatformCloudMap.ForCloud(CloudInstance.Public));

        CopilotEnvironmentContext resolved =
            await workspaceResolver.ResolveAsync(
                "environment-deployment",
                CancellationToken.None);
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        scope.Dispose();
        await session.DisposeAsync();

        Assert.Equal("environment-deployment", resolved.EnvironmentId);
        Assert.Equal(
            "environment-deployment",
            Assert.Single(capture.LastTarget!.Environments).EnvironmentId);
    }

    [Fact]
    public async Task TenantWideEnumerationDoesNotObserveEveryAvailableEnvironment()
    {
        var observer = new CommandTelemetryEnvironmentObserver();
        var capture = new TargetCaptureResolver();
        await using var session = Session(capture, observer);
        session.RecordAttempt(Path());
        using IDisposable scope = session.EnterObservationScope()!;
        EnvironmentSelectionService environments = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(
                Environment(
                    "environment-one",
                    instanceUrl: "https://one.crm.dynamics.com/"),
                Environment(
                    "environment-two",
                    instanceUrl: "https://two.crm.dynamics.com/")),
            new InMemoryEnvironmentSelectionStore(),
            InMemoryAuthProfileStore.SignedIn(
                "operator@example.invalid",
                EnvironmentFixtures.ContosoTenant),
            observer);
        var workspaceResolver = new CopilotEnvironmentResolver(
            environments,
            PowerPlatformCloudMap.ForCloud(CloudInstance.Public));

        CopilotEnvironmentInventory inventory =
            await workspaceResolver.ListAsync(CancellationToken.None);
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        scope.Dispose();
        await session.DisposeAsync();

        Assert.Equal(2, inventory.Reachable.Count);
        Assert.False(capture.LastTarget!.IsComplete);
        Assert.Empty(capture.LastTarget.Environments);
    }

    [Fact]
    public async Task RealResolutionClusterHttpAndBatchTransportRouteBothRecordsToUs()
    {
        const string environmentId =
            "Default-11111111-2222-3333-4444-555555555555";
        var handler = new StubHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "clusterNumber":"2",
                      "geoName":"us",
                      "environment":"Default-11111111-2222-3333-4444-555555555555",
                      "clusterType":"Production",
                      "clusterCategory":"prod",
                      "clusterName":"prod-us-2",
                      "geoLongName":"unitedstates"
                    }
                    """),
            });
        var token = new FixedSelectedAccountTokenProvider();
        var geography = new PowerPlatformApiEnvironmentGeographyClient(
            new HttpClient(handler),
            token,
            PowerPlatformApiTargetConfig.Public,
            new PpapiTokenClaimsValidator());
        var environmentObserver = new CommandTelemetryEnvironmentObserver();
        var transport = new CapturingTransport();
        var resolver = Resolver(geography);
        await using var session = new CommandTelemetrySession(
            resolver,
            new FixedTelemetryClock(),
            "1.2.3-test",
            environmentObserver: environmentObserver,
            flushTimeout: TelemetryTestBudgets.Flush,
            transport: transport);
        session.RecordAttempt(Path());
        using IDisposable scope = session.EnterObservationScope()!;
        EnvironmentSelectionService environments = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(Environment(environmentId)),
            new InMemoryEnvironmentSelectionStore(),
            InMemoryAuthProfileStore.SignedIn(
                "operator@example.invalid",
                EnvironmentFixtures.ContosoTenant),
            environmentObserver);

        await environments.ResolveForCommandAsync(
            environmentId,
            CancellationToken.None);
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        scope.Dispose();
        await session.DisposeAsync();

        Assert.Equal(
            OneDSCollectorEndpoints.RegionalPublicCloud.US,
            transport.Destination?.Endpoint);
        JsonElement[] events = Parse(Assert.IsType<string>(transport.Payload));
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            events.Select(item =>
                item.GetProperty("data")
                    .GetProperty("event_Name")
                    .GetString()));
        Assert.All(events, item => Assert.Equal(
            "global",
            item.GetProperty("data")
                .GetProperty("telemetryLocation")
                .GetString()));
        Assert.Equal(
            "default111111112222333344445555555555.55.environment.api.powerplatform.com",
            Assert.Single(handler.Requests).Uri.Host);
        Assert.Equal(
            [EnvironmentFixtures.ContosoTenant],
            token.RequestedTenants);
    }

    [Fact]
    public async Task MetadataAuthenticationCannotReplaceNormalTenantAttribution()
    {
        Guid normalTenant =
            new("11111111-1111-1111-1111-111111111111");
        Guid metadataTenant =
            new("22222222-2222-2222-2222-222222222222");
        var tenantObserver = new CommandTelemetryTenantObserver();
        var environmentObserver = new CommandTelemetryEnvironmentObserver();
        var transport = new CapturingTransport();
        var geography = new CallbackGeographyClient(() =>
            tenantObserver.Observe(
                new AuthenticatedTenantContext(metadataTenant)));
        var resolver = new TelemetryDestinationResolver(
            PowerPlatformApiTargetConfig.Public,
            geography,
            SyntheticApiKey(),
            SyntheticApiKey('a'),
            tenantObserver);
        await using var session = new CommandTelemetrySession(
            resolver,
            new FixedTelemetryClock(),
            "1.2.3-test",
            tenantObserver,
            environmentObserver,
            flushTimeout: TelemetryTestBudgets.Flush,
            transport: transport);
        session.RecordAttempt(Path());
        using IDisposable scope = session.EnterObservationScope()!;
        tenantObserver.Observe(new AuthenticatedTenantContext(normalTenant));
        environmentObserver.Observe(Target("environment-one"));
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        scope.Dispose();

        await session.DisposeAsync();

        string payload = Assert.IsType<string>(transport.Payload);
        Assert.Contains(normalTenant.ToString("D"), payload, StringComparison.Ordinal);
        Assert.DoesNotContain(metadataTenant.ToString("D"), payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConflictingTargetTenantBindingFallsBackWithoutMetadataLookup()
    {
        var environmentObserver = new CommandTelemetryEnvironmentObserver();
        var geography = new FixedGeographyClient("unitedstates");
        var capture = new TargetCaptureResolver(Resolver(geography));
        await using var session = Session(capture, environmentObserver);
        session.RecordAttempt(Path());
        using IDisposable scope = session.EnterObservationScope()!;
        environmentObserver.Observe(
            new ResolvedEnvironmentTarget(
                "environment-one",
                EnvironmentFixtures.ContosoTenant));
        environmentObserver.Observe(
            new ResolvedEnvironmentTarget(
                "environment-one",
                EnvironmentFixtures.FabrikamTenant));
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        scope.Dispose();

        await session.DisposeAsync();

        Assert.False(capture.LastTarget!.IsComplete);
        Assert.Empty(geography.Targets);
        Assert.Equal(TelemetryLocation.Europe, capture.LastDestination?.Location);
    }

    [Fact]
    public async Task MoreThanFourTargetsFallsBackWithoutMetadataLookup()
    {
        var environmentObserver = new CommandTelemetryEnvironmentObserver();
        var geography = new FixedGeographyClient("unitedstates");
        var capture = new TargetCaptureResolver(Resolver(geography));
        await using var session = Session(capture, environmentObserver);
        session.RecordAttempt(Path());
        using IDisposable scope = session.EnterObservationScope()!;
        foreach (string environmentId in new[]
                 {
                     "environment-one",
                     "environment-two",
                     "environment-three",
                     "environment-four",
                     "environment-five",
                 })
        {
            environmentObserver.Observe(Target(environmentId));
        }

        session.RecordOutcome(CommandTelemetryOutcome.Success);
        scope.Dispose();

        await session.DisposeAsync();

        Assert.False(capture.LastTarget!.IsComplete);
        Assert.Empty(geography.Targets);
        Assert.Equal(TelemetryLocation.Europe, capture.LastDestination?.Location);
    }

    [Fact]
    public async Task MetadataDeadlineFallsBackOnceAndLateCompletionCannotReroute()
    {
        var geography = new BlockingGeographyClient();
        var transport = new CapturingTransport();
        var resolver = new TelemetryDestinationResolver(
            PowerPlatformApiTargetConfig.Public,
            geography,
            SyntheticApiKey(),
            SyntheticApiKey('a'),
            metadataTimeout: TimeSpan.FromMilliseconds(40));
        await using var session = new CommandTelemetrySession(
            resolver,
            new FixedTelemetryClock(),
            "1.2.3-test",
            flushTimeout: TelemetryTestBudgets.Flush,
            transport: transport);
        session.RecordAttempt(Path());
        session.ObserveResolvedEnvironment(Target("environment-one"));
        session.RecordOutcome(CommandTelemetryOutcome.Success);

        try
        {
            await session.DisposeAsync();
            Assert.Equal(1, transport.CallCount);
            Assert.Equal(
                OneDSCollectorEndpoints.RegionalPublicCloud.EU,
                transport.Destination?.Endpoint);
        }
        finally
        {
            geography.Release();
        }

        await geography.Returned.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, transport.CallCount);
        Assert.Equal(
            OneDSCollectorEndpoints.RegionalPublicCloud.EU,
            transport.Destination?.Endpoint);
    }

    private static EnvironmentSelectionService EnvironmentService(
        IResolvedEnvironmentObserver observer,
        params PowerPlatformEnvironment[] environments) =>
        EnvironmentFixtures.Service(
            new FakeEnvironmentClient(environments),
            new InMemoryEnvironmentSelectionStore(),
            InMemoryAuthProfileStore.SignedIn(
                "operator@example.invalid",
                EnvironmentFixtures.ContosoTenant),
            observer);

    private static TelemetryDestinationResolver Resolver(
        IEnvironmentGeographyClient geographyClient) =>
        new(
            PowerPlatformApiTargetConfig.Public,
            geographyClient,
            SyntheticApiKey(),
            SyntheticApiKey('a'));

    private static CommandTelemetrySession Session(
        ITelemetryDestinationResolver resolver,
        CommandTelemetryEnvironmentObserver environmentObserver) =>
        new(
            resolver,
            new FixedTelemetryClock(),
            "1.2.3-test",
            environmentObserver: environmentObserver,
            flushTimeout: TelemetryTestBudgets.Flush,
            transport: new CapturingTransport());

    private static TelemetryEnvironmentTarget ResolvedTarget(
        string environmentId) =>
        TelemetryEnvironmentTarget.Resolved([Target(environmentId)]);

    private static ResolvedEnvironmentTarget Target(string environmentId) =>
        new(environmentId, EnvironmentFixtures.ContosoTenant);

    private static PowerPlatformEnvironment Environment(
        string environmentId,
        string? instanceUrl = null) =>
        EnvironmentFixtures.Environment(
            environmentId,
            instanceUrl: instanceUrl);

    private static ActiveEnvironment Active(string environmentId) => new()
    {
        EnvironmentId = environmentId,
        DisplayName = environmentId,
        TenantId = EnvironmentFixtures.ContosoTenant,
        ApiEndpoint = new Uri("https://api.powerplatform.com/"),
        ApiAudience = EnvironmentFixtures.Audience,
        SelectedAt = Timestamp,
    };

    private static CommandTelemetryPath Path() =>
        CommandTelemetryPath.FromMetadata(["agent", "deploy"]);

    private static string SyntheticApiKey(char first = '0') => string.Join(
        '-',
        new string(first, 32),
        new string('1', 8),
        new string('2', 4),
        new string('3', 4),
        new string('4', 4),
        new string('5', 12),
        new string('6', 4));

    private static JsonElement[] Parse(string payload) =>
        payload
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private sealed class FixedTelemetryClock : IClock
    {
        public DateTimeOffset UtcNow => Timestamp;
    }

    private sealed class FixedGeographyClient(string? geography) :
        IEnvironmentGeographyClient
    {
        internal List<ResolvedEnvironmentTarget> Targets { get; } = [];

        public Task<string?> GetGeographyAsync(
            ResolvedEnvironmentTarget target,
            CancellationToken cancellationToken)
        {
            Targets.Add(target);
            return Task.FromResult(geography);
        }
    }

    private sealed class PerTargetGeographyClient(
        IReadOnlyDictionary<string, string?> geographies) :
        IEnvironmentGeographyClient
    {
        public Task<string?> GetGeographyAsync(
            ResolvedEnvironmentTarget target,
            CancellationToken cancellationToken) =>
            Task.FromResult(geographies[target.EnvironmentId]);
    }

    private sealed class ThrowingGeographyClient : IEnvironmentGeographyClient
    {
        public Task<string?> GetGeographyAsync(
            ResolvedEnvironmentTarget target,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic lookup failure");
    }

    private sealed class CallbackGeographyClient(Action callback) :
        IEnvironmentGeographyClient
    {
        public Task<string?> GetGeographyAsync(
            ResolvedEnvironmentTarget target,
            CancellationToken cancellationToken)
        {
            callback();
            return Task.FromResult<string?>("unitedstates");
        }
    }

    private sealed class BlockingGeographyClient : IEnvironmentGeographyClient
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _returned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Returned => _returned.Task;

        internal void Release() => _release.TrySetResult();

        public async Task<string?> GetGeographyAsync(
            ResolvedEnvironmentTarget target,
            CancellationToken cancellationToken)
        {
            await _release.Task;
            _returned.TrySetResult();
            return "unitedstates";
        }
    }

    private sealed class FixedSelectedAccountTokenProvider :
        ISelectedAccountTokenProvider
    {
        internal List<string> RequestedTenants { get; } = [];

        public Task<TokenAcquisitionResult?> TryAcquireTokenSilentAsync(
            string resource,
            string targetTenantId,
            CancellationToken cancellationToken)
        {
            RequestedTenants.Add(targetTenantId);
            return Task.FromResult<TokenAcquisitionResult?>(
                new TokenAcquisitionResult
                {
                    AccessToken = FakeJwt.Create(),
                    TenantId = targetTenantId,
                    ExpiresOn = Timestamp.AddHours(1),
                });
        }
    }

    private sealed class CapturingTransport : ICommandTelemetryTransport
    {
        internal int CallCount { get; private set; }

        internal string? Payload { get; private set; }

        internal TelemetryDestination? Destination { get; private set; }

        public ValueTask<HttpStatusCode> SendAsync(
            string payload,
            TelemetryDestination destination,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Payload = payload;
            Destination = destination;
            return ValueTask.FromResult(HttpStatusCode.OK);
        }
    }

    private sealed class TargetCaptureResolver(
        ITelemetryDestinationResolver? inner = null) :
        ITelemetryDestinationResolver
    {
        public bool ActivationAvailable => true;

        internal TelemetryEnvironmentTarget? LastTarget { get; private set; }

        internal TelemetryDestination? LastDestination { get; private set; }

        public async ValueTask<TelemetryDestination?> ResolveAsync(
            TelemetryEnvironmentTarget target,
            CancellationToken cancellationToken)
        {
            LastTarget = target;
            LastDestination = inner is null
                ? null
                : await inner.ResolveAsync(target, cancellationToken);
            return LastDestination;
        }
    }
}
