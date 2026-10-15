using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Reads the fields an MCS deployment always carries, which are nullable only because a CodeAgent deployment shares the record.</summary>
public static class McsDeploymentFields
{
    /// <summary>The environment an MCS deployment targets, which the store guarantees is present.</summary>
    /// <param name="deployment">The stored deployment.</param>
    public static string McsEnvironmentId(this AgentDeploymentRecord deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        return deployment.EnvironmentId ?? throw new InvalidOperationException($"MCS deployment '{deployment.DeploymentId}' has no environment.");
    }

    /// <summary>The home environment an MCS deployment belongs to, which the store guarantees is present.</summary>
    /// <param name="deployment">The stored deployment.</param>
    public static string McsHomeEnvironmentId(this AgentDeploymentRecord deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        return deployment.HomeEnvironmentId ?? throw new InvalidOperationException($"MCS deployment '{deployment.DeploymentId}' has no home environment.");
    }
}
