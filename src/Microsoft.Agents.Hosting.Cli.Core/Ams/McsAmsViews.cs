using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Projects Agent Management Service records into the views AMS command results show.</summary>
public static class McsAmsViews
{
    /// <summary>Projects an agent record of the AMS view into an <c>msagent agent list</c> row.</summary>
    /// <param name="agent">The record.</param>
    public static ManagedAgentListItem ListItem(AmsMcsAgentListItem agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return new ManagedAgentListItem
        {
            AgentId = agent.AgentId,
            TenantId = agent.TenantId,
            EnvironmentId = agent.EnvironmentId,
            DisplayName = agent.DisplayName,
            AgentType = agent.AgentType,
            SchemaName = agent.SchemaName,
            RepositoryId = agent.RepositoryId,
            AgentFolder = agent.AgentFolder,
            Branch = agent.Branch,
            HomeDeploymentId = agent.HomeDeploymentId,
            DeploymentSlotCount = agent.DeploymentCount,
            CloudState = agent.CloudState,
        };
    }

    /// <summary>Projects operation notices, wording each by its kind and showing an unknown kind generically.</summary>
    /// <param name="notices">The notices the operation reported.</param>
    public static IReadOnlyList<McsAmsNoticeView> Notices(IEnumerable<AmsMcsOperationNotice> notices)
    {
        ArgumentNullException.ThrowIfNull(notices);

        return [.. notices.Select(notice => new McsAmsNoticeView { Kind = notice.Kind, Target = notice.Target, RelatedItems = notice.RelatedItems, Code = notice.Code, Detail = notice.Detail, Message = NoticeMessage(notice) })];
    }

    /// <summary>Words one operation failure detail as its code and message.</summary>
    /// <param name="detail">The detail.</param>
    public static string DetailText(AmsMcsOperationErrorDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        return string.Join(": ", new[] { detail.Code, detail.Message }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string NoticeMessage(AmsMcsOperationNotice notice)
    {
        var target = notice.Target is null ? null : TerminalText.Sanitize(notice.Target);
        var related = string.Join(", ", notice.RelatedItems.Select(TerminalText.Sanitize));
        return notice.Kind switch
        {
            AmsMcsOperationNoticeKind.FlowLeftInDraft => $"{(target is null ? "A flow" : $"Flow '{target}'")} stays in draft{(related.Length == 0 ? "." : $": connection reference(s) {related} not bound.")}",
            AmsMcsOperationNoticeKind.CollectionSharedWithOtherBots => $"{(target is null ? "A pushed collection" : $"Collection '{target}'")} is also used by{(related.Length == 0 ? " other agents." : $": {related}.")}",
            AmsMcsOperationNoticeKind.PushWarning => TerminalText.Sanitize(DeployWarningText.PushFinding(notice.Code ?? notice.Kind, notice.Target, notice.Detail, notice.Message)),
            _ => $"{TerminalText.Sanitize(notice.Kind)}: {target ?? "-"}{(related.Length == 0 ? string.Empty : $" ({related})")}",
        };
    }
}