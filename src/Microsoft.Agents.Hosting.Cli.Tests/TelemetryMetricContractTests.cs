using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Telemetry;
using Microsoft.PowerPlatform.Tooling.OneDS;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class TelemetryMetricContractTests
{
    private static readonly DateTimeOffset StartUtc =
        new(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndUtc = StartUtc.AddDays(1);
    private static readonly Guid TenantId =
        new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PrincipalObjectId =
        new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void ActualSerializedFixturesSupportDocumentedSeparateCounts()
    {
        CommandTelemetryPath path =
            CommandTelemetryPath.FromMetadata(["auth", "status"]);
        JsonElement[] rows =
        [
            .. Serialize(
                "00000000-0000-0000-0000-000000000001",
                CommandTelemetryRecord.Host(
                    CommandTelemetryHostCategory.Help,
                    StartUtc,
                    tenantId: null)),
            .. Serialize(
                "00000000-0000-0000-0000-000000000002",
                CommandTelemetryRecord.Attempt(
                    path,
                    StartUtc,
                    TenantId,
                    PrincipalObjectId),
                CommandTelemetryRecord.OutcomeRecord(
                    path,
                    CommandTelemetryOutcome.Success,
                    StartUtc.AddMinutes(1),
                    TenantId)),
            .. Serialize(
                "00000000-0000-0000-0000-000000000003",
                CommandTelemetryRecord.Attempt(
                    path,
                    EndUtc.AddTicks(-1),
                    tenantId: null)),
            .. Serialize(
                "00000000-0000-0000-0000-000000000004",
                CommandTelemetryRecord.Attempt(
                    path,
                    EndUtc,
                    TenantId)),
        ];

        JsonElement[] window =
        [
            .. rows.Where(row =>
            {
                DateTimeOffset occurrence = DateTimeOffset.Parse(
                    row.GetProperty("time").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture);
                return occurrence >= StartUtc && occurrence < EndUtc;
            }),
        ];
        JsonElement[] attempts =
        [
            .. window.Where(row =>
                Data(row).GetProperty("event_Name").GetString()
                    == "CommandAttempt"),
        ];
        JsonElement[] outcomes =
        [
            .. window.Where(row =>
                Data(row).GetProperty("event_Name").GetString()
                    == "CommandOutcome"),
        ];
        JsonElement[] hostCases =
        [
            .. window.Where(row =>
                Data(row).GetProperty("event_Name").GetString()
                    == "HostCase"),
        ];
        JsonElement[] successfulOutcomes =
        [
            .. outcomes.Where(row =>
                Dimensions(row).GetProperty("outcomeCategory").GetString()
                    == "success"),
        ];
        string[] knownAttemptSubjects =
        [
            .. attempts
                .Select(Dimensions)
                .Where(data =>
                    data.TryGetProperty(
                        "principalObjectId",
                        out JsonElement principal)
                    && !string.IsNullOrEmpty(principal.GetString()))
                .Select(data =>
                    string.Join(
                        "/",
                        data.GetProperty("tenantId").GetString(),
                        data.GetProperty("principalObjectId").GetString())),
        ];

        Assert.Equal(4, window.Length);
        Assert.Equal(2, attempts.Length);
        Assert.Single(outcomes);
        Assert.Single(hostCases);
        Assert.Single(successfulOutcomes);
        Assert.Single(knownAttemptSubjects);
        Assert.Single(knownAttemptSubjects.Distinct(StringComparer.Ordinal));
        Assert.Equal(1, attempts.Length - knownAttemptSubjects.Length);
    }

    [Fact]
    public void SerializedPlacementMatchesTheDocumentedSharedContract()
    {
        JsonElement attempt = Assert.Single(Serialize(
            "00000000-0000-0000-0000-000000000005",
            CommandTelemetryRecord.Attempt(
                CommandTelemetryPath.FromMetadata(["auth", "status"]),
                StartUtc,
                TenantId,
                PrincipalObjectId)));
        JsonElement data = Data(attempt);
        JsonElement dimensions = Dimensions(attempt);

        Assert.Equal("event", attempt.GetProperty("name").GetString());
        Assert.Equal("CommandAttempt", data.GetProperty("event_Name").GetString());
        Assert.Equal(
            "AgentHostingCLI.auth.status",
            data.GetProperty("operation_Name").GetString());
        Assert.Equal(
            data.GetProperty("operation_Name").GetString(),
            dimensions.GetProperty("operationName").GetString());
        Assert.Equal("eu", data.GetProperty("telemetryLocation").GetString());
        Assert.Equal(
            data.GetProperty("telemetryLocation").GetString(),
            dimensions.GetProperty("telemetryLocation").GetString());
        Assert.Equal(TenantId.ToString("D"), dimensions.GetProperty("tenantId").GetString());
        Assert.Equal(
            TenantId.ToString("D"),
            data.GetProperty("tenantId").GetString());
        Assert.Equal(
            PrincipalObjectId.ToString("D"),
            data.GetProperty("principalObjectId").GetString());
        Assert.Equal(
            data.GetProperty("principalObjectId").GetString(),
            dimensions.GetProperty("principalObjectId").GetString());
        Assert.Equal(
            JsonValueKind.String,
            data.GetProperty("customDimensions").ValueKind);
        Assert.False(data.TryGetProperty("objectId", out _));
        Assert.False(data.TryGetProperty("pptoolsDeviceId", out _));
        Assert.False(data.TryGetProperty("identityMode", out _));
        Assert.False(data.TryGetProperty("schemaVersion", out _));
    }

    [Fact]
    public void UnknownAndNonAttemptRowsOmitPrincipalFromBothLocations()
    {
        CommandTelemetryPath path =
            CommandTelemetryPath.FromMetadata(["auth", "status"]);
        JsonElement[] rows = Serialize(
            "00000000-0000-0000-0000-000000000006",
            CommandTelemetryRecord.Host(
                CommandTelemetryHostCategory.Help,
                StartUtc,
                TenantId),
            CommandTelemetryRecord.Attempt(path, StartUtc, TenantId),
            CommandTelemetryRecord.Attempt(path, StartUtc, tenantId: null),
            CommandTelemetryRecord.OutcomeRecord(
                path,
                CommandTelemetryOutcome.Success,
                StartUtc,
                TenantId));

        Assert.All(rows, row =>
        {
            Assert.False(Data(row).TryGetProperty("principalObjectId", out _));
            Assert.False(Dimensions(row).TryGetProperty("principalObjectId", out _));
        });
    }

    private static JsonElement[] Serialize(
        string sessionId,
        params CommandTelemetryRecord[] records)
    {
        IOneDSTelemetry[] events =
        [
            .. records.Select((record, index) =>
            {
                var item = new CommandTelemetryEvent(
                    record,
                    Destination(),
                    "1.2.3-test",
                    sessionId,
                    index + 1);
                var endpoint = new OneDSEndpointConfiguration(
                    Destination().Endpoint,
                    Destination().ApiKey);
                item.InstrumentationKey = endpoint.InstrumentationKey;
                return (IOneDSTelemetry)item;
            }),
        ];

        return CommonSchemaV4Serializer
            .SerializeToJsonStream(events)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
    }

    private static JsonElement Data(JsonElement row) =>
        row.GetProperty("data");

    private static JsonElement Dimensions(JsonElement row) =>
        JsonDocument.Parse(
            Data(row).GetProperty("customDimensions").GetString()!)
            .RootElement.Clone();

    private static TelemetryDestination Destination() =>
        new(
            OneDSCollectorEndpoints.RegionalPublicCloud.EU,
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
}
