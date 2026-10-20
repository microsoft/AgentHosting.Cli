using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class TelemetryControlTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 9, 22, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TelemetrySuppressionIsDeclaredOnlyByTheControlGroup()
    {
        IReadOnlyList<CommandGroupMetadata> roots =
            CommandReflector.Discover(
                typeof(TelemetryGroup).Assembly,
                TestFeatureFlags.AllProductSurfaces);
        CommandGroupMetadata telemetry = Assert.Single(
            roots,
            group => group.DeclaringType == typeof(TelemetryGroup));

        Assert.True(telemetry.SuppressTelemetry);
        Assert.Equal(
            ["enable", "disable", "status"],
            telemetry.Commands.Select(command => command.Name));
        Assert.DoesNotContain(
            roots,
            group => group.DeclaringType != typeof(TelemetryGroup)
                && group.SuppressTelemetry);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("1", 1)]
    [InlineData("true", 1)]
    [InlineData("TRUE", 1)]
    [InlineData("0", 2)]
    [InlineData("false", 2)]
    [InlineData("FALSE", 2)]
    [InlineData("yes", 3)]
    [InlineData(" true ", 3)]
    public void EnvironmentOverrideAcceptsOnlyExactDocumentedValues(
        string? value,
        int expected)
    {
        Assert.Equal(
            (TelemetryEnvironmentOverride)expected,
            TelemetryControlService.ParseEnvironmentOverride(value));
    }

    [Fact]
    public async Task MissingPreferenceAndStatusDoNotCreateTelemetryStorage()
    {
        using var directory = new TempDir();
        string configDirectory = directory.File("config");
        var store =
            FileTelemetryPreferenceStore.CreateForAhConfigDirectory(configDirectory);
        var controls = Controls(
            store,
            environment: null,
            fallback: false,
            activation: false);

        TelemetryControlReport report =
            await controls.InspectAsync("status", CancellationToken.None);

        Assert.Equal("not-set", report.RememberedPreference);
        Assert.False(report.EffectiveCollectionEnabled);
        Assert.Equal(
            "collection-activation-unavailable",
            report.EffectiveReason);
        Assert.False(Directory.Exists(Path.Combine(configDirectory, "telemetry")));
    }

    [Fact]
    public async Task RememberedPreferenceRoundTripsInSeparateAtomicFile()
    {
        using var directory = new TempDir();
        string configDirectory = directory.File("config");
        var store =
            FileTelemetryPreferenceStore.CreateForAhConfigDirectory(configDirectory);

        await store.WriteAsync(enabled: true, CancellationToken.None);
        Assert.Equal(
            TelemetryRememberedPreference.Enabled,
            await store.ReadAsync(CancellationToken.None));
        await store.WriteAsync(enabled: false, CancellationToken.None);

        Assert.Equal(
            TelemetryRememberedPreference.Disabled,
            await store.ReadAsync(CancellationToken.None));
        Assert.EndsWith(
            Path.Combine("telemetry", "collection-preference.json"),
            store.FilePath,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "user-settings.json",
            store.FilePath,
            StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            configDirectory,
            "telemetry",
            "user-settings.json")));
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(store.FilePath)!,
            "*.tmp-*"));
    }

    [Fact]
    public async Task EnvironmentOnlyOptOutCreatesNoTelemetryState()
    {
        using var directory = new TempDir();
        string configDirectory = directory.File("config");
        var store =
            FileTelemetryPreferenceStore.CreateForAhConfigDirectory(configDirectory);
        var controls = Controls(
            store,
            environment: "1",
            fallback: true,
            activation: true);

        Assert.False(await controls.IsCollectionEnabledAsync(CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(configDirectory, "telemetry")));
    }

    public static TheoryData<string, string> MalformedPreferenceCases => new()
    {
        { "malformed-json", "{" },
        { "json-null", "null" },
        { "unsupported-version", """{"schemaVersion":2,"enabled":true}""" },
        { "missing-enabled", """{"schemaVersion":1}""" },
        {
            "duplicate-enabled",
            """{"schemaVersion":1,"enabled":false,"enabled":true}"""
        },
        {
            "extra-property",
            """{"schemaVersion":1,"enabled":true,"unexpected":"value"}"""
        },
        {
            "schema-version-string",
            """{"schemaVersion":"1","enabled":true}"""
        },
        {
            "schema-version-null",
            """{"schemaVersion":null,"enabled":true}"""
        },
        {
            "schema-version-boolean",
            """{"schemaVersion":true,"enabled":true}"""
        },
        {
            "schema-version-object",
            """{"schemaVersion":{},"enabled":true}"""
        },
        {
            "schema-version-array",
            """{"schemaVersion":[],"enabled":true}"""
        },
    };

    [Theory]
    [MemberData(nameof(MalformedPreferenceCases))]
    public async Task RealMalformedPreferenceFilesFailClosed(
        string caseName,
        string contents)
    {
        using var directory = new TempDir();
        string filePath = directory.File("collection-preference.json");
        await File.WriteAllTextAsync(filePath, contents);
        var store = new FileTelemetryPreferenceStore(filePath);
        var controls = Controls(
            store,
            environment: null,
            fallback: true,
            activation: true);

        TelemetryControlReport report =
            await controls.InspectAsync("status", CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(caseName));
        Assert.Equal("invalid", report.RememberedPreference);
        Assert.False(report.EffectiveCollectionEnabled);
        Assert.Equal(
            "invalid-remembered-preference",
            report.EffectiveReason);
    }

    [Fact]
    public async Task DirectoryPreferenceWriteTargetReportsBoundedFailure()
    {
        using var directory = new TempDir();
        var store = new FileTelemetryPreferenceStore(directory.Path);

        AgentHostingStateException failure =
            await Assert.ThrowsAsync<AgentHostingStateException>(
                async () =>
                    await store.WriteAsync(
                        enabled: true,
                        CancellationToken.None));

        Assert.Equal(
            "The AgentHost telemetry preference could not be written.",
            failure.Message);
        Assert.DoesNotContain(
            directory.Path,
            failure.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, null, true, true, "enabled")]
    [InlineData(true, true, "1", true, false, "environment-opt-out")]
    [InlineData(true, true, "invalid", true, false, "invalid-environment-override")]
    [InlineData(false, true, null, true, false, "remembered-disabled")]
    [InlineData(null, false, null, true, false, "composition-default-disabled")]
    [InlineData(null, true, null, true, true, "enabled")]
    [InlineData(false, false, "false", true, true, "environment-opt-in")]
    [InlineData(null, false, "0", true, true, "environment-opt-in")]
    [InlineData(true, true, null, false, false, "collection-activation-unavailable")]
    [InlineData(false, true, "false", false, false, "collection-activation-unavailable")]
    public async Task StatusReportsCompletePrecedenceMatrix(
        bool? preference,
        bool fallback,
        string? environment,
        bool activation,
        bool expectedEnabled,
        string expectedReason)
    {
        var controls = Controls(
            new TrackingPreferenceStore(preference),
            environment,
            fallback,
            activation);

        TelemetryControlReport report =
            await controls.InspectAsync("status", CancellationToken.None);

        Assert.Equal(expectedEnabled, report.EffectiveCollectionEnabled);
        Assert.Equal(expectedReason, report.EffectiveReason);
    }

    [Fact]
    public async Task EnabledPreferenceWithHardGateOffIsReportedInactive()
    {
        var controls = Controls(
            new TrackingPreferenceStore(enabled: true),
            environment: null,
            fallback: true,
            activation: false);

        TelemetryControlReport report =
            await controls.InspectAsync("status", CancellationToken.None);

        Assert.Equal("enabled", report.RememberedPreference);
        Assert.False(report.CollectionActivationAvailable);
        Assert.False(report.EffectiveCollectionEnabled);
        Assert.Equal(
            "collection-activation-unavailable",
            report.EffectiveReason);
    }

    [Theory]
    [InlineData("1", true, false)]
    [InlineData("garbage", true, false)]
    [InlineData(null, false, false)]
    [InlineData("false", true, true)]
    [InlineData("0", true, true)]
    public async Task EnvironmentAndHardGateShortCircuitPreferenceRead(
        string? environment,
        bool activation,
        bool expectedEnabled)
    {
        var store = new TrackingPreferenceStore(
            enabled: true,
            throwOnRead: true);
        var controls = Controls(
            store,
            environment,
            fallback: true,
            activation);

        Assert.Equal(
            expectedEnabled,
            await controls.IsCollectionEnabledAsync(CancellationToken.None));
        Assert.Equal(0, store.ReadCount);
    }

    [Theory]
    [InlineData(false, false, "false", true, true)]
    [InlineData(null, false, "0", true, true)]
    [InlineData(null, false, null, true, false)]
    [InlineData(true, true, "false", false, false)]
    [InlineData(true, true, "1", true, false)]
    public async Task StatusAndRuntimePolicyAgree(
        bool? preference,
        bool fallback,
        string? environment,
        bool activation,
        bool expectedEnabled)
    {
        var controls = Controls(
            new TrackingPreferenceStore(preference),
            environment,
            fallback,
            activation);

        TelemetryControlReport status =
            await controls.InspectAsync("status", CancellationToken.None);
        bool runtime =
            await controls.IsCollectionEnabledAsync(CancellationToken.None);

        Assert.Equal(expectedEnabled, status.EffectiveCollectionEnabled);
        Assert.Equal(expectedEnabled, runtime);
    }

    [Fact]
    public async Task FalseOverrideReportsIgnoredSavedDisable()
    {
        var controls = Controls(
            new TrackingPreferenceStore(enabled: false),
            environment: "false",
            fallback: false,
            activation: true);

        TelemetryControlReport status =
            await controls.InspectAsync("status", CancellationToken.None);

        Assert.Equal("disabled", status.RememberedPreference);
        Assert.Equal("opted-in", status.EnvironmentOverride);
        Assert.True(status.EffectiveCollectionEnabled);
        Assert.Equal("environment-opt-in", status.EffectiveReason);
    }

    [Fact]
    public async Task EnableDisableAndStatusUseNormalHumanAndJsonHostEnvelopes()
    {
        using var directory = new TempDir();
        var store =
            FileTelemetryPreferenceStore.CreateForAhConfigDirectory(directory.Path);
        var controls = Controls(
            store,
            environment: null,
            fallback: false,
            activation: false);
        await using ServiceProvider container = Container(controls);
        AgentHostCli host = Host(
            controls,
            container,
            new ThrowingResolver());

        InvocationResult enabled =
            await InvokeAsync(host, "telemetry", "enable", "--json");
        InvocationResult status =
            await InvokeAsync(host, "telemetry", "status");
        InvocationResult disabled =
            await InvokeAsync(host, "telemetry", "disable", "--json");

        Assert.Equal(ExitCodes.Success, enabled.ExitCode);
        Assert.Empty(enabled.Stderr);
        using JsonDocument enabledJson = JsonDocument.Parse(enabled.Stdout);
        Assert.True(enabledJson.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(
            "enabled",
            enabledJson.RootElement
                .GetProperty("rememberedPreference")
                .GetString());
        Assert.False(
            enabledJson.RootElement
                .GetProperty("effectiveCollectionEnabled")
                .GetBoolean());
        Assert.Contains(
            "Telemetry preference: enabled.",
            status.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            "Collection activation: unavailable.",
            status.Stdout,
            StringComparison.Ordinal);
        using JsonDocument disabledJson = JsonDocument.Parse(disabled.Stdout);
        Assert.Equal(
            "disabled",
            disabledJson.RootElement
                .GetProperty("rememberedPreference")
                .GetString());
    }

    [Fact]
    public async Task PreferenceWriteFailureUsesBoundedJsonErrorContract()
    {
        var controls = Controls(
            new ThrowingPreferenceStore(throwOnWrite: true),
            environment: null,
            fallback: false,
            activation: false);
        await using ServiceProvider container = Container(controls);
        AgentHostCli host = Host(
            controls,
            container,
            new ThrowingResolver());

        InvocationResult result =
            await InvokeAsync(host, "telemetry", "enable", "--json");

        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Empty(result.Stderr);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(
            "state-unusable",
            json.RootElement.GetProperty("errorKind").GetString());
        Assert.Equal(
            "The AgentHost telemetry preference could not be written.",
            json.RootElement.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task DirectoryPreferencePathFailsClosedForRuntimeAndStatus()
    {
        using var directory = new TempDir();
        string preferencePath = Path.Combine(
            directory.Path,
            "config",
            "telemetry",
            "collection-preference.json");
        Directory.CreateDirectory(preferencePath);
        var store = new FileTelemetryPreferenceStore(preferencePath);
        var controls = Controls(
            store,
            environment: null,
            fallback: true,
            activation: true);
        var resolver = new ThrowingResolver();
        AgentHostCli productHost = Host(controls, services: null, resolver);

        InvocationResult product =
            await InvokeAsync(productHost, "auth", "status");
        await using ServiceProvider container = Container(controls);
        InvocationResult status = await InvokeAsync(
            Host(controls, container, resolver),
            "telemetry",
            "status",
            "--json");

        Assert.Equal(ExitCodes.Success, product.ExitCode);
        Assert.Empty(product.Stderr);
        Assert.Equal(0, resolver.ResolveCalls);
        Assert.Equal(ExitCodes.Failure, status.ExitCode);
        Assert.Empty(status.Stderr);
        using JsonDocument json = JsonDocument.Parse(status.Stdout);
        Assert.Equal(
            "The AgentHost telemetry preference could not be read.",
            json.RootElement.GetProperty("errorMessage").GetString());
        Assert.Equal(
            "state-unusable",
            json.RootElement.GetProperty("errorKind").GetString());
        Assert.False(
            json.RootElement.TryGetProperty("rememberedPreference", out _));
        Assert.DoesNotContain(
            preferencePath,
            status.Stdout,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("telemetry", "enable")]
    [InlineData("telemetry", "disable")]
    [InlineData("telemetry", "status")]
    [InlineData("telemetry", "--help")]
    [InlineData("telemetry", "status", "--unexpected")]
    public async Task TelemetryControlSubtreeNeverCreatesEnabledSession(
        params string[] args)
    {
        var controls = Controls(
            new TrackingPreferenceStore(enabled: true),
            environment: null,
            fallback: true,
            activation: true);
        await using ServiceProvider container = Container(controls);
        var resolver = new ThrowingResolver();

        await InvokeAsync(Host(controls, container, resolver), args);

        Assert.Equal(0, resolver.ResolveCalls);
    }

    [Fact]
    public async Task EffectiveDisabledPolicyAccessesNoEnabledComponents()
    {
        var controls = Controls(
            new TrackingPreferenceStore(enabled: false),
            environment: null,
            fallback: true,
            activation: true);
        var resolver = new ThrowingResolver();
        var transport = new ThrowingTransport();
        AgentHostCli host = Host(
            controls,
            services: null,
            resolver,
            transport);

        InvocationResult result =
            await InvokeAsync(host, "auth", "status");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(0, resolver.ResolveCalls);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task PreferenceReadFailureFailsClosedWithoutChangingProduct()
    {
        var controls = Controls(
            new ThrowingPreferenceStore(throwOnRead: true),
            environment: null,
            fallback: true,
            activation: true);
        var resolver = new ThrowingResolver();
        InvocationResult baseline = await InvokeAsync(
            new AgentHostCli(new FakeOperations()),
            "auth",
            "status");
        InvocationResult observed = await InvokeAsync(
            Host(controls, services: null, resolver),
            "auth",
            "status");

        Assert.Equal(baseline, observed);
        Assert.Equal(0, resolver.ResolveCalls);
    }

    [Fact]
    public async Task EffectiveEnabledPolicyUsesRealHostAndSessionFactory()
    {
        var controls = Controls(
            new TrackingPreferenceStore(enabled: true),
            environment: null,
            fallback: false,
            activation: true);
        var resolver = new CapturingResolver();
        var transport = new CapturingTransport();

        InvocationResult result = await InvokeAsync(
            Host(controls, services: null, resolver, transport),
            "auth",
            "status");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(1, resolver.ResolveCalls);
        Assert.Equal(1, transport.CallCount);
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            EventNames(Assert.IsType<string>(transport.Payload)));
    }

    [Fact]
    public async Task ProductArgumentTelemetryDoesNotSuppressCollection()
    {
        var controls = Controls(
            new TrackingPreferenceStore(enabled: true),
            environment: null,
            fallback: false,
            activation: true);
        var resolver = new CapturingResolver();
        var transport = new CapturingTransport();

        InvocationResult result = await InvokeAsync(
            Host(controls, services: null, resolver, transport),
            "env",
            "select",
            "telemetry");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(1, resolver.ResolveCalls);
        Assert.Equal(1, transport.CallCount);
        Assert.All(
            Parse(Assert.IsType<string>(transport.Payload)),
            item => Assert.Equal(
                "AgentHostingCLI.env.select",
                item.GetProperty("data")
                    .GetProperty("operation_Name")
                    .GetString()));
    }

    private static TelemetryControlService Controls(
        ITelemetryPreferenceStore store,
        string? environment,
        bool fallback,
        bool activation) =>
        new(store, () => environment, fallback, activation);

    private static ServiceProvider Container(TelemetryControlService controls)
    {
        var services = new ServiceCollection();
        services.AddSingleton<
            ICommandHandler<TelemetryEnableRequest, TelemetryControlReport>>(
            controls);
        services.AddSingleton<
            ICommandHandler<TelemetryDisableRequest, TelemetryControlReport>>(
            controls);
        services.AddSingleton<
            ICommandHandler<TelemetryStatusRequest, TelemetryControlReport>>(
            controls);
        return services.BuildServiceProvider();
    }

    private static AgentHostCli Host(
        TelemetryControlService controls,
        IServiceProvider? services,
        ITelemetryDestinationResolver resolver,
        ICommandTelemetryTransport? transport = null)
    {
        var operations = new FakeOperations();
        IServiceProvider? commandServices = services is null
            ? null
            : new CompositeHandlerProvider(
                new FacadeHandlerProvider(operations),
                services);
        var factory = new CommandTelemetrySessionFactory(
            controls,
            resolver,
            new FixedClock(),
            typeof(AgentHostCli).Assembly,
            policyTimeout: TelemetryTestBudgets.Policy,
            flushTimeout: TelemetryTestBudgets.Flush,
            transport: transport);
        return new AgentHostCli(
            operations,
            factory,
            commandServices,
            TestFeatureFlags.AllProductSurfaces);
    }

    private static async Task<InvocationResult> InvokeAsync(
        AgentHostCli host,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exitCode =
            await host.InvokeAsync(args, stdout, stderr, color: false);
        return new InvocationResult(
            exitCode,
            stdout.ToString(),
            stderr.ToString());
    }

    private static JsonElement[] Parse(string payload) =>
        payload
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private static string[] EventNames(string payload) =>
        Parse(payload)
            .Select(item =>
                item.GetProperty("data")
                    .GetProperty("event_Name")
                    .GetString()!)
            .ToArray();

    private static TelemetryDestination Destination() =>
        new(
            new Uri("http://127.0.0.1:1/OneCollector/1.0/"),
            string.Join(
                '-',
                new string('0', 32),
                new string('1', 8),
                new string('2', 4),
                new string('3', 4),
                new string('4', 4),
                new string('5', 12),
                new string('6', 4)),
            "powerappsclient",
            "prod",
            "prod",
            TelemetryLocation.Europe);

    private sealed record InvocationResult(
        int ExitCode,
        string Stdout,
        string Stderr);

    private sealed class TrackingPreferenceStore(bool? enabled) :
        ITelemetryPreferenceStore
    {
        private bool? _enabled = enabled;
        private readonly bool _throwOnRead;

        internal TrackingPreferenceStore(
            bool? enabled,
            bool throwOnRead) : this(enabled)
        {
            _throwOnRead = throwOnRead;
        }

        internal int ReadCount { get; private set; }

        public ValueTask<TelemetryRememberedPreference> ReadAsync(
            CancellationToken cancellationToken)
        {
            ReadCount++;
            if (_throwOnRead)
            {
                throw new AgentHostingStateException(
                    "synthetic telemetry preference read failure");
            }

            return ValueTask.FromResult(
                _enabled switch
                {
                    true => TelemetryRememberedPreference.Enabled,
                    false => TelemetryRememberedPreference.Disabled,
                    null => TelemetryRememberedPreference.NotSet,
                });
        }

        public ValueTask WriteAsync(
            bool enabled,
            CancellationToken cancellationToken)
        {
            _enabled = enabled;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingPreferenceStore(
        bool throwOnRead = false,
        bool throwOnWrite = false) : ITelemetryPreferenceStore
    {
        public ValueTask<TelemetryRememberedPreference> ReadAsync(
            CancellationToken cancellationToken) =>
            throwOnRead
                ? ValueTask.FromException<TelemetryRememberedPreference>(
                    new AgentHostingStateException(
                        "The AgentHost telemetry preference could not be read.",
                        "Verify that the AgentHost config directory is accessible, then retry."))
                : ValueTask.FromResult(TelemetryRememberedPreference.NotSet);

        public ValueTask WriteAsync(
            bool enabled,
            CancellationToken cancellationToken) =>
            throwOnWrite
                ? ValueTask.FromException(
                    new AgentHostingStateException(
                        "The AgentHost telemetry preference could not be written.",
                        "Verify that the AgentHost config directory is accessible, then retry."))
                : ValueTask.CompletedTask;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Timestamp;
    }

    private sealed class CapturingResolver : ITelemetryDestinationResolver
    {
        public bool ActivationAvailable => true;

        internal int ResolveCalls { get; private set; }

        public ValueTask<TelemetryDestination?> ResolveAsync(
            TelemetryEnvironmentTarget target,
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return ValueTask.FromResult<TelemetryDestination?>(Destination());
        }
    }

    private sealed class ThrowingResolver : ITelemetryDestinationResolver
    {
        public bool ActivationAvailable => true;

        internal int ResolveCalls { get; private set; }

        public ValueTask<TelemetryDestination?> ResolveAsync(
            TelemetryEnvironmentTarget target,
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            throw new InvalidOperationException(
                "synthetic destination should not be reached");
        }
    }

    private sealed class CapturingTransport : ICommandTelemetryTransport
    {
        internal int CallCount { get; private set; }

        internal string? Payload { get; private set; }

        public ValueTask<HttpStatusCode> SendAsync(
            string payload,
            TelemetryDestination destination,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Payload = payload;
            return ValueTask.FromResult(HttpStatusCode.OK);
        }
    }

    private sealed class ThrowingTransport : ICommandTelemetryTransport
    {
        internal int CallCount { get; private set; }

        public ValueTask<HttpStatusCode> SendAsync(
            string payload,
            TelemetryDestination destination,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException(
                "synthetic transport should not be reached");
        }
    }
}
