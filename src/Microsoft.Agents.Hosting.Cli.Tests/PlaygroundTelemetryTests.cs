using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.Agents.Hosting.Cli.Telemetry;
using Microsoft.PowerPlatform.Tooling.OneDS;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class PlaygroundTelemetryTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 10, 13, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId =
        new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PrincipalObjectId =
        new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void DetailsValidateDurationEnumsAndReleaseVersion()
    {
        var details = new PlaygroundTelemetryDetails(
            durationMs: 1250,
            playgroundVersion: "0.2.29",
            installResult: PlaygroundInstallResult.Installed);

        Assert.Equal(1250, details.DurationMs);
        Assert.Equal("0.2.29", details.PlaygroundVersion);
        Assert.Equal(PlaygroundInstallResult.Installed, details.InstallResult);

        var prerelease = new PlaygroundTelemetryDetails(
            durationMs: 1,
            playgroundVersion: "0.2.29-beta.1",
            installResult: PlaygroundInstallResult.NotNeeded);
        Assert.Null(prerelease.PlaygroundVersion);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PlaygroundTelemetryDetails(
                durationMs: -1,
                playgroundVersion: null,
                installResult: PlaygroundInstallResult.NotNeeded));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PlaygroundTelemetryDetails(
                durationMs: 1,
                playgroundVersion: null,
                installResult: (PlaygroundInstallResult)int.MaxValue));
        Assert.Throws<ArgumentException>(() =>
            CommandTelemetryRecord.OutcomeRecord(
                Path("auth", "status"),
                CommandTelemetryOutcome.Success,
                Timestamp,
                tenantId: null,
                playgroundDetails: details));
    }

    [Fact]
    public async Task DisabledSessionIgnoresPlaygroundDetails()
    {
        await using CommandTelemetrySession session =
            CommandTelemetrySession.Disabled();

        session.RecordPlaygroundDetails(Details());
        await session.DisposeAsync();

        Assert.Equal(0, session.InvalidTransitionCount);
        Assert.Equal(0, session.FailedOperationCount);
    }

    [Fact]
    public async Task PlaygroundOutcomeUsesAllowlistedDetailsAndAuthenticatedAttribution()
    {
        var transport = new CapturingTransport();
        await using var session = new CommandTelemetrySession(
            new CapturingResolver(),
            new FixedClock(),
            "1.2.3-test",
            selectedProfile: new("account", TenantId, PrincipalObjectId),
            flushTimeout: TelemetryTestBudgets.Flush,
            transport: transport);

        session.RecordAttempt(PlaygroundPath());
        session.RecordPlaygroundDetails(Details());
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        JsonElement[] events = Parse(Assert.IsType<string>(transport.Payload));
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            events.Select(EventName));
        Assert.All(events, item =>
        {
            JsonElement data = Data(item);
            JsonElement dimensions = Dimensions(item);
            Assert.Equal(TenantId.ToString("D"), data.GetProperty("tenantId").GetString());
            Assert.Equal(PrincipalObjectId.ToString("D"), data.GetProperty("principalObjectId").GetString());
            Assert.Equal(TenantId.ToString("D"), dimensions.GetProperty("tenantId").GetString());
            Assert.Equal(PrincipalObjectId.ToString("D"), dimensions.GetProperty("principalObjectId").GetString());
            Assert.False(dimensions.TryGetProperty("installResult", out _));
            Assert.False(dimensions.TryGetProperty("errorCategory", out _));
            Assert.False(dimensions.TryGetProperty("playgroundErrorCatagory", out _));
        });

        JsonElement attemptDimensions = Dimensions(events[0]);
        Assert.False(attemptDimensions.TryGetProperty("durationMs", out _));
        Assert.False(attemptDimensions.TryGetProperty("playgroundVersion", out _));
        Assert.False(attemptDimensions.TryGetProperty("installResult", out _));
        Assert.False(attemptDimensions.TryGetProperty("errorCategory", out _));

        JsonElement outcome = events[1];
        JsonElement outcomeDimensions = Dimensions(outcome);
        Assert.Equal(
            "1250",
            outcomeDimensions.GetProperty("durationMs").GetString());
        Assert.Equal(
            "0.2.29",
            outcomeDimensions.GetProperty("playgroundVersion").GetString());
        Assert.Equal(
            "installed",
            outcomeDimensions.GetProperty("playgroundInstallResult").GetString());
        Assert.Equal(
            Data(outcome).GetProperty("session_Id").GetString(),
            Data(events[0]).GetProperty("session_Id").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaygroundUsesSharedUnknownAndTenantOnlyAttribution(bool tenantKnown)
    {
        var transport = new CapturingTransport();
        await using var session = new CommandTelemetrySession(
            new CapturingResolver(),
            new FixedClock(),
            "1.2.3-test",
            selectedProfile: tenantKnown ? new("account", TenantId, null) : null,
            flushTimeout: TelemetryTestBudgets.Flush,
            transport: transport);
        session.RecordAttempt(PlaygroundPath());
        session.RecordPlaygroundDetails(Details());
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        JsonElement[] events = Parse(Assert.IsType<string>(transport.Payload));
        Assert.Equal(["CommandAttempt", "CommandOutcome"], events.Select(EventName));
        Assert.All(events, item =>
        {
            JsonElement data = Data(item);
            JsonElement dimensions = Dimensions(item);
            if (tenantKnown)
            {
                Assert.Equal(TenantId.ToString("D"), data.GetProperty("tenantId").GetString());
                Assert.Equal(TenantId.ToString("D"), dimensions.GetProperty("tenantId").GetString());
            }
            else
            {
                Assert.False(data.TryGetProperty("tenantId", out _));
                Assert.False(dimensions.TryGetProperty("tenantId", out _));
            }

            Assert.False(data.TryGetProperty("principalObjectId", out _));
            Assert.False(dimensions.TryGetProperty("principalObjectId", out _));
        });
    }

    [Fact]
    public async Task DetailsArePathSpecificAndDoNotChangeRecordCapacityOrOutcomeState()
    {
        var transport = new CapturingTransport();
        await using var normal = CreateSession(transport);
        normal.RecordPlaygroundDetails(Details());
        normal.RecordAttempt(Path("auth", "status"));
        normal.RecordOutcome(CommandTelemetryOutcome.Success);
        await normal.DisposeAsync();

        Assert.Equal(1, normal.InvalidTransitionCount);
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            EventNames(Assert.IsType<string>(transport.Payload)));
        Assert.DoesNotContain(
            "playgroundVersion",
            Assert.IsType<string>(transport.Payload),
            StringComparison.Ordinal);

        var playgroundTransport = new CapturingTransport();
        await using var playground = CreateSession(playgroundTransport);
        playground.RecordAttempt(PlaygroundPath());
        playground.RecordPlaygroundDetails(Details());
        playground.RecordPlaygroundDetails(Details());
        playground.RecordOutcome(CommandTelemetryOutcome.Success);
        playground.RecordPlaygroundDetails(Details());
        await playground.DisposeAsync();

        Assert.Equal(2, playground.InvalidTransitionCount);
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            EventNames(Assert.IsType<string>(playgroundTransport.Payload)));
    }

    [Fact]
    public void AttributionUpdatesPreservePlaygroundDetailsAndSerializeIdentity()
    {
        CommandTelemetryRecord original = CommandTelemetryRecord.OutcomeRecord(
            PlaygroundPath(),
            CommandTelemetryOutcome.Success,
            Timestamp,
            TenantId,
            Details());
        CommandTelemetryRecord updated =
            original.WithAttribution(TenantId, PrincipalObjectId);

        Assert.Same(original.PlaygroundDetails, updated.PlaygroundDetails);

        var telemetry = new CommandTelemetryEvent(
            updated,
            Destination(),
            "1.2.3-test",
            "00000000-0000-0000-0000-000000000001",
            sequenceNumber: 1);
        JsonElement item = Parse(
            CommonSchemaV4Serializer.SerializeToJsonStream([telemetry]))[0];

        Assert.Equal(TenantId.ToString("D"), Data(item).GetProperty("tenantId").GetString());
        Assert.Equal(PrincipalObjectId.ToString("D"), Data(item).GetProperty("principalObjectId").GetString());
        Assert.Equal(TenantId.ToString("D"), Dimensions(item).GetProperty("tenantId").GetString());
        Assert.Equal(PrincipalObjectId.ToString("D"), Dimensions(item).GetProperty("principalObjectId").GetString());
        Assert.Equal(
            "installed",
            Dimensions(item).GetProperty("playgroundInstallResult").GetString());
    }

    [Theory]
    [InlineData((int)CommandTelemetryOutcome.Failure, "19015")]
    [InlineData((int)CommandTelemetryOutcome.Timeout, "19017")]
    [InlineData((int)CommandTelemetryOutcome.Success, null)]
    [InlineData((int)CommandTelemetryOutcome.Cancellation, null)]
    public async Task PlaygroundUsesSharedErrorCodeWithoutCategory(
        int outcomeValue,
        string? expectedErrorCode)
    {
        var outcome = (CommandTelemetryOutcome)outcomeValue;
        var transport = new CapturingTransport();
        await using var session = CreateSession(transport);
        session.RecordAttempt(PlaygroundPath());
        session.RecordPlaygroundDetails(Details());
        session.RecordOutcome(outcome, outcome == CommandTelemetryOutcome.Timeout ? 19017 : 19015);
        await session.DisposeAsync();

        JsonElement dimensions = Dimensions(Parse(Assert.IsType<string>(transport.Payload))[1]);
        if (expectedErrorCode is null)
        {
            Assert.False(dimensions.TryGetProperty("errorCode", out _));
        }
        else
        {
            Assert.Equal(expectedErrorCode, dimensions.GetProperty("errorCode").GetString());
        }
        Assert.False(dimensions.TryGetProperty("errorCategory", out _));
        Assert.False(dimensions.TryGetProperty("playgroundErrorCatagory", out _));
    }

    [Theory]
    [InlineData((int)CommandTelemetryOutcome.Success)]
    [InlineData((int)CommandTelemetryOutcome.Failure)]
    [InlineData((int)CommandTelemetryOutcome.Usage)]
    [InlineData((int)CommandTelemetryOutcome.NotSignedIn)]
    [InlineData((int)CommandTelemetryOutcome.Timeout)]
    [InlineData((int)CommandTelemetryOutcome.Cancellation)]
    public async Task OrdinaryOutcomesMeasureMonotonicDuration(int outcomeValue)
    {
        var transport = new CapturingTransport();
        var time = new ManualTimeProvider();
        await using var session = new CommandTelemetrySession(
            new CapturingResolver(),
            new FixedClock(),
            "1.2.3-test",
            transport: transport,
            timeProvider: time);

        session.RecordAttempt(Path("auth", "status"));
        time.Advance(TimeSpan.FromMilliseconds(1250));
        session.RecordOutcome((CommandTelemetryOutcome)outcomeValue);
        time.Advance(TimeSpan.FromMinutes(1));
        await session.DisposeAsync();

        JsonElement[] events = Parse(Assert.IsType<string>(transport.Payload));
        Assert.False(Dimensions(events[0]).TryGetProperty("durationMs", out _));
        Assert.Equal("1250", Dimensions(events[1]).GetProperty("durationMs").GetString());
        Assert.False(Dimensions(events[1]).TryGetProperty("playgroundInstallResult", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DurationClockFailureDoesNotLoseOutcome(bool failStart)
    {
        var transport = new CapturingTransport();
        var time = new ThrowingTimestampProvider { Throw = failStart };
        await using var session = new CommandTelemetrySession(
            new CapturingResolver(), new FixedClock(), "1.2.3-test",
            transport: transport, timeProvider: time);
        session.RecordAttempt(Path("auth", "status"));
        time.Throw = !failStart;
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        JsonElement[] events = Parse(Assert.IsType<string>(transport.Payload));
        Assert.Equal(["CommandAttempt", "CommandOutcome"], events.Select(EventName));
        Assert.False(Dimensions(events[1]).TryGetProperty("durationMs", out _));
        Assert.Equal(1, session.FailedOperationCount);
        Assert.Equal(CommandTelemetryDiagnosticStatus.ClockFailure, session.LastDiagnosticStatus);
    }

    private sealed class ThrowingTimestampProvider : TimeProvider
    {
        internal bool Throw { get; set; }

        public override long GetTimestamp() =>
            Throw ? throw new InvalidOperationException("CANARY") : base.GetTimestamp();
    }

    [Fact]
    public async Task DataverseRequestsShareTheActiveCommandEventSession()
    {
        var context = new CommandTelemetrySessionAccessor();
        var transport = new CapturingTransport();
        await using var session = CreateSession(transport);
        using var scope = context.Push(session);
        var ids = new List<string?>();
        var handler = new CapturingPackageHandler(request =>
        {
            ids.Add(request.Headers.TryGetValues("x-ms-client-session-id", out var values) ? Assert.Single(values) : null);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler, context);
        accessor.SetDataverseOrganization(new Uri("https://contoso.crm.dynamics.com/"));
        using var client = accessor.CreateClient();
        Assert.Null(context.SessionId);

        session.RecordAttempt(Path("agent", "deploy"));
        Guid id = Assert.IsType<Guid>(context.SessionId);
        using var first = await client.GetAsync("https://contoso.crm.dynamics.com/api/data/v9.2/bots");
        using var second = await client.GetAsync("https://contoso.crm.dynamics.com/api/data/v9.2/workflows");
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        Assert.All(ids, value => Assert.Equal(id.ToString("D"), value));
        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
            Assert.Equal(id.ToString("D"), Data(item).GetProperty("session_Id").GetString()));
        Assert.Equal(2, handler.Requests.Select(request => request.ClientRequestId).Distinct().Count());
        Assert.Null(context.SessionId);
    }

    [Fact]
    public async Task NestedSuppressedSessionCannotInheritOuterCorrelation()
    {
        var context = new CommandTelemetrySessionAccessor();
        await using var outer = CreateSession(new CapturingTransport());
        using var outerScope = context.Push(outer);
        outer.RecordAttempt(Path("agent", "deploy"));
        Guid outerId = Assert.IsType<Guid>(context.SessionId);

        await using (var disabled = CommandTelemetrySession.Disabled())
        using (context.Push(disabled))
        {
            Assert.Null(context.SessionId);
            await Task.Yield();
            Assert.Null(context.SessionId);
        }

        Assert.Equal(outerId, context.SessionId);
        await outer.DisposeAsync();
        Assert.Null(context.SessionId);
    }

    private static PlaygroundTelemetryDetails Details() =>
        new(
            durationMs: 1250,
            playgroundVersion: "0.2.29",
            installResult: PlaygroundInstallResult.Installed);

    private static CommandTelemetryPath PlaygroundPath() =>
        Path("test", "playground");

    private static CommandTelemetryPath Path(params string[] segments) =>
        CommandTelemetryPath.FromMetadata(segments);

    private static CommandTelemetrySession CreateSession(
        CapturingTransport transport) =>
        new(
            new CapturingResolver(),
            new FixedClock(),
            "1.2.3-test",
            flushTimeout: TelemetryTestBudgets.Flush,
            transport: transport);

    private static JsonElement[] Parse(string payload) =>
        payload
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private static string[] EventNames(string payload) =>
        Parse(payload).Select(EventName).ToArray();

    private static string EventName(JsonElement item) =>
        Data(item).GetProperty("event_Name").GetString()!;

    private static JsonElement Data(JsonElement item) =>
        item.GetProperty("data");

    private static JsonElement Dimensions(JsonElement item) =>
        JsonDocument.Parse(
            Data(item).GetProperty("customDimensions").GetString()!)
            .RootElement.Clone();

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

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Timestamp;
    }

    private sealed class CapturingResolver : ITelemetryDestinationResolver
    {
        public bool ActivationAvailable => true;

        public ValueTask<TelemetryDestination?> ResolveAsync(
            TelemetryEnvironmentTarget target,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<TelemetryDestination?>(Destination());
    }

    private sealed class CapturingTransport : ICommandTelemetryTransport
    {
        internal string? Payload { get; private set; }

        public ValueTask<HttpStatusCode> SendAsync(
            string payload,
            TelemetryDestination destination,
            CancellationToken cancellationToken)
        {
            Payload = payload;
            return ValueTask.FromResult(HttpStatusCode.OK);
        }
    }
}
