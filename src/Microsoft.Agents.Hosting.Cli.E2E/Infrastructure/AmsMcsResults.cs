using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public static class AmsMcsResults
{
    private static readonly string[] BoundStates = ["bound", "already-bound", "created"];

    public static AmsAgentDetails? Details(ManagedAgentShowResult shown, string agentId) => shown.Ams?.FirstOrDefault(details => AmsIds.Same(details.AgentId, agentId));

    public static IReadOnlyList<string> DeploymentIds(AmsAgentDetails? details) => [.. (details?.Deployments ?? []).Select(deployment => deployment.DeploymentId.ToLowerInvariant()).Order(StringComparer.Ordinal)];

    public static AgentDeploymentListItem? Slot(AgentDeploymentListResult listed, string deploymentId) => listed.Slots.FirstOrDefault(slot => AmsIds.Same(slot.DeploymentId, deploymentId));

    public static IReadOnlyList<ManagedAgentListItem> Holding(ManagedAgentListResult listed, string? agentId, string? folder = null, string? agentType = null) => [.. listed.Agents.Where(agent => AmsIds.Same(agent.AgentId, agentId) && (folder is null || AmsIds.Same(agent.AgentFolder, folder)) && (agentType is null || AmsIds.Same(agent.AgentType, agentType)))];

    public static IReadOnlyList<ManagedAgentListItem> Named(ManagedAgentListResult listed, string displayName) => [.. listed.Agents.Where(agent => string.Equals(agent.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))];

    public static IReadOnlyList<ManagedAgentListItem> HeldIn(ManagedAgentListResult listed, string project) => [.. listed.Agents.Where(agent => agent.AgentFolder is { Length: > 0 } folder && Directory.Exists(Path.Combine(project, folder)))];

    public static bool OnlyOfType(ManagedAgentListResult listed, string agentType) => listed.Agents.All(agent => AmsIds.Same(agent.AgentType, agentType));

    public static IReadOnlyList<string> Ids(IEnumerable<ManagedAgentListItem> rows) => [.. rows.Select(row => (row.AgentId ?? string.Empty).ToLowerInvariant()).Order(StringComparer.Ordinal)];

    public static IReadOnlyList<string> RestoredIds(AmsAgentRegistration? registration) => [.. (registration?.Restored ?? []).Select(agent => agent.AgentId.ToLowerInvariant()).Order(StringComparer.Ordinal)];

    public static bool Restores(AmsAgentRegistration? registration, IEnumerable<string> agentIds) => RestoredIds(registration).SequenceEqual(agentIds.Select(agentId => agentId.ToLowerInvariant()).Order(StringComparer.Ordinal));

    public static string Describe(ManagedAgentListResult listed) => string.Join(", ", listed.Agents.Select(agent => $"{agent.AgentId}@{agent.AgentFolder} {agent.AgentType}"));

    public static string Describe(IReadOnlyList<DataverseBot> bots) => string.Join(", ", bots.Select(bot => $"{bot.BotId} '{bot.Name}'"));

    public static bool Later(DateTimeOffset? after, DateTimeOffset? before) => after is { } value && (before is not { } earlier || value > earlier);

    public static IReadOnlyList<McsAmsNoticeView> Notices(McsAmsAgentDeployResult deployed, string kind) => [.. deployed.Notices.Where(notice => string.Equals(notice.Kind, kind, StringComparison.OrdinalIgnoreCase))];

    public static string DescribeNotices(McsAmsAgentDeployResult deployed) => string.Join(" | ", deployed.Notices.Select(notice => $"{notice.Kind}: {string.Join(',', notice.RelatedItems)}"));

    public static bool NoticeNames(McsAmsAgentDeployResult deployed, string kind, string item) => Notices(deployed, kind).Any(notice => notice.RelatedItems.Contains(item, StringComparer.OrdinalIgnoreCase));

    public static bool Usable(AgentDeploymentConnectionResult listed) => listed.Connections.Count > 0 && listed.Connections.All(connection => IsBound(connection) || connection.Candidates.Count > 0);

    public static IReadOnlyList<AgentDeploymentConnectionItem> Bound(AgentDeploymentConnectionResult listed) => [.. listed.Connections.Where(connection => IsBound(connection) && connection.ConnectionId is { Length: > 0 })];

    public static bool AllBound(AgentDeploymentConnectionResult listed) => listed.Connections.Count > 0 && Bound(listed).Count == listed.Connections.Count;

    public static bool Holds(AgentDeploymentConnectionResult listed, string? connectionId) => listed.Connections.Any(connection => AmsIds.Same(connection.ConnectionId, connectionId));

    public static string? ConsentLink(AgentDeploymentConnectionResult listed) => listed.Connections.Select(connection => connection.ConsentLink).FirstOrDefault(link => !string.IsNullOrEmpty(link));

    public static string Describe(AgentDeploymentConnectionResult listed) => string.Join(" | ", listed.Connections.Select(connection => $"{connection.ConnectionReference}={connection.State} {connection.ConnectionId} owner={connection.ConnectionOwner} connector={connection.ConnectorStatus} candidates={string.Join(';', connection.Candidates)}"));

    private static bool IsBound(AgentDeploymentConnectionItem connection) => BoundStates.Contains(connection.State, StringComparer.OrdinalIgnoreCase);
}
