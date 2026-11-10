using System.Collections.Immutable;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public sealed class EnvironmentOverlay
{
    private readonly ImmutableDictionary<string, string?> _variables;

    private EnvironmentOverlay(ImmutableDictionary<string, string?> variables) => _variables = variables;

    public static EnvironmentOverlay Empty { get; } = new(ImmutableDictionary<string, string?>.Empty);

    public IReadOnlyDictionary<string, string?> Variables => _variables;

    public EnvironmentOverlay Set(string name, string? value) => new(_variables.SetItem(name, value));

    public EnvironmentOverlay Remove(string name) => Set(name, null);

    public EnvironmentOverlay Then(EnvironmentOverlay other) => new(_variables.SetItems(other._variables));

    public void ApplyTo(IDictionary<string, string?> environment)
    {
        foreach (var (name, value) in _variables)
        {
            if (value is null)
            {
                environment.Remove(name);
            }
            else
            {
                environment[name] = value;
            }
        }
    }
}
