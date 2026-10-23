using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

internal static class WorkspaceDirectoryGuard
{
    public static void RequireEmpty(string workspaceDirectory)
    {
        if (Directory.Exists(workspaceDirectory) && Directory.EnumerateFileSystemEntries(workspaceDirectory).Any())
        {
            throw ManagedAgentException.WorkspaceNotEmpty(workspaceDirectory);
        }
    }
}
