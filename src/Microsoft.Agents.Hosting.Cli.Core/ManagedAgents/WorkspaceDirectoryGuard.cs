using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

internal static class WorkspaceDirectoryGuard
{
    public static void RequireEmpty(string workspaceDirectory, bool projectChoosesDestination = true)
    {
        if (Directory.Exists(workspaceDirectory) && Directory.EnumerateFileSystemEntries(workspaceDirectory).Any())
        {
            throw ManagedAgentException.WorkspaceNotEmpty(workspaceDirectory, projectChoosesDestination: projectChoosesDestination);
        }
    }
}
