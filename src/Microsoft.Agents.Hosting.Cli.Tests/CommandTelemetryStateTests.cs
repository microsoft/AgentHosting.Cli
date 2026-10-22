using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Rendering;
using Microsoft.Agents.Hosting.Cli.Telemetry;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class CommandTelemetryStateTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 9, 22, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SuppressedSessionIsBenignAndDoesNotAccessDependencies()
    {
        var preferenceStore = new ThrowingPreferenceStore();
        var resolver = new CapturingResolver();
        var clock = new ThrowingClock();
        var transport = new CapturingTransport();
        var controls = new TelemetryControlService(
            preferenceStore,
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);
        var factory = new CommandTelemetrySessionFactory(
            controls,
            resolver,
            clock,
            typeof(AgentHostCli).Assembly,
            transport: transport);
        await using CommandTelemetrySession session =
            await factory.CreateAsync(suppressTelemetry: true, CancellationToken.None);

        session.RecordAttempt(Path());
        session.ObserveResolvedEnvironment(
            new ResolvedEnvironmentTarget("environment-1", "tenant-1"));
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        session.RecordHost(CommandTelemetryHostCategory.Help);
        session.ObserveAuthenticatedTenant(
            new Guid("11111111-1111-1111-1111-111111111111"));
        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Null(session.EnterObservationScope());
        Assert.Equal(0, session.InvalidTransitionCount);
        Assert.Equal(0, session.FailedOperationCount);
        Assert.Equal(
            CommandTelemetryDiagnosticStatus.None,
            session.LastDiagnosticStatus);
        Assert.Equal(0, preferenceStore.ReadCalls);
        Assert.Equal(0, clock.ReadCalls);
        Assert.Equal(0, resolver.ResolveCalls);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task DuplicateAndOverflowTransitionsSaturateWithoutGrowingBatch()
    {
        var resolver = new CapturingResolver();
        var transport = new CapturingTransport();
        var session = CreateSession(resolver, new FixedClock(), transport);
        session.RecordAttempt(Path());
        session.RecordOutcome(CommandTelemetryOutcome.Success);

        for (var index = 0; index < 10; index++)
        {
            session.RecordOutcome(CommandTelemetryOutcome.Failure);
            session.RecordHost(CommandTelemetryHostCategory.Help);
        }

        Assert.Null(transport.Payload);
        Assert.Equal(0, transport.CallCount);
        Assert.Equal(0, resolver.ResolveCalls);
        await session.DisposeAsync();

        Assert.Equal(4, session.InvalidTransitionCount);
        Assert.Equal(
            CommandTelemetryDiagnosticStatus.InvalidTransition,
            session.LastDiagnosticStatus);
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            EventNames(Assert.IsType<string>(transport.Payload)));
        Assert.Equal(1, resolver.ResolveCalls);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task ClockFailureIsBoundedAndLeavesRecordingStateRetryable()
    {
        var resolver = new CapturingResolver();
        var transport = new CapturingTransport();
        var clock = new FailOnceClock();
        var session = CreateSession(resolver, clock, transport);

        session.RecordAttempt(Path());
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        session.RecordAttempt(Path());
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        Assert.Equal(1, session.FailedOperationCount);
        Assert.Equal(1, session.InvalidTransitionCount);
        Assert.Equal(
            CommandTelemetryDiagnosticStatus.InvalidTransition,
            session.LastDiagnosticStatus);
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            EventNames(Assert.IsType<string>(transport.Payload)));
    }

    [Fact]
    public async Task RepeatedClockFailuresSaturateAndNeverReachResolver()
    {
        var resolver = new CapturingResolver();
        var clock = new ThrowingClock();
        var session = CreateSession(
            resolver,
            clock,
            new CapturingTransport());

        for (var index = 0; index < 10; index++)
        {
            session.RecordHost(CommandTelemetryHostCategory.Help);
        }

        await session.DisposeAsync();

        Assert.Equal(4, session.FailedOperationCount);
        Assert.Equal(
            CommandTelemetryDiagnosticStatus.ClockFailure,
            session.LastDiagnosticStatus);
        Assert.Equal(10, clock.ReadCalls);
        Assert.Equal(0, resolver.ResolveCalls);
    }

    [Fact]
    public async Task MalformedOutcomeCannotPoisonBufferedAttempt()
    {
        var transport = new CapturingTransport();
        var session = CreateSession(
            new CapturingResolver(),
            new FixedClock(),
            transport);
        session.RecordAttempt(Path());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.RecordOutcome((CommandTelemetryOutcome)int.MaxValue));

        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        Assert.Equal(0, session.InvalidTransitionCount);
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            EventNames(Assert.IsType<string>(transport.Payload)));
    }

    [Fact]
    public void ControlledShapesRejectMalformedEnumsPathsTargetsAndProvenance()
    {
        Assert.Equal(
            [
                "ErrorCode",
                "HostCategory",
                "Kind",
                "Outcome",
                "Path",
                "PrincipalObjectId",
                "TenantId",
                "Timestamp",
            ],
            typeof(CommandTelemetryRecord)
                .GetProperties()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CommandTelemetryRecord.Host(
                (CommandTelemetryHostCategory)int.MaxValue,
                Timestamp,
                tenantId: null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CommandTelemetryRecord.OutcomeRecord(
                Path(),
                (CommandTelemetryOutcome)int.MaxValue,
                Timestamp,
                tenantId: null));
        foreach (string[] invalidPath in new[]
        {
            new[] { "auth", "status --tenant synthetic-secret" },
            new[] { "auth", "--status" },
            new[] { "identity", "Create" },
            new[] { "agent", "package/upload" },
        })
        {
            Assert.Throws<ArgumentException>(() =>
                CommandTelemetryPath.FromMetadata(invalidPath));
        }
        Assert.Throws<ArgumentException>(() =>
            TelemetryEnvironmentTarget.Resolved(
                [new ResolvedEnvironmentTarget(" ", "tenant-1")]));
        Assert.Throws<ArgumentNullException>(() =>
            new CommandTelemetrySession(
                null!,
                new FixedClock(),
                "1.2.3-test"));
        Assert.Throws<ArgumentNullException>(() =>
            new CommandTelemetrySession(
                new CapturingResolver(),
                null!,
                "1.2.3-test"));
        Assert.Throws<ArgumentException>(() =>
            new CommandTelemetrySession(
                new CapturingResolver(),
                new FixedClock(),
                " "));
        Assert.Throws<ArgumentException>(() =>
            new CommandTelemetryEvent(
                CommandTelemetryRecord.Attempt(Path(), Timestamp, tenantId: null),
                Destination(),
                "1.2.3-test",
                Guid.Empty.ToString("D"),
                sequenceNumber: 1));
    }

    [Fact]
    public async Task EmptyAndRepeatedDisposalResolveAndSendAtMostOnce()
    {
        var emptyResolver = new CapturingResolver();
        var emptyTransport = new CapturingTransport();
        var empty = CreateSession(
            emptyResolver,
            new FixedClock(),
            emptyTransport);

        await empty.DisposeAsync();
        await empty.DisposeAsync();

        Assert.Equal(0, emptyResolver.ResolveCalls);
        Assert.Equal(0, emptyTransport.CallCount);

        var resolver = new CapturingResolver();
        var transport = new CapturingTransport();
        var recorded = CreateSession(resolver, new FixedClock(), transport);
        recorded.RecordHost(CommandTelemetryHostCategory.Help);

        await recorded.DisposeAsync();
        await recorded.DisposeAsync();

        Assert.Equal(1, resolver.ResolveCalls);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task CompletionPreventsTenantTargetAndOutcomeMutation()
    {
        var resolver = new CapturingResolver();
        var transport = new CapturingTransport();
        var session = CreateSession(resolver, new FixedClock(), transport);
        session.RecordAttempt(Path());

        await session.DisposeAsync();
        session.ObserveAuthenticatedTenant(
            new Guid("11111111-1111-1111-1111-111111111111"));
        session.ObserveResolvedEnvironment(
            new ResolvedEnvironmentTarget("environment-2", "tenant-2"));
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        Assert.Equal(TelemetryEnvironmentTarget.Unknown, resolver.LastTarget);
        Assert.Equal(1, transport.CallCount);
        string payload = Assert.IsType<string>(transport.Payload);
        Assert.Equal(["CommandAttempt"], EventNames(payload));
        Assert.DoesNotContain("tenantId", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessorRestoresNestedAndOutOfOrderScopes()
    {
        var accessor = new CommandTelemetrySessionAccessor();
        await using var first = CreateSession();
        await using var second = CreateSession();

        IDisposable firstScope = accessor.Push(first);
        Assert.Same(first, accessor.Current);
        IDisposable secondScope = accessor.Push(second);
        Assert.Same(second, accessor.Current);

        firstScope.Dispose();
        Assert.Same(second, accessor.Current);
        secondScope.Dispose();
        Assert.NotSame(first, accessor.Current);
        Assert.NotSame(second, accessor.Current);

        firstScope = accessor.Push(first);
        secondScope = accessor.Push(second);
        secondScope.Dispose();
        Assert.Same(first, accessor.Current);
        firstScope.Dispose();
        Assert.NotSame(first, accessor.Current);
    }

    [Fact]
    public async Task AccessorIsolatesOverlappingExecutionContexts()
    {
        var accessor = new CommandTelemetrySessionAccessor();
        await using var first = CreateSession();
        await using var second = CreateSession();
        var bothReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int readyCount = 0;

        async Task ObserveAsync(CommandTelemetrySession expected)
        {
            using IDisposable scope = accessor.Push(expected);
            if (Interlocked.Increment(ref readyCount) == 2)
            {
                bothReady.TrySetResult();
            }

            await bothReady.Task;
            Assert.Same(expected, accessor.Current);
            await release.Task;
            Assert.Same(expected, accessor.Current);
        }

        Task firstTask = Task.Run(() => ObserveAsync(first));
        Task secondTask = Task.Run(() => ObserveAsync(second));
        await bothReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.TrySetResult();
        await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotSame(first, accessor.Current);
        Assert.NotSame(second, accessor.Current);
    }

    [Fact]
    public async Task StartupAndFlushTimeoutsAcceptAtMostOneMinute()
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(250),
            CommandTelemetrySessionFactory.DefaultPolicyTimeout);
        foreach (TimeSpan invalid in new[]
                 {
                     TimeSpan.Zero,
                     TimeSpan.FromTicks(-1),
                     TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1),
                     TimeSpan.MaxValue,
                 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateSession(flushTimeout: invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CommandTelemetrySessionFactory(
                    new TelemetryControlService(
                        new ThrowingPreferenceStore(),
                        () => null,
                        compositionFallbackEnabled: true,
                        collectionActivationAvailable: true),
                    new CapturingResolver(),
                    new FixedClock(),
                    typeof(AgentHostCli).Assembly,
                    policyTimeout: invalid));
        }

        var controls = new TelemetryControlService(
            new ThrowingPreferenceStore(),
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CommandTelemetrySessionFactory(
                controls,
                new CapturingResolver(),
                new FixedClock(),
                typeof(AgentHostCli).Assembly,
                flushTimeout: TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1)));
        _ = new CommandTelemetrySessionFactory(
            controls,
            new CapturingResolver(),
            new FixedClock(),
            typeof(AgentHostCli).Assembly,
            policyTimeout: TimeSpan.FromMinutes(1));

        await using CommandTelemetrySession maximum =
            CreateSession(flushTimeout: TimeSpan.FromMinutes(1));
        await maximum.DisposeAsync();
    }

    [Fact]
    public async Task StartedNoncooperativeTransportGetsNoRetryAfterDeadline()
    {
        using var transport = new BlockingTransport();
        var timeProvider = new ManualDeadlineTimeProvider();
        var session = CreateSession(
            new CapturingResolver(),
            new FixedClock(),
            transport,
            TimeSpan.FromMilliseconds(50),
            timeProvider);
        session.RecordHost(CommandTelemetryHostCategory.Help);

        Task disposal = session.DisposeAsync().AsTask();
        try
        {
            await transport.Started.WaitAsync(TelemetryTestBudgets.Flush);
            Assert.False(disposal.IsCompleted);
            timeProvider.Expire();
            await disposal.WaitAsync(TelemetryTestBudgets.Flush);
            Assert.True(transport.Token.IsCancellationRequested);
            Assert.Equal(1, transport.CallCount);
        }
        finally
        {
            transport.Release();
        }

        await transport.Returned.WaitAsync(TelemetryTestBudgets.Flush);
        await session.DisposeAsync();
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public void PublicEmbeddingConstructorsRetainCurrentMainSignatures()
    {
        ConstructorInfo host = Assert.Single(
            typeof(AgentHostCli).GetConstructors(
                BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(
            [
                typeof(IAgentHostingOperations),
                typeof(IServiceProvider),
                typeof(IFeatureFlags),
            ],
            host.GetParameters().Select(parameter => parameter.ParameterType));

        ConstructorInfo[] builders =
            typeof(CommandTreeBuilder).GetConstructors(
                BindingFlags.Public | BindingFlags.Instance);
        Assert.Collection(
            builders.OrderBy(constructor => constructor.GetParameters().Length),
            builder => Assert.Equal(
                [
                    typeof(CliOutputRenderer),
                    typeof(IServiceProvider),
                    typeof(IFeatureFlags),
                    typeof(System.CommandLine.Option<bool>),
                    typeof(System.CommandLine.Option<bool>),
                ],
                builder.GetParameters().Select(parameter => parameter.ParameterType)),
            builder => Assert.Equal(
                [
                    typeof(CliOutputRenderer),
                    typeof(IServiceProvider),
                    typeof(IFeatureFlags),
                    typeof(System.CommandLine.Option<bool>),
                    typeof(System.CommandLine.Option<bool>),
                    typeof(System.CommandLine.Option<bool>),
                ],
                builder.GetParameters().Select(parameter => parameter.ParameterType)));
        Assert.False(typeof(CommandTelemetrySessionFactory).IsPublic);
        Assert.False(typeof(CommandTelemetrySessionAccessor).IsPublic);
        Assert.Same(
            typeof(AgentHostCli).Assembly,
            typeof(CommandTelemetrySession).Assembly);
    }

    private static CommandTelemetryPath Path() =>
        CommandTelemetryPath.FromMetadata(["auth", "status"]);

    private static CommandTelemetrySession CreateSession(
        CapturingResolver? resolver = null,
        IClock? clock = null,
        ICommandTelemetryTransport? transport = null,
        TimeSpan? flushTimeout = null,
        TimeProvider? timeProvider = null) =>
        new(
            resolver ?? new CapturingResolver(),
            clock ?? new FixedClock(),
            "1.2.3-test",
            flushTimeout: flushTimeout ?? TelemetryTestBudgets.Flush,
            transport: transport ?? new CapturingTransport(),
            timeProvider: timeProvider);

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

    private static string[] EventNames(string payload) =>
        payload
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                using JsonDocument document = JsonDocument.Parse(line);
                return document.RootElement
                    .GetProperty("data")
                    .GetProperty("event_Name")
                    .GetString()!;
            })
            .ToArray();

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Timestamp;
    }

    private sealed class ThrowingClock : IClock
    {
        internal int ReadCalls { get; private set; }

        public DateTimeOffset UtcNow
        {
            get
            {
                ReadCalls++;
                throw new InvalidOperationException("synthetic clock failure");
            }
        }
    }

    private sealed class FailOnceClock : IClock
    {
        private int _reads;

        public DateTimeOffset UtcNow =>
            Interlocked.Increment(ref _reads) == 1
                ? throw new InvalidOperationException("synthetic clock failure")
                : Timestamp;
    }

    private sealed class ThrowingPreferenceStore : ITelemetryPreferenceStore
    {
        internal int ReadCalls { get; private set; }

        public ValueTask<TelemetryRememberedPreference> ReadAsync(
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            throw new InvalidOperationException("synthetic preference failure");
        }

        public ValueTask WriteAsync(
            bool enabled,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic preference failure");
    }

    private sealed class CapturingResolver : ITelemetryDestinationResolver
    {
        public bool ActivationAvailable => true;

        internal int ResolveCalls { get; private set; }

        internal TelemetryEnvironmentTarget? LastTarget { get; private set; }

        public ValueTask<TelemetryDestination?> ResolveAsync(
            TelemetryEnvironmentTarget target,
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            LastTarget = target;
            return ValueTask.FromResult<TelemetryDestination?>(Destination());
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

    private sealed class ManualDeadlineTimeProvider : TimeProvider
    {
        private ManualDeadlineTimer? _timer;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(50), dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            _timer = new ManualDeadlineTimer(callback, state);
            return _timer;
        }

        internal void Expire() =>
            Assert.IsType<ManualDeadlineTimer>(_timer).Expire();

        private sealed class ManualDeadlineTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;

            internal void Expire()
            {
                Assert.False(_disposed);
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                throw new NotSupportedException();

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class BlockingTransport :
        ICommandTelemetryTransport,
        IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _returned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int CallCount { get; private set; }

        internal Task Started => _started.Task;

        internal Task Returned => _returned.Task;

        internal CancellationToken Token { get; private set; }

        public ValueTask<HttpStatusCode> SendAsync(
            string payload,
            TelemetryDestination destination,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Token = cancellationToken;
            _started.TrySetResult();
            _release.Wait(CancellationToken.None);
            _returned.TrySetResult();
            return ValueTask.FromResult(HttpStatusCode.OK);
        }

        internal void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }
}
