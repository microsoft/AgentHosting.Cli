using Microsoft.Agents.Hosting.Cli.Core;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>The live <see cref="Microsoft.Agents.Hosting.Cli.Core.Ports.ISecureTokenCacheProbe"/>, which round-trips a probe item through the platform secret store.</summary>
public sealed class MsalSecureTokenCacheProbe : ISecureTokenCacheProbe
{
    private const int MaximumAttempts = 3;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryWait = TimeSpan.FromMilliseconds(100);

    private readonly Action<StorageCreationProperties> _verifyPersistence;
    private readonly Func<string, CancellationToken, Task<IDisposable>> _acquireLock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates the probe over the platform secret store.</summary>
    public MsalSecureTokenCacheProbe() : this(VerifyPersistence, AcquireLockAsync, Task.Delay)
    {
    }

    internal MsalSecureTokenCacheProbe(Action<StorageCreationProperties> verifyPersistence, Func<string, CancellationToken, Task<IDisposable>> acquireLock, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _verifyPersistence = verifyPersistence;
        _acquireLock = acquireLock;
        _delay = delay;
    }

    /// <inheritdoc />
    public async Task<bool> IsProtectedPersistenceAvailableAsync(AgentHostCacheDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Directory.CreateDirectory(descriptor.CacheDirectory);
        var properties = MsalCacheStorage.Build(descriptor, useUnprotectedFile: false);

        using var validationLock = await _acquireLock($"{descriptor.CacheFilePath}.test.lock", cancellationToken).ConfigureAwait(false);

        for (var attempt = 1; !TryVerifyPersistence(properties); attempt++)
        {
            if (attempt == MaximumAttempts)
            {
                return false;
            }

            await _delay(RetryWait * attempt, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static void VerifyPersistence(StorageCreationProperties properties) => Storage.Create(properties).VerifyPersistence();

    private static async Task<IDisposable> AcquireLockAsync(string path, CancellationToken cancellationToken) => await ExclusiveFileLock.AcquireAsync(path, LockTimeout, cancellationToken).ConfigureAwait(false);

    private bool TryVerifyPersistence(StorageCreationProperties properties)
    {
        try
        {
            _verifyPersistence(properties);

            return true;
        }
        catch (MsalCachePersistenceException)
        {
            return false;
        }
    }
}
