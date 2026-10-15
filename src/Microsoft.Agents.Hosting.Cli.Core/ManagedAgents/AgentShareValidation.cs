using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// The rules every sharing verb enforces.
/// </summary>
public static class AgentShareValidation
{
    /// <summary>The failure an agent selector breaks, or <see langword="null"/> when it is valid.</summary>
    /// <param name="agentId">The supplied <c>--agent-id</c>.</param>
    /// <param name="agentName">The supplied <c>--agent-name</c>.</param>
    /// <returns>The failure, or <see langword="null"/>.</returns>
    public static ManagedAgentException? AgentSelector(string? agentId, string? agentName) =>
        string.IsNullOrWhiteSpace(agentId) && string.IsNullOrWhiteSpace(agentName)
            ? ManagedAgentException.ShareAgentSelectorRequired()
            : null;

    /// <summary>The failure a <c>msagent agent share</c> input breaks, or <see langword="null"/> when it is valid.</summary>
    /// <param name="user">The supplied <c>--user</c>.</param>
    /// <param name="role">The supplied <c>--role</c>.</param>
    /// <param name="shareWithOrg">The supplied <c>--share-with-org</c>, absent when it was not named.</param>
    /// <returns>The failure, or <see langword="null"/>.</returns>
    public static ManagedAgentException? ShareGrant(string? user, string? role, bool? shareWithOrg)
    {
        if (!string.IsNullOrWhiteSpace(user))
        {
            return AgentShareRoles.TryParse(role, out _) ? null : ManagedAgentException.ShareRoleInvalid(role, AgentShareRoles.Accepted);
        }

        if (shareWithOrg is null)
        {
            return ManagedAgentException.SharePrincipalSelectorRequired();
        }

        return string.IsNullOrWhiteSpace(role) || (AgentShareRoles.TryParse(role, out var named) && named == AgentSharingRole.Consumer)
            ? null
            : ManagedAgentException.ShareRoleNotApplicable(role);
    }

    /// <summary>The failure an <c>msagent agent unshare</c> input breaks, or <see langword="null"/> when it is valid.</summary>
    /// <param name="user">The supplied <c>--user</c>.</param>
    /// <returns>The failure, or <see langword="null"/>.</returns>
    public static ManagedAgentException? UnshareTarget(string? user) =>
        string.IsNullOrWhiteSpace(user) ? ManagedAgentException.SharePrincipalSelectorRequired() : null;

    /// <summary>Throws the failure a sharing selector breaks, if it breaks one.</summary>
    /// <param name="selector">The selectors the verb was given.</param>
    public static void RequireAgentSelector(ManagedAgentShareSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        if (AgentSelector(selector.AgentId, selector.AgentName) is { } failure)
        {
            throw failure;
        }
    }
}
