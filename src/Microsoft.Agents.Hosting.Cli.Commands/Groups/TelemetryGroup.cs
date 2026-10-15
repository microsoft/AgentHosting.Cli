using Microsoft.Agents.Hosting.Cli.Commands.Model;

namespace Microsoft.Agents.Hosting.Cli.Commands.Groups;

/// <summary>The <c>telemetry</c> control group.</summary>
[CommandGroup(
    "telemetry",
    Description = "Manage command telemetry collection preferences.",
    SortPriority = 70,
    SuppressTelemetry = true)]
public sealed class TelemetryGroup;
