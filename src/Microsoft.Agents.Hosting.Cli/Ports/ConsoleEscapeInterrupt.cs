using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Ports;

/// <summary>
/// The default <see cref="IOperatorInterrupt"/>: completes when the operator presses Esc at the
/// console. When no console is attached the wait never completes, leaving the caller's own timeout
/// as the only limit.
/// </summary>
public sealed class ConsoleEscapeInterrupt : IOperatorInterrupt
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    /// <inheritdoc />
    public async Task WaitForInterruptAsync(CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            while (KeyAvailable())
            {
                if (ReadKey() is ConsoleKey.Escape)
                {
                    return;
                }
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool KeyAvailable()
    {
        try
        {
            return Console.KeyAvailable;
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static ConsoleKey? ReadKey()
    {
        try
        {
            return Console.ReadKey(intercept: true).Key;
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
