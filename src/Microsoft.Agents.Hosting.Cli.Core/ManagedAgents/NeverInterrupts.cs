using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>The interrupt used when a flow has no way to hear from the operator.</summary>
internal sealed class NeverInterrupts : IOperatorInterrupt
{
    public static readonly NeverInterrupts Instance = new();

    public Task WaitForInterruptAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
}
