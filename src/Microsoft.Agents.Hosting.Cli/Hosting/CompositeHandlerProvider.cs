namespace Microsoft.Agents.Hosting.Cli.Hosting;

/// <summary>
/// Resolves a command handler from the first provider that supplies one, so a command may take its
/// handler from the operations facade or from the application container.
/// </summary>
public sealed class CompositeHandlerProvider : IServiceProvider
{
    private readonly IReadOnlyList<IServiceProvider> _providers;

    /// <summary>Creates the provider.</summary>
    /// <param name="providers">The providers to consult, in order.</param>
    public CompositeHandlerProvider(params IServiceProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        if (Array.Exists(providers, provider => provider is null))
        {
            throw new ArgumentException("A composed provider cannot be null.", nameof(providers));
        }

        _providers = [.. providers];
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType)
    {
        foreach (var provider in _providers)
        {
            if (provider.GetService(serviceType) is { } service)
            {
                return service;
            }
        }

        return null;
    }
}
