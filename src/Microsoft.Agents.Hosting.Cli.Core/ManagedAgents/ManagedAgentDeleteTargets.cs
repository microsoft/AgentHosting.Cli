using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

internal static class ManagedAgentDeleteTargets
{
    public const string DeletedStatus = "agent-deleted";

    public static IReadOnlyList<string> ResourceDeleteOrder { get; } = [CopilotCopyIdentityKinds.AIModel, CopilotCopyIdentityKinds.Workflow, CopilotCopyIdentityKinds.ConnectionReference, CopilotCopyIdentityKinds.Connector];

    public static IReadOnlyList<CopilotCopyLink> Resources(IReadOnlyList<CopilotCopyLink> workspaceResources) => [.. workspaceResources.DistinctBy(resource => (resource.Kind, resource.Identity.ToUpperInvariant())).OrderBy(resource => ResourceDeleteOrder.TakeWhile(kind => !string.Equals(kind, resource.Kind, StringComparison.Ordinal)).Count())];

    public static async Task<ManagedAgentDeleteResult?> StopBeforeDeletingAsync(IConfirmationPrompt confirmations, ManagedAgentDeleteRequest request, ManagedAgentDeleteResult planned, CancellationToken cancellationToken)
    {
        var outcome = await ConfirmationPolicy.ConfirmAsync(confirmations, request.NonInteractive, string.Join(Environment.NewLine, ["About to delete:", .. planned.PlanLines(), string.Empty, "This deletes remote resources. Local files are not touched."]), planned.DisplayName, cancellationToken).ConfigureAwait(false);

        return outcome == ConfirmationOutcome.Confirmed ? null : new ManagedAgentDeleteResult
        {
            Status = outcome == ConfirmationOutcome.Mismatched ? "name-mismatch" : "cancelled",
            AgentId = planned.AgentId,
            DisplayName = planned.DisplayName,
            McsWorkflowId = planned.McsWorkflowId,
            EnvironmentId = planned.EnvironmentId,
            DeploymentId = planned.DeploymentId,
            OrphanedTargets = planned.OrphanedTargets,
        };
    }

    public static IReadOnlyList<ManagedAgentOrphanedTarget> OrphanedBy(ManagedAgentRecord agent, IReadOnlyList<AgentDeploymentRecord> deployments, string? deletedEnvironmentId, bool includeHome)
    {
        var seen = new HashSet<string?>(StringComparer.OrdinalIgnoreCase) { deletedEnvironmentId };
        var orphaned = new List<ManagedAgentOrphanedTarget>();

        foreach (var deployment in deployments)
        {
            var environmentId = ManagedAgentResolver.TargetEnvironmentId(agent, deployment);

            if (seen.Add(environmentId))
            {
                orphaned.Add(new ManagedAgentOrphanedTarget { DeploymentName = deployment.DeploymentName, EnvironmentId = environmentId });
            }
        }

        if (includeHome && !string.IsNullOrWhiteSpace(agent.EnvironmentId) && seen.Add(agent.EnvironmentId))
        {
            orphaned.Add(new ManagedAgentOrphanedTarget { DeploymentName = WorkflowDeleteService.HomeTargetName, EnvironmentId = agent.EnvironmentId });
        }

        return orphaned;
    }

    public static int RemoveRecords(IManagedAgentRegistry agents, string projectDirectory, ManagedAgentRecord agent, string? deletedEnvironmentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return agents.RemoveWithDeployments(projectDirectory, agent.AgentId, agent.EntityTag);
        }
        catch (Exception failure) when (deletedEnvironmentId is not null && failure is not OperationCanceledException)
        {
            throw ManagedAgentException.RecordsSurvivedCloudDelete(agent.AgentId, deletedEnvironmentId, failure.Message);
        }
    }

    public static IEnumerable<string> Section<T>(string heading, IReadOnlyList<T> items) => items.Count == 0 ? [] : [string.Empty, $"  {heading}:", .. items.Select(item => $"    {item}")];

    public static string CloudAgentLine(string? target, string existence) => target is not null ? $"  cloud agent  {target}" : $"  cloud agent  none — this agent {existence} nowhere in Copilot Studio";
}
