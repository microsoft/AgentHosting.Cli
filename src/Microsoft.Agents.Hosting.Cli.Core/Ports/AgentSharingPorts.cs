namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>The access levels Copilot Studio records against an agent.</summary>
public enum AgentSharingRole
{
    /// <summary>The agent's owner, which sharing never assigns or removes.</summary>
    Owner,

    /// <summary>Full authoring access, shown in the portal as "can edit".</summary>
    Collaborator,

    /// <summary>Chat access, shown in the portal as "can use".</summary>
    Consumer,

    /// <summary>Analytics-only access, shown in the portal as "can view analytics".</summary>
    Analyst,
}

/// <summary>
/// One principal's access to an agent, addressed by Entra object id.
/// </summary>
public sealed record AgentShareTarget
{
    /// <summary>The <see cref="ObjectIdType"/> the service uses for a principal addressed by object id.</summary>
    public const string PrincipalObjectIdType = "Object";

    private static readonly Dictionary<string, AgentSharingRole> RolesByWireName = new(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(AgentSharingRole.Owner)] = AgentSharingRole.Owner,
        [nameof(AgentSharingRole.Collaborator)] = AgentSharingRole.Collaborator,
        [nameof(AgentSharingRole.Consumer)] = AgentSharingRole.Consumer,
        [nameof(AgentSharingRole.Analyst)] = AgentSharingRole.Analyst,
    };

    /// <summary>The principal's Entra object id, which is what the service records.</summary>
    public required string ObjectId { get; init; }

    /// <summary>The access the service records, spelled as the service spells it.</summary>
    public required IReadOnlyList<string> SharingTypes { get; init; }

    /// <summary>How <see cref="ObjectId"/> is to be interpreted, carried through unchanged.</summary>
    public string ObjectIdType { get; init; } = PrincipalObjectIdType;

    /// <summary>The application the access was granted through, carried through unchanged.</summary>
    public string? ClientId { get; init; }

    /// <summary>Every access level this build recognises, in the order the service listed them.</summary>
    public IReadOnlyList<AgentSharingRole> Roles => [.. SharingTypes.Select(TryParseWireName).OfType<AgentSharingRole>()];

    /// <summary>
    /// Whether the principal owns the agent.
    /// </summary>
    public bool IsOwner => SharingTypes.Any(sharingType => TryParseWireName(sharingType) == AgentSharingRole.Owner);

    /// <summary>
    /// Whether the principal holds the agent's ownership and nothing besides it.
    ///
    /// Sharing never sends <see cref="AgentSharingRole.Owner"/>, so a target holding only ownership
    /// is the service's own record of who owns the agent rather than access this CLI caused.
    /// </summary>
    public bool IsOwnerOnly =>
        SharingTypes.Count > 0 && SharingTypes.All(sharingType => TryParseWireName(sharingType) == AgentSharingRole.Owner);

    /// <summary>Whether the principal holds exactly <paramref name="role"/> and nothing else.</summary>
    /// <param name="role">The access to compare against.</param>
    /// <returns>Whether the recorded access is already just that role.</returns>
    public bool HoldsExactly(AgentSharingRole role) =>
        SharingTypes.Count == 1 && TryParseWireName(SharingTypes[0]) == role;

    /// <summary>A target granting one principal one role.</summary>
    /// <param name="objectId">The principal receiving access.</param>
    /// <param name="role">The access to grant.</param>
    /// <returns>The target.</returns>
    public static AgentShareTarget For(string objectId, AgentSharingRole role) => new()
    {
        ObjectId = objectId,
        SharingTypes = [role.ToString()],
    };

    /// <summary>The same principal holding one role, keeping the metadata the service recorded against it.</summary>
    /// <param name="role">The access to grant.</param>
    /// <returns>The updated target.</returns>
    public AgentShareTarget WithRole(AgentSharingRole role) => this with { SharingTypes = [role.ToString()] };

    /// <summary>The role a wire value names, or <see langword="null"/> when it names none.</summary>
    /// <param name="value">The value the service supplied.</param>
    /// <returns>The role, or <see langword="null"/>.</returns>
    public static AgentSharingRole? TryParseWireName(string? value) =>
        value is not null && RolesByWireName.TryGetValue(value.Trim(), out var role) ? role : null;
}

