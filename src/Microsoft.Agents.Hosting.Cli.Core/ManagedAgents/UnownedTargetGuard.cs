using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Decides when a local deploy has to show that the record owns the agent or workflow it would write over.</summary>
public static class UnownedTargetGuard
{
    /// <summary>Whether a deploy has to show that the record owns what it writes over, which holds for a dev-stage deploy outside the record's home environment that --overwrite did not acknowledge.</summary>
    /// <param name="agent">The record being deployed.</param>
    /// <param name="deployment">The deployment being deployed to.</param>
    /// <param name="environmentId">The environment the deploy writes to.</param>
    /// <param name="acknowledged">Whether the operator passed --overwrite.</param>
    /// <returns><see langword="true"/> when the deploy has to show that the record owns what it writes over.</returns>
    public static bool Applies(ManagedAgentRecord agent, AgentDeploymentRecord deployment, string environmentId, bool acknowledged) => !acknowledged && deployment.DeploymentType == DeploymentStages.Dev && !string.Equals(agent.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase);
}
