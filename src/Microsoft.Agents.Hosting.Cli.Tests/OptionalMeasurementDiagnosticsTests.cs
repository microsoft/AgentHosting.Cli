using System.Diagnostics.Tracing;
using Microsoft.Agents.Hosting.Cli.Core.Diagnostics;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class OptionalMeasurementDiagnosticsTests
{
    [Fact]
    public async Task OptInEventCarriesOnlyTheFixedCategoryCode()
    {
        using var listener = new DiagnosticEventListener(
            OptionalMeasurementFailureCategory.TelemetrySessionCreation);

        OptionalMeasurementDiagnostics.Report(
            OptionalMeasurementFailureCategory.TelemetrySessionCreation);

        EventWrittenEventArgs diagnostic = await listener.NextAsync();
        Assert.Equal(
            OptionalMeasurementDiagnostics.EventSourceName,
            diagnostic.EventSource.Name);
        Assert.Equal("OptionalMeasurementFailure", diagnostic.EventName);
        Assert.Equal(["category"], diagnostic.PayloadNames);
        Assert.Equal(
            (int)OptionalMeasurementFailureCategory.TelemetrySessionCreation,
            Assert.IsType<int>(Assert.Single(diagnostic.Payload!)));
    }
}

internal sealed class DiagnosticEventListener : EventListener
{
    private readonly int _expectedCategory;
    private readonly TaskCompletionSource<EventWrittenEventArgs> _next =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal DiagnosticEventListener(
        OptionalMeasurementFailureCategory expectedCategory)
    {
        _expectedCategory = (int)expectedCategory;
        foreach (EventSource source in EventSource.GetSources())
        {
            EnableIfTarget(source);
        }
    }

    internal Task<EventWrittenEventArgs> NextAsync() =>
        _next.Task.WaitAsync(TimeSpan.FromSeconds(2));

    protected override void OnEventSourceCreated(EventSource eventSource) =>
        EnableIfTarget(eventSource);

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventSource.Name
                == OptionalMeasurementDiagnostics.EventSourceName
            && eventData.Payload is { Count: 1 }
            && eventData.Payload[0] is int category
            && category == _expectedCategory)
        {
            _next.TrySetResult(eventData);
        }
    }

    private void EnableIfTarget(EventSource eventSource)
    {
        if (eventSource.Name == OptionalMeasurementDiagnostics.EventSourceName)
        {
            EnableEvents(eventSource, EventLevel.Warning);
        }
    }
}
