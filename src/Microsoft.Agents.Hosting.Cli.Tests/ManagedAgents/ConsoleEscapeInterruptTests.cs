using Microsoft.Agents.Hosting.Cli.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ConsoleEscapeInterruptTests
{
    [Fact]
    public async Task WaitForInterrupt_KeepsListeningUntilTheWaitIsAbandoned()
    {
        var interrupt = new ConsoleEscapeInterrupt();
        using var cancellation = new CancellationTokenSource();

        var waiting = interrupt.WaitForInterruptAsync(cancellation.Token);

        Assert.False(waiting.IsCompleted);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task WaitForInterrupt_DoesNotFaultWhenNoConsoleIsAttached()
    {
        var interrupt = new ConsoleEscapeInterrupt();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var waiting = interrupt.WaitForInterruptAsync(cancellation.Token);

        var failure = await Record.ExceptionAsync(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.IsAssignableFrom<OperationCanceledException>(failure);
    }
}
