using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Maps the access levels <c>msagent agent share</c> accepts onto the roles the service records.</summary>
public static class AgentShareRoles
{
    private static readonly Dictionary<string, AgentSharingRole> Spellings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["use"] = AgentSharingRole.Consumer,
        ["edit"] = AgentSharingRole.Collaborator,
        ["view"] = AgentSharingRole.Analyst,
    };

    /// <summary>The spellings <see cref="TryParse"/> accepts, for use in failure messages.</summary>
    public static IReadOnlyList<string> Accepted => ["use", "edit", "view"];

    /// <summary>Parses a role, ignoring case and surrounding whitespace.</summary>
    /// <param name="value">The supplied text.</param>
    /// <param name="role">The parsed role.</param>
    /// <returns>Whether the value named a role.</returns>
    public static bool TryParse(string? value, out AgentSharingRole role)
    {
        role = AgentSharingRole.Consumer;

        return !string.IsNullOrWhiteSpace(value) && Spellings.TryGetValue(value.Trim(), out role);
    }

    /// <summary>
    /// The name a role is reported under. The owner is reported as <c>owner</c> even though no
    /// <c>--role</c> value produces it, because the service always returns the owner in the state.
    /// </summary>
    /// <param name="role">The role to name.</param>
    /// <returns>The lowercase name.</returns>
    public static string NameOf(AgentSharingRole role) => role switch
    {
        AgentSharingRole.Owner => "owner",
        AgentSharingRole.Collaborator => "edit",
        AgentSharingRole.Consumer => "use",
        AgentSharingRole.Analyst => "view",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown share role."),
    };

    /// <summary>
    /// The name a principal's access is reported under. 
    /// </summary>
    /// <param name="target">The access the service reported, or <see langword="null"/> for none.</param>
    /// <returns>The reported name.</returns>
    public static string NameOf(AgentShareTarget? target) =>
        target is null || target.SharingTypes.Count == 0
            ? "none"
            : string.Join("+", target.SharingTypes.Select(sharingType => AgentShareTarget.TryParseWireName(sharingType) is { } role ? NameOf(role) : sharingType));
}