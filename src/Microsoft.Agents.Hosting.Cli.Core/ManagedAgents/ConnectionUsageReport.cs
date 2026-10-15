using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Names the components using a connection reference, for the prompts and messages that have to say
/// what would break.
/// </summary>
public static class ConnectionUsageReport
{
    private const int NamedLimit = 5;

    /// <summary>Names the components a reference is used by.</summary>
    /// <param name="usages">The usages the service reported.</param>
    /// <returns>The components, named and then counted.</returns>
    public static string Describe(IReadOnlyList<CopilotConnectionUsage> usages)
    {
        ArgumentNullException.ThrowIfNull(usages);

        string[] named = [.. usages.Take(NamedLimit).Select(usage => usage.DisplayName ?? usage.FilePath)];

        if (named.Length == 0)
        {
            return "this workspace";
        }

        return usages.Count > named.Length
            ? $"{string.Join(", ", named)}, and {usages.Count - named.Length} more"
            : string.Join(", ", named);
    }
}
