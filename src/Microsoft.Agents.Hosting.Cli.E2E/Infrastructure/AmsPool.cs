using System.Collections.Concurrent;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public sealed class AmsPool
{
    private readonly ConcurrentDictionary<string, object> _setups = new(StringComparer.Ordinal);

    public AmsSetup<T> Setup<T>(string name) where T : class => (AmsSetup<T>)_setups.GetOrAdd(name, key => new AmsSetup<T>(key));
}

public sealed class AmsSetup<T> where T : class
{
    private readonly SessionCache<string, T> _value = new();
    private string? _failure;

    public AmsSetup(string name) => Name = name;

    public string Name { get; }

    public async Task<T> GetAsync(Func<Task<T>> build)
    {
        if (_failure is not null)
        {
            throw new ScenarioBlockedException($"{Name} couldn't be set up earlier in the run: {_failure}", Name);
        }

        try
        {
            return await _value.GetAsync(Name, build).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _failure = exception.Message;
            throw;
        }
    }

    public async Task ReplaceAsync(T value)
    {
        _value.Consume(Name);
        await _value.GetAsync(Name, () => Task.FromResult(value)).ConfigureAwait(false);
    }
}
