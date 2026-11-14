using System.Security.Cryptography;
using System.Text;

namespace Microsoft.Agents.Hosting.Cli.Core;

internal sealed class NamedMutexLock : IDisposable
{
    private readonly Mutex _mutex;

    private NamedMutexLock(Mutex mutex) => _mutex = mutex;

    public static NamedMutexLock? TryAcquire(string namePrefix, string key, TimeSpan patience)
    {
        var mutex = new Mutex(initiallyOwned: false, $"{namePrefix}{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant())))[..32]}");

        try
        {
            if (mutex.WaitOne(patience))
            {
                return new NamedMutexLock(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            return new NamedMutexLock(mutex);
        }

        mutex.Dispose();

        return null;
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
