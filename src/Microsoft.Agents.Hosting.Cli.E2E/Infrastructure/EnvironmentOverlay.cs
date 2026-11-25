using System.Collections.Immutable;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>Environment variables one child process gets on top of the harness's own, where a null value removes the variable.</summary>
public sealed class EnvironmentOverlay
{
    private readonly ImmutableDictionary<string, string?> _variables;

    private EnvironmentOverlay(ImmutableDictionary<string, string?> variables) => _variables = variables;

    /// <summary>The overlay that changes nothing.</summary>
    public static EnvironmentOverlay Empty { get; } = new(ImmutableDictionary<string, string?>.Empty);

    /// <summary>Every variable the overlay sets or removes.</summary>
    public IReadOnlyDictionary<string, string?> Variables => _variables;

    /// <summary>Returns an overlay that also sets the variable, or removes it when the value is null.</summary>
    public EnvironmentOverlay Set(string name, string? value) => new(_variables.SetItem(name, value));

    /// <summary>Returns an overlay that also removes the variable.</summary>
    public EnvironmentOverlay Remove(string name) => Set(name, null);

    /// <summary>Returns an overlay whose variables are these, then the other overlay's, which win on a clash.</summary>
    public EnvironmentOverlay Then(EnvironmentOverlay other) => new(_variables.SetItems(other._variables));

    /// <summary>Applies the overlay to a child process's environment.</summary>
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
