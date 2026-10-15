using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Reads a single principal's access, which only a test needs.
///
/// Production addresses a principal through <see cref="AgentShareTarget.IsOwner"/> and
/// <see cref="AgentShareTarget.HoldsExactly"/> rather than through one role, because a principal may
/// hold several and naming only one of them would misreport its access. These helpers answer only
/// for a principal holding exactly one access level, so an assertion cannot pass by reading past the
/// rest.
/// </summary>
internal static class AgentSharingStateAssertions
{
    /// <summary>The only access level the principal holds, or null when it holds none or several.</summary>
    internal static AgentSharingRole? SoleRole(this AgentShareTarget target) =>
        target.Roles is [var role] && target.SharingTypes.Count == 1 ? role : null;

    /// <summary>The only access level a principal holds in this state.</summary>
    internal static AgentSharingRole? RoleOf(this AgentSharingState state, string objectId) => state.TargetFor(objectId)?.SoleRole();
}
