using System.Diagnostics.Tracing;

namespace Microsoft.Agents.Hosting.Cli.Core.Diagnostics;

/// <summary>Fixed, data-free failure categories for optional local diagnostics.</summary>
public enum OptionalMeasurementFailureCategory
{
    AuthenticationObserverAvailability = 1,
    AuthenticationTenantExtraction = 2,
    AuthenticationObserverCallback = 3,
    TelemetrySessionCreation = 4,
    TelemetryDestinationResolution = 5,
    TelemetrySerialization = 6,
    TelemetryTransport = 7,
    TelemetryTransportResponse = 8,
    TelemetryDeadline = 9,
    TelemetryFlush = 10,
    TelemetryPolicyDeadline = 11,
    TelemetryEnvironmentObservation = 12,
    TelemetryGeographyLookup = 13,
    TelemetryGeographyDeadline = 14,
    TelemetryProfileRead = 15,
    TelemetryProfileDeadline = 16,
}

/// <summary>Opt-in local diagnostics for failures hidden by optional measurement isolation.</summary>
public static class OptionalMeasurementDiagnostics
{
    public const string EventSourceName =
        "Microsoft-AgentHosting-Cli-OptionalMeasurement";

    public static void Report(OptionalMeasurementFailureCategory category)
    {
        try
        {
            DiagnosticEventSource.Log.OptionalMeasurementFailure((int)category);
        }
        catch (Exception)
        {
            // Diagnostics must not alter authentication or command behavior.
        }
    }

    [EventSource(Name = EventSourceName)]
    private sealed class DiagnosticEventSource : EventSource
    {
        internal static readonly DiagnosticEventSource Log = new();

        [Event(1, Level = EventLevel.Warning)]
        public void OptionalMeasurementFailure(int category)
        {
            if (IsEnabled())
            {
                WriteEvent(1, category);
            }
        }
    }
}
