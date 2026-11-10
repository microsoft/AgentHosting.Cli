using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.CopilotStudio.Sync;
using SyncClusterCategoryValue = Microsoft.CopilotStudio.Sync.CoreServicesClusterCategory;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

internal static class ControlPlaneAccess
{
    internal static Uri RequireTrustedGateway(Uri gateway, string environmentId, PowerPlatformCloudMap cloudMap, SyncClusterCategoryValue cluster) => CopilotStudioControlPlane.IsTrustedGateway(gateway, cluster) ? gateway : throw cloudMap.UntrustedEndpoint(gateway, $"environment '{environmentId}' Copilot Studio agent-management", [CopilotStudioControlPlane.GatewayDomainFor(cluster)]);

    internal static IReadOnlyList<string> TokenScopes(SyncClusterCategoryValue cluster) => AgentHostSyncAuthProvider.ApplicationScopes(CopilotStudioControlPlane.AudienceFor(cluster));
}
