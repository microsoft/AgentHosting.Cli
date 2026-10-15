using CoreCluster = Microsoft.Agents.Hosting.Cli.Core.Environments.CoreServicesClusterCategory;
using SyncCluster = Microsoft.CopilotStudio.Sync.CoreServicesClusterCategory;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Restates the host's cluster category as the sync library's own enum.</summary>
internal static class SyncClusterCategory
{
    /// <summary>The library category matching a host category, matched by name rather than cast.</summary>
    /// <param name="cluster">The category the host resolved for the active ring and cloud.</param>
    /// <returns>The library's equivalent.</returns>
    /// <exception cref="InvalidOperationException">The library has no category of that name.</exception>
    public static SyncCluster For(CoreCluster cluster) =>
        Enum.TryParse<SyncCluster>(cluster.ToString(), ignoreCase: false, out var mapped) && Enum.IsDefined(mapped)
            ? mapped
            : throw new InvalidOperationException(
                $"The sync library has no cluster category named '{cluster}'. The host and library cluster enums have diverged.");
}