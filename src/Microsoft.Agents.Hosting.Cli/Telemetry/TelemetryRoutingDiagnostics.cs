using System.Diagnostics;
using System.Globalization;
using Microsoft.Agents.Hosting.Cli.Core.Diagnostics;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Telemetry;

internal sealed class TelemetryRoutingDiagnostics : IEnvironmentGeographyDiagnostics
{
    private readonly TextWriter _output;
    private readonly object _gate = new();
    private long? _started;
    private string? _lastStage;
    private bool _completed;

    internal TelemetryRoutingDiagnostics(TextWriter output) =>
        _output = output ?? throw new ArgumentNullException(nameof(output));

    public void Report(EnvironmentGeographyStage stage, int? httpStatusCode = null)
    {
        Write(stage.ToString(), httpStatusCode is >= 100 and <= 599
            ? "httpStatus=" + httpStatusCode.Value.ToString(CultureInfo.InvariantCulture)
            : null);
    }

    internal void Write(string stage, string? detail = null)
    {
        try
        {
            lock (_gate)
            {
                if (_completed) return;
                _lastStage = stage;
                long started = _started ??= Stopwatch.GetTimestamp();
                _output.WriteLine(FormattableString.Invariant(
                    $"[telemetry-routing] elapsedMs={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds} stage={stage}{(detail is null ? "" : " " + detail)}"));
            }
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.TelemetryGeographyDiagnostics);
        }
    }

    internal void WriteDeadline()
    {
        lock (_gate)
        {
            Write("lookup-deadline", "pendingStage=" + (_lastStage ?? "not-started"));
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            _completed = true;
        }
    }
}
