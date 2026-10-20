using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
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
            installResult: PlaygroundInstallResult.Installed,
            errorCategory: PlaygroundErrorCategory.None);

        Assert.Equal(1250, details.DurationMs);
        Assert.Equal("0.2.29", details.PlaygroundVersion);
        Assert.Equal(PlaygroundInstallResult.Installed, details.InstallResult);
        Assert.Equal(PlaygroundErrorCategory.None, details.ErrorCategory);

        var prerelease = new PlaygroundTelemetryDetails(
            durationMs: 1,
            playgroundVersion: "0.2.29-beta.1",
            installResult: PlaygroundInstallResult.NotNeeded,
            errorCategory: PlaygroundErrorCategory.None);
        Assert.Null(prerelease.PlaygroundVersion);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PlaygroundTelemetryDetails(
                durationMs: -1,
                playgroundVersion: null,
                installResult: PlaygroundInstallResult.NotNeeded,
                errorCategory: PlaygroundErrorCategory.None));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PlaygroundTelemetryDetails(
                durationMs: 1,
                playgroundVersion: null,
                installResult: (PlaygroundInstallResult)int.MaxValue,
                errorCategory: PlaygroundErrorCategory.None));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PlaygroundTelemetryDetails(
                durationMs: 1,
                playgroundVersion: null,
                installResult: PlaygroundInstallResult.NotNeeded,
                errorCategory: (PlaygroundErrorCategory)int.MaxValue));
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
    public async Task PlaygroundOutcomeUsesAllowlistedDetailsAndRedactsAttribution()
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
            Assert.False(data.TryGetProperty("tenantId", out _));
            Assert.False(data.TryGetProperty("principalObjectId", out _));
            Assert.False(dimensions.TryGetProperty("tenantId", out _));
            Assert.False(dimensions.TryGetProperty("principalObjectId", out _));
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
            outcomeDimensions.GetProperty("installResult").GetString());
        Assert.Equal(
            "none",
            outcomeDimensions.GetProperty("errorCategory").GetString());
        Assert.Equal(
            Data(outcome).GetProperty("session_Id").GetString(),
            Data(events[0]).GetProperty("session_Id").GetString());
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
            "durationMs",
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
    public void AttributionUpdatesPreservePlaygroundDetailsButNeverSerializeIdentity()
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

        Assert.False(Data(item).TryGetProperty("tenantId", out _));
        Assert.False(Data(item).TryGetProperty("principalObjectId", out _));
        Assert.False(Dimensions(item).TryGetProperty("tenantId", out _));
        Assert.False(Dimensions(item).TryGetProperty("principalObjectId", out _));
        Assert.Equal(
            "installed",
            Dimensions(item).GetProperty("installResult").GetString());
    }

    private static PlaygroundTelemetryDetails Details() =>
        new(
            durationMs: 1250,
            playgroundVersion: "0.2.29",
            installResult: PlaygroundInstallResult.Installed,
            errorCategory: PlaygroundErrorCategory.None);

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
