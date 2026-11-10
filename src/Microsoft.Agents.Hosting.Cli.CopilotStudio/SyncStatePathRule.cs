using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Recognizes the sync library's local-state paths through the library's own test.</summary>
public sealed class SyncStatePathRule : ISyncStatePathRule
{
    /// <inheritdoc />
    public bool IsSyncStatePath(string path) => SyncStatePaths.IsSyncStatePath(path);
}