/// <summary>
/// An agent's complete sharing state.
/// </summary>
public sealed record AgentSharingState
{
    /// <summary>Whether everyone in the organization may use the agent.</summary>
    public bool ShareWithOrg { get; init; }

    /// <summary>Whether the agent is discoverable in the organization's agent store.</summary>
    public bool StoreDiscoverable { get; init; }

    /// <summary>Whether principals holding access may see each other's activity.</summary>
    public bool EnableActivitySharing { get; init; }

    /// <summary>Every principal holding access, owner included.</summary>
    public IReadOnlyList<AgentShareTarget> Targets { get; init; } = [];

    /// <summary>
    /// The state with one principal granted a role, replacing any access it already held.
    /// </summary>
    /// <param name="objectId">The principal receiving access.</param>
    /// <param name="role">The access to grant.</param>
    /// <returns>The updated state.</returns>
    public AgentSharingState With(string objectId, AgentSharingRole role) => this with
    {
        Targets = [.. Without(objectId).Targets, TargetFor(objectId)?.WithRole(role) ?? AgentShareTarget.For(objectId, role)],
    };

    /// <summary>The state with one principal's access removed.</summary>
    /// <param name="objectId">The principal losing access.</param>
    /// <returns>The updated state.</returns>
    public AgentSharingState Without(string objectId) => this with
    {
        Targets = [.. Targets.Where(target => !string.Equals(target.ObjectId, objectId, StringComparison.OrdinalIgnoreCase))],
    };

    /// <summary>
    /// The principals this state lists more than once.
    /// </summary>
    public IReadOnlyList<string> DuplicateObjectIds =>
    [
        .. Targets
            .GroupBy(target => target.ObjectId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key),
    ];

    /// <summary>The access a principal currently holds, or <see langword="null"/> when it holds none.</summary>
    /// <param name="objectId">The principal to look for.</param>
    /// <returns>The target, or <see langword="null"/>.</returns>
    public AgentShareTarget? TargetFor(string objectId) =>
        Targets.FirstOrDefault(target => string.Equals(target.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How the state the service reported differs from the one that was asked for.
    ///
    /// An owner the request did not carry is not a divergence: a freshly deployed agent reads back no
    /// targets and writes back the owner, and sharing never grants or revokes ownership.
    /// </summary>
    /// <param name="intended">The state the write asked for.</param>
    /// <returns>One description per divergence, empty when the service applied the state as asked.</returns>
    public IReadOnlyList<string> DifferencesFrom(AgentSharingState intended)
    {
        ArgumentNullException.ThrowIfNull(intended);

        var differences = new List<string>();

        AddSetting(differences, "org-wide use access", ShareWithOrg, intended.ShareWithOrg);
        AddSetting(differences, "agent-store discoverability", StoreDiscoverable, intended.StoreDiscoverable);
        AddSetting(differences, "activity sharing", EnableActivitySharing, intended.EnableActivitySharing);

        var applied = GroupByObjectId(Targets);
        var wanted = GroupByObjectId(intended.Targets);

        foreach (var (objectId, targets) in wanted)
        {
            differences.AddRange(applied.TryGetValue(objectId, out var holding)
                ? Differences(objectId, holding, targets)
                : [$"{objectId} holds no access, and should hold '{DescribeAccess(targets[0])}'"]);
        }

        differences.AddRange(applied
            .Where(entry => !wanted.ContainsKey(entry.Key) && !entry.Value.All(target => target.IsOwnerOnly))
            .Select(entry => $"{entry.Key} holds '{DescribeAccess(entry.Value[0])}', which was not asked for"));

        return differences;
    }

    /// <summary>
    /// The targets keyed by principal, built once so comparing two states costs one pass over each
    /// rather than a scan of one for every entry of the other.
    /// </summary>
    private static Dictionary<string, List<AgentShareTarget>> GroupByObjectId(IReadOnlyList<AgentShareTarget> targets)
    {
        var grouped = new Dictionary<string, List<AgentShareTarget>>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            if (!grouped.TryGetValue(target.ObjectId, out var group))
            {
                grouped[target.ObjectId] = group = [];
            }

            group.Add(target);
        }

        return grouped;
    }

    private static IEnumerable<string> Differences(string objectId, List<AgentShareTarget> applied, List<AgentShareTarget> intended) =>
        applied.Count == intended.Count
            ? applied.SelectMany((target, index) => Differences(objectId, target, intended[index]))
            : [$"{objectId} is listed {applied.Count} time(s) rather than {intended.Count}"];

    private static IEnumerable<string> Differences(string objectId, AgentShareTarget applied, AgentShareTarget intended)
    {
        if (!SortedAccess(applied).SequenceEqual(SortedAccess(intended), StringComparer.OrdinalIgnoreCase))
        {
            yield return $"{objectId} holds '{DescribeAccess(applied)}' rather than '{DescribeAccess(intended)}'";
        }

        if (!string.Equals(applied.ObjectIdType, intended.ObjectIdType, StringComparison.OrdinalIgnoreCase))
        {
            yield return $"{objectId} is recorded as '{applied.ObjectIdType}' rather than '{intended.ObjectIdType}'";
        }

        if (!string.Equals(applied.ClientId, intended.ClientId, StringComparison.OrdinalIgnoreCase))
        {
            yield return $"{objectId} holds access through {DescribeClient(applied.ClientId)} rather than {DescribeClient(intended.ClientId)}";
        }
    }

    private static void AddSetting(List<string> differences, string name, bool applied, bool intended)
    {
        if (applied != intended)
        {
            differences.Add($"{name} is {Describe(applied)} rather than {Describe(intended)}");
        }
    }

    private static IEnumerable<string> SortedAccess(AgentShareTarget target) => target.SharingTypes.OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

    private static string DescribeClient(string? clientId) => string.IsNullOrEmpty(clientId) ? "no application" : $"application {clientId}";

    private static string Describe(bool enabled) => enabled ? "on" : "off";

    private static string DescribeAccess(AgentShareTarget target) => target.SharingTypes.Count == 0 ? "none" : string.Join("+", target.SharingTypes);
}

/// <summary>Reads and writes an agent's sharing through the Copilot Studio control plane.</summary>
public interface IAgentSharingPort
{
    /// <summary>Reads an agent's complete sharing state.</summary>
    /// <param name="environment">The environment holding the agent.</param>
    /// <param name="agentId">The agent to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The current sharing state.</returns>
    Task<AgentSharingState> ReadAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken);

