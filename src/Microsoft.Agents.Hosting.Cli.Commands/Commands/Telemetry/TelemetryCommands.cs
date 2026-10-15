using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Telemetry;

[Command(
    "enable",
    Description = "Remember a preference to allow telemetry when collection is available.",
    ParentGroup = typeof(TelemetryGroup),
    SortPriority = 10)]
public sealed class TelemetryEnableCommand : CommandBase<TelemetryEnableRequest, TelemetryControlReport>
{
    public override IReadOnlyList<CommandSymbol> Symbols => [];

    protected override TelemetryEnableRequest Bind(ICommandInput input) => new();
}

[Command(
    "disable",
    Description = "Remember a preference that disables telemetry collection.",
    ParentGroup = typeof(TelemetryGroup),
    SortPriority = 20)]
public sealed class TelemetryDisableCommand : CommandBase<TelemetryDisableRequest, TelemetryControlReport>
{
    public override IReadOnlyList<CommandSymbol> Symbols => [];

    protected override TelemetryDisableRequest Bind(ICommandInput input) => new();
}

[Command(
    "status",
    Description = "Show remembered, environment, activation, and effective telemetry state.",
    ParentGroup = typeof(TelemetryGroup),
    SortPriority = 30)]
public sealed class TelemetryStatusCommand : CommandBase<TelemetryStatusRequest, TelemetryControlReport>
{
    public override IReadOnlyList<CommandSymbol> Symbols => [];

    protected override TelemetryStatusRequest Bind(ICommandInput input) => new();
}
