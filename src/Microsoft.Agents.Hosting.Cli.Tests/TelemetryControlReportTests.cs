using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class TelemetryControlReportTests
{
    [Fact]
    public void StatusOmitsDeferredIdentityModesFromTextAndJson()
    {
        var report = new TelemetryControlReport
        {
            Status = "status",
            RememberedPreference = "not-set",
            EnvironmentOverride = "not-set",
            CompositionFallbackEnabled = true,
            CollectionActivationAvailable = true,
            EffectiveCollectionEnabled = true,
            EffectiveReason = "composition-default",
        };

        Assert.DoesNotContain(
            report.ToTextLines(),
            line => line.StartsWith("Identity mode:", StringComparison.Ordinal));

        var stdout = new StringWriter();
        new CliOutputRenderer(stdout, new StringWriter(), color: false)
            .RenderSuccess(report, json: true);
        using JsonDocument document = JsonDocument.Parse(stdout.ToString());
        Assert.False(document.RootElement.TryGetProperty("identityMode", out _));
    }
}
