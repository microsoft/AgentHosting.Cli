namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>
/// A lazily-populated pool of run-scoped resources, so an expensive create happens once and every
/// later scenario reuses it.
/// </summary>
public sealed class SessionCache<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _entries = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Returns the entry, creating it on first use so any subset of the suite is runnable.</summary>
    public async Task<TValue> GetAsync(TKey key, Func<Task<TValue>> create)
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var created = await create().ConfigureAwait(false);
            _entries[key] = created;
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Forgets an entry a scenario destroyed, so a later scenario creates a fresh one rather than
    /// addressing something that no longer exists.
    /// </summary>
    public void Consume(TKey key)
    {
        _gate.Wait();

        try
        {
            _entries.Remove(key);
        }
        finally
        {
            _gate.Release();
        }
    }
}
