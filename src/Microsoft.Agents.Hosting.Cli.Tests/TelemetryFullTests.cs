using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Diagnostics;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;
using Microsoft.PowerPlatform.Tooling.OneDS;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class TelemetryFullTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 9, 21, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NormalAuthenticationEnrichesInvocationWithTenantOnly()
    {
        Guid tenantId = new("11111111-1111-1111-1111-111111111111");
        var observer = new CommandTelemetryTenantObserver();
        var operations = new FakeOperations
        {
            OnStatus = () =>
            {
                AuthAccountMapper.FromResult(
                    Result(tenantId),
                    observer);
                return new AuthStatusReport { Status = "signed-in", SignedIn = true };
            },
        };

        JsonElement[] events = await InvokeAsync(
            ["auth", "status"],
            operations,
            observer);

        Assert.Equal(2, events.Length);
        Assert.All(events, item =>
        {
            Assert.Equal(
                tenantId.ToString("D"),
                Dimensions(item).GetProperty("tenantId").GetString());
            AssertDeferredIdentityFieldsAbsent(Data(item));
        });
        Assert.DoesNotContain(
            "22222222-2222-2222-2222-222222222222",
            string.Join('\n', events.Select(item => item.GetRawText())),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConflictingTenantsClearAttributionForTheInvocation()
    {
        var observer = new CommandTelemetryTenantObserver();
        var operations = new FakeOperations
        {
            OnStatus = () =>
            {
                AuthAccountMapper.FromResult(
                    Result(new Guid("11111111-1111-1111-1111-111111111111")),
                    observer);
                AuthAccountMapper.FromResult(
                    Result(new Guid("22222222-2222-2222-2222-222222222222")),
                    observer);
                return new AuthStatusReport { Status = "signed-in", SignedIn = true };
            },
        };

        JsonElement[] events = await InvokeAsync(
            ["auth", "status"],
            operations,
            observer);

        Assert.All(events, item =>
            Assert.False(Dimensions(item).TryGetProperty("tenantId", out _)));
    }

    [Fact]
    public async Task UnknownTenantDoesNotPreventUsageCollection()
    {
        var observer = new CommandTelemetryTenantObserver();
        var operations = new FakeOperations
        {
            OnStatus = () =>
            {
                AuthAccountMapper.FromResult(Result("not-a-guid"), observer);
                return new AuthStatusReport { Status = "signed-in", SignedIn = true };
            },
        };

        JsonElement[] events = await InvokeAsync(
            ["auth", "status"],
            operations,
            observer);

        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            events.Select(item => Data(item).GetProperty("event_Name").GetString()));
        Assert.All(events, item =>
            Assert.False(Dimensions(item).TryGetProperty("tenantId", out _)));
    }

    [Fact]
    public async Task TenantContextDoesNotCrossSequentialInvocations()
    {
        Guid tenantId = new("11111111-1111-1111-1111-111111111111");
        var observer = new CommandTelemetryTenantObserver();
        var withTenant = new FakeOperations
        {
            OnStatus = () =>
            {
                AuthAccountMapper.FromResult(Result(tenantId), observer);
                return new AuthStatusReport { Status = "signed-in", SignedIn = true };
            },
        };
        var withoutTenant = new FakeOperations
        {
            OnStatus = () =>
                new AuthStatusReport { Status = "signed-out", SignedIn = false },
        };

        JsonElement[] first = await InvokeAsync(
            ["auth", "status"],
            withTenant,
            observer);
        JsonElement[] second = await InvokeAsync(
            ["auth", "status"],
            withoutTenant,
            observer);

        Assert.All(first, item =>
            Assert.Equal(
                tenantId.ToString("D"),
                Dimensions(item).GetProperty("tenantId").GetString()));
        Assert.All(second, item =>
            Assert.False(Dimensions(item).TryGetProperty("tenantId", out _)));
    }

    [Fact]
    public async Task SharedHostSequentialAndOverlappingAuthenticationKeepsInvocationsIndependent()
    {
        Guid sequentialStatusTenant =
            new("11111111-1111-1111-1111-111111111111");
        Guid sequentialSwitchTenant =
            new("22222222-2222-2222-2222-222222222222");
        Guid overlappingStatusTenant =
            new("33333333-3333-3333-3333-333333333333");
        Guid overlappingSwitchTenant =
            new("44444444-4444-4444-4444-444444444444");
        var overlappingStatusStarted =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOverlappingStatus =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new CommandTelemetryTenantObserver();
        var transport = new BatchCapturingTransport();
        var resolver = new FixedDestinationResolver(
            Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/")));
        var factory = new CommandTelemetrySessionFactory(
            EnabledControls(),
            resolver,
            new FixedClock(),
            typeof(AgentHostCli).Assembly,
            observer,
            policyTimeout: TelemetryTestBudgets.Policy,
            flushTimeout: TelemetryTestBudgets.Flush,
            transport: transport);
        var statusCalls = 0;
        var switchCalls = 0;
        var operations = new FakeOperations
        {
            OnStatusAsync = async _ =>
            {
                int call = Interlocked.Increment(ref statusCalls);
                Guid tenantId = call == 1
                    ? sequentialStatusTenant
                    : overlappingStatusTenant;
                if (call == 2)
                {
                    overlappingStatusStarted.TrySetResult();
                    await releaseOverlappingStatus.Task;
                }

                AuthAccountMapper.FromResult(Result(tenantId), observer);
                return new AuthStatusReport
                {
                    Status = $"status-{call}",
                    SignedIn = true,
                    Account = $"status-{call}@example.invalid",
                    TenantId = tenantId.ToString("D"),
                };
            },
            OnSwitch = account =>
            {
                int call = Interlocked.Increment(ref switchCalls);
                Guid tenantId = call == 1
                    ? sequentialSwitchTenant
                    : overlappingSwitchTenant;
                AuthAccountMapper.FromResult(Result(tenantId), observer);
                if (call == 2)
                {
                    releaseOverlappingStatus.TrySetResult();
                }

                return new AuthProfileSummary
                {
                    Status = $"switch-{call}",
                    Account = account,
                };
            },
        };
        var host = new AgentHostCli(operations, telemetryFactory: factory);

        var sequentialStatus = await InvokeHostAsync(
            host,
            ["auth", "status", "--json"]);
        var sequentialSwitch = await InvokeHostAsync(
            host,
            ["auth", "switch", "--account", "sequential", "--json"]);
        Task<(int ExitCode, string Stdout, string Stderr)> overlappingStatus =
            InvokeHostAsync(host, ["auth", "status", "--json"]);
        await overlappingStatusStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<(int ExitCode, string Stdout, string Stderr)> overlappingSwitch =
            InvokeHostAsync(
                host,
                ["auth", "switch", "--account", "overlapping", "--json"]);
        await Task.WhenAll(overlappingStatus, overlappingSwitch)
            .WaitAsync(TimeSpan.FromSeconds(2));

        AssertJsonResult(
            sequentialStatus,
            "status-1",
            "status-1@example.invalid");
        AssertJsonResult(sequentialSwitch, "switch-1", "sequential");
        AssertJsonResult(
            await overlappingStatus,
            "status-2",
            "status-2@example.invalid");
        AssertJsonResult(
            await overlappingSwitch,
            "switch-2",
            "overlapping");

        string[] payloads = transport.Payloads;
        Assert.Equal(4, payloads.Length);
        var operationByTenant = new Dictionary<string, string>();
        var sessionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string payload in payloads)
        {
            JsonElement[] events = Parse(payload);
            Assert.Equal(
                ["CommandAttempt", "CommandOutcome"],
                events.Select(item => Data(item).GetProperty("event_Name").GetString()));
            string operationName =
                Data(events[0]).GetProperty("operation_Name").GetString()!;
            string tenantId =
                Dimensions(events[0]).GetProperty("tenantId").GetString()!;
            string sessionId =
                Data(events[0]).GetProperty("session_Id").GetString()!;

            Assert.All(events, item =>
            {
                Assert.Equal(
                    operationName,
                    Data(item).GetProperty("operation_Name").GetString());
                Assert.Equal(
                    tenantId,
                    Dimensions(item).GetProperty("tenantId").GetString());
                Assert.Equal(
                    sessionId,
                    Data(item).GetProperty("session_Id").GetString());
                if (Data(item).GetProperty("event_Name").GetString()
                    == "CommandOutcome")
                {
                    Assert.Equal(
                        "success",
                        Dimensions(item)
                            .GetProperty("outcomeCategory")
                            .GetString());
                }
                else
                {
                    Assert.False(
                        Dimensions(item)
                            .TryGetProperty("outcomeCategory", out _));
                }
            });
            Assert.True(operationByTenant.TryAdd(tenantId, operationName));
            Assert.True(sessionIds.Add(sessionId));
        }

        Assert.Equal(
            "AgentHostingCLI.auth.status",
            operationByTenant[sequentialStatusTenant.ToString("D")]);
        Assert.Equal(
            "AgentHostingCLI.auth.switch",
            operationByTenant[sequentialSwitchTenant.ToString("D")]);
        Assert.Equal(
            "AgentHostingCLI.auth.status",
            operationByTenant[overlappingStatusTenant.ToString("D")]);
        Assert.Equal(
            "AgentHostingCLI.auth.switch",
            operationByTenant[overlappingSwitchTenant.ToString("D")]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedDisabledOrSuppressedHostMasksAndRestoresTenantObservation(
        bool suppressInnerCommand)
    {
        Guid outerTenant =
            new("11111111-1111-1111-1111-111111111111");
        Guid innerTenant =
            new("22222222-2222-2222-2222-222222222222");
        var observer = new CommandTelemetryTenantObserver();
        var outerTransport = new BatchCapturingTransport();
        var outerResolver = new FixedDestinationResolver(
            Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/")));
        var outerFactory = new CommandTelemetrySessionFactory(
            EnabledControls(),
            outerResolver,
            new FixedClock(),
            typeof(AgentHostCli).Assembly,
            observer,
            policyTimeout: TelemetryTestBudgets.Policy,
            flushTimeout: TelemetryTestBudgets.Flush,
            transport: outerTransport);
        var innerOperations = new FakeOperations
        {
            OnStatus = () =>
            {
                Assert.False(observer.IsObservationEnabled);
                AuthAccountMapper.FromResult(Result(innerTenant), observer);
                return new AuthStatusReport
                {
                    Status = "inner",
                    SignedIn = true,
                };
            },
        };
        var services = new ServiceCollection();
        services.AddSingleton<
            ICommandHandler<TelemetryStatusRequest, TelemetryControlReport>>(
                new ObservingTelemetryStatusHandler(observer, innerTenant));
        await using ServiceProvider provider = services.BuildServiceProvider();
        CommandTelemetrySessionFactory innerFactory = suppressInnerCommand
            ? outerFactory
            : new CommandTelemetrySessionFactory(
                DisabledControls(),
                new ThrowingDestinationResolver(),
                new ThrowingClock(),
                typeof(AgentHostCli).Assembly,
                observer,
                transport: new ThrowingTransport());
        var innerHost = new AgentHostCli(
            innerOperations,
            telemetryFactory: innerFactory,
            services: suppressInnerCommand ? provider : null,
            featureFlags: TestFeatureFlags.AllProductSurfaces);
        (int ExitCode, string Stdout, string Stderr) innerResult =
            (0, string.Empty, string.Empty);
        var outerOperations = new FakeOperations
        {
            OnStatusAsync = async _ =>
            {
                innerResult = await InvokeHostAsync(
                    innerHost,
                    suppressInnerCommand
                        ? ["telemetry", "status", "--json"]
                        : ["auth", "status", "--json"]);
                Assert.True(observer.IsObservationEnabled);
                AuthAccountMapper.FromResult(Result(outerTenant), observer);
                return new AuthStatusReport
                {
                    Status = "outer",
                    SignedIn = true,
                    Account = "outer@example.invalid",
                    TenantId = outerTenant.ToString("D"),
                };
            },
        };
        var outerHost = new AgentHostCli(
            outerOperations,
            telemetryFactory: outerFactory);

        var outerResult = await InvokeHostAsync(
            outerHost,
            ["auth", "status", "--json"]);

        Assert.Equal(ExitCodes.Success, innerResult.ExitCode);
        Assert.Empty(innerResult.Stderr);
        Assert.Equal(ExitCodes.Success, outerResult.ExitCode);
        Assert.Empty(outerResult.Stderr);
        Assert.Contains("outer", outerResult.Stdout, StringComparison.Ordinal);
        Assert.Equal(1, outerResolver.ResolveCalls);
        JsonElement[] events = Parse(Assert.Single(outerTransport.Payloads));
        Assert.Equal(2, events.Length);
        Assert.All(events, item => Assert.Equal(
            outerTenant.ToString("D"),
            Dimensions(item).GetProperty("tenantId").GetString()));
        Assert.DoesNotContain(
            innerTenant.ToString("D"),
            string.Join('\n', events.Select(item => item.GetRawText())),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedSameTenantKeepsTwoRecordCardinality()
    {
        await using var server = new LoopbackServer();
        Guid tenantId =
            new("11111111-1111-1111-1111-111111111111");
        var session = new CommandTelemetrySession(
            new FixedDestinationResolver(Destination(server.Address)),
            new FixedClock(),
            "1.2.3-test");

        session.ObserveAuthenticatedTenant(tenantId);
        session.ObserveAuthenticatedTenant(tenantId);
        session.RecordAttempt(
            CommandTelemetryPath.FromMetadata(["auth", "status"]));
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        JsonElement[] events = Parse(await server.RequestBody);
        Assert.Equal(2, events.Length);
        Assert.All(events, item => Assert.Equal(
            tenantId.ToString("D"),
            Dimensions(item).GetProperty("tenantId").GetString()));
    }

    [Fact]
    public async Task ConcurrentConflictingTenantsProduceStickyUnknownBatch()
    {
        await using var server = new LoopbackServer();
        Guid firstTenant =
            new("11111111-1111-1111-1111-111111111111");
        Guid secondTenant =
            new("22222222-2222-2222-2222-222222222222");
        var session = new CommandTelemetrySession(
            new FixedDestinationResolver(Destination(server.Address)),
            new FixedClock(),
            "1.2.3-test");
        session.RecordAttempt(
            CommandTelemetryPath.FromMetadata(["auth", "status"]));

        await Task.WhenAll(
            Task.Run(() => session.ObserveAuthenticatedTenant(firstTenant)),
            Task.Run(() => session.ObserveAuthenticatedTenant(secondTenant)));
        session.ObserveAuthenticatedTenant(firstTenant);
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        JsonElement[] events = Parse(await server.RequestBody);
        Assert.Equal(2, events.Length);
        Assert.All(events, item =>
            Assert.False(Dimensions(item).TryGetProperty("tenantId", out _)));
    }

    [Theory]
    [InlineData("usage", "usage", "usage")]
    [InlineData("not-signed-in", "not-signed-in", "not-signed-in")]
    [InlineData("failure", "failure", "failure")]
    [InlineData("timeout", "timeout", "failure")]
    public async Task LeafFailuresMapToBoundedOutcomeCategories(
        string failureKind,
        string expectedOutcome,
        string expectedExitClass)
    {
        var operations = new FakeOperations
        {
            OnStatus = () => throw failureKind switch
            {
                "usage" => new UsageException("synthetic usage"),
                "not-signed-in" => new NotSignedInException(
                    "synthetic not signed in",
                    remediation: "synthetic remediation"),
                "timeout" => new OperationCanceledException("synthetic timeout"),
                _ => new InvalidOperationException("synthetic failure"),
            },
        };

        JsonElement[] events = await InvokeAsync(
            ["auth", "status"],
            operations);

        JsonElement outcome = Assert.Single(
            events,
            item => Data(item).GetProperty("event_Name").GetString() == "CommandOutcome");
        Assert.Equal(
            expectedOutcome,
            Dimensions(outcome).GetProperty("outcomeCategory").GetString());
        Assert.Equal(
            expectedExitClass,
            Dimensions(outcome).GetProperty("exitClass").GetString());
    }

    [Fact]
    public async Task CallerCancellationIsDistinctAndHasNoExitClass()
    {
        await using var server = new LoopbackServer();
        using var cancellation = new CancellationTokenSource();
        OperationCanceledException? expected = null;
        var operations = new FakeOperations
        {
            OnStatus = () =>
            {
                cancellation.Cancel();
                expected = new OperationCanceledException(cancellation.Token);
                throw expected;
            },
        };
        var factory = Factory(server.Address);

        OperationCanceledException actual =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AgentHostCli(operations, telemetryFactory: factory)
                .InvokeAsync(
                    ["auth", "status"],
                    new StringWriter(),
                    new StringWriter(),
                    color: false,
                    cancellation.Token));

        Assert.Same(expected, actual);
        JsonElement[] events = Parse(await server.RequestBody);
        Assert.Equal(2, events.Length);
        JsonElement outcome = Assert.Single(
            events,
            item => Data(item).GetProperty("event_Name").GetString() == "CommandOutcome");
        JsonElement dimensions = Dimensions(outcome);
        Assert.Equal("cancellation", dimensions.GetProperty("outcomeCategory").GetString());
        Assert.False(dimensions.TryGetProperty("exitClass", out _));
    }

    [Fact]
    public async Task RendererFailureLeavesAttemptWithoutInventingAnOutcome()
    {
        await using var server = new LoopbackServer();
        var factory = Factory(server.Address);

        int exitCode = await new AgentHostCli(
                new FakeOperations(),
                telemetryFactory: factory)
            .InvokeAsync(
                ["auth", "status"],
                new ThrowingTextWriter(),
                new StringWriter(),
                color: false);

        JsonElement[] events = Parse(await server.RequestBody);
        Assert.Equal(1, exitCode);
        Assert.Single(events);
        Assert.Equal(
            "CommandAttempt",
            Data(events[0]).GetProperty("event_Name").GetString());
    }

    [Theory]
    [InlineData(0, "success")]
    [InlineData(1, "failure")]
    public async Task StreamingCommandRecordsOutcomeForReturnedExitCode(int streamingExitCode, string expectedCategory)
    {
        await using var server = new LoopbackServer();
        var services = new StreamingHandlerProvider(streamingExitCode);
        var host = new AgentHostCli(
            new FakeOperations(),
            telemetryFactory: Factory(server.Address),
            services: services,
            featureFlags: new TestFeatureFlags(FeatureName.LiveTestFeatures));

        int exitCode = await host.InvokeAsync(
            ["test", "ask", "--query", "hello", "--agent-harness", "standard"],
            new StringWriter(),
            new StringWriter(),
            color: false);

        JsonElement[] events = Parse(await server.RequestBody);
        Assert.Equal(streamingExitCode, exitCode);
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            events.Select(item => Data(item).GetProperty("event_Name").GetString()));
        JsonElement outcome = events[1];
        Assert.Equal(expectedCategory, Dimensions(outcome).GetProperty("outcomeCategory").GetString());
    }

    [Fact]
    public async Task StreamingExceptionRecordsFailureOutcome()
    {
        await using var server = new LoopbackServer();
        var services = new StreamingHandlerProvider(throwDuringRender: true);
        var host = new AgentHostCli(
            new FakeOperations(),
            telemetryFactory: Factory(server.Address),
            services: services,
            featureFlags: new TestFeatureFlags(FeatureName.LiveTestFeatures));

        int exitCode = await host.InvokeAsync(
            ["test", "ask", "--query", "hello", "--agent-harness", "standard"],
            new StringWriter(),
            new StringWriter(),
            color: false);

        JsonElement[] events = Parse(await server.RequestBody);
        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            events.Select(item => Data(item).GetProperty("event_Name").GetString()));
        Assert.Equal("failure", Dimensions(events[1]).GetProperty("outcomeCategory").GetString());
    }

    [Fact]
    public async Task FactoryHasNoPersistentIdentityDependency()
    {
        var controls = EnabledControls();
        var resolver = new FixedDestinationResolver(
            Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/")));
        var factory = new CommandTelemetrySessionFactory(
            controls,
            resolver,
            new FixedClock(),
            typeof(AgentHostCli).Assembly);

        await using CommandTelemetrySession session =
            await factory.CreateAsync(suppressTelemetry: false, CancellationToken.None);

        Assert.Null(session.EnterObservationScope());
    }

    [Fact]
    public async Task CancellationIgnoringResolverCannotStartTransportAfterDeadline()
    {
        var resolver = new CancellationIgnoringResolver(
            Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/")));
        var transport = new CountingTransport();
        var session = new CommandTelemetrySession(
            resolver,
            new FixedClock(),
            "1.2.3-test",
            flushTimeout: TimeSpan.FromMilliseconds(50),
            transport: transport);
        session.RecordHost(CommandTelemetryHostCategory.Help);

        Task disposal = session.DisposeAsync().AsTask();
        try
        {
            await resolver.Started.WaitAsync(TimeSpan.FromSeconds(2));
            await disposal.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(resolver.Token.IsCancellationRequested);
        }
        finally
        {
            resolver.Release();
        }

        await resolver.Returned.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task ResolutionAndTransportShareOneDisposalDeadline()
    {
        var resolver = new CancellationIgnoringResolver(
            Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/")));
        var transport = new CancellationBlockingTransport();
        var session = new CommandTelemetrySession(
            resolver,
            new FixedClock(),
            "1.2.3-test",
            flushTimeout: TimeSpan.FromMilliseconds(100),
            transport: transport);
        session.RecordHost(CommandTelemetryHostCategory.Help);

        Task disposal = session.DisposeAsync().AsTask();
        await resolver.Started.WaitAsync(TimeSpan.FromSeconds(2));
        resolver.Release();
        await transport.Started.WaitAsync(TimeSpan.FromSeconds(2));
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(resolver.Token, transport.Token);
        Assert.True(transport.Token.IsCancellationRequested);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task SessionCreationFailureDisablesCollectionAndReportsCategory()
    {
        using var listener = new DiagnosticEventListener(
            OptionalMeasurementFailureCategory.TelemetrySessionCreation);
        var controls = new TelemetryControlService(
            new ThrowingPreferenceStore(),
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);
        var factory = new CommandTelemetrySessionFactory(
            controls,
            new FixedDestinationResolver(
                Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/"))),
            new FixedClock(),
            typeof(AgentHostCli).Assembly);

        await using CommandTelemetrySession session =
            await factory.CreateAsync(suppressTelemetry: false, CancellationToken.None);

        Assert.Null(session.EnterObservationScope());
        await AssertCategoryAsync(
            listener,
            OptionalMeasurementFailureCategory.TelemetrySessionCreation);
    }

    [Fact]
    public async Task DestinationFailureIsIsolatedAndReportsCategory()
    {
        using var listener = new DiagnosticEventListener(
            OptionalMeasurementFailureCategory.TelemetryDestinationResolution);
        var transport = new CountingTransport();
        var session = new CommandTelemetrySession(
            new ThrowingDestinationResolver(),
            new FixedClock(),
            "1.2.3-test",
            transport: transport);
        session.RecordHost(CommandTelemetryHostCategory.Help);

        await session.DisposeAsync();

        Assert.Equal(0, transport.CallCount);
        await AssertCategoryAsync(
            listener,
            OptionalMeasurementFailureCategory.TelemetryDestinationResolution);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(500)]
    public async Task RejectedTransportResponseReportsOnlyItsCategory(
        int statusCode)
    {
        using var listener = new DiagnosticEventListener(
            OptionalMeasurementFailureCategory.TelemetryTransportResponse);
        var transport =
            new FixedStatusTransport((HttpStatusCode)statusCode);
        var session = new CommandTelemetrySession(
            new FixedDestinationResolver(
                Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/"))),
            new FixedClock(),
            "1.2.3-test",
            transport: transport);
        session.RecordHost(CommandTelemetryHostCategory.Help);

        await session.DisposeAsync();

        await AssertCategoryAsync(
            listener,
            OptionalMeasurementFailureCategory.TelemetryTransportResponse);
    }

    [Fact]
    public async Task TransportFailureIsIsolatedAndReportsCategory()
    {
        using var listener = new DiagnosticEventListener(
            OptionalMeasurementFailureCategory.TelemetryTransport);
        var session = new CommandTelemetrySession(
            new FixedDestinationResolver(
                Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/"))),
            new FixedClock(),
            "1.2.3-test",
            transport: new ThrowingTransport());
        session.RecordHost(CommandTelemetryHostCategory.Help);

        await session.DisposeAsync();

        await AssertCategoryAsync(
            listener,
            OptionalMeasurementFailureCategory.TelemetryTransport);
    }

    [Fact]
    public void ActualSdkSerializationMatchesSharedProviderForEveryEventShape()
    {
        Guid tenantId = new("11111111-1111-1111-1111-111111111111");
        CommandTelemetryPath path =
            CommandTelemetryPath.FromMetadata(["auth", "status"]);
        CommandTelemetryRecord[] records =
        [
            CommandTelemetryRecord.Host(
                CommandTelemetryHostCategory.Help,
                Timestamp,
                tenantId),
            CommandTelemetryRecord.Attempt(
                path,
                Timestamp,
                tenantId),
            CommandTelemetryRecord.OutcomeRecord(
                path,
                CommandTelemetryOutcome.Success,
                Timestamp,
                tenantId),
        ];

        CommandTelemetryEvent[] events = records.Select((record, index) => new CommandTelemetryEvent(
                record,
                Destination(OneDSCollectorEndpoints.RegionalPublicCloud.EU),
                "1.2.3-test",
                "00000000-0000-0000-0000-000000000001",
                index + 1)).ToArray();
        Assert.All(
            events,
            item => Assert.Equal(UtcFlags.Microsoft_IsPII_Hash, item.Flags));

        JsonElement[] serialized =
            Parse(CommonSchemaV4Serializer.SerializeToJsonStream(events));
        Assert.Equal(
            ["HostCase", "CommandAttempt", "CommandOutcome"],
            serialized.Select(item => Data(item).GetProperty("event_Name").GetString()));
        Assert.Equal(
            [1, 2, 3],
            serialized.Select(item => Data(item).GetProperty("sequenceNumber").GetInt32()));
        Assert.All(serialized, item =>
        {
            Assert.Equal("event", item.GetProperty("name").GetString());
            Assert.Equal(
                0x100000,
                item.GetProperty("ext").GetProperty("utc")
                    .GetProperty("eventFlags").GetInt32());
            Assert.Equal(Timestamp, item.GetProperty("time").GetDateTimeOffset());

            JsonElement data = Data(item);
            JsonElement dimensions = Dimensions(item);
            Assert.Equal(
                [
                    "app_Name",
                    "clientType",
                    "clusterCategory",
                    "customDimensions",
                    "device_Id",
                    "event_Name",
                    "operation_Name",
                    "sequenceNumber",
                    "session_Id",
                    "severity",
                    "telemetryLocation",
                    "timestamp",
                ],
                data.EnumerateObject()
                    .Select(property => property.Name)
                    .Order(StringComparer.Ordinal));
            Assert.Equal(
                data.GetProperty("operation_Name").GetString(),
                dimensions.GetProperty("operationName").GetString());
            Assert.Equal(
                data.GetProperty("telemetryLocation").GetString(),
                dimensions.GetProperty("telemetryLocation").GetString());
            Assert.Equal("false", dimensions.GetProperty("isOnePlayer").GetString());
            Assert.Equal(
                "AgentHosting_CLI_Authoring",
                dimensions.GetProperty("webPlayerScenario").GetString());
            Assert.Equal("prod", dimensions.GetProperty("cloud").GetString());
            Assert.Equal("Unknown", dimensions.GetProperty("station").GetString());
            Assert.Equal("1.2.3-test", dimensions.GetProperty("cliVersion").GetString());
            Assert.Equal(
                tenantId.ToString("D"),
                dimensions.GetProperty("tenantId").GetString());
            AssertDeferredIdentityFieldsAbsent(data);
        });

        Assert.Equal(
            [
                "cliVersion",
                "cloud",
                "exitClass",
                "hostCategory",
                "isOnePlayer",
                "operationName",
                "station",
                "telemetryLocation",
                "tenantId",
                "webPlayerScenario",
            ],
            Dimensions(serialized[0]).EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "cliVersion",
                "cloud",
                "isOnePlayer",
                "operationName",
                "station",
                "telemetryLocation",
                "tenantId",
                "webPlayerScenario",
            ],
            Dimensions(serialized[1]).EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "cliVersion",
                "cloud",
                "exitClass",
                "isOnePlayer",
                "operationName",
                "outcomeCategory",
                "station",
                "telemetryLocation",
                "tenantId",
                "webPlayerScenario",
            ],
            Dimensions(serialized[2]).EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task DirectHostSessionReachesLoopback()
    {
        await using var server = new LoopbackServer();
        var session = new CommandTelemetrySession(
            new FixedDestinationResolver(Destination(server.Address)),
            new FixedClock(),
            "1.2.3-test");
        session.RecordHost(CommandTelemetryHostCategory.Help);

        await session.DisposeAsync();

        Assert.NotEmpty(await server.RequestBody);
    }

    private static async Task<JsonElement[]> InvokeAsync(
        string[] args,
        FakeOperations operations,
        CommandTelemetryTenantObserver? observer = null)
    {
        await using var server = new LoopbackServer();
        var resolver = new FixedDestinationResolver(Destination(server.Address));
        var factory = Factory(server.Address, observer, resolver);

        await new AgentHostCli(operations, telemetryFactory: factory)
            .InvokeAsync(
                args,
                new StringWriter(),
                new StringWriter(),
                color: false);

        Assert.Equal(1, resolver.ResolveCalls);
        return Parse(await server.RequestBody);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)>
        InvokeHostAsync(AgentHostCli host, string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exitCode = await host.InvokeAsync(
            args,
            stdout,
            stderr,
            color: false);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static void AssertJsonResult(
        (int ExitCode, string Stdout, string Stderr) result,
        string expectedStatus,
        string expectedAccount)
    {
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Empty(result.Stderr);
        using JsonDocument output = JsonDocument.Parse(result.Stdout);
        Assert.Equal(
            expectedStatus,
            output.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            expectedAccount,
            output.RootElement.GetProperty("account").GetString());
    }

    private static JsonElement[] Parse(string payload) =>
        payload
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private static CommandTelemetrySessionFactory Factory(
        Uri endpoint,
        CommandTelemetryTenantObserver? observer = null,
        ITelemetryDestinationResolver? resolver = null) =>
        new(
            EnabledControls(),
            resolver ?? new FixedDestinationResolver(Destination(endpoint)),
            new FixedClock(),
            typeof(AgentHostCli).Assembly,
            observer);

    private sealed class StreamingHandlerProvider(int exitCode = 0, bool throwDuringRender = false) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ICommandHandler<LiveTestRequest, ILiveTestStreamingResult>)
                ? new StreamingCommandHandler(exitCode, throwDuringRender)
                : null;
    }

    private sealed class StreamingCommandHandler(int exitCode, bool throwDuringRender)
        : ICommandHandler<LiveTestRequest, ILiveTestStreamingResult>
    {
        public Task<ILiveTestStreamingResult> HandleAsync(
            LiveTestRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<ILiveTestStreamingResult>(
                new StreamingCommandResult(exitCode, throwDuringRender));
    }

    private sealed class StreamingCommandResult(int exitCode, bool throwDuringRender)
        : ILiveTestStreamingResult
    {
        public async Task<int> RenderAsync(
            TextWriter stdout,
            TextWriter stderr,
            bool color,
            CancellationToken cancellationToken)
        {
            await stdout.WriteAsync("streamed");
            if (throwDuringRender)
            {
                throw new InvalidOperationException("synthetic streaming failure");
            }

            return exitCode;
        }
    }

    private static TelemetryControlService EnabledControls() =>
        new(
            new EnabledPreferenceStore(),
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);

    private static TelemetryControlService DisabledControls() =>
        new(
            new DisabledPreferenceStore(),
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);

    private static async Task AssertCategoryAsync(
        DiagnosticEventListener listener,
        OptionalMeasurementFailureCategory expected)
    {
        EventWrittenEventArgs diagnostic = await listener.NextAsync();
        Assert.Equal(
            (int)expected,
            Assert.IsType<int>(Assert.Single(diagnostic.Payload!)));
    }

    private static JsonElement Data(JsonElement item) => item.GetProperty("data");

    private static JsonElement Dimensions(JsonElement item) =>
        JsonDocument.Parse(Data(item).GetProperty("customDimensions").GetString()!)
            .RootElement.Clone();

    private static void AssertDeferredIdentityFieldsAbsent(JsonElement data)
    {
        Assert.False(data.TryGetProperty("identityMode", out _));
        Assert.False(data.TryGetProperty("tenantId", out _));
        Assert.False(data.TryGetProperty("objectId", out _));
        Assert.False(data.TryGetProperty("principalObjectId", out _));
        Assert.False(data.TryGetProperty("pptoolsDeviceId", out _));

        JsonElement dimensions =
            JsonDocument.Parse(data.GetProperty("customDimensions").GetString()!)
                .RootElement;
        Assert.False(dimensions.TryGetProperty("identityMode", out _));
        Assert.False(dimensions.TryGetProperty("objectId", out _));
        Assert.False(dimensions.TryGetProperty("principalObjectId", out _));
        Assert.False(dimensions.TryGetProperty("pptoolsDeviceId", out _));
    }

    private static TelemetryDestination Destination(Uri endpoint) =>
        new(
            endpoint,
            SyntheticApiKey(),
            "powerappsclient",
            "prod",
            "prod",
            TelemetryLocation.Europe);

    private static string SyntheticApiKey() => string.Join(
        '-',
        new string('0', 32),
        new string('1', 8),
        new string('2', 4),
        new string('3', 4),
        new string('4', 4),
        new string('5', 12),
        new string('6', 4));

    private static AuthenticationResult Result(Guid tenantId) =>
        Result(tenantId.ToString("D"));

    private static AuthenticationResult Result(string tenantId) =>
        new(
            accessToken: "synthetic-access-token",
            isExtendedLifeTimeToken: false,
            uniqueId: "22222222-2222-2222-2222-222222222222",
            expiresOn: Timestamp.AddHours(1),
            extendedExpiresOn: Timestamp.AddHours(2),
            tenantId,
            account: new SyntheticAccount(),
            idToken: "synthetic-id-token",
            scopes: ["synthetic.scope"],
            correlationId: Guid.NewGuid(),
            tokenType: "Bearer",
            authenticationResultMetadata: null!,
            claimsPrincipal: new ClaimsPrincipal(),
            spaAuthCode: null,
            additionalResponseParameters: null!);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Timestamp;
    }

    private sealed class EnabledPreferenceStore : ITelemetryPreferenceStore
    {
        public ValueTask<TelemetryRememberedPreference> ReadAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(TelemetryRememberedPreference.Enabled);

        public ValueTask WriteAsync(bool enabled, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class DisabledPreferenceStore : ITelemetryPreferenceStore
    {
        public ValueTask<TelemetryRememberedPreference> ReadAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(TelemetryRememberedPreference.Disabled);

        public ValueTask WriteAsync(bool enabled, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class ObservingTelemetryStatusHandler(
        CommandTelemetryTenantObserver observer,
        Guid tenantId) :
        ICommandHandler<TelemetryStatusRequest, TelemetryControlReport>
    {
        public Task<TelemetryControlReport> HandleAsync(
            TelemetryStatusRequest request,
            CancellationToken cancellationToken)
        {
            Assert.False(observer.IsObservationEnabled);
            AuthAccountMapper.FromResult(Result(tenantId), observer);
            return Task.FromResult(new TelemetryControlReport
            {
                Status = "inner-suppressed",
                RememberedPreference = "enabled",
                EnvironmentOverride = "not-set",
                CompositionFallbackEnabled = true,
                CollectionActivationAvailable = true,
                EffectiveCollectionEnabled = true,
                EffectiveReason = "enabled",
            });
        }
    }

    private sealed class ThrowingClock : IClock
    {
        public DateTimeOffset UtcNow =>
            throw new InvalidOperationException("clock must not be read");
    }

    private sealed class ThrowingPreferenceStore : ITelemetryPreferenceStore
    {
        public ValueTask<TelemetryRememberedPreference> ReadAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromException<TelemetryRememberedPreference>(
                new InvalidOperationException("synthetic preference failure"));

        public ValueTask WriteAsync(bool enabled, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class FixedDestinationResolver(TelemetryDestination destination) :
        ITelemetryDestinationResolver
    {
        public bool ActivationAvailable => true;

        public int ResolveCalls { get; private set; }

        public ValueTask<TelemetryDestination?> ResolveAsync(
            TelemetryEnvironmentTarget target,
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return ValueTask.FromResult<TelemetryDestination?>(destination);
        }
    }

    private sealed class ThrowingDestinationResolver : ITelemetryDestinationResolver
    {
        public bool ActivationAvailable => true;

        public ValueTask<TelemetryDestination?> ResolveAsync(
            TelemetryEnvironmentTarget target,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic destination failure");
    }

    private sealed class CancellationIgnoringResolver(
        TelemetryDestination destination) : ITelemetryDestinationResolver
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _returned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ActivationAvailable => true;

        internal Task Started => _started.Task;

        internal Task Returned => _returned.Task;

        internal CancellationToken Token { get; private set; }

        public async ValueTask<TelemetryDestination?> ResolveAsync(
            TelemetryEnvironmentTarget target,
            CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            _started.TrySetResult();
            await _release.Task;
            _returned.TrySetResult();
            return destination;
        }

        internal void Release() => _release.TrySetResult();
    }

    private sealed class CountingTransport : ICommandTelemetryTransport
    {
        internal int CallCount { get; private set; }

        public ValueTask<HttpStatusCode> SendAsync(
            string payload,
            TelemetryDestination destination,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(HttpStatusCode.OK);
        }
    }

    private sealed class BatchCapturingTransport : ICommandTelemetryTransport
    {
        private readonly ConcurrentQueue<string> _payloads = new();

        internal string[] Payloads => [.. _payloads];

        public ValueTask<HttpStatusCode> SendAsync(
            string payload,
            TelemetryDestination destination,
            CancellationToken cancellationToken)
        {
            _payloads.Enqueue(payload);
            return ValueTask.FromResult(HttpStatusCode.OK);
        }
    }

    private sealed class FixedStatusTransport(HttpStatusCode status) :
        ICommandTelemetryTransport
    {
        public ValueTask<HttpStatusCode> SendAsync(
            string payload,
            TelemetryDestination destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(status);
    }

    private sealed class ThrowingTransport : ICommandTelemetryTransport
    {
        public ValueTask<HttpStatusCode> SendAsync(
            string payload,
            TelemetryDestination destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<HttpStatusCode>(
                new InvalidOperationException("synthetic transport failure"));
    }

    private sealed class CancellationBlockingTransport : ICommandTelemetryTransport
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Started => _started.Task;

        internal int CallCount { get; private set; }

        internal CancellationToken Token { get; private set; }

        public async ValueTask<HttpStatusCode> SendAsync(
            string payload,
            TelemetryDestination destination,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Token = cancellationToken;
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return HttpStatusCode.OK;
        }
    }

    private sealed class SyntheticAccount : IAccount
    {
        public string Username => "synthetic@example.invalid";

        public string Environment => "login.microsoftonline.com";

        public AccountId HomeAccountId { get; } =
            new("home-object.home-tenant", "home-object", "home-tenant");
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) =>
            throw new IOException("synthetic renderer failure");
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation =
            new(TimeSpan.FromSeconds(10));
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
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8
                    .ToArray();
            await stream.WriteAsync(response, _cancellation.Token);
            return Encoding.UTF8.GetString(
                received.Skip(headerEnd).Take(contentLength).ToArray());
        }
    }
}
