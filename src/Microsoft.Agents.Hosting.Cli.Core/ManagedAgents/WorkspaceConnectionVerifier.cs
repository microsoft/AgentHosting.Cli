using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Checks that a connected workspace still describes what it claims to.
/// </summary>
public static class WorkspaceConnectionVerifier
{
    /// <summary>Confirms a workspace records the organization an environment resolves to.</summary>
    /// <param name="workspace">The connected workspace.</param>
    /// <param name="environment">The environment the operation targets.</param>
    public static void RequireOriginMatches(CopilotWorkspaceInfo workspace, CopilotEnvironmentContext environment)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(environment);

        var recorded = workspace.DataverseOrganization;

        if (recorded is null
            || !recorded.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(recorded.UserInfo)
            || !recorded.IsDefaultPort
            || !recorded.Host.Equals(environment.DataverseOrganization.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw CopilotStudioException.WorkspaceOriginMismatch(
                recorded?.GetLeftPart(UriPartial.Authority) ?? "(none)",
                environment.DataverseOrganization.GetLeftPart(UriPartial.Authority),
                environment.EnvironmentId,
                workspace.WorkspacePath);
        }
    }

    /// <summary>
    /// Confirms the agent a selector resolved to is the one the workspace is bound to.
    /// </summary>
    /// <param name="workspace">The connected workspace, when there is one.</param>
    /// <param name="environment">The environment the operation targets.</param>
    /// <param name="resolvedAgentId">The agent the selector resolved to.</param>
    public static void RequireBoundAgent(CopilotWorkspaceInfo? workspace, CopilotEnvironmentContext environment, string resolvedAgentId)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (workspace is null
            || !string.Equals(workspace.EnvironmentId, environment.EnvironmentId, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(workspace.AgentId)
            || string.Equals(workspace.AgentId, resolvedAgentId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw ManagedAgentException.WorkspaceIdentityMismatch(workspace.AgentId, resolvedAgentId);
    }
}
