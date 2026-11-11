using System.Diagnostics;

namespace Microsoft.Agents.Hosting.Cli.Core;

/// <summary>An exclusive lock on a file, held across awaits and threads until it is disposed or its process ends.</summary>
public sealed class ExclusiveFileLock : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly FileStream _stream;

    private ExclusiveFileLock(FileStream stream) => _stream = stream;

    /// <summary>Takes the lock on a file, creating the file when it does not exist.</summary>
    /// <param name="path">The lock file, in a directory that exists.</param>
    /// <returns>The lock, or <see langword="null"/> when another holder has it.</returns>
    public static ExclusiveFileLock? TryAcquire(string path)
    {
        try
        {
            return new ExclusiveFileLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException failure) when (failure is not (DirectoryNotFoundException or DriveNotFoundException or PathTooLongException))
        {
            return null;
        }
    }

    /// <summary>Takes the lock on a file, waiting while another holder has it.</summary>
    /// <param name="path">The lock file, in a directory that exists.</param>
    /// <param name="timeout">How long to wait for another holder to release the lock before a <see cref="TimeoutException"/> is thrown.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>The lock.</returns>
    public static async Task<ExclusiveFileLock> AcquireAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();

        while (true)
        {
            if (TryAcquire(path) is { } acquired)
            {
                return acquired;
            }

            if (waited.Elapsed >= timeout)
            {
                throw new TimeoutException($"The lock file {path} was still locked after {timeout.TotalSeconds:0.#} seconds.");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Releases the lock and leaves the file in place.</summary>
    public void Dispose() => _stream.Dispose();
}
