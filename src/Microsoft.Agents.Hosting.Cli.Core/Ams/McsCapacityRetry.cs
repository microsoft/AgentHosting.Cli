using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Sends an MCS write, retrying a bounded number of times after the service reports no free worker.</summary>
public static class McsCapacityRetry
{
    /// <summary>The start attempts repeated after the service reports no free worker.</summary>
    public const int MaximumCapacityRetries = 3;

    /// <summary>The wait before a capacity retry when the service names none.</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(10);

    /// <summary>The longest wait before a capacity retry, whatever the service asks for.</summary>
    public static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>Sends the start request, retrying it with its idempotency key after the capped Retry-After of each capacity rejection, and reports a cancellation while it waits for the answer as an operation that may still be running.</summary>
    /// <typeparam name="T">What the start request returns.</typeparam>
    /// <param name="start">Sends the start request once.</param>
    /// <param name="delay">Waits before a retry.</param>
    /// <param name="operation">The operation the request starts, as a cancellation names it.</param>
    /// <param name="showCommand">The command that shows the operation, which a cancellation names.</param>
    /// <param name="cancellationToken">Cancels the start and the waits.</param>
    public static async Task<T> StartAsync<T>(Func<CancellationToken, Task<T>> start, Func<TimeSpan, CancellationToken, Task> delay, string operation, string showCommand, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(delay);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await start(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationStartInterruptedException(operation, showCommand, exception);
            }
            catch (AmsAgentException exception) when (exception.ErrorCode == ErrorCodes.AmsAgent.OperationCapacityExceeded && attempt < MaximumCapacityRetries)
            {
                await delay(RetryDelay(exception.RetryAfter), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan RetryDelay(TimeSpan? retryAfter) =>
        retryAfter is not { } requested || requested <= TimeSpan.Zero ? DefaultRetryAfter : requested > MaximumRetryAfter ? MaximumRetryAfter : requested;
}