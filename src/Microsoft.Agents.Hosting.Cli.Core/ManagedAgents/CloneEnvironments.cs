using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Decides whether a clone copies its source from another environment or adopts it in the record's home environment.</summary>
public static class CloneEnvironments
{
    /// <summary>Whether the clone copies its source from another environment rather than adopting it in the record's home environment.</summary>
    /// <param name="source">The environment holding the clone source.</param>
    /// <param name="home">The record's home environment.</param>
    /// <returns><see langword="true"/> for a copy, <see langword="false"/> for an adoption.</returns>
    public static bool IsCopy(CopilotEnvironmentContext source, CopilotEnvironmentContext home) => !string.Equals(source.EnvironmentId, home.EnvironmentId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The name a copy declares in place of its source's, which is the requested name for a copy and none for an adoption, whose source keeps its own name.</summary>
    /// <param name="requestedName">The name the operator asked for, or <see langword="null"/> when none was given.</param>
    /// <param name="source">The environment holding the clone source.</param>
    /// <param name="home">The record's home environment.</param>
    /// <returns>The requested name for a copy, otherwise <see langword="null"/>.</returns>
    public static string? NameOverride(string? requestedName, CopilotEnvironmentContext source, CopilotEnvironmentContext home) => IsCopy(source, home) ? requestedName : null;
}
