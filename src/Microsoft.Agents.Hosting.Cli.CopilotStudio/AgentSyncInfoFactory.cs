using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Builds the <see cref="AgentSyncInfo"/> the sync library expects.
/// </summary>
internal static class AgentSyncInfoFactory
{
    /// <summary>Builds sync info for one agent, reading the environment's solution versions.</summary>
    /// <param name="dataverseClient">The client the solution versions are read from.</param>
    /// <param name="environment">The environment the agent belongs to.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Sync info with every field the sync library requires.</returns>
    public static async Task<AgentSyncInfo> CreateAsync(ISyncDataverseClient dataverseClient, CopilotEnvironmentContext environment, Guid agentId, CancellationToken cancellationToken) => new()
    {
        AgentId = agentId,
        DataverseEndpoint = environment.DataverseOrganization,
        EnvironmentId = environment.EnvironmentId,
        SolutionVersions = await dataverseClient.GetSolutionVersionsAsync(cancellationToken).ConfigureAwait(false),
        AccountInfo = new AccountInfo { TenantId = Guid.TryParse(environment.TenantId, out var tenantId) ? tenantId : Guid.Empty },
    };
}
