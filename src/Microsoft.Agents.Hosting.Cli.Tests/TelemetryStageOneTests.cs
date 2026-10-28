using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.PowerPlatform.Tooling.OneDS;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class TelemetryStageOneTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ConfiguredIngestionKeysAreValidAndDistinct()
    {
        var production = new OneDSEndpointConfiguration(
            OneDSCollectorEndpoints.RegionalPublicCloud.EU,
            TelemetryIngestionConfiguration.ProductionKey);
        var nonProduction = new OneDSEndpointConfiguration(
            OneDSCollectorEndpoints.RegionalPublicCloud.US,
            TelemetryIngestionConfiguration.NonProductionKey);

        Assert.NotEqual(production.ApiKey, nonProduction.ApiKey);
    }

    [Fact]
    public async Task ConfiguredPublicRingsSelectConfiguredKeysAndPolicyDefaults()
    {
        var production = new TelemetryDestinationResolver(
            PowerPlatformApiTargetConfig.Public,
            new FixedGeographyClient("unitedstates"));
        var nonProduction = new TelemetryDestinationResolver(
            PowerPlatformApiTargetConfig.Test,
            new ThrowingGeographyClient());

        Assert.True(production.ActivationAvailable);
        Assert.True(nonProduction.ActivationAvailable);
        TelemetryDestination productionDestination =
            Assert.IsType<TelemetryDestination>(
                await production.ResolveAsync(
                    TelemetryEnvironmentTarget.Unknown,
                    default));
        TelemetryDestination nonProductionDestination =
            Assert.IsType<TelemetryDestination>(
                await nonProduction.ResolveAsync(
                    TelemetryEnvironmentTarget.Resolved(
                        [Target("environment")]),
                    default));
        Assert.Equal(
            TelemetryIngestionConfiguration.ProductionKey,
            productionDestination.ApiKey);
        Assert.Equal(
            OneDSCollectorEndpoints.RegionalPublicCloud.EU,
            productionDestination.Endpoint);
        Assert.Equal(
            TelemetryIngestionConfiguration.NonProductionKey,
            nonProductionDestination.ApiKey);
        Assert.Equal(
            OneDSCollectorEndpoints.RegionalPublicCloud.US,
            nonProductionDestination.Endpoint);
    }

    [Fact]
    public void MissingSelectedKeyStillDisablesActivation()
    {
        var resolver = new TelemetryDestinationResolver(
            PowerPlatformApiTargetConfig.Public,
            new FixedGeographyClient("unitedstates"),
            productionKey: null,
            nonProductionKey: SyntheticApiKey());

        Assert.False(resolver.ActivationAvailable);
    }

    [Theory]
    [InlineData(null, "Europe")]
    [InlineData("", "Europe")]
    [InlineData("Europe", "Europe")]
    [InlineData("United Kingdom FirstRelease", "Europe")]
    [InlineData("United States", "Global")]
    [InlineData("Australia-preview", "Global")]
    [InlineData("Chile", "Global")]
    [InlineData("Indonesia EUAP", "Global")]
    [InlineData("New Zealand FirstRelease", "Global")]
    [InlineData("South America", "Global")]
    [InlineData("United Arab Emirates", "Global")]
    [InlineData("unrecognized-geography", "Europe")]
    public void GeographyPolicyUsesTargetEnvironmentAndFailsClosedToEurope(
        string? geography,
        string expected)
    {
        Assert.Equal(
            expected,
            TelemetryDestinationResolver.ResolveLocation(geography).ToString());
    }

    [Fact]
    public async Task SovereignCloudNeverResolvesAPublicCollector()
    {
        var resolver = new TelemetryDestinationResolver(
            PowerPlatformApiTargetConfig.Public with { Cloud = CloudInstance.UsGov },
            new ThrowingGeographyClient(),
            productionKey: SyntheticApiKey());

        Assert.False(resolver.ActivationAvailable);
        Assert.Null(await resolver.ResolveAsync(TelemetryEnvironmentTarget.Unknown, default));
    }

    [Fact]
    public async Task AnUnclassifiedInvocationNeverResolvesACollector()
    {
        var resolver = new UnclassifiedTelemetryDestinationResolver();

        Assert.False(resolver.ActivationAvailable);
        Assert.Null(await resolver.ResolveAsync(TelemetryEnvironmentTarget.Unknown, default));
        Assert.Null(
            await resolver.ResolveAsync(
                TelemetryEnvironmentTarget.Resolved([Target("environment")]),
                default));
    }

    [Fact]
    public async Task AnInvocationWithNoKnownRingDoesNotBorrowProductionIngestion()
    {
        var classified = new TelemetryDestinationResolver(
            PowerPlatformApiTargetConfig.Public,
            new ThrowingGeographyClient(),
            productionKey: TelemetryIngestionConfiguration.ProductionKey);

        var known = Program.SelectTelemetryDestinationResolver(ringKnown: true, classified);
        var unknown = Program.SelectTelemetryDestinationResolver(ringKnown: false, classified);

        Assert.Same(classified, known);
        Assert.IsType<UnclassifiedTelemetryDestinationResolver>(unknown);
        Assert.True(known.ActivationAvailable);
        Assert.False(unknown.ActivationAvailable);
        Assert.NotNull(await known.ResolveAsync(TelemetryEnvironmentTarget.Unknown, default));
        Assert.Null(await unknown.ResolveAsync(TelemetryEnvironmentTarget.Unknown, default));
    }

    [Fact]
    public void SelectingATelemetryDestinationResolverRequiresTheClassifiedOne()
    {
        Assert.Throws<ArgumentNullException>(
            () => Program.SelectTelemetryDestinationResolver(ringKnown: true, null!));
    }

    [Fact]
    public async Task ExplicitEnvironmentTargetUsesItsAuthoritativeGeography()
    {
        var geography = new FixedGeographyClient("unitedstates");
        var resolver = new TelemetryDestinationResolver(
            PowerPlatformApiTargetConfig.Public,
            geography,
            productionKey: SyntheticApiKey());

        TelemetryDestination destination =
            Assert.IsType<TelemetryDestination>(
                await resolver.ResolveAsync(
                    TelemetryEnvironmentTarget.Resolved(
                        [Target("different-environment")]),
                    default));

        Assert.Equal(TelemetryLocation.Global, destination.Location);
        Assert.Equal(
            OneDSCollectorEndpoints.RegionalPublicCloud.US,
            destination.Endpoint);
        Assert.Equal("different-environment", Assert.Single(geography.Targets).EnvironmentId);
    }

    [Fact]
    public async Task ProductionAndNonProductionSelectTheirOwnSharedTableCoordinates()
    {
        string productionKey = SyntheticApiKey('0');
        string nonProductionKey = SyntheticApiKey('a');
        var production = new TelemetryDestinationResolver(
            PowerPlatformApiTargetConfig.Public,
            new FixedGeographyClient("unitedstates"),
            productionKey,
            nonProductionKey);
        var nonProduction = new TelemetryDestinationResolver(
            PowerPlatformApiTargetConfig.Test,
            new ThrowingGeographyClient(),
            productionKey,
            nonProductionKey);

        TelemetryDestination productionDestination =
            Assert.IsType<TelemetryDestination>(
                await production.ResolveAsync(
                    TelemetryEnvironmentTarget.Resolved(
                        [Target("environment")]),
                    default));
        TelemetryDestination nonProductionDestination =
            Assert.IsType<TelemetryDestination>(
                await nonProduction.ResolveAsync(
                    TelemetryEnvironmentTarget.Resolved(
                        [Target("environment")]),
                    default));

        Assert.Equal(productionKey, productionDestination.ApiKey);
        Assert.Equal("powerappsclient", productionDestination.AppName);
        Assert.Equal("prod", productionDestination.ClusterCategory);
        Assert.Equal(
            OneDSCollectorEndpoints.RegionalPublicCloud.US,
            productionDestination.Endpoint);
        Assert.Equal(nonProductionKey, nonProductionDestination.ApiKey);
        Assert.Equal("powerappsclientnonprod", nonProductionDestination.AppName);
        Assert.Equal("test", nonProductionDestination.ClusterCategory);
        Assert.Equal(
            OneDSCollectorEndpoints.RegionalPublicCloud.US,
            nonProductionDestination.Endpoint);
    }

    [Fact]
    public async Task NonProductionDefaultsToGlobalWithoutReadingEnvironmentGeography()
    {
        var resolver = new TelemetryDestinationResolver(
            PowerPlatformApiTargetConfig.Test,
            new ThrowingGeographyClient(),
            productionKey: SyntheticApiKey(),
            nonProductionKey: SyntheticApiKey('a'));

        TelemetryDestination destination =
            Assert.IsType<TelemetryDestination>(
                await resolver.ResolveAsync(
                    TelemetryEnvironmentTarget.Resolved(
                        [Target("environment")]),
                    default));

        Assert.Equal(TelemetryLocation.Global, destination.Location);
        Assert.Equal(
            OneDSCollectorEndpoints.RegionalPublicCloud.US,
            destination.Endpoint);
    }

    [Fact]
    public void SharedEventIsIdentifierFreeAndUsesTheHashPrivacyFlag()
    {
        var telemetry = Event(
            isAttempt: false,
            CommandTelemetryOutcome.Success,
            sequenceNumber: 2);
        string payload =
            CommonSchemaV4Serializer.SerializeToJsonStream([telemetry]);
        using JsonDocument document = JsonDocument.Parse(payload.Trim());
        JsonElement root = document.RootElement;
        JsonElement data = root.GetProperty("data");

        Assert.Equal("event", root.GetProperty("name").GetString());
        Assert.Equal(UtcFlags.Microsoft_IsPII_Hash, telemetry.Flags);
        Assert.Equal("AgentHostingCLI", data.GetProperty("clientType").GetString());
        Assert.Equal("CommandOutcome", data.GetProperty("event_Name").GetString());
        Assert.Equal(
            "AgentHostingCLI.env.list",
            data.GetProperty("operation_Name").GetString());
        Assert.Equal(
            [
                "IsFailure",
                "cliVersion",
                "cloud",
                "exitClass",
                "isOnePlayer",
                "operationName",
                "outcomeCategory",
                "station",
                "telemetryLocation",
                "webPlayerScenario",
            ],
            JsonDocument.Parse(data.GetProperty("customDimensions").GetString()!)
                .RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.DoesNotContain(
            data.EnumerateObject().Select(property => property.Name),
            name => name.Contains("tenant", StringComparison.OrdinalIgnoreCase)
                || name.Contains("principal", StringComparison.OrdinalIgnoreCase)
                || name.Contains("user", StringComparison.OrdinalIgnoreCase)
                || name.Contains("argument", StringComparison.OrdinalIgnoreCase)
                || name.Contains("error", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OrdinaryLeafRunsThroughHostFactoryAndPackageTransportToEurope()
    {
        await using var server = new LoopbackServer();
        var resolver = new LoopbackDestinationResolver(
            new TelemetryDestinationResolver(
                PowerPlatformApiTargetConfig.Public,
                new ThrowingGeographyClient(),
                productionKey: SyntheticApiKey()),
            server.Address);
        var controls = new TelemetryControlService(
            new MemoryPreferenceStore(TelemetryRememberedPreference.Enabled),
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: resolver.ActivationAvailable);
        var factory = new CommandTelemetrySessionFactory(
            controls,
            resolver,
            new FixedClock(Timestamp),
            typeof(AgentHostCli).Assembly,
            policyTimeout: TelemetryTestBudgets.Policy,
            flushTimeout: TelemetryTestBudgets.Flush);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int exitCode = await new AgentHostCli(
                new FakeOperations(),
                telemetryFactory: factory)
            .InvokeAsync(["env", "list", "--json"], stdout, stderr, color: false);

        string body = await server.RequestBody;
        string[] events = body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());
        Assert.Equal(1, resolver.ResolveCalls);
        Assert.Equal(TelemetryEnvironmentTarget.Unknown, resolver.LastTarget);
        Assert.Equal(TelemetryLocation.Europe, resolver.LastDestination?.Location);
        Assert.Equal(2, events.Length);
        Assert.All(events, item =>
        {
            using JsonDocument document = JsonDocument.Parse(item);
            Assert.Equal("event", document.RootElement.GetProperty("name").GetString());
            JsonElement data = document.RootElement.GetProperty("data");
            Assert.Equal("AgentHostingCLI", data.GetProperty("clientType").GetString());
            Assert.Equal(
                "AgentHostingCLI.env.list",
                data.GetProperty("operation_Name").GetString());
            Assert.DoesNotContain(
                data.EnumerateObject().Select(property => property.Name),
                name => name.Contains("tenant", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("principal", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("object", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("argument", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("error", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task UnknownWorkspaceTargetDoesNotInheritActiveRowGeography()
    {
        var resolver = new TelemetryDestinationResolver(
            PowerPlatformApiTargetConfig.Public,
            new FixedGeographyClient("unitedstates"),
            productionKey: SyntheticApiKey());

        TelemetryDestination destination =
            Assert.IsType<TelemetryDestination>(
                await resolver.ResolveAsync(TelemetryEnvironmentTarget.Unknown, default));

        Assert.Equal(TelemetryLocation.Europe, destination.Location);
    }

    [Fact]
    public async Task EnvironmentlessInvocationsDoNotInventTargets()
    {
        TelemetryEnvironmentTarget target =
            Assert.IsType<TelemetryEnvironmentTarget>(
                await CaptureInvocationTargetAsync(
                    "env",
                    "list",
                    "--json"));

        Assert.False(target.IsComplete);
        Assert.Empty(target.Environments);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DisabledNoKeyAndSovereignCompositionNeverReachTransport(
        bool userDisabled,
        bool sovereign)
    {
        var inner = new TelemetryDestinationResolver(
            sovereign
                ? PowerPlatformApiTargetConfig.Public with { Cloud = CloudInstance.UsGov }
                : PowerPlatformApiTargetConfig.Public,
            new ThrowingGeographyClient(),
            productionKey: userDisabled || sovereign ? SyntheticApiKey() : null);
        var resolver = new CountingDestinationResolver(inner);
        var controls = new TelemetryControlService(
            new MemoryPreferenceStore(
                userDisabled
                    ? TelemetryRememberedPreference.Disabled
                    : TelemetryRememberedPreference.Enabled),
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: resolver.ActivationAvailable);
        var factory = new CommandTelemetrySessionFactory(
            controls,
            resolver,
            new FixedClock(Timestamp),
            typeof(AgentHostCli).Assembly);

        int exitCode = await new AgentHostCli(
                new FakeOperations(),
                telemetryFactory: factory)
            .InvokeAsync(
                ["env", "list", "--json"],
                new StringWriter(),
                new StringWriter(),
                color: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, resolver.ResolveCalls);
    }

    [Fact]
    public async Task TelemetryControlCommandsNeverMeasureThemselves()
    {
        var preferenceStore = new MemoryPreferenceStore(
            TelemetryRememberedPreference.Enabled);
        var controls = new TelemetryControlService(
            preferenceStore,
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);
        var resolver = new CapturingDestinationResolver(
            Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/")));
        var services = new ServiceCollection();
        services.AddSingleton<
            ICommandHandler<TelemetryStatusRequest, TelemetryControlReport>>(controls);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var factory = new CommandTelemetrySessionFactory(
            controls,
            resolver,
            new FixedClock(Timestamp),
            typeof(AgentHostCli).Assembly);
        var output = new StringWriter();

        int exitCode = await new AgentHostCli(
                new FakeOperations(),
                telemetryFactory: factory,
                services: new CompositeHandlerProvider(
                    new FacadeHandlerProvider(new FakeOperations()),
                    provider),
                featureFlags: TestFeatureFlags.AllProductSurfaces)
            .InvokeAsync(
                ["telemetry", "status", "--json"],
                output,
                new StringWriter(),
                color: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, resolver.ResolveCalls);
        Assert.True(
            CommandReflector
                .Discover(typeof(TelemetryGroup).Assembly, new TestFeatureFlags())
                .Single(group => group.Name == "telemetry")
                .SuppressTelemetry);
    }

    [Theory]
    [InlineData(null, "NotSet")]
    [InlineData("", "NotSet")]
    [InlineData("1", "OptedOut")]
    [InlineData("TRUE", "OptedOut")]
    [InlineData("0", "OptedIn")]
    [InlineData("false", "OptedIn")]
    [InlineData("maybe", "Invalid")]
    public void EnvironmentOverrideIsStrictAndNullableBoolean(
        string? value,
        string expected)
    {
        Assert.Equal(
            expected,
            TelemetryControlService.ParseEnvironmentOverride(value).ToString());
    }

    [Fact]
    public async Task FilePreferenceIsSeparateAtomicStateAndRoundTrips()
    {
        string root = Path.Combine(
            AppContext.BaseDirectory,
            "telemetry-stage-one-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = FileTelemetryPreferenceStore.CreateForAhConfigDirectory(root);

            Assert.Equal(
                TelemetryRememberedPreference.NotSet,
                await store.ReadAsync(default));
            Assert.False(Directory.Exists(root));

            await store.WriteAsync(enabled: false, default);

            Assert.Equal(
                TelemetryRememberedPreference.Disabled,
                await new FileTelemetryPreferenceStore(store.FilePath)
                    .ReadAsync(default));
            Assert.Equal(
                [store.FilePath],
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InvalidPreferenceAndInvalidEnvironmentBothFailClosed()
    {
        var invalidPreference = new TelemetryControlService(
            new MemoryPreferenceStore(TelemetryRememberedPreference.Invalid),
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);
        var invalidEnvironment = new TelemetryControlService(
            new MemoryPreferenceStore(TelemetryRememberedPreference.Enabled),
            () => "maybe",
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);

        Assert.False(await invalidPreference.IsCollectionEnabledAsync(default));
        Assert.False(await invalidEnvironment.IsCollectionEnabledAsync(default));
    }

    [Fact]
    public async Task OptOutWinsAndUnsetDefaultsOnOnlyWhenAKeyIsAvailable()
    {
        var optedOut = new TelemetryControlService(
            new MemoryPreferenceStore(TelemetryRememberedPreference.Enabled),
            () => "true",
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);
        var keyUnavailable = new TelemetryControlService(
            new MemoryPreferenceStore(TelemetryRememberedPreference.Enabled),
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: false);
        var defaultWithKey = new TelemetryControlService(
            new MemoryPreferenceStore(TelemetryRememberedPreference.NotSet),
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);

        Assert.False(await optedOut.IsCollectionEnabledAsync(default));
        Assert.False(await keyUnavailable.IsCollectionEnabledAsync(default));
        Assert.True(await defaultWithKey.IsCollectionEnabledAsync(default));
    }

    private static ResolvedEnvironmentTarget Target(string environmentId) =>
        new(environmentId, EnvironmentFixtures.ContosoTenant);

    private static async Task<TelemetryEnvironmentTarget?> CaptureInvocationTargetAsync(
        params string[] args)
    {
        var resolver = new TargetCaptureResolver();
        var controls = new TelemetryControlService(
            new MemoryPreferenceStore(TelemetryRememberedPreference.Enabled),
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);
        var factory = new CommandTelemetrySessionFactory(
            controls,
            resolver,
            new FixedClock(Timestamp),
            typeof(AgentHostCli).Assembly,
            policyTimeout: TelemetryTestBudgets.Policy,
            flushTimeout: TelemetryTestBudgets.Flush);

        await new AgentHostCli(new FakeOperations(), telemetryFactory: factory)
            .InvokeAsync(
                args,
                new StringWriter(),
                new StringWriter(),
                color: false);

        return resolver.LastTarget;
    }

    private static CommandTelemetryEvent Event(
        bool isAttempt,
        CommandTelemetryOutcome? outcome,
        int sequenceNumber) =>
        new(
            isAttempt
                ? CommandTelemetryRecord.Attempt(
                    CommandTelemetryPath.FromMetadata(["env", "list"]),
                    Timestamp,
                    tenantId: null)
                : CommandTelemetryRecord.OutcomeRecord(
                    CommandTelemetryPath.FromMetadata(["env", "list"]),
                    outcome ?? throw new ArgumentNullException(nameof(outcome)),
                    Timestamp,
                    tenantId: null),
            Destination(OneDSCollectorEndpoints.RegionalPublicCloud.EU),
            "1.2.3-test",
            "00000000-0000-0000-0000-000000000001",
            sequenceNumber);

    private static TelemetryDestination Destination(Uri endpoint) =>
        new(
            endpoint,
            SyntheticApiKey(),
            "powerappsclient",
            "prod",
            "prod",
            TelemetryLocation.Europe);

    private static string SyntheticApiKey(char first = '0') => string.Join(
        '-',
        new string(first, 32),
        new string('1', 8),
        new string('2', 4),
        new string('3', 4),
        new string('4', 4),
        new string('5', 12),
        new string('6', 4));

    private sealed class CapturingDestinationResolver : ITelemetryDestinationResolver
    {
        private readonly TelemetryDestination _destination;

        internal CapturingDestinationResolver(TelemetryDestination destination) =>
            _destination = destination;

        public bool ActivationAvailable => true;

        internal int ResolveCalls { get; private set; }

        public ValueTask<TelemetryDestination?> ResolveAsync(
            TelemetryEnvironmentTarget target,
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return ValueTask.FromResult<TelemetryDestination?>(_destination);
        }
    }

    private sealed class LoopbackDestinationResolver(
        ITelemetryDestinationResolver inner,
        Uri endpoint) : ITelemetryDestinationResolver
    {
        public bool ActivationAvailable => inner.ActivationAvailable;

        internal int ResolveCalls { get; private set; }

        internal TelemetryEnvironmentTarget? LastTarget { get; private set; }

        internal TelemetryDestination? LastDestination { get; private set; }

        public async ValueTask<TelemetryDestination?> ResolveAsync(
            TelemetryEnvironmentTarget target,
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            LastTarget = target;
            TelemetryDestination? destination =
                await inner.ResolveAsync(target, cancellationToken);
            LastDestination = destination;
            return destination is null ? null : destination with { Endpoint = endpoint };
        }
    }

    private sealed class CountingDestinationResolver(
        ITelemetryDestinationResolver inner) : ITelemetryDestinationResolver
    {
        public bool ActivationAvailable => inner.ActivationAvailable;

        internal int ResolveCalls { get; private set; }

        public async ValueTask<TelemetryDestination?> ResolveAsync(
            TelemetryEnvironmentTarget target,
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return await inner.ResolveAsync(target, cancellationToken);
        }
    }

    private sealed class TargetCaptureResolver : ITelemetryDestinationResolver
    {
        public bool ActivationAvailable => true;

        internal TelemetryEnvironmentTarget? LastTarget { get; private set; }

        public ValueTask<TelemetryDestination?> ResolveAsync(
            TelemetryEnvironmentTarget target,
            CancellationToken cancellationToken)
        {
            LastTarget = target;
            return ValueTask.FromResult<TelemetryDestination?>(null);
        }
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

    private sealed class ThrowingGeographyClient : IEnvironmentGeographyClient
    {
        public Task<string?> GetGeographyAsync(
            ResolvedEnvironmentTarget target,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Geography lookup must not run.");
    }

    private sealed class MemoryPreferenceStore : ITelemetryPreferenceStore
    {
        private TelemetryRememberedPreference _preference;

        internal MemoryPreferenceStore(TelemetryRememberedPreference preference) =>
            _preference = preference;

        public ValueTask<TelemetryRememberedPreference> ReadAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_preference);

        public ValueTask WriteAsync(bool enabled, CancellationToken cancellationToken)
        {
            _preference = enabled
                ? TelemetryRememberedPreference.Enabled
                : TelemetryRememberedPreference.Disabled;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(10));
        private readonly Task<string> _capture;

        internal LoopbackServer()
        {
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Address = new Uri($"http://127.0.0.1:{port}/OneCollector/1.0/");
            _capture = CaptureAsync();
        }

        internal Uri Address { get; }

        internal Task<string> RequestBody => _capture;

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try
            {
                await _capture;
            }
            catch (Exception failure) when (
                failure is OperationCanceledException
                    or ObjectDisposedException
                    or SocketException)
            {
            }

            _cancellation.Dispose();
        }

        private async Task<string> CaptureAsync()
        {
            using TcpClient client =
                await _listener.AcceptTcpClientAsync(_cancellation.Token);
            await using NetworkStream stream = client.GetStream();
            var received = new List<byte>();
            int headerEnd = -1;
            int contentLength = 0;
            var buffer = new byte[4096];

            while (true)
            {
                int read = await stream.ReadAsync(buffer, _cancellation.Token);
                if (read == 0)
                {
                    break;
                }

                received.AddRange(buffer.AsSpan(0, read).ToArray());
                if (headerEnd < 0)
                {
                    string text = Encoding.ASCII.GetString([.. received]);
                    headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd >= 0)
                    {
                        foreach (string header in text[..headerEnd].Split("\r\n"))
                        {
                            if (header.StartsWith(
                                    "Content-Length:",
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                contentLength = int.Parse(
                                    header["Content-Length:".Length..].Trim(),
                                    System.Globalization.CultureInfo.InvariantCulture);
                            }
                        }

                        headerEnd += 4;
                    }
                }

                if (headerEnd >= 0 && received.Count >= headerEnd + contentLength)
                {
                    break;
                }
            }

            byte[] response =
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
            await stream.WriteAsync(response, _cancellation.Token);
            return Encoding.UTF8.GetString(
                received.Skip(headerEnd).Take(contentLength).ToArray());
        }
    }
}
