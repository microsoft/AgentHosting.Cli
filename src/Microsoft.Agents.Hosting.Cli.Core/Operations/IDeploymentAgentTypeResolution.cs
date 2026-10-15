namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Resolves which agent type a unified <c>msagent deployment</c> verb targets.</summary>
public interface IDeploymentAgentTypeResolution
{
    /// <summary>Resolves the agent type for the supplied selectors.</summary>
    /// <param name="query">The routing query.</param>
    DeploymentAgentTypeResult Resolve(DeploymentAgentTypeQuery query);
}