    /// <summary>Replaces an agent's complete sharing state.</summary>
    /// <param name="environment">The environment holding the agent.</param>
    /// <param name="agentId">The agent to write.</param>
    /// <param name="state">The state to apply, which must carry every principal that keeps access.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The state the service reports after the write.</returns>
    Task<AgentSharingState> WriteAsync(CopilotEnvironmentContext environment, string agentId, AgentSharingState state, CancellationToken cancellationToken);
}

/// <summary>A directory principal an agent can be shared with.</summary>
public sealed record DirectoryPrincipal
{
    /// <summary>The principal's Entra object id.</summary>
    public required string ObjectId { get; init; }

    /// <summary>The principal's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Whether the principal is a <c>User</c> or a <c>Group</c>.</summary>
    public required string PrincipalType { get; init; }

    /// <summary>The user's sign-in address, absent for a group.</summary>
    public string? SignInAddress { get; init; }
}

/// <summary>
/// Resolves directory principals, which sharing addresses by Entra object id rather than by any
/// per-environment identity.
/// </summary>
public interface IDirectoryPrincipalPort
{
    /// <summary>Resolves a user by sign-in address or Entra object id.</summary>
    /// <param name="environment">The environment to resolve within.</param>
    /// <param name="selector">A sign-in address or an object id.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The principal, or <see langword="null"/> when nothing matched.</returns>
    Task<DirectoryPrincipal?> ResolveUserAsync(CopilotEnvironmentContext environment, string selector, CancellationToken cancellationToken);

    /// <summary>Names a set of object ids in as few requests as the directory allows.</summary>
    /// <param name="environment">The environment to resolve within.</param>
    /// <param name="objectIds">The object ids to name.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The principals that resolved, keyed by object id.</returns>
    Task<IReadOnlyDictionary<string, DirectoryPrincipal>> ResolveManyAsync(CopilotEnvironmentContext environment, IReadOnlyCollection<string> objectIds, CancellationToken cancellationToken);
}
